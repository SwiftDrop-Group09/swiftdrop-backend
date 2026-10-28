#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Browser,
    [ValidateRange(1024, 65535)][int]$Port = 5088
)

$ErrorActionPreference = 'Stop'
$backendRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = Split-Path $backendRoot
$frontendRoot = Join-Path $workspaceRoot 'swiftdrop-frontend'
$mobileRoot = Join-Path $workspaceRoot 'swiftdrop-mobile'
$artifactRoot = Join-Path $backendRoot 'artifacts/courier-phase8-clients'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$checks = New-Object 'System.Collections.Generic.List[object]'
$hostProcess = $null
$hostErrors = $null
$completed = $false

function Quote-Argument([string]$Value) {
    if ($Value.Contains('"')) { throw 'An executable argument contains an unsupported quote.' }
    return '"' + $Value + '"'
}

function Start-CheckProcess([string]$Executable, [string]$Arguments, [string]$Directory) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = $Arguments
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # The socket fixture owns its private cluster; no external database fallback.
    $info.EnvironmentVariables.Remove('SWIFTDROP_TEST_DELIVERY_DATABASE')
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    if (-not $process.Start()) { throw 'A verification process did not start.' }
    return $process
}

function Invoke-ClientCheck([string]$Name, [string]$Executable, [string]$Arguments, [string]$Directory) {
    $process = Start-CheckProcess $Executable $Arguments $Directory
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "$Name timed out."
        }
        $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() |
            Set-Content -LiteralPath (Join-Path $artifactRoot "$Name.txt") -Encoding UTF8
        $status = if ($process.ExitCode -eq 0) { 'passed' } else { 'failed' }
        $checks.Add([pscustomobject]@{ check = $Name; status = $status; exitCode = $process.ExitCode })
        if ($process.ExitCode -ne 0) { throw "$Name failed; see its artifact log." }
    } finally { $process.Dispose() }
}

try {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $node = (Get-Command node -ErrorAction Stop).Source
    $nodeVersion = (& $node --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $nodeVersion -notmatch '^v24\.[0-9]+\.[0-9]+$' -or
        [Version]$nodeVersion.Substring(1) -lt [Version]'24.16.0') {
        throw 'Use the frontend-pinned Node 24.16.0 or later in the 24.x line.'
    }
    $dart = (Get-Command dart.bat -ErrorAction Stop).Source
    $harness = Join-Path $backendRoot 'tests/SwiftDrop.Delivery.Harness/bin/Release/net10.0/SwiftDrop.Delivery.Harness.dll'
    if (-not (Test-Path -LiteralPath $harness)) { throw 'Build the Release solution before running this check.' }
    if ($Browser -and $Port -ne 5088) { throw 'The existing browser fixture requires port 5088.' }
    $hostProcess = Start-CheckProcess $dotnet ((Quote-Argument $harness) + " --actor courier --port $Port --courier-workflow") $backendRoot
    $hostErrors = $hostProcess.StandardError.ReadToEndAsync()
    $startup = $hostProcess.StandardOutput.ReadLineAsync()
    if (-not $startup.Wait(60000)) { throw 'Courier fixture startup timed out.' }
    $address = $startup.GetAwaiter().GetResult()
    if ($address -ne "http://127.0.0.1:$Port/") { throw 'The Courier fixture did not report its expected loopback address.' }
    if ($Browser) {
        # Requires the existing Vite server at 127.0.0.1:5173 with its gateway
        # proxy pointed at this fixture. Identity stubs exist only in that script.
        Invoke-ClientCheck 'react-browser' $node (Quote-Argument (Join-Path $frontendRoot 'test_support/courier-browser-smoke.mjs')) $frontendRoot
    } else { $checks.Add([pscustomobject]@{ check = 'react-browser'; status = 'not-requested' }) }
    Invoke-ClientCheck 'react-http' $node ((Quote-Argument (Join-Path $frontendRoot 'test_support/courier-http-smoke.mjs')) + ' ' + (Quote-Argument $address)) $frontendRoot
    # Batch SDK launcher runs through cmd, with actual exit code and both streams
    # captured by Process; informational Dart stderr is not a PowerShell failure.
    $dartArguments = '/d /s /c "' + (Quote-Argument $dart) + ' run ' +
        (Quote-Argument (Join-Path $mobileRoot 'test_support/courier_http_smoke.dart')) + ' ' + (Quote-Argument $address) + '"'
    Invoke-ClientCheck 'flutter-http' $env:ComSpec $dartArguments $mobileRoot
    $hostProcess.StandardInput.WriteLine('verify-courier')
    $verification = $hostProcess.StandardOutput.ReadLineAsync()
    if (-not $verification.Wait(60000)) { throw 'Post-client verification timed out.' }
    $payload = $verification.GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($payload)) { throw 'Post-client verification returned no result.' }
    $database = $payload | ConvertFrom-Json
    if ($database.Parcels -ne 5 -or $database.Delivered -ne 2 -or $database.Failed -ne 3 -or
        $database.Proofs -ne 2 -or $database.OutboxRows -ne 27 -or $database.HistoryRows -ne 27 -or $database.ReplayRows -ne 10) {
        throw 'The owned fixture did not satisfy its complete post-client invariants.'
    }
    $checks.Add([pscustomobject]@{ check = 'postgres-after-both-clients'; status = 'passed'; totals = $database })
    $completed = $true
} finally {
    $cleanupSucceeded = $true
    if ($null -ne $hostProcess) {
        try {
            if (-not $hostProcess.HasExited) {
                $hostProcess.StandardInput.WriteLine('stop')
                if (-not $hostProcess.WaitForExit(15000)) { $hostProcess.Kill(); $hostProcess.WaitForExit(); $cleanupSucceeded = $false }
            }
            if ($hostProcess.ExitCode -ne 0) { $cleanupSucceeded = $false }
            if ($null -ne $hostErrors) { $hostErrors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $artifactRoot 'fixture-stderr.txt') -Encoding UTF8 }
        } catch { $cleanupSucceeded = $false }
        finally { $hostProcess.Dispose() }
    }
    [pscustomobject]@{ mode = 'owned-postgres-fixed-principal-memory-proof-storage'; completed = ($completed -and $cleanupSucceeded);
        liveJwtVerified = $false; liveObjectStorageVerified = $false; cleanupSucceeded = $cleanupSucceeded; checks = $checks.ToArray() } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifactRoot 'results.json') -Encoding UTF8
    if (-not $cleanupSucceeded) { throw 'Fixture cleanup did not complete; inspect the owned test processes.' }
}
Write-Output 'PASS: both client adapters and post-client PostgreSQL/history/outbox/replay invariants. Live JWT/storage acceptance remains separate.'
