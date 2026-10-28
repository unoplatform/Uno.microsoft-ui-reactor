#Requires -Version 7
<#
.SYNOPSIS
    Runs Counter.cs the way a consumer does: from a folder outside the repo, against a packed
    Uno.Microsoft.UI.Reactor instead of the src/Reactor.Uno project.

.DESCRIPTION
    Copies Counter.cs to -WorkDir and rewrites its `#:project` line to
    `#:package Uno.Microsoft.UI.Reactor@[<Version>]`, so the repo sample stays the one source
    for both forms. Restores from -Feed (only that package; everything else from nuget.org)
    into a private NUGET_PACKAGES, so a package already in the global cache can't stand in for
    the one under test. Builds the desktop app, starts it, and over UI Automation (no input
    injection) checks it shows "Count: 0", clicks "+", and waits for "Count: 1".
    Exits 0 on success, 1 on any failure.

    -WorkDir must not sit under the repo: a global.json, Directory.Build.* or nuget.config above
    it would pin Uno.Sdk or add the repo's settings, and the check would pass for the wrong reason.

    Pack first, for example:
        dotnet pack src/Reactor.Uno/Reactor.Uno.csproj -c Release -p:PackageVersion=0.0.0-local1 -o <feed>
#>
param(
    # Folder holding Uno.Microsoft.UI.Reactor.<Version>.nupkg.
    [Parameter(Mandatory)][string]$Feed,
    [Parameter(Mandatory)][string]$Version,
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "reactor-uno-package-smoke-$PID"),
    # Covers the app start and the click; restore and build are not timed.
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$packageId = 'Uno.Microsoft.UI.Reactor'

$Feed = (Resolve-Path $Feed).Path
if (-not (Test-Path (Join-Path $Feed "$packageId.$Version.nupkg"))) {
    throw "$packageId.$Version.nupkg is not in $Feed"
}

New-Item -ItemType Directory -Force $WorkDir | Out-Null
$WorkDir = (Resolve-Path $WorkDir).Path
for ($dir = Split-Path $WorkDir -Parent; $dir; $dir = Split-Path $dir -Parent) {
    $found = @('global.json', 'nuget.config', 'NuGet.Config', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props') |
        ForEach-Object { Join-Path $dir $_ } | Where-Object { Test-Path $_ }
    if ($found) { throw "-WorkDir must not sit under a folder with build settings; found $($found -join ', ')" }
}

# The package form of the sample.
$source = Get-Content (Join-Path $PSScriptRoot 'Counter.cs') -Raw
$projectLine = '(?m)^#:project .*$'
$matched = [regex]::Matches($source, $projectLine).Count
if ($matched -ne 1) { throw "Expected one #:project line in Counter.cs, found $matched" }
$app = Join-Path $WorkDir 'Counter.cs'
Set-Content $app ([regex]::Replace($source, $projectLine, "#:package $packageId@[$Version]")) -NoNewline

# The package comes from -Feed only: nuget.org has dev builds under the same ID.
Set-Content (Join-Path $WorkDir 'nuget.config') @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$Feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="$packageId" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@

$env:NUGET_PACKAGES = Join-Path $WorkDir 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $WorkDir 'http-cache'

Push-Location $WorkDir
try {
    dotnet --version
    $out = Join-Path $WorkDir 'out'
    dotnet build Counter.cs -o $out "-bl:$(Join-Path $WorkDir 'build.binlog')"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build Counter.cs failed (exit $LASTEXITCODE)" }
}
finally {
    Pop-Location
}

$restored = Join-Path $env:NUGET_PACKAGES "$($packageId.ToLowerInvariant())/$($Version.ToLowerInvariant())"
if (-not (Test-Path $restored)) { throw "$packageId $Version was not restored into $env:NUGET_PACKAGES" }
$restoredFrom = (Get-Content (Join-Path $restored '.nupkg.metadata') -Raw | ConvertFrom-Json).source
if ($restoredFrom.TrimEnd('\', '/') -ne $Feed.TrimEnd('\', '/')) { throw "$packageId $Version came from $restoredFrom, not $Feed" }
Write-Host "Restored $packageId $Version from $restoredFrom"

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$automation = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]::Descendants

function Find-Named($window, [string]$name) {
    $window.FindFirst($tree, [System.Windows.Automation.PropertyCondition]::new($automation::NameProperty, $name))
}

function Wait-Until([scriptblock]$condition, [string]$what) {
    while (-not ($value = & $condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out after $TimeoutSeconds s waiting for $what" }
        Start-Sleep -Milliseconds 250
    }
    $value
}

$appLog = Join-Path $WorkDir 'app.log'
$process = Start-Process (Join-Path $out 'Counter.exe') -PassThru -NoNewWindow `
    -RedirectStandardOutput $appLog -RedirectStandardError (Join-Path $WorkDir 'app.err.log')
$null = $process.Handle  # cache the handle, or ExitCode can read back as null
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ownWindow = [System.Windows.Automation.PropertyCondition]::new($automation::ProcessIdProperty, $process.Id)
    $window = Wait-Until {
        if ($process.HasExited) { throw "The app exited with code $($process.ExitCode)" }
        $automation::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $ownWindow)
    } 'the window'
    Write-Host "Window: $($window.Current.Name)"

    $null = Wait-Until { Find-Named $window 'Count: 0' } '"Count: 0"'
    $plus = Wait-Until { Find-Named $window '+' } 'the "+" button'
    $plus.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-Until { Find-Named $window 'Count: 1' } '"Count: 1" after clicking "+"'
    Write-Host 'PASS: the package-form Counter.cs started, and clicking "+" re-rendered "Count: 0" as "Count: 1"'
}
catch {
    Write-Host "FAIL: $_"
    exit 1
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Get-Content $appLog, (Join-Path $WorkDir 'app.err.log') -ErrorAction SilentlyContinue
}
