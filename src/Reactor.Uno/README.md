# Microsoft.UI.Reactor on Uno Platform (Skia)

A port of [Microsoft.UI.Reactor](../../README.md) — the declarative, hooks-based,
component MVU framework for WinUI 3 — to **Uno Platform Skia** targets, so the
exact same Reactor C# can run on **desktop, WebAssembly, Android and iOS** instead
of only Windows / Windows App SDK.

Reactor renders *real `Microsoft.UI.Xaml` controls*. Uno Platform reimplements
that same `Microsoft.UI.Xaml` API surface across Skia heads. So this port is
fundamentally a **retargeting**: the Reactor reconciler, hooks, DSL, layout
engine, charting, data grid, etc. are shared *verbatim* from `../Reactor`,
recompiled against Uno's `Microsoft.UI.Xaml`, with a small Uno-specific hosting
layer replacing the Windows-only windowing/shell stack.

## Status

| Target | TFM | State |
|---|---|---|
| Desktop (Win32 / X11 / macOS / Framebuffer) | `net10.0-desktop` | ✅ Builds **and runs** — interactive |
| WebAssembly | `net10.0-browserwasm` | ✅ Builds **and runs** in the browser |
| Android | `net10.0-android` | ✅ Builds to a **signed APK** and **runs** on a physical device and the emulator |
| iOS | `net10.0-ios` | ✅ **Runs** on the iOS Simulator; compiles on Windows, but deploying needs a Mac |

