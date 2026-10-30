#Requires -Version 7
<#
.SYNOPSIS
    Builds samples/Uno/ReactorUnoCounter the way the samples readme (samples/Uno/README.md) tells
    a consumer to take a sample out of the repo: with Reactor from the `Reactor` UnoFeature instead
    of the src/Reactor.Uno project.

.DESCRIPTION
    Copies the counter project to -WorkDir and makes the readme's changes: drops the ProjectReference to
    src/Reactor.Uno, adds `Reactor` to `UnoFeatures`, and writes a global.json (the repo root's, with
    the Uno.Sdk the package readme pins; the repo's own Uno.Sdk may predate the feature).
    `UnoReactorVersion=<Version>` points the feature at the package under test instead of the one
    the Uno.Sdk pins. Restores from -Feed (only that package; everything else from nuget.org) into
    a private NUGET_PACKAGES, and builds each of -Framework.
    Build only: the package's desktop run is smoke-package.ps1's.
    Exits 0 on success, 1 on any failure.

    -WorkDir must not sit under the repo: a global.json, Directory.Build.* or nuget.config above
    it would pin Uno.Sdk or add the repo's settings, and the check would pass for the wrong reason.
    Android and iOS need their workloads (`dotnet workload install android ios`).

    Pack first, for example:
        dotnet pack src/Reactor.Uno/Reactor.Uno.csproj -c Release -p:PackageVersion=0.0.0-local1 -o <feed>
#>
param(
    # Folder holding Uno.Microsoft.UI.Reactor.<Version>.nupkg.
    [Parameter(Mandatory)][string]$Feed,
    [Parameter(Mandatory)][string]$Version,
    [string[]]$Framework = @('net10.0-browserwasm', 'net10.0-android', 'net10.0-ios'),
    [string]$WorkDir = (Join-Path ([IO.Path]::GetTempPath()) "reactor-uno-package-build-$PID")
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'package-consumer.psm1') -Force
$repoRoot = Join-Path $PSScriptRoot '../../..'
$samples = Join-Path $repoRoot 'samples/Uno'

try {
    $consumer = Initialize-ConsumerFolder $Feed $Version $WorkDir
    $Feed = $consumer.Feed
    $WorkDir = $consumer.WorkDir

    # The project, without build output.
    $project = Join-Path $WorkDir 'ReactorUnoCounter'
    if (Test-Path $project) { Remove-Item -Recurse -Force $project }
    New-Item -ItemType Directory $project | Out-Null
    Get-ChildItem (Join-Path $samples 'ReactorUnoCounter') -Exclude bin, obj, *.binlog, *.user | Copy-Item -Destination $project -Recurse

    # The samples readme's form of the project.
    $csprojPath = Join-Path $project 'ReactorUnoCounter.csproj'
    $csproj = Get-Content $csprojPath -Raw
    $referencePattern = '(?m)^[ \t]*<ProjectReference Include="[^"]*Reactor\.Uno\.csproj"\s*/>\r?\n'
    $null = Get-OneMatch $csproj $referencePattern 'ProjectReference to Reactor.Uno.csproj'
    $featuresPattern = '(?s)(<UnoFeatures>)(.*?)(\s*</UnoFeatures>)'
    $features = Get-OneMatch $csproj $featuresPattern 'UnoFeatures property'
    if ($features.Groups[2].Value -match '(^|;)\s*Reactor\s*(;|$)') { throw 'ReactorUnoCounter.csproj already has the Reactor feature' }
    $csproj = [regex]::Replace($csproj, $referencePattern, '')
    $csproj = [regex]::Replace($csproj, $featuresPattern, {
            param($m)
            $list = $m.Groups[2].Value.TrimEnd()
            if ($list -and -not $list.EndsWith(';')) { $list += ';' }
            "$($m.Groups[1].Value)$($list)Reactor;$($m.Groups[3].Value)`n    <UnoReactorVersion>$Version</UnoReactorVersion>"
        })
    Set-Content $csprojPath $csproj -NoNewline
    Write-Host 'ReactorUnoCounter.csproj:'
    Get-Content $csprojPath | ForEach-Object { Write-Host "  $_" }

    $globalJson = Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
    $globalJson.'msbuild-sdks'.'Uno.Sdk' = Get-ReadmeUnoSdkVersion
    $globalJson | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $WorkDir 'global.json')
    Write-Host "global.json: $(Get-Content (Join-Path $WorkDir 'global.json') -Raw)"

    Push-Location $WorkDir
    try {
        dotnet --version
        foreach ($tfm in $Framework) {
            dotnet build $csprojPath -f $tfm "-bl:$(Join-Path $WorkDir "build-$tfm.binlog")"
            if ($LASTEXITCODE -ne 0) { throw "dotnet build -f $tfm failed (exit $LASTEXITCODE)" }
        }
    }
    finally {
        Pop-Location
    }

    Assert-RestoredFromFeed $Feed $Version
    Write-Host "PASS: the counter built from the package for $($Framework -join ', ')"
}
catch {
    Write-Host "FAIL: $_"
    exit 1
}
