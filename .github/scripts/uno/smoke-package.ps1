#Requires -Version 7
<#
.SYNOPSIS
    Runs samples/Uno/file-based/Counter.cs the way the package readme
    (src/Reactor.Uno/PackageReadme.md) tells a consumer to: from a folder outside the repo, with
    Reactor from the `Reactor` UnoFeature instead of the src/Reactor.Uno project.

.DESCRIPTION
    Copies the sample's Counter.cs to -WorkDir and turns it into the readme's form, so the sample stays
    the one source for both: drops the `#:project` line, adds `Reactor` to `UnoFeatures`, and
    takes the `#:sdk Uno.Sdk@<version>` line from the readme (the sample's own Uno.Sdk may
    predate the feature). `#:property UnoReactorVersion=<Version>`, the readme's way to pick a
    version, points the feature at the package under test instead of the one the Uno.Sdk pins.
    Restores from -Feed (only that package; everything else from nuget.org) into a private
    NUGET_PACKAGES, so a package already in the global cache can't stand in for the one under
    test. Builds the desktop app, starts it, and over UI Automation (no input injection) checks
    it shows "Count: 0", clicks "+", and waits for "Count: 1".
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
Import-Module (Join-Path $PSScriptRoot 'package-consumer.psm1') -Force

$consumer = Initialize-ConsumerFolder $Feed $Version $WorkDir
$Feed = $consumer.Feed
$WorkDir = $consumer.WorkDir

# The package readme's form of the sample.
$sdkLine = "#:sdk Uno.Sdk@$(Get-ReadmeUnoSdkVersion)"
$source = Get-Content (Join-Path $PSScriptRoot '../../../samples/Uno/file-based/Counter.cs') -Raw
$null = Get-OneMatch $source '(?m)^#:project .*\r?\n' '#:project line in Counter.cs'
$null = Get-OneMatch $source '(?m)^#:sdk Uno\.Sdk@\S+(?=\r?$)' '#:sdk Uno.Sdk line in Counter.cs'
$features = Get-OneMatch $source '(?m)^#:property UnoFeatures=([^\r\n]*)(?=\r?$)' 'UnoFeatures line in Counter.cs'
if ($features.Groups[1].Value -match '(^|;)\s*Reactor\s*(;|$)') { throw 'Counter.cs already has the Reactor feature' }
$source = [regex]::Replace($source, '(?m)^#:project .*\r?\n', '')
$source = [regex]::Replace($source, '(?m)^#:sdk Uno\.Sdk@\S+(?=\r?$)', "$sdkLine`n#:property UnoReactorVersion=$Version")
$source = [regex]::Replace($source, '(?m)^(#:property UnoFeatures=[^\r\n]*)(?=\r?$)', '$1;Reactor')
$app = Join-Path $WorkDir 'Counter.cs'
Set-Content $app $source -NoNewline
Write-Host "Counter.cs header:"
Get-Content $app | Where-Object { $_ -like '#:*' } | ForEach-Object { Write-Host "  $_" }

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

Assert-RestoredFromFeed $Feed $Version

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
    Write-Host 'PASS: the readme-form Counter.cs started, and clicking "+" re-rendered "Count: 0" as "Count: 1"'
}
catch {
    Write-Host "FAIL: $_"
    exit 1
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Get-Content $appLog, (Join-Path $WorkDir 'app.err.log') -ErrorAction SilentlyContinue
}
