# Reactor on Uno Platform — samples

Samples that run **Microsoft.UI.Reactor** on Uno Platform Skia targets
(desktop, WebAssembly, Android, iOS), backed by the
[`Reactor.Uno`](../../src/Reactor.Uno) port library.

| Sample | What it shows | Targets |
| --- | --- | --- |
| [`ReactorUnoCounter`](ReactorUnoCounter) | Minimal counter — the smallest Reactor-on-Uno app | desktop, wasm, android, ios |
| [`ReactorUnoShowcase`](ReactorUnoShowcase) | ToggleSwitch, Slider, ProgressBar, CheckBox, ComboBox, file picker, multi-window (desktop only) | desktop, wasm, android, ios |
| [`ReactorGalleryUno`](ReactorGalleryUno) | The Reactor gallery, sharing [`samples/ReactorGallery`](../ReactorGallery)'s source; the docking and WebView2 pages are stand-ins | desktop, wasm, android, ios |
| [`file-based/Counter.cs`](file-based/Counter.cs) | A whole WinUI-style Reactor app in **one `.cs` file** (`dotnet run Counter.cs`) | desktop |

All three app samples are **single Uno projects targeting all four heads** — the
`Component` source is identical everywhere; only the entry point differs (see
[Status](../../src/Reactor.Uno/README.md#status) in the port README).

The samples reference the port as a project (`src/Reactor.Uno`). To use one
outside this repo, replace that `ProjectReference` with a `PackageReference` to
[`Uno.Microsoft.UI.Reactor`](https://www.nuget.org/packages/Uno.Microsoft.UI.Reactor).

## Prerequisites

- **.NET 10 SDK** (`10.0.100`+ — see [`global.json`](global.json))
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
`./smoke-wasm.ps1` (PowerShell 7). It serves the build, opens it with
`?smoke` in headless Chrome or Edge, and exits with the result the app logs to the
browser console. It only looks for the browser in the Windows install folders;
elsewhere, pass `-Browser <path>`.

### Android / iOS

The same projects, different heads. Android has no console entry point, so each
sample starts there from `Platforms/Android/Main.Android.cs` (for example
[the counter's](ReactorUnoCounter/Platforms/Android/Main.Android.cs)); the port
README's [Status](../../src/Reactor.Uno/README.md#status) explains each target's
entry point.

```bash
cd samples/Uno/ReactorUnoCounter

# Deploy + launch on a connected device or emulator (check `adb devices` first)
dotnet build -f net10.0-android -t:Run

# iOS — compiles on Windows; deploying to a device needs a Mac
dotnet build -f net10.0-ios
```

> **Don't sideload the Debug APK.** A Debug Android build uses .NET's *Fast
> Deployment*: the managed assemblies are **not inside the APK** — the deploy
> tooling pushes them separately to `files/.__override__/`. Installing the APK by
> hand therefore produces an app that aborts at startup with
> `No assemblies found in '…/.__override__/arm64-v8a'` (SIGABRT), which the phone
> reports only as a generic failure. Use `-t:Run`, or build a self-contained APK
> with `-p:EmbedAssembliesIntoApk=true` (or `-c Release`) if you really need to
> sideload one.

> Reactor apps contain **no XAML**, so there is no `ApplicationDefinition` for
> Uno's iOS Hot Restart helper generator to read and it fails with `Uno0005`. The
> samples set `<UnoDisableHotRestartHelperGeneration>true</...>`, which disables
> only VS's iOS *Hot Restart* deploy helper — not Hot Reload.

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
`UseState`. This has only been tried on desktop; on wasm, Android and iOS the Uno
Dev Server should drive it the same way, but that hasn't been run yet. How it
works is in the port README's [Hot Reload](../../src/Reactor.Uno/README.md#hot-reload)
section.

**From the CLI, `dotnet watch` doesn't work on these samples as they are.** They
target four frameworks, and `dotnet watch` against a multi-targeted head fails
every edit with `CS0518`
([details](../../src/Reactor.Uno/README.md#caveat-multi-targeted-heads-under-dotnet-watch)).
Either use an IDE, which picks one target per debug session, or cut the sample's
`<TargetFrameworks>` down to `net10.0-desktop` while you work:

```bash
# after editing ReactorUnoCounter.csproj to <TargetFrameworks>net10.0-desktop</TargetFrameworks>
cd samples/Uno/ReactorUnoCounter
dotnet watch
```
