#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$backendRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# Validate the full graph, including each edge's ownership and cycles.
$projects = @{}
Get-ChildItem -LiteralPath (Join-Path $backendRoot 'src'), (Join-Path $backendRoot 'tests') -Filter '*.csproj' -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    ForEach-Object { $projects[$_.BaseName] = $_ }
$allowed = @{
    'SwiftDrop.Contracts' = @()
    'SwiftDrop.Gateway' = @()
    'SwiftDrop.Identity.Api' = @('SwiftDrop.Contracts')
    'SwiftDrop.Delivery.Api' = @('SwiftDrop.Contracts')
    'SwiftDrop.Merchant.Api' = @('SwiftDrop.Contracts')
    'SwiftDrop.Notification.Worker' = @('SwiftDrop.Contracts')
    'SwiftDrop.Identity.Tests' = @('SwiftDrop.Identity.Api')
    'SwiftDrop.Delivery.Tests' = @('SwiftDrop.Delivery.Api')
    # Local client fixture runner; never referenced or published by a service.
    'SwiftDrop.Delivery.Harness' = @('SwiftDrop.Delivery.Tests')
    'SwiftDrop.Merchant.Tests' = @('SwiftDrop.Merchant.Api')
    'SwiftDrop.Notification.Tests' = @('SwiftDrop.Notification.Worker')
    'SwiftDrop.Gateway.Tests' = @('SwiftDrop.Gateway')
}
if ($projects.Count -ne $allowed.Count) { throw 'Unexpected project inventory; review the architecture.' }
$graph = @{}
foreach ($name in $projects.Keys) {
    [xml]$projectXml = Get-Content -LiteralPath $projects[$name].FullName -Raw
    $references = @($projectXml.SelectNodes('//ProjectReference') | ForEach-Object {
        $target = [IO.Path]::GetFullPath((Join-Path $projects[$name].DirectoryName $_.Include))
        $targetName = [IO.Path]::GetFileNameWithoutExtension($target)
        if (-not $projects.ContainsKey($targetName) -or $projects[$targetName].FullName -ne $target) {
            throw "Missing or unexpected reference in $name : $target"
        }
        $targetName
    })
    $graph[$name] = $references
    if (@(Compare-Object @($allowed[$name]) @($references)).Count -ne 0) {
        throw "Disallowed project references in $name"
    }
}
$visiting = @{}
$visited = @{}
function Visit-Project([string]$Name) {
    if ($visiting.ContainsKey($Name)) { throw "Circular project dependency at $Name" }
    if ($visited.ContainsKey($Name)) { return }
    $visiting[$Name] = $true
    foreach ($dependency in $graph[$Name]) { Visit-Project $dependency }
    $visiting.Remove($Name)
    $visited[$Name] = $true
}
foreach ($name in $projects.Keys) { Visit-Project $name }
Write-Output "PASS: $($projects.Count) projects; allowed references only; no circular dependencies."

$hosts = @(
    @{ Name = 'SwiftDrop.Identity.Api'; Directory = 'src/Services/Identity/SwiftDrop.Identity.Api'; Address = 'http://127.0.0.2:8080' },
    @{ Name = 'SwiftDrop.Delivery.Api'; Directory = 'src/Services/Delivery/SwiftDrop.Delivery.Api'; Address = 'http://127.0.0.3:8080' },
    @{ Name = 'SwiftDrop.Merchant.Api'; Directory = 'src/Services/Merchant/SwiftDrop.Merchant.Api'; Address = 'http://127.0.0.4:8080' },
    @{ Name = 'SwiftDrop.Notification.Worker'; Directory = 'src/Workers/SwiftDrop.Notification.Worker'; Address = 'http://127.0.0.5:8080' },
    @{ Name = 'SwiftDrop.Gateway'; Directory = 'src/Gateway/SwiftDrop.Gateway'; Address = 'http://127.0.0.1:5000' }
)
# Fail before launching anything if a required listener is already in use.
$addresses = @($hosts | ForEach-Object { $_.Address }) + @('http://127.0.0.3:8081')
foreach ($address in $addresses) {
    $uri = [uri]$address
    $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Parse($uri.Host)), $uri.Port
    try { $listener.Start() }
    finally { $listener.Stop() }
}

