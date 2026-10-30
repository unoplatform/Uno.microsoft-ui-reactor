# Shared by the scripts that build a sample the way a consumer does: in a folder outside the repo,
# against a packed Uno.Microsoft.UI.Reactor instead of the src/Reactor.Uno project
# (smoke-package.ps1, build-package.ps1).

$ErrorActionPreference = 'Stop'
$packageId = 'Uno.Microsoft.UI.Reactor'

<#
.SYNOPSIS
    Checks -Feed holds the package, creates -WorkDir, writes a nuget.config there that takes the
    package from -Feed only, and points NUGET_PACKAGES and the http cache into -WorkDir.
    Returns the resolved feed and work folder.

.DESCRIPTION
    -WorkDir must not sit under the repo: a global.json, Directory.Build.* or nuget.config above
    it would pin Uno.Sdk or add the repo's settings, and the check would pass for the wrong reason.
    The private NUGET_PACKAGES keeps a package already in the global cache from standing in for
    the one under test.
#>
function Initialize-ConsumerFolder([string]$Feed, [string]$Version, [string]$WorkDir) {
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

    [pscustomobject]@{ Feed = $Feed; WorkDir = $WorkDir }
}

# The one match of -Pattern in -Text; throws when there are none or several.
function Get-OneMatch([string]$Text, [string]$Pattern, [string]$What) {
    $found = [regex]::Matches($Text, $Pattern)
    if ($found.Count -ne 1) { throw "Expected one $What, found $($found.Count)" }
    $found[0]
}

# Writes -WorkDir's global.json with the repo-root one's .NET SDK and Uno.Sdk pins, as the readmes
# tell a consumer to: the samples' `Sdk="Uno.Sdk"` and `#:sdk Uno.Sdk` name no version.
function Write-ConsumerGlobalJson([string]$WorkDir) {
    $root = Get-Content (Join-Path $PSScriptRoot '../../../global.json') -Raw | ConvertFrom-Json
    if (-not $root.'msbuild-sdks'.'Uno.Sdk') { throw 'The repo-root global.json pins no Uno.Sdk' }
    $path = Join-Path $WorkDir 'global.json'
    [ordered]@{ sdk = $root.sdk; 'msbuild-sdks' = [ordered]@{ 'Uno.Sdk' = $root.'msbuild-sdks'.'Uno.Sdk' } } |
        ConvertTo-Json -Depth 5 | Set-Content $path
    Write-Host "global.json: $(Get-Content $path -Raw)"
}

# Fails unless the build restored the package under test from -Feed (and not, say, from nuget.org).
function Assert-RestoredFromFeed([string]$Feed, [string]$Version) {
    $restored = Join-Path $env:NUGET_PACKAGES "$($packageId.ToLowerInvariant())/$($Version.ToLowerInvariant())"
    if (-not (Test-Path $restored)) { throw "$packageId $Version was not restored into $env:NUGET_PACKAGES" }
    $restoredFrom = (Get-Content (Join-Path $restored '.nupkg.metadata') -Raw | ConvertFrom-Json).source
    if ($restoredFrom.TrimEnd('\', '/') -ne $Feed.TrimEnd('\', '/')) { throw "$packageId $Version came from $restoredFrom, not $Feed" }
    Write-Host "Restored $packageId $Version from $restoredFrom"
}

Export-ModuleMember -Function Initialize-ConsumerFolder, Get-OneMatch, Write-ConsumerGlobalJson, Assert-RestoredFromFeed
