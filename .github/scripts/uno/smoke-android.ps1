#Requires -Version 7
<#
.SYNOPSIS
    The Android run of the counter's smoke test (see samples/Uno/ReactorUnoCounter/SmokeTest.cs).

.DESCRIPTION
    Installs the counter's APK on a running emulator or device, starts it with
    REACTOR_SMOKE=1 (Android calls Main with no arguments, so there is no --smoke), and
    waits for the app's "[smoke] PASS" or "[smoke] FAIL" line in logcat.
    Exits 0 on PASS, and 1 on FAIL, a crash, or when no result arrives within -TimeoutSeconds.

    The APK has to carry its assemblies, which a Debug build only does with
    EmbedAssembliesIntoApk; build it for the emulator's ABI:
        dotnet build samples/Uno/ReactorUnoCounter/ReactorUnoCounter.csproj -f net10.0-android `
            -p:EmbedAssembliesIntoApk=true -p:RuntimeIdentifier=android-x64
#>
param(
    [Parameter(Mandatory)]
    [string]$Apk,
    [string]$PackageName = 'dev.reactor.unocounter',
    # Covers the boot wait, the install and the app start; the app gives up on its own 60s after it starts.
    [int]$TimeoutSeconds = 300,
    # Where to save the full logcat; nothing is saved when empty.
    [string]$LogcatPath
)

$ErrorActionPreference = 'Stop'

function Invoke-Adb {
    $output = & adb @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "adb $args failed (exit $LASTEXITCODE): $output" }
    return $output
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$result = $null
try {
    Invoke-Adb wait-for-device | Out-Null
    while ((Invoke-Adb shell getprop sys.boot_completed) -ne '1') {
        if ((Get-Date) -gt $deadline) { throw "The device didn't finish booting within $TimeoutSeconds s." }
        Start-Sleep -Seconds 2
    }

    Write-Host "Installing $Apk"
    Invoke-Adb install -r $Apk | Out-Null

    # .NET for Android reads extra environment variables for a debuggable app from this
    # property when the app starts.
    Invoke-Adb shell setprop debug.mono.env REACTOR_SMOKE=1 | Out-Null
    Invoke-Adb logcat -c | Out-Null

    $activity = (Invoke-Adb shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER $PackageName | Select-Object -Last 1).Trim()
    if ($activity -notlike "$PackageName/*") { throw "Found no launcher activity in $PackageName ($activity)." }
    Write-Host "Starting $activity"
    Invoke-Adb shell am start -W -n $activity | Out-Null

    while (-not $result) {
        $log = (Invoke-Adb logcat -d) -join "`n"
        if ($log -match '(?m)\[smoke\] (PASS|FAIL).*$') { $result = $Matches[0]; break }
        if ($log -match '(?m)FATAL EXCEPTION.*$') { throw "The app crashed: $($Matches[0])" }
        if ((Get-Date) -gt $deadline) { throw "No smoke result within $TimeoutSeconds s." }
        # The app exits right after its result line, so read the log once more before
        # calling a missing process a crash.
        $running = & adb shell pidof $PackageName 2>&1
        Start-Sleep -Seconds 2
        if (-not $running -and -not ((Invoke-Adb logcat -d) -match '\[smoke\]')) { throw "The app exited with no smoke result." }
    }
}
catch {
    Write-Host "::error::$_"
}
finally {
    # Leave the device as it was for the next app.
    & adb shell setprop debug.mono.env "''" 2>&1 | Out-Null
    $log = (& adb logcat -d 2>&1) -join "`n"
    if ($LogcatPath) {
        New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($LogcatPath))) | Out-Null
        Set-Content -Path $LogcatPath -Value $log
    }
    if ($result) {
        Write-Host $result
    }
    else {
        # No result: show what the runtime and the app said.
        Write-Host '--- logcat (errors, the runtime and the app) ---'
        $log -split "`n" | Where-Object { $_ -match ' [EF] |DOTNET|monodroid|mono-rt|AndroidRuntime|\[smoke\]' } | Select-Object -Last 200 | Write-Host
    }
}

if ($result -like '`[smoke`] PASS*') { exit 0 }
exit 1
