#Requires -Version 7
<#
.SYNOPSIS
    The WebAssembly run of the counter's smoke test (see SmokeTest.cs).

.DESCRIPTION
    Serves the existing net10.0-browserwasm build with `dotnet run --no-build`, opens it
    with `?smoke` in headless Chrome (or Edge), and waits for the app's "[smoke] PASS" or
    "[smoke] FAIL" line on the browser console, which the browser logs to stderr.
    Exits 0 on PASS, and 1 on FAIL or when no result arrives within -TimeoutSeconds.

    Build first:
        dotnet build samples/Uno/ReactorUnoCounter/ReactorUnoCounter.csproj -f net10.0-browserwasm
#>
param(
    [string]$Configuration = 'Debug',
    # Covers the server start and the page load; the app gives up on its own 60s after it starts.
    [int]$TimeoutSeconds = 180,
    # chrome.exe or msedge.exe; defaults to the first one installed.
    [string]$Browser
)

$ErrorActionPreference = 'Stop'

if (-not $Browser) {
    $Browser = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Browser) { throw 'Found neither Chrome nor Edge; pass -Browser.' }
}

$work = Join-Path ([IO.Path]::GetTempPath()) "reactor-uno-smoke-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
$serverLog = Join-Path $work 'server.log'
$browserLog = Join-Path $work 'browser.log'

# Reads a log another process is still writing to.
function Read-Log([string]$path) {
    if (-not (Test-Path $path)) { return '' }
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$server = $null
$browserProcess = $null
$result = $null
try {
    # On a browserwasm project, `dotnet run` starts WasmAppHost, which serves the build
    # and prints "App url: http://127.0.0.1:<port>/".
    $project = Join-Path $PSScriptRoot 'ReactorUnoCounter.csproj'
    $server = Start-Process dotnet -PassThru -NoNewWindow `
        -ArgumentList 'run', '--no-build', '--project', $project, '-f', 'net10.0-browserwasm', '-c', $Configuration `
        -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err"

    $url = $null
    while (-not $url) {
        if ($server.HasExited) { throw "The wasm server exited before printing its URL:`n$(Read-Log $serverLog)$(Read-Log "$serverLog.err")" }
        if ((Get-Date) -gt $deadline) { throw "The wasm server printed no URL within $TimeoutSeconds s." }
        Start-Sleep -Milliseconds 500
        if ((Read-Log $serverLog) -match 'App url: (http://\S+?)/?\s') { $url = "$($Matches[1])/?smoke" }
    }
    Write-Host "Serving at $url; opening it in $Browser"

    # --disable-gpu so a local run renders the way a GPU-less CI runner does.
    $browserProcess = Start-Process $Browser -PassThru `
        -ArgumentList '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', `
            "--user-data-dir=$(Join-Path $work 'profile')", '--enable-logging=stderr', '--v=0', $url `
        -RedirectStandardOutput "$browserLog.out" -RedirectStandardError $browserLog

    while (-not $result) {
        $log = Read-Log $browserLog
        # Console lines look like: [...:INFO:CONSOLE:3] "[smoke] PASS: ...", source: http://... (3)
        if ($log -match '(?m)\[smoke\] (PASS|FAIL).*?(?=", source: |$)') { $result = $Matches[0]; break }
        # An exception escaping the app's Main: it never gets as far as the smoke check.
        if ($log -match '(?m)"(Uncaught .*?)", source: \S*/_framework/dotnet\.') { throw "The app crashed: $($Matches[1])" }
        if ($browserProcess.HasExited) { throw "The browser exited (code $($browserProcess.ExitCode)) with no smoke result." }
        if ((Get-Date) -gt $deadline) { throw "No smoke result within $TimeoutSeconds s." }
        Start-Sleep -Milliseconds 500
    }
}
catch {
    Write-Host "::error::$_"
}
finally {
    foreach ($process in $browserProcess, $server) {
        if ($process -and -not $process.HasExited) { $process.Kill($true) }
    }
    if ($result) {
        Write-Host $result
    }
    else {
        # No result: show what the app and the browser said, minus the browser's own chatter.
        Write-Host '--- server ---'
        Write-Host (Read-Log $serverLog)
        Write-Host '--- browser console ---'
        (Read-Log $browserLog) -split "`n" | Where-Object { $_ -match ':CONSOLE' } | Write-Host
    }
}

if ($result -like '`[smoke`] PASS*') { exit 0 }
exit 1
