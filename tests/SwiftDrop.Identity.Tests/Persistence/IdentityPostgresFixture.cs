using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Npgsql;

using SwiftDrop.Identity.Api.Infrastructure;

namespace SwiftDrop.Identity.Tests.Persistence;

[CollectionDefinition(Name)]
public sealed class IdentityPostgresCollection : ICollectionFixture<IdentityPostgresFixture>
{
    public const string Name = "Identity private PostgreSQL";
}

/// <summary>
/// Owns a disposable, loopback-only PostgreSQL cluster. It never uses configured
/// application credentials or connects to another service's database.
/// </summary>
public sealed class IdentityPostgresFixture : IAsyncLifetime
{
    private const string DirectoryPrefix = "swiftdrop-identity-postgres-";
    private string? _temporaryDirectory;
    private string? _temporaryParent;
    private string? _postgresBin;
    private Process? _server;
    private Task<string>? _serverOutput;
    private Task<string>? _serverError;

    public string ConnectionString { get; private set; } = string.Empty;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            _postgresBin = FindPostgresBin()
                ?? throw new InvalidOperationException(
                    "Identity persistence tests require real PostgreSQL binaries. " +
                    "Install PostgreSQL or set SWIFTDROP_TEST_POSTGRES_BIN to its bin directory. " +
                    "Tests fail rather than silently skip database verification.");
            await StartPrivateClusterAsync();

            // The public migration's grants require this role. Neither role nor
            // database is ever created on an existing or shared PostgreSQL server.
            await using (var connection = new NpgsqlConnection(ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("CREATE ROLE identity_app NOLOGIN", connection);
                await command.ExecuteNonQueryAsync();
                command.CommandText = "CREATE DATABASE swiftdrop_identity";
                await command.ExecuteNonQueryAsync();
            }

            ConnectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = "swiftdrop_identity"
            }.ConnectionString;
            await IdentitySchemaMigrator.ApplyAsync(ConnectionString);
            DataSource = NpgsqlDataSource.Create(ConnectionString);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_temporaryDirectory is null)
        {
            return;
        }

        EnsureOwnedDirectory(_temporaryDirectory, _temporaryParent!);
        var dataDirectory = Path.Combine(_temporaryDirectory, "data");
        if (_server is not null)
        {
            try
            {
                if (!_server.HasExited && File.Exists(Path.Combine(dataDirectory, "postmaster.pid")))
                {
                    await RunProcessAsync(ExecutablePath(_postgresBin!, "pg_ctl"),
                        ["stop", "-D", dataDirectory, "-m", "immediate", "-w", "-t", "15"]);
                }
            }
            catch (Exception)
            {
                // The process handle below belongs exclusively to this fixture.
            }
            finally
            {
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
                    // Both the resolved parent and generated directory name have
                    // been checked before this recursive removal.
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
        ConnectionString = string.Empty;
    }

    private async Task StartPrivateClusterAsync()
    {
        var configuredParent = Environment.GetEnvironmentVariable("SWIFTDROP_TEST_POSTGRES_TEMP_ROOT");
        _temporaryParent = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredParent)
            ? Path.GetTempPath()
            : configuredParent);
        _temporaryDirectory = Path.Combine(_temporaryParent, DirectoryPrefix + Guid.NewGuid().ToString("N"));
        EnsureOwnedDirectory(_temporaryDirectory, _temporaryParent);
        Directory.CreateDirectory(_temporaryDirectory);
        var dataDirectory = Path.Combine(_temporaryDirectory, "data");
        await RunProcessAsync(ExecutablePath(_postgresBin!, "initdb"),
            ["-D", dataDirectory, "--encoding=UTF8", "--locale=C", "--auth=trust", "--username=swiftdrop_identity_test"]);

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

        // Trust authentication is confined to this disposable loopback server.
        await File.AppendAllTextAsync(Path.Combine(dataDirectory, "postgresql.conf"),
            $"\nlisten_addresses = '127.0.0.1'\nport = {port}\nunix_socket_directories = ''\n");
        ConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "postgres",
            Username = "swiftdrop_identity_test",
            SslMode = SslMode.Disable,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 15
        }.ConnectionString;

        _server = Process.Start(CreateStartInfo(ExecutablePath(_postgresBin!, "postgres"), ["-D", dataDirectory]))
            ?? throw new InvalidOperationException("Could not start the private Identity PostgreSQL process.");
        _serverOutput = _server.StandardOutput.ReadToEndAsync();
        _serverError = _server.StandardError.ReadToEndAsync();
        var startup = Stopwatch.StartNew();
        Exception? connectionFailure = null;
        while (startup.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (_server.HasExited)
            {
                throw new InvalidOperationException(
                    $"Private Identity PostgreSQL exited before becoming ready.\n{await _serverOutput}\n{await _serverError}");
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
                    throw new InvalidOperationException("Connection reached a PostgreSQL cluster not owned by this fixture.");
                }

                return;
            }
            catch (NpgsqlException exception)
            {
                connectionFailure = exception;
                await Task.Delay(200);
            }
        }

        throw new InvalidOperationException("Private Identity PostgreSQL did not become ready within 30 seconds.", connectionFailure);
    }

    private static string? FindPostgresBin()
    {
        var configured = Environment.GetEnvironmentVariable("SWIFTDROP_TEST_POSTGRES_BIN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return HasPostgresBinaries(configured)
                ? Path.GetFullPath(configured)
                : throw new InvalidOperationException("SWIFTDROP_TEST_POSTGRES_BIN must contain initdb, pg_ctl and postgres.");
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
        using var process = Process.Start(CreateStartInfo(executable, arguments))
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
}