$processes = @()
$handler = New-Object Net.Http.HttpClientHandler
$handler.UseProxy = $false
$client = New-Object Net.Http.HttpClient $handler
$client.Timeout = [TimeSpan]::FromSeconds(2)
$failed = $true
try {
    foreach ($hostInfo in $hosts) {
        $projectDirectory = Join-Path $backendRoot $hostInfo.Directory
        $dll = Join-Path $projectDirectory "bin/$Configuration/net10.0/$($hostInfo.Name).dll"
        if (-not (Test-Path -LiteralPath $dll)) { throw "Build the $Configuration solution first: $dll" }
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = (Get-Command dotnet -ErrorAction Stop).Source
        $startInfo.Arguments = '"' + $dll + '"'
        $startInfo.WorkingDirectory = $projectDirectory
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Development'
        $startInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Development'
        # This smoke check is deliberately offline, including when its caller has
        # configured Neon secrets. Do not pass service/migration secrets to hosts.
        foreach ($secretName in @(
            'RABBITMQ_DEFAULT_PASS',
            'IDENTITY_DATABASE_CONNECTION_STRING',
            'DELIVERY_DATABASE_CONNECTION_STRING',
            'MERCHANT_DATABASE_CONNECTION_STRING',
            'NOTIFICATIONS_DATABASE_CONNECTION_STRING',
            'IDENTITY_MIGRATION_CONNECTION_STRING',
            'ConnectionStrings__Database',
            'ConnectionStrings__MigrationDatabase'
        )) {
            $startInfo.EnvironmentVariables.Remove($secretName)
        }
        $startInfo.EnvironmentVariables['Logging__EventLog__LogLevel__Default'] = 'None'
        if ($hostInfo.Name -eq 'SwiftDrop.Identity.Api') {
            # Command-line configuration also overrides Development user secrets.
            $startInfo.Arguments += ' --ConnectionStrings:Database='
        }
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $startInfo
        $null = $process.Start()
        $entry = @{
            Process = $process
            Name = $hostInfo.Name
            Output = $process.StandardOutput.ReadToEndAsync()
            Error = $process.StandardError.ReadToEndAsync()
        }
        $processes += $entry
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $healthy = $false
        do {
            if ($process.HasExited) { throw "$($hostInfo.Name) exited during startup." }
            try {
                $response = $client.GetAsync($hostInfo.Address + '/health').GetAwaiter().GetResult()
                try {
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $healthy = [int]$response.StatusCode -eq 200 -and $body -eq 'Healthy'
                }
                finally { $response.Dispose() }
            }
            catch [Net.Http.HttpRequestException] { }
            catch [System.Threading.Tasks.TaskCanceledException] { }
            if (-not $healthy) { Start-Sleep -Milliseconds 200 }
        } while (-not $healthy -and [DateTime]::UtcNow -lt $deadline)
        if (-not $healthy) { throw "$($hostInfo.Name) never became live." }
        Write-Output "PASS: $($hostInfo.Name) startup and /health."
    }

    $probes = @(
        'http://127.0.0.1:5000/api/v1/auth/login',
        'http://127.0.0.1:5000/api/v1/merchant/orders',
        'http://127.0.0.1:5000/graphql',
        'http://127.0.0.1:5000/api/v1/parcels/example/events',
        'http://127.0.0.1:5000/internal/v1/webhook-dispatches',
        'http://127.0.0.1:5000/swiftdrop.delivery.v1.DeliveryService/CreateBooking'
    )
    foreach ($hostInfo in $hosts) {
        if ($hostInfo.Name -ne 'SwiftDrop.Identity.Api') {
            $probes += $hostInfo.Address + '/ready'
        }
        $probes += $hostInfo.Address + '/weatherforecast'
    }
    foreach ($address in $probes) {
        $response = $client.GetAsync($address).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne 404) {
                throw "Expected 404 at $address; got $([int]$response.StatusCode)."
            }
        }
        finally { $response.Dispose() }
    }
    Write-Output "PASS: $($probes.Count) missing-feature/internal/readiness probes; real gateway destinations reachable."

    $identityReadiness = $client.GetAsync('http://127.0.0.2:8080/ready').GetAwaiter().GetResult()
    try {
        $readinessBody = $identityReadiness.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $problem = ConvertFrom-Json $readinessBody
        if ([int]$identityReadiness.StatusCode -ne 503 -or $problem.status -ne 503 -or
            $problem.type -ne 'https://example.invalid/problems/database-not-configured') {
            throw 'Expected Identity readiness to fail closed with database-not-configured and HTTP 503.'
        }
    }
    finally { $identityReadiness.Dispose() }
    Write-Output 'PASS: Identity /ready returns database-not-configured/503 without database access.'

    foreach ($writePath in @('/api/v1/quotes', '/api/v1/parcels')) {
        $writeRequest = New-Object Net.Http.HttpRequestMessage ([Net.Http.HttpMethod]::Post), ('http://127.0.0.1:5000' + $writePath)
        $writeRequest.Content = New-Object Net.Http.StringContent '{}', ([Text.Encoding]::UTF8), 'application/json'
        $writeRequest.Headers.Add('X-User-Id', '11111111-1111-4111-8111-111111111111')
        $writeRequest.Headers.Add('X-User-Role', 'Customer')
        $writeRequest.Headers.Authorization = New-Object Net.Http.Headers.AuthenticationHeaderValue 'Bearer', 'unverified-smoke-token'
        try {
            $writeResponse = $client.SendAsync($writeRequest).GetAwaiter().GetResult()
            try {
                $writeProblem = ConvertFrom-Json $writeResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if ([int]$writeResponse.StatusCode -ne 401 -or $writeProblem.code -ne 'unauthorized' -or
                    [string]::IsNullOrWhiteSpace($writeProblem.correlationId)) {
                    throw "Expected $writePath through Gateway to reject unverified identity with safe HTTP 401."
                }
            }
            finally { $writeResponse.Dispose() }
        }
        finally { $writeRequest.Dispose() }
    }
    Write-Output 'PASS: real Gateway quote and booking POSTs fail closed with HTTP 401 despite caller-supplied identity.'

    $parcelRead = $client.GetAsync('http://127.0.0.1:5000/api/v1/parcels').GetAwaiter().GetResult()
    try {
        if ([int]$parcelRead.StatusCode -ne 405) { throw 'Parcel listing remains unimplemented; expected HTTP 405 at the POST-only route.' }
    }
    finally { $parcelRead.Dispose() }
    Write-Output 'PASS: parcel collection GET has no handler (HTTP 405 at the POST-only route).'

    $grpcConnection = New-Object Net.Sockets.TcpClient
    try { $grpcConnection.Connect('127.0.0.3', 8081) }
    finally { $grpcConnection.Dispose() }
    Write-Output 'PASS: private Delivery 8081 TCP listener bound (configured HTTP/2; no gRPC methods implemented).'
    foreach ($entry in $processes) {
        if ($entry.Process.HasExited) { throw "$($entry.Name) exited unexpectedly." }
    }
    $failed = $false
}
finally {
    $client.Dispose()
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) { $entry.Process.Kill() }
        $entry.Process.WaitForExit()
        if ($failed) {
            Write-Output "Host log: $($entry.Name)"
            Write-Output $entry.Output.GetAwaiter().GetResult()
            Write-Output $entry.Error.GetAwaiter().GetResult()
        }
        $entry.Process.Dispose()
    }
    Write-Output 'Stopped all hosts started by this script.'
}
