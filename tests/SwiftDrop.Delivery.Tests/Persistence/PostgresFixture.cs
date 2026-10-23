using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace SwiftDrop.Delivery.Tests.Persistence;

/// <summary>
/// Runs persistence tests against a private PostgreSQL cluster, or an explicitly
/// configured database with a unique schema. Never connects to a shared local
/// service by guessing its credentials, and never creates or drops databases.
/// </summary>
/// <remarks>
/// SWIFTDROP_TEST_POSTGRES_BIN overrides binary discovery. A sandbox may set
/// SWIFTDROP_TEST_POSTGRES_TEMP_ROOT to an accessible temporary parent directory.
/// Without local binaries, SWIFTDROP_TEST_DELIVERY_DATABASE explicitly enables
/// the schema-isolated fallback. Missing PostgreSQL always fails these tests.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DirectoryPrefix = "swiftdrop-delivery-postgres-";
    private const string SchemaPrefix = "swiftdrop_test_";
    private string? _temporaryDirectory;
    private string? _temporaryParent;
    private string? _postgresBin;
    private string? _externalConnectionString;
    private string? _schema;
    private bool _schemaCreated;
    private Process? _server;
    private Task<string>? _serverOutput;
    private Task<string>? _serverError;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var externalConnectionString = Environment.GetEnvironmentVariable("SWIFTDROP_TEST_DELIVERY_DATABASE");
        Exception? localFailure = null;

        try
        {
            _postgresBin = FindPostgresBin();
            if (_postgresBin is not null)
            {
                await StartPrivateClusterAsync();
                return;
            }
        }
        catch (Exception exception)
        {
            localFailure = exception;
            await DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(externalConnectionString))
        {
            try
            {
                await InitializeExternalSchemaAsync(externalConnectionString);
                return;
            }
            catch
            {
                await DisposeAsync();
                throw;
            }
        }

        throw new InvalidOperationException(
            "Real PostgreSQL integration tests require working initdb, pg_ctl, and postgres binaries. " +
            "Install PostgreSQL, set SWIFTDROP_TEST_POSTGRES_BIN to its bin directory, or explicitly set " +
            "SWIFTDROP_TEST_DELIVERY_DATABASE to a test database where this login may create a temporary schema. " +
            "These tests fail when PostgreSQL is unavailable; they do not silently skip persistence verification.",
            localFailure);
    }

    public async Task DisposeAsync()
    {
        if (_schemaCreated && _externalConnectionString is not null && _schema is not null)
        {
            EnsureOwnedSchema(_schema);
            await using var connection = new NpgsqlConnection(_externalConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            _schemaCreated = false;
        }

        if (_temporaryDirectory is not null)
        {
            EnsureOwnedDirectory(_temporaryDirectory, _temporaryParent!);
            var dataDirectory = Path.Combine(_temporaryDirectory, "data");
            if (_server is not null)
            {
                try
                {
                    if (!_server.HasExited && File.Exists(Path.Combine(dataDirectory, "postmaster.pid")))
                    {
                        await RunProcessAsync(
                            ExecutablePath(_postgresBin!, "pg_ctl"),
                            ["stop", "-D", dataDirectory, "-m", "immediate", "-w", "-t", "15"]);
                    }
                }
                catch (Exception)
                {
                    // If pg_ctl cannot stop the server, the owned process
                    // handle below still guarantees private-cluster cleanup.
                }
                finally
                {
                    // This handle was created by this fixture, never obtained
                    // from another server's PID or a shared service.
                    if (!_server.HasExited)
                    {
                        _server.Kill(entireProcessTree: true);
                    }

                    await _server.WaitForExitAsync();
                    await _serverOutput!;
                    await _serverError!;
                    _server.Dispose();
                    _server = null;
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(_temporaryDirectory))
                    {
                        Directory.Delete(_temporaryDirectory, recursive: true);
                    }
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(100);
                }
            }

            _temporaryDirectory = null;
        }

        ConnectionString = string.Empty;
        _externalConnectionString = null;
        _schema = null;
    }

    private async Task StartPrivateClusterAsync()
    {
        var configuredTemporaryParent = Environment.GetEnvironmentVariable("SWIFTDROP_TEST_POSTGRES_TEMP_ROOT");
        _temporaryParent = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredTemporaryParent)
            ? Path.GetTempPath()
            : configuredTemporaryParent);
        _temporaryDirectory = Path.Combine(_temporaryParent, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        EnsureOwnedDirectory(_temporaryDirectory, _temporaryParent);
        Directory.CreateDirectory(_temporaryDirectory);
        var dataDirectory = Path.Combine(_temporaryDirectory, "data");

        await RunProcessAsync(
            ExecutablePath(_postgresBin!, "initdb"),
            ["-D", dataDirectory, "--encoding=UTF8", "--locale=C", "--auth=trust", "--username=swiftdrop_test"]);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        int port;
        try
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }

        // Trust authentication applies only to this disposable loopback cluster.
        // Disable Unix sockets so no second connection surface is exposed.
        await File.AppendAllTextAsync(
            Path.Combine(dataDirectory, "postgresql.conf"),
            $"\nlisten_addresses = '127.0.0.1'\nport = {port}\nunix_socket_directories = ''\n");
        ConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "postgres",
            Username = "swiftdrop_test",
            SslMode = SslMode.Disable,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 15
        }.ConnectionString;

        // Start postgres directly and keep its process handle. On Windows this
        // also works in restricted sandboxes where pg_ctl cannot create a
        // further restricted token for its child process.
        var startInfo = CreateStartInfo(ExecutablePath(_postgresBin!, "postgres"), ["-D", dataDirectory]);
        _server = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the private PostgreSQL process.");
        _serverOutput = _server.StandardOutput.ReadToEndAsync();
        _serverError = _server.StandardError.ReadToEndAsync();
        var startup = Stopwatch.StartNew();
        Exception? connectionFailure = null;
        while (startup.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_server.HasExited)
            {
                throw new InvalidOperationException(
                    $"Private PostgreSQL exited before becoming ready.\n{await _serverOutput}\n{await _serverError}");
            }

            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SHOW data_directory", connection);
                var actualDataDirectory = (string)(await command.ExecuteScalarAsync())!;
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!string.Equals(Path.GetFullPath(actualDataDirectory), Path.GetFullPath(dataDirectory), comparison))
                {
                    throw new InvalidOperationException("The connection did not reach this fixture's private PostgreSQL cluster.");
                }

                return;
            }
            catch (NpgsqlException exception)
            {
                connectionFailure = exception;
                await Task.Delay(200);
            }
        }

        throw new InvalidOperationException("Private PostgreSQL did not become ready within 30 seconds.", connectionFailure);
    }

    private async Task InitializeExternalSchemaAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 15
        };
        _externalConnectionString = builder.ConnectionString;
        _schema = SchemaPrefix + Guid.NewGuid().ToString("N");
        EnsureOwnedSchema(_schema);

        await using var connection = new NpgsqlConnection(_externalConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{_schema}\"", connection);
        await command.ExecuteNonQueryAsync();
        _schemaCreated = true;
        builder.SearchPath = _schema;
        ConnectionString = builder.ConnectionString;
    }

    private static string? FindPostgresBin()
    {
        var configured = Environment.GetEnvironmentVariable("SWIFTDROP_TEST_POSTGRES_BIN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return HasPostgresBinaries(configured)
                ? Path.GetFullPath(configured)
                : throw new InvalidOperationException(
                    "SWIFTDROP_TEST_POSTGRES_BIN must contain initdb, pg_ctl, and postgres executables.");
        }

        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            AddVersionedDirectories(candidates, Path.Combine(programFiles, "PostgreSQL"));
        }

        AddVersionedDirectories(candidates, "/usr/lib/postgresql");
        candidates.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/local/pgsql/bin", "/usr/bin"]);
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return candidates.FirstOrDefault(HasPostgresBinaries);
    }

    private static void AddVersionedDirectories(List<string> candidates, string parent)
    {
        if (Directory.Exists(parent))
        {
            candidates.AddRange(Directory.EnumerateDirectories(parent)
                .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var version) ? version : new Version())
                .Select(path => Path.Combine(path, "bin")));
        }
    }

    private static bool HasPostgresBinaries(string directory) =>
        new[] { "initdb", "pg_ctl", "postgres" }.All(name => File.Exists(ExecutablePath(directory, name)));

    private static string ExecutablePath(string directory, string name) =>
        Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    private static async Task RunProcessAsync(string executable, IEnumerable<string> arguments)
    {
        var startInfo = CreateStartInfo(executable, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within 60 seconds.");
        }

        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}.\n{output}\n{error}");
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void EnsureOwnedDirectory(string directory, string temporaryParent)
    {
        var fullPath = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(fullPath);
        var name = Path.GetFileName(fullPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryParent)), comparison)
            || !name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[DirectoryPrefix.Length..], "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside this fixture's temporary cluster.");
        }
    }

    private static void EnsureOwnedSchema(string schema)
    {
        if (!schema.StartsWith(SchemaPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(schema[SchemaPrefix.Length..], "N", out _))
        {
            throw new InvalidOperationException("Refusing to remove a schema not generated by this fixture.");
        }
    }
}