Both samples are **single Uno projects targeting all four heads** — the component
source is identical everywhere. Only the entry point differs: desktop and iOS use
`ReactorApp.Run<T>()` (Uno gives the Apple heads the same host-builder shape as
desktop), wasm uses `RunAsync` (the browser thread can't block), and Android — the
only target with no console entry point — overrides Uno's `NativeApplication.CreateHost`
to return `ReactorApp.CreateHost<TRoot>()`.

Verified end-to-end: `UseState` → `Button` click → reconciler diff → patched
`TextBlock` on Skia desktop, and the same app rendering **and responding to
clicks** in the browser via WASM and on Android (API 36 emulator). On the iOS
Simulator (iOS 26.3) the counter and gallery samples launch and render their
first page; interaction there is not yet verified.

## Quick start — a file-based, single-file Reactor app

This mirrors the [xaml.dev "file-based WinUI apps with Microsoft.UI.Reactor"](https://xaml.dev/post/file-based-winui-apps-with-microsoft-ui-reactor)
post, but cross-platform. The whole app is one `.cs` file
([`samples/Uno/file-based/Counter.cs`](../../samples/Uno/file-based/Counter.cs)):

```csharp
#:sdk Uno.Sdk@7.0.0-dev.81
#:project ../../../src/Reactor.Uno/Reactor.Uno.csproj
#:property TargetFramework=net10.0-desktop
#:property OutputType=Exe
#:property UnoSingleProject=true
#:property UnoFeatures=SkiaRenderer
#:property PublishAot=false
#:property PackAsTool=false

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run<CounterApp>("Single-File Reactor on Uno", width: 520, height: 400);

class CounterApp : Component
{
    public override Element Render()
    {
        var (name, setName) = UseState("Uno");
        var (count, setCount) = UseState(0);

        return VStack(12,
            Heading($"Hello, {name}!"),
            TextBox(name, setName, placeholderText: "Your name"),
            Heading($"Count: {count}"),
            HStack(8,
                Button("-", () => setCount(count - 1)),
                Button("+", () => setCount(count + 1))
            )
        ).Padding(24);
    }
}
```

Run it:

```bash
cd samples/Uno/file-based
dotnet run Counter.cs
```

> **Versus the WinUI version.** The WinUI file-based app uses
> `#:property TargetFramework=net10.0-windows10.0.22621.0` +
> `#:property WindowsAppSDKSelfContained=true` and `dotnet run App.cs -a x64`.
> Here we swap to `#:sdk Uno.Sdk`, `TargetFramework=net10.0-desktop`, and add the
> Reactor.Uno reference. The Reactor code itself is **identical**.

### Run on WebAssembly instead

A single file-based app targets one TFM at a time. For WASM, set
`#:property TargetFramework=net10.0-browserwasm` and use the async entry point
(`await ReactorApp.RunAsync<CounterApp>(...)`) because the browser thread can't
block. See [`samples/Uno/ReactorUnoCounter`](../../samples/Uno/ReactorUnoCounter) for a normal
multi-targeted project that does both from one source via `#if __WASM__`:

```bash
cd samples/Uno/ReactorUnoCounter
dotnet run -f net10.0-desktop          # desktop window
dotnet run -f net10.0-browserwasm      # prints a localhost URL — open it
```

## Architecture

The port lives under the repo's conventional `src/` and `samples/` folders, but
**each Uno location carries its own build-isolation files** so it never inherits
the Windows / Windows App SDK build infrastructure — the repo-root
`Directory.Build.props` imports `Reactor.targets` and pins WinAppSDK, and the
root `Directory.Packages.props` turns Central Package Management on. MSBuild
stops walking up at the nearest match, so these shadow files pin the `Uno.Sdk`
and turn CPM off for the Uno projects only; the Windows framework build is
untouched (and the Uno projects stay out of the main `Reactor.slnx`, so CI never
restores them). Which files are ours and which are Microsoft's is listed in
[What's ours, what's Microsoft's](#whats-ours-whats-microsofts).

```
src/Reactor.Uno/            # the port library (assembly `Reactor`, ns Microsoft.UI.Reactor)
  Reactor.Uno.csproj        # shares ../Reactor source, targets Uno TFMs
  GlobalUsings.cs           # disambiguation aliases (see below)
  Hosting/
    ReactorApp.cs           # ReactorApp.Run / RunAsync + the static surface the core needs
    UnoBootstrap.cs         # UnoPlatformHostBuilder wiring + the code-only Application
    ReactorHost.cs          # the render loop (reconcile → Window.Content → effects)
    Windowing.cs            # minimal ReactorWindow + windowing types for the shared hooks
  global.json               # pins Uno.Sdk 7.0.0-dev.81
  nuget.config              # nuget.org (self-contained restore)
  Directory.Build.props     # stops inheritance of the repo-root Windows build infra
  Directory.Packages.props  # CPM off for this project

samples/Uno/                # Uno samples — one shared isolation set for all three
  global.json · nuget.config · Directory.Build.props · Directory.Packages.props
  ReactorUnoCounter/        # normal Uno single-project app (desktop + wasm)
  ReactorUnoShowcase/       # larger showcase app
  file-based/Counter.cs     # the single-file app

Reactor.Uno.slnx            # dedicated solution (kept out of the main Reactor.slnx)
```

### Source sharing

`Reactor.Uno.csproj` pulls the portable framework folders in via explicit
`<Compile Include>` globs, from **two** projects:

- from `../Reactor` (core): `Core`, `Hooks`, `Elements`, `Input`,
  `Accessibility`, `Animation`, `Yoga`, `Data`, `Controls`, `Diagnostics`, plus a
  few portable `Hosting/*` files (the charting bridge, render stats, hot-reload
  service, XAML interop);
- from `../Reactor.Advanced`: `Charting`, `Markdown` and `Controls/DataGrid`.
  Spec 062 Track B moved these out of core into a separate Windows-only
  *project*, but the *source* is portable — verified free of Win2D and Docking
  coupling. `Docking/` and `Win2D/` stay excluded.

The wrapper source-generator (`Reactor.Wrappers.Generator`) runs as an analyzer
to emit the control descriptors, exactly as in the Windows build.

> **Source-share drift is the main hazard of this design.** A `<Compile Include>`
> glob that matches zero files is completely silent in MSBuild, so when Track B
> moved those three subsystems the port lost 74 files with a green build. The
> `VerifySharedSourceRoots` target in `Reactor.Uno.csproj` now fails the build if
> any shared root stops existing; `ci-uno.yml` is path-gated on both
> `src/Reactor/**` and `src/Reactor.Advanced/**`.

The assembly is named **`Reactor`** with root namespace
**`Microsoft.UI.Reactor`** — identical to the Windows package — so consumer
`using Microsoft.UI.Reactor;` and `ReactorApp.Run<T>` work unchanged.

### What's replaced or excluded

The Windows `Hosting/` stack is Win32/DWM/AppWindow/shell P/Invoke-heavy, so it
is **not** shared; this project provides a slim Uno hosting layer instead:

- **`ReactorApp`** — `Run` (sync, desktop) / `RunAsync` (async, WASM) build an
  `UnoPlatformHostBuilder` and a code-only `Application`. Platform providers are
  selected per-TFM (`UseX11`/`UseWin32`/`UseMacOS`/`UseLinuxFrameBuffer` on
  desktop, `UseWebAssembly` on WASM).
- **`ReactorHost`** — a trimmed render loop (keeps coalescing, reconcile, effects
  flush, perf stats, and the charting-activation seam; drops the system-backdrop,
  dev-overlay, focus-revalidation, and in-render accessibility machinery).
- **`ReactorWindow` + windowing types** — minimal Uno-backed equivalents so the
  shared windowing hooks (`UseWindow`, `UseWindowSize`, `UseDpi`, …) compile and
  degrade gracefully.

**Excluded subsystems:** `Docking/` (native tab tear-off / floating windows via
Win32), the Windows shell integration (tray/jumplist/taskbar),
window persistence, and the in-app devtools menu.

### What's ours, what's Microsoft's

This repository is a fork of
[microsoft/microsoft-ui-reactor](https://github.com/microsoft/microsoft-ui-reactor).
`upstream-sync.yml` merges Microsoft's `main` into `uno` every night, so every
line of theirs we change has to be merged again, and can conflict, on every sync.

**Don't modify Microsoft's files.** The port's code, build settings and docs go
in the paths below. When shared source doesn't compile on Uno, work around it
from this project: a `<Compile Remove>` in `Reactor.Uno.csproj`, a shim under
`src/Reactor.Uno/`, an alias in `GlobalUsings.cs`, or a build setting in
`Directory.Build.props`. If the fix really belongs in Microsoft's code, send it to
their repository and let the sync bring it in.

Ours:

```
src/Reactor.Uno/                     # the port library
samples/Uno/                         # the Uno samples
Reactor.Uno.slnx                     # the port's solution
version.json                         # Nerdbank.GitVersioning, for the Uno package
.github/actions/nuget-org-publish/
.github/actions/nuget-uno-publish/
.github/actions/tag-release/
.github/workflows/ci-uno.yml
.github/workflows/conventional-commits.yml
.github/workflows/upstream-sync.yml
```

Everything else is Microsoft's. That includes the shared source the port
compiles (`src/Reactor/`, `src/Reactor.Advanced/`), the root build files
(`Directory.Build.props`, `Directory.Packages.props`, `Reactor.slnx`,
`global.json`), `AGENTS.md`, `docs/`, `tests/`, and their workflows in
`.github/workflows/`. Those workflows stay in the tree so the sync merges cleanly,
but they are switched off on this fork.

These Microsoft files are modified today. Don't add to the list, and remove an
entry when you find a way to do without it:

| File | Change | Why it isn't in our paths |
|---|---|---|
| `README.md` | An Uno Platform section at the top | GitHub shows the root README on the fork's front page. |
| `global.json` | An `msbuild-sdks` pin for `Uno.Sdk` | `Reactor.Uno.slnx` sits at the root, so building it resolves SDKs from the root `global.json`; the ones under `src/Reactor.Uno/` and `samples/Uno/` are read only by builds started in those folders. No Windows project uses `Uno.Sdk`, so the pin doesn't affect the Windows build. |
| `src/Reactor/Core/Reconciler.Mount.cs` | `return null` → `return default` in a generic helper | Satisfies Uno's nullable analysis. Behaves the same on Windows, since `T` is constrained to a reference type. |

The shared source has no `#if REACTOR_UNO` blocks. A guard around
`Hyperlink.UnderlineStyle` was removed once Uno made `UnderlineStyleProperty` public
([unoplatform/uno#23652](https://github.com/unoplatform/uno/issues/23652),
`Uno.Sdk 6.7.0-dev.117`).

### Name disambiguation (`GlobalUsings.cs`)

Uno's implicit `Microsoft.UI.Xaml.Controls` global using collides with Reactor's
own `Controls`/`Core` types and with `Microsoft.UI.Xaml`. The shared source
already carries its own `using` directives, so global aliases pin the few bare
names that would otherwise be ambiguous (`SelectionMode` → Reactor's;
`ElementFactoryGetArgs`/`RecycleArgs` → `Microsoft.UI.Xaml`).

## Feature support on Uno Skia

Legend: ✅ works · 🟡 partial / unverified · ❌ not supported (compiles, but no-ops or throws at runtime).

| Area | Status | Notes |
| --- | :---: | --- |
| MVU core: hooks, state, effects, memo, reconciler | ✅ | Full parity with the Windows framework. |
| Layout: Grid, Stack, panels, Yoga/Flex | ✅ | |
| Core controls: Button, TextBlock, TextBox, ToggleSwitch, Slider, ProgressBar, CheckBox, ComboBox, ScrollView | ✅ | Sample-verified (desktop + wasm). |
| Theming (Light/Dark) + live theme-change re-render | ✅ | |
| Implicit animations & transitions (Scale/Rotation/Opacity/Translation transitions, spring / natural-motion, `ImplicitAnimationCollection`) | ❌ | Not implemented in Uno — the calls no-op. |
| RichTextBlock / rich inline text / Markdown rendering | 🟡 | Most `RichTextBlock` members are not implemented in Uno; rich text and Markdown render incompletely. |
| RichEditBox | ❌ | Not implemented in Uno. |
| Custom TitleBar (drag regions, back button, panes) | ❌ | Not implemented in Uno. |
| Specialty controls: ParallaxView, MapControl, SemanticZoom, LinedFlowLayout, AnnotatedScrollBar | ❌ | Not implemented in Uno. |
| Gesture/access flags: `IsTapEnabled` / `IsHoldingEnabled` / `IsDoubleTapEnabled` / `IsRightTapEnabled`, `AccessKey`, `CharacterReceived` | 🟡 | Those specific flags no-op; basic pointer/click still works. |
| High-contrast / forced-colors detection | ❌ | `AccessibilitySettings.HighContrast` not implemented — charts don't adapt to high contrast. |
| Single window + render loop + error fallback | ✅ | |
| Safe area (status/navigation bar, notch, cutouts) | ✅ Android · 🟡 iOS | On by default for all edges; `ReactorHost.SafeAreaEdges` picks which edges are padded (`None` turns it off). Uses Uno's `VisibleBoundsPadding` on the window root, so it pads the whole app, not individual elements, and ignores the soft keyboard. Verified on a Pixel 8 in portrait and landscape; iOS runs the same code but is unverified (see [Safe area](#safe-area)). |
| Multi-window (`OpenWindow` / `UseOpenWindow`) | ✅ desktop | Real secondary windows on every desktop head (X11 / Win32 / macOS / FrameBuffer), each with its own `ReactorHost`, render loop and state. Android/iOS throw `InvalidOperationException` (Uno doesn't support secondary windows there) — `UseOpenWindow` catches it and degrades to a null handle. No OS windows in the browser (wasm). Demoed in `samples/Uno/ReactorUnoShowcase`. |
| DPI (`UseDpi`) | ✅ | Per-window DPI from `XamlRoot.RasterizationScale`, and live changes (window dragged to a monitor with a different scale, or the display scale changed) re-render via `XamlRoot.Changed`. Correctly per-window, since each window has its own `XamlRoot`. |
| File / folder pickers (`UseFilePickerAsync` / `UseFolderPickerAsync`) | ✅ desktop | Uno implements the WinRT pickers. The shared hook's `WindowNative.GetWindowHandle` + `InitializeWithWindow` association is exactly the initialization Uno's own docs prescribe for Uno.WinUI apps — it is not a Windows-only path. On **wasm** it depends on the browser: Uno uses the File System Access API where available (Chromium) and falls back to download/upload pickers; `FolderPicker` needs the File System Access API. Demoed in `samples/Uno/ReactorUnoShowcase`. |
| Tray icons / shell (jump list, taskbar) | ❌ | Stub no-ops. |
| Window persistence (placement save/restore) | ❌ | Not shared. |
| System backdrop / Mica / DWM effects | ❌ | Not shared. |
| Multi-monitor / display enumeration | ❌ | `ReactorDisplay.Displays` returns empty. |
| Window closing guards (`UseClosingGuard`) | ✅ desktop | Backed by Uno's `AppWindow.Closing`. Guards stack; any returning `false` cancels the close, and a throwing guard fail-safes to "cancel" (same as the Windows framework). Honoured on desktop Windows / macOS / Linux. On Android, iOS and wasm the event still fires but cancellation has no effect (per Uno), so the close proceeds. Demoed on the Showcase's second window. |
| Hot Reload (edit `Render()` while running) | ✅ | Reactor registers `[assembly: MetadataUpdateHandler]`, and Uno's own `HotReloadAgent` discovers and invokes **every** registered handler — so Reactor's re-render is driven by Uno's hot-reload pipeline on the targets `dotnet watch` alone can't reach. Needs no opt-in. `UseState` survives; hook add/remove/reorder recovers by remounting. See [Hot Reload](#hot-reload) below. |
| Declarative caption height (`TitleBar(...).Tall()`, `WindowSpec.TitleBarHeight`) | ✅ desktop | Backed by Uno's `AppWindowTitleBar.PreferredHeightOption` (Standard 32 / Tall 48 / Collapsed 0), which Uno implements for real. The WinUI `TitleBar` *control* is still an Uno stub, so only the caption half is visible today. |
| Window drag-move, aspect-ratio lock | ❌ | Still no-op stubs — **not yet audited** against Uno's API surface (multi-window, DPI, pickers and closing guards all turned out to be implementable, so these may be too). |
| Docking (dock manager, tab tear-off, floating windows, splitters) | ❌ | Excluded from the port entirely. |
| In-app devtools | ❌ | `DevtoolsEnabled` is `false`. |
| Charting / DataGrid / PropertyGrid | 🟡 | Source-shared from `Reactor.Advanced` and compiling on all three TFMs; render path shared with WinUI, but not yet runtime-verified on Skia. |
| Markdown rendering | 🟡 | Source-shared from `Reactor.Advanced`. Compiles, but depends on `RichTextBlock`, which Uno implements only partially — so rich output renders incompletely. |

> The ❌ / 🟡 runtime rows line up with the `Uno0001` *not-implemented* build warnings: the code compiles, but those specific APIs no-op (or throw) on Uno. They don't affect the ✅ rows.

## Hot Reload

**Yes — editing a `Component.Render()` body updates the running app, and
`UseState` survives.** Verified on Skia desktop:

```
dotnet watch ⌚ File updated: .\Program.cs
dotnet watch 🔥 C# and Razor changes applied in 424ms.
```
…with the counter still reading the value it held before the edit.

### How it composes with Uno's Hot Reload

Reactor does **not** implement a competing mechanism. `Hosting/HotReloadService.cs`
(source-shared from the Windows framework) registers the standard

```csharp
[assembly: MetadataUpdateHandler(typeof(HotReloadService))]
```

and Uno's own `ClientHotReloadProcessor` registers itself the same way. They are
peers on one runtime mechanism, not layers. Crucially, Uno's `HotReloadAgent`
scans **every** loaded assembly for `MetadataUpdateHandlerAttribute` and invokes
what it finds:

```csharp
// Uno.UI.RemoteControl/HotReload/MetadataUpdater/HotReloadAgent.cs
handlerActions.ClearCache.ForEach(a => a(updatedTypes));
handlerActions.UpdateApplication.ForEach(a => a(updatedTypes));
```

So on the targets where the Uno **Dev Server** delivers the deltas rather than
`dotnet watch` — WebAssembly, Android, iOS — Uno should drive Reactor's re-render
for free, with no Reactor-side work at all. That follows from the code above; it
hasn't been run on those targets yet.

Nothing has to be opted into for this. There is **no `HotReload` UnoFeature**
(passing one just warns `Unable to parse 'hotreload' to a known Uno Feature` and
is ignored); the dev-server client `Uno.WinUI.DevServer` / `Uno.UI.RemoteControl`
is referenced automatically for Debug builds, and the Dev Server process itself is
started by the IDE. `.UseStudio()` is likewise not needed — it lives in
`Uno.UI.HotDesign` and turns on **Hot Design**, Uno's runtime *XAML* visual
designer, which does not apply to a XAML-free framework like Reactor.

On an update Reactor re-renders the whole tree with `force: true` (bypassing memo),
migrates hook cells whose types were edited, and treats a `HookOrderException` as
"the edit changed the hook shape" — it drops that context's hook state and
remounts instead of showing the error overlay.

### Caveat: multi-targeted heads under `dotnet watch`

`dotnet watch -f <tfm>` against an app head whose csproj lists **more than one**
TFM currently loads a Roslyn workspace with no references, and every edit fails
with a wall of `CS0518: Predefined type 'System.Object' is not defined`. This is
not Reactor-specific — it reproduces from the TFM list alone:

| Head `TargetFrameworks` | Result |
| --- | --- |
| `net10.0-desktop;net10.0-browserwasm` | ❌ `CS0518` cascade, hot reload dead |
| `net10.0-desktop` | ✅ `🔥 C# changes applied` |

The referenced library may stay multi-targeted — only the **head** matters. So
when hot-reloading from the CLI, either single-target the head or use an IDE
(VS / VS Code / Rider), which drives the Uno Dev Server and picks one TFM per
debug session. The unrelated `Found project reference without a matching
metadata reference` warning is benign — it is present in the working case too.

All the samples list four TFMs, so this hits each of them as shipped (re-checked
on SDK 10.0.401 with `ReactorUnoCounter`). Overriding the list from the command
line (`dotnet watch --property:TargetFrameworks=net10.0-desktop`) doesn't help:
the global property also reaches the referenced source-generator project, which
fails with `NETSDK1139`. Edit the head's `<TargetFrameworks>` instead.

## Known limitations / notes

- **Uno version.** The port needs Uno **6.7+** APIs (`DispatcherQueueSynchronizationContext`,
  the `TitleBar` drag-region APIs) and tracks the Uno **7.0** pre-release —
  `Uno.Sdk 7.0.0-dev.81`, set in `global.json` (repo root, `src/Reactor.Uno/`,
  `samples/Uno/`, and the file-based `Counter.cs` header). Uno 7.0 renamed the runtime
  packages (`Uno.WinUI.Runtime.Skia.X11` → `Uno.WinUI.Runtime.X11`, …) and moved the
  Android entry types to `Uno.UI.Runtime.Android`. The runtime packages take their
  version from `$(UnoVersion)`, which **Uno.Sdk supplies**, so they track the SDK
  automatically — don't hardcode a version for them (it drifts and trips `NU1605`).
- **Don't name a file-based app's root component `App`** — Uno.Sdk generates an
  `App` type for single-project EXEs, which would clash. Use any other name
  (`CounterApp`, `MyApp`, …); `ReactorApp.Run<T>` doesn't care.
- See the **Feature support on Uno Skia** matrix above for what works, what's
  partial, and what's unsupported. The unsupported/partial rows surface as
  `Uno0001` *not-implemented* build warnings — runtime no-ops in Uno, not build
  breaks — and only affect those specific features.

## Mobile

The samples are single Uno projects whose `net10.0-android` and `net10.0-ios`
heads run as-is; no separate mobile head project is needed. Only the entry point
is platform-specific (see [Status](#status)), and the framework code itself is
platform-agnostic `Microsoft.UI.Xaml`. Deploy commands are in
[samples/Uno](../../samples/Uno/README.md#android--ios).

### Safe area

On Android and iOS the app is kept clear of the status bar, navigation bar,
notch and display cutouts: Uno draws mobile apps edge-to-edge, so the host pads
its window root to the window's visible bounds. Component code needs nothing for
this. An app that draws under the bars itself picks the edges to keep padded when
it creates the host, e.g. `SafeAreaEdges.None` for fully edge-to-edge, or
`SafeAreaEdges.Bottom` to let a header run under the status bar:

```csharp
using Microsoft.UI.Reactor.Hosting;

ReactorApp.CreateHost<CounterApp>("My App", configure: host => host.SafeAreaEdges = SafeAreaEdges.None);
```

On a padded edge, the bar shows the page background, not the app root's own
background. It doesn't move content out from under the soft keyboard.

Known mobile layout gap: on iOS the window doesn't fill the screen (black bars
above and below), so the padding hasn't been seen working there yet.
