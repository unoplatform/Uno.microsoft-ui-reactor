# Reactor on Uno Platform — samples

Samples that run **Microsoft.UI.Reactor** on Uno Platform Skia targets
(desktop, WebAssembly, Android, iOS), backed by the
[`Reactor.Uno`](../../src/Reactor.Uno) port library.

| Sample | What it shows | Targets |
| --- | --- | --- |
| [`ReactorUnoCounter`](ReactorUnoCounter) | Minimal counter — the smallest Reactor-on-Uno app | desktop, wasm, android, ios |
| [`ReactorUnoShowcase`](ReactorUnoShowcase) | ToggleSwitch, Slider, ProgressBar, CheckBox, ComboBox, file picker, multi-window (desktop only) | desktop, wasm, android, ios |
| [`ReactorGalleryUno`](ReactorGalleryUno) | The Reactor gallery, sharing [`samples/ReactorGallery`](../ReactorGallery)'s source; the docking and WebView2 pages are stand-ins | desktop, wasm, android, ios |
| [`file-based/Counter.cs`](file-based/Counter.cs) | A whole WinUI-style Reactor app in **one `.cs` file** (`dotnet run Counter.cs`) | desktop; wasm and Android with a header change ([wasm](../../src/Reactor.Uno/README.md#run-on-webassembly-instead), [Android](../../src/Reactor.Uno/README.md#run-on-android-instead)) |

All three app samples are **single Uno projects targeting all four heads** — the
`Component` source is identical everywhere; only the entry point differs (see
[Status](../../src/Reactor.Uno/README.md#status) in the port README).

The samples reference the port as a project (`src/Reactor.Uno`). To use one
outside this repo, drop that `ProjectReference`, add `Reactor` to `UnoFeatures`,
and pin Uno.Sdk `7.0.0-dev.104` or later in `global.json`: the Uno.Sdk then brings
in [`Uno.Microsoft.UI.Reactor`](https://www.nuget.org/packages/Uno.Microsoft.UI.Reactor).
For a new app, start from `dotnet new unoapp-reactor` instead (see the port
README's [quick start](../../src/Reactor.Uno/README.md#quick-start--a-new-app-from-the-template)).

## Prerequisites

- **.NET 10 SDK** (`10.0.400`+ — see [`global.json`](global.json)). Reactor's
  source generators build against Roslyn 5.9, which older SDKs can't load: the
  compiler drops them (`CS9057`) and the build fails with a wall of `CS0759`.
- **[uno-check](https://platform.uno/docs/articles/external/uno.check/doc/using-uno-check.html)**
  for the rest: the `wasm-tools`, `android` and `ios` workloads, the Android SDK
  and a JDK. Add `--target desktop`, `wasm`, `android` or `ios` (repeatable) to
  set up only the heads you need.

  ```bash
  dotnet tool install -g uno.check
  uno-check
  ```

iOS compiles on Windows; deploying to a device still needs a Mac.

The Uno projects resolve `Uno.Sdk` from the nearby `global.json` and restore from
`nuget.org` only (see [`nuget.config`](nuget.config)); they do **not** inherit the
repo-root Windows/WinAppSDK build infrastructure.

## Run

### Desktop / WebAssembly

```bash
# Desktop (X11 / Win32 / macOS / FrameBuffer — picked at runtime)
cd samples/Uno/ReactorUnoCounter
dotnet run -f net10.0-desktop

# WebAssembly (serves the app and prints "App url: http://127.0.0.1:<port>/";
# open that URL in a browser)
dotnet run -f net10.0-browserwasm
```

Swap `ReactorUnoCounter` for `ReactorUnoShowcase` to run the control showcase, or
for `ReactorGalleryUno` to run the gallery.

`dotnet run -f net10.0-desktop -- --smoke` runs the counter as CI's runtime check: it
clicks "+" once, verifies the re-render, and exits 0 (pass) or 1 (fail, after 60s).
For WebAssembly, build with `-f net10.0-browserwasm` and run
[`.github/scripts/uno/smoke-wasm.ps1`](../../.github/scripts/uno/smoke-wasm.ps1) (PowerShell 7).
It serves the build, opens it with
`?smoke` in headless Chrome or Edge, and exits with the result the app logs to the
browser console. It only looks for the browser in the Windows install folders;
elsewhere, pass `-Browser <path>`.

[`.github/scripts/uno/smoke-package.ps1`](../../.github/scripts/uno/smoke-package.ps1)
`-Feed <folder> -Version <version>` is CI's check of the
single-file app against the **package**, in the form the
[package readme](../../src/Reactor.Uno/PackageReadme.md#a-single-file-app) shows: it copies
`Counter.cs` out of the repo, swaps its `#:project` line for the `Reactor` UnoFeature on the
readme's Uno.Sdk plus `#:property UnoReactorVersion=<version>`, restores that version from
the `-Feed` folder (a `dotnet pack` output) into a private package cache, and clicks "+" over
UI Automation (Windows, PowerShell 7).

[`.github/scripts/uno/build-package.ps1`](../../.github/scripts/uno/build-package.ps1)
`-Feed <folder> -Version <version>` does the same for
the counter **project** on the other heads: it copies the project out of the repo and makes
the [changes above](#reactor-on-uno-platform--samples) (drops the `ProjectReference`, adds
`Reactor` to `UnoFeatures`, pins the package readme's Uno.Sdk in `global.json`), sets
`UnoReactorVersion=<version>`, and builds `net10.0-browserwasm`, `net10.0-android` and
`net10.0-ios` (pick with `-Framework`). It builds only; Android and iOS need their
workloads.

### Android / iOS

The same projects, different heads, and the same `Program.cs`: Android never calls
`Main` on its own, so Reactor adds the Android Application and Activity that run it
(the port README's [Status](../../src/Reactor.Uno/README.md#status) explains how).
`Platforms/Android` keeps only the manifest and `environment.conf`.

```bash
cd samples/Uno/ReactorUnoCounter

# Deploy + launch on a connected device or emulator (check `adb devices` first);
# the app's logcat streams to the terminal until Ctrl+C
dotnet run -f net10.0-android

# iOS — compiles on Windows; running it needs a Mac
dotnet build -f net10.0-ios
```

On a Mac, to run on the iOS Simulator, boot a simulator first
(`xcrun simctl list devices booted` shows it), then build for the simulator and
install the app on it. Use `iossimulator-arm64` on Apple silicon and
`iossimulator-x64` on an Intel Mac (`uname -m`):

```bash
cd samples/Uno/ReactorUnoCounter
dotnet build -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64
xcrun simctl install booted bin/Debug/net10.0-ios/iossimulator-arm64/ReactorUnoCounter.app
SIMCTL_CHILD_DOTNET_MODIFIABLE_ASSEMBLIES=debug xcrun simctl launch --console booted dev.reactor.unocounter
```

The `SIMCTL_CHILD_` prefix passes `DOTNET_MODIFIABLE_ASSEMBLIES=debug` to the
app, which [Hot Reload](#ios-simulator) needs, and `--console` keeps the app's
output in the terminal. Launching with `dotnet build -t:Run` instead failed:
mlaunch stops with `error MT0002: Could not parse the environment variable
'DOTNET_MODIFIABLE_ASSEMBLIES'`, because the Dev Server package passes that
variable in a form the 26.0 iOS SDK pack doesn't read. Newer packs weren't tried.

The other bundle IDs are `dev.reactor.unoshowcase` and `dev.reactor.gallery`
(`ApplicationId` in each `.csproj`). A Debug build takes 30–40 seconds to show its
first page on the simulator.

> These steps were run on an Intel Mac with Xcode 26.2 (iPhone 17 Pro simulator,
> iOS 26.3), for the counter and the gallery (the `SIMCTL_CHILD_` launch for the
> counter only). That iOS workload wanted a newer Xcode, so the run targeted the
> older SDK pack instead: every `net10.0-ios` changed to `net10.0-ios26.0` in the
> sample's and `src/Reactor.Uno`'s `.csproj` files, including the `Condition` on
> `src/Reactor.Uno`'s iOS runtime package (a local edit, not committed), plus
> `-p:ValidateXcodeVersion=false` on the build. The `-f` value and the `bin/` path
> in the commands change to match. If your Xcode matches your iOS workload, you
> don't need any of this.

> **Don't sideload the Debug APK.** A Debug Android build uses .NET's *Fast
> Deployment*: the managed assemblies are **not inside the APK** — the deploy
> tooling pushes them separately to `files/.__override__/`. Installing the APK by
> hand therefore produces an app that aborts at startup with
> `No assemblies found in '…/.__override__/arm64-v8a'` (SIGABRT), which the phone
> reports only as a generic failure. Use `dotnet run`, or build a self-contained APK
> with `-p:EmbedAssembliesIntoApk=true` (or `-c Release`) if you really need to
> sideload one.

### file-based/Counter.cs

A single-file app — no project, no `.csproj`. Two things differ from the projects
above: its root component must **not** be named `App`, and its `#:sdk` line pins
the Uno.Sdk version itself instead of reading `global.json`. Both are explained in
the port README's
[Known limitations / notes](../../src/Reactor.Uno/README.md#known-limitations--notes).

```bash
cd samples/Uno/file-based
dotnet run Counter.cs
```

## Build the whole Uno set

From the repo root, one head at a time, as CI does (each needs that head's
workload):

```bash
dotnet build Reactor.Uno.slnx -f net10.0-desktop
dotnet build Reactor.Uno.slnx -f net10.0-browserwasm
dotnet build Reactor.Uno.slnx -f net10.0-android
dotnet build Reactor.Uno.slnx -f net10.0-ios
```

> The file-based `Counter.cs` is not a project and is not in the solution — run it
> directly with `dotnet run Counter.cs`.

## Hot Reload

Edits to a `Component.Render()` body re-render the running app and keep
`UseState`. This has been tried on desktop, and through the Uno Dev Server on
wasm, an Android phone and the [iOS Simulator](#ios-simulator). How it works is
in the port README's [Hot Reload](../../src/Reactor.Uno/README.md#hot-reload)
section.

**From the CLI, tell `dotnet watch` which target to run.** The samples target four
frameworks; cut the list down to `net10.0-desktop` from the command line
([details](../../src/Reactor.Uno/README.md#from-the-command-line)). An IDE picks one
target per debug session by itself.

```bash
cd samples/Uno/ReactorUnoCounter
dotnet watch --property:TargetFrameworks=net10.0-desktop
```

### iOS Simulator

On iOS the edits come through the Uno Dev Server, which an IDE normally starts.
From the command line, start it **before** building: it writes its port into the
sample's `.csproj.user`, and the build stamps that port onto the app. If the Dev
Server restarts on a new port, rebuild. It has to be started from a folder holding
a solution, and `samples/Uno` has none, so make a throwaway one-project solution
there:

```bash
cd samples/Uno
cat > HotReload.slnx <<'EOF'
<Solution>
  <Project Path="ReactorUnoCounter/ReactorUnoCounter.csproj" />
</Solution>
EOF
dotnet dnx uno.devserver start
```

Then build, install and launch as in [Android / iOS](#android--ios), with the
`SIMCTL_CHILD_` launch, and edit `ReactorUnoCounter/Program.cs`. When you're
done, stop the Dev Server with `dotnet dnx uno.devserver stop` and delete
`HotReload.slnx`.

What was run, on the counter:

- **Text edit in `Render()`:** applies (about 5 s for the first edit after a
  launch, under 0.5 s after that), the count is kept, and the next tap adds 1.
- **Hook added before the existing one:** that component's state resets, with no
  error. **Added after it:** state is kept.
- **Property with an initializer added to a record held in state:** the existing
  value is kept, the new property reads `0` rather than its initializer, and
  `with` keeps working.
- **Edit that doesn't compile:** nothing applies and the app keeps running; the
  fix applies.
- **Rude edit** (for example deleting a type): refused with `ENC0033 … requires
  restarting the application`, and the app keeps running unchanged. Unlike
  `dotnet watch`, nothing offers to restart it: rebuild and relaunch.
- **Release build:** carries no Dev Server client and runs normally.

> Run on an Intel Mac with Xcode 26.2 (iPhone 17 Pro simulator, iOS 26.3), with
> the `net10.0-ios26.0` workaround above, `uno.devserver` CLI 6.7.135 and Dev
> Server host 7.0.0-dev.2347.
