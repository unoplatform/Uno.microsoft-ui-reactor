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

Mac Catalyst and tvOS are out of scope. Uno dropped Mac Catalyst from its templates
in 6.0 and recommends `net10.0-desktop` for macOS, which the desktop head already
covers.

Every target starts from one `Program.cs` that calls `ReactorApp.Run<T>()`;
[The entry point](#the-entry-point) explains how, Android included.

Verified end-to-end: `UseState` → `Button` click → reconciler diff → patched
`TextBlock` on Skia desktop, and the same app rendering **and responding to
clicks** in the browser via WASM and on Android (API 36 emulator). On the iOS
Simulator (iOS 26.3) the counter and gallery samples launch and render their
first page, and the counter responds to taps.

## Quick start — a new app from the template

Uno's `unoapp-reactor` template creates a single Uno project for desktop,
WebAssembly, Android and iOS that takes Reactor from the `Reactor` UnoFeature.
Create it from the command line: Visual Studio's New Project dialog doesn't list
this template.

```bash
dotnet new install Uno.Templates@7.0.0-dev.119
dotnet new unoapp-reactor -n MyApp
cd MyApp
dotnet run -f net10.0-desktop
```

`7.0.0-dev.119` is the first `Uno.Templates` whose app starts on Android from
`Program.cs`, like every other target; later ones work too. The generated
`.csproj` lists `Reactor` in `UnoFeatures` and has no `PackageReference`: the
Uno.Sdk pinned in its `global.json` picks the
[`Uno.Microsoft.UI.Reactor`](https://www.nuget.org/packages/Uno.Microsoft.UI.Reactor)
version. `-platforms desktop wasm` (any of `android`, `ios`, `wasm`, `desktop`)
cuts the target list.

To add Reactor to an existing Uno app instead, put `Reactor` in its `UnoFeatures`
and pin Uno.Sdk `7.0.0-dev.119` or later in its `global.json` (or in the project:
`<Project Sdk="Uno.Sdk/x.y.z">`). The feature first shipped in Uno.Sdk
`7.0.0-dev.97`; an older Uno.Sdk ignores it silently, and the build fails with
`CS0234` on `Microsoft.UI.Reactor`. An app started on an earlier preview is
covered under [Known limitations](#known-limitations--notes).

## Quick start — a file-based, single-file Reactor app

This mirrors the [xaml.dev "file-based WinUI apps with Microsoft.UI.Reactor"](https://xaml.dev/post/file-based-winui-apps-with-microsoft-ui-reactor)
post, but cross-platform. The whole app is one `.cs` file
([`samples/Uno/file-based/Counter.cs`](../../samples/Uno/file-based/Counter.cs)):

```csharp
#:sdk Uno.Sdk
#:project ../../../src/Reactor.Uno/Reactor.Uno.csproj
#:property TargetFramework=net10.0-desktop
#:property OutputType=Exe
#:property UnoSingleProject=true
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

The `#:project` line builds the port from this repo, and the bare `#:sdk Uno.Sdk`
takes its version from the repo-root [`global.json`](../../global.json),
the one nearest the file. Outside the repo, delete the `#:project` line,
put the file next to a `global.json` that pins Uno.Sdk `7.0.0-dev.119` or later
(or name the version on the line: `#:sdk Uno.Sdk@x.y.z`), and add a
`#:property UnoFeatures=Reactor` line, as the [package readme](PackageReadme.md#a-single-file-app) shows.

> **Versus the WinUI version.** The WinUI file-based app uses
> `#:property TargetFramework=net10.0-windows10.0.22621.0` +
> `#:property WindowsAppSDKSelfContained=true` and `dotnet run App.cs -a x64`.
> Here we swap to `#:sdk Uno.Sdk`, `TargetFramework=net10.0-desktop`, and add the
> Reactor.Uno reference. The Reactor code itself is **identical**.

### Run on WebAssembly instead

A single file-based app targets one TFM at a time. For WASM, change the
`TargetFramework` line and keep the same `ReactorApp.Run` call:

```csharp
#:property TargetFramework=net10.0-browserwasm
```

`dotnet run Counter.cs` then prints a localhost URL — open it.

This needs Uno.Sdk `7.0.0-dev.104` or later. An older Uno.Sdk picks the WebAssembly
SDK from the target framework before it reads the file's `#:property` lines, so
the build is an ordinary console app with no `wwwroot`, and it crashes at launch
with `Failed to load 'icuuc77'`.

### Run on Android instead

For Android, change the `TargetFramework` line, again with the same `ReactorApp.Run` call:

```csharp
#:property TargetFramework=net10.0-android
```

With a device or emulator connected (`adb devices`), `dotnet run Counter.cs` then
installs the app, starts it and streams its logcat until Ctrl+C. `dotnet run`
itself only installs apps that have a project file. For a single file, Reactor's
build targets install the app before `dotnet run` starts it.

Here the `#:project` line builds those targets from source. Outside the repo, they
come with the package, which needs Uno.Sdk `7.0.0-dev.119` or later: an older
Uno.Sdk brings a version of the package without them, so the app is never installed.

### Every target from one project

A single file targets one TFM at a time; a project can target them all. A single
file can't run on iOS yet, so for iOS use a project.
[`samples/Uno/ReactorUnoCounter`](../../samples/Uno/ReactorUnoCounter) is a normal
multi-targeted project that runs on each from one source, with the same
`ReactorApp.Run` call on each (iOS, which needs a Mac to run, is in
[samples/Uno](../../samples/Uno/README.md#android--ios)):

```bash
cd samples/Uno/ReactorUnoCounter
dotnet run -f net10.0-desktop          # desktop window
dotnet run -f net10.0-browserwasm      # prints a localhost URL — open it
dotnet run -f net10.0-android          # installs on the connected device, streams logcat
```

## The entry point

The samples are **single Uno projects targeting all four heads** — the component
source is identical everywhere, and so is the entry point: every target calls
`ReactorApp.Run<T>()` from one `Program.cs` (Uno gives the Apple heads the same
host-builder shape as desktop). Android never calls `Main` on its own, so the
package's build targets add the Android Application and launcher Activity
(`ReactorAndroidEntry`, on by default): the Application runs `Main`, then starts the
app. They also compile the app with an entry point, which the Android SDK otherwise
leaves out, and take the launcher icon and splash screen from the app's `UnoIcon` and
`UnoSplashScreen`, so `Platforms/Android` needs no C# and no `Styles.xml`. An app that
keeps its own `NativeApplication` sets `<ReactorAndroidEntry>false</ReactorAndroidEntry>`
and returns `ReactorApp.CreateHost<TRoot>()` from its `CreateHost`; with the entry
left on, such an app fails the build with `REACTORUNO0001`, which names the file.

On desktop and iOS, `Run` blocks until the window closes. The browser thread can't
block, so on wasm `Run` starts the app and returns, and any code after it runs
right away. On Android it only records the root and returns. `RunAsync` is there
for callers that want the host's `Task`; on wasm it never completes.

## Architecture

The port lives under the repo's conventional `src/` and `samples/` folders, but
**each Uno location carries its own build-isolation files** so it never inherits
the Windows / Windows App SDK build infrastructure — the repo-root
`Directory.Build.props` imports `Reactor.targets` and pins WinAppSDK, and the
root `Directory.Packages.props` turns Central Package Management on. MSBuild
stops walking up at the nearest match, so these shadow files stop that
inheritance and turn CPM off for the Uno projects only; the `Uno.Sdk` itself is
pinned only in the repo-root `global.json`. The Windows framework build is
untouched, and the Uno projects stay out of Microsoft's `Reactor.slnx`, so
Microsoft's CI never restores them. Which files are ours and which are
Microsoft's, and which workflows run on this fork, is in
[MAINTAINING.md](MAINTAINING.md).

```
src/Reactor.Uno/            # the port library (assembly `Reactor`, ns Microsoft.UI.Reactor)
  Reactor.Uno.csproj        # shares ../Reactor source, targets Uno TFMs
  GlobalUsings.cs           # disambiguation aliases (see below)
  ElementFactoryShim.cs     # lets Uno's ItemsRepeater take Reactor's ElementFactory<T>
  WinRTShims.cs             # CsWinRT types the shared source names but Uno lacks
  GlobalizationShims.cs     # Language.IsWellFormed, which Uno only stubs
  Hosting/
    ReactorApp.cs           # ReactorApp.Run / RunAsync + the static surface the core needs
    UnoBootstrap.cs         # UnoPlatformHostBuilder wiring + the code-only Application
    ReactorHost.cs          # the render loop (reconcile → Window.Content → effects)
    Windowing.cs            # minimal ReactorWindow + windowing types for the shared hooks
    ReactorAndroidApplication.cs  # Android: runs the app's Main from the Application
    DevServerConnection.cs  # connects to the Uno Dev Server for Hot Reload
    SafeAreaEdges.cs        # which edges the mobile safe-area padding keeps clear
    ReactorHostControl.cs · WindowIcon.cs  # Uno stand-ins for excluded Windows types
  buildTransitive/          # package build targets: the Android entry (ReactorAndroidEntry),
                            #   single-file Android install, launcher icon / splash
  PackageReadme.md          # the nuget.org readme
  nuget.config              # nuget.org (self-contained restore)
  Directory.Build.props     # stops inheritance of the repo-root Windows build infra
  Directory.Packages.props  # CPM off for this project

samples/Uno/                # Uno samples — one shared isolation set for all of them
  nuget.config · Directory.Build.props · Directory.Packages.props
  ReactorUnoCounter/        # minimal Uno single-project app (desktop, wasm, android, ios)
  ReactorUnoShowcase/       # larger showcase app
  ReactorGalleryUno/        # the Reactor gallery, sharing samples/ReactorGallery's source
  file-based/Counter.cs     # the single-file app

Reactor.Uno.slnx            # dedicated solution (kept out of Microsoft's Reactor.slnx)
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
> any shared root stops existing, and `ci-uno.yml` has no path filter, so a
> change under `src/Reactor/` or `src/Reactor.Advanced/` builds the port too.

The assembly is named **`Reactor`** with root namespace
**`Microsoft.UI.Reactor`** — identical to the Windows package — so consumer
`using Microsoft.UI.Reactor;` and `ReactorApp.Run<T>` work unchanged.

### What's replaced or excluded

The Windows `Hosting/` stack is Win32/DWM/AppWindow/shell P/Invoke-heavy, so it
is **not** shared; this project provides a slim Uno hosting layer instead:

- **`ReactorApp`** — `Run` / `RunAsync` build an `UnoPlatformHostBuilder` and a
  code-only `Application`. On WASM, where Uno's blocking `host.Run()` throws, `Run`
  starts the host without waiting for it. Platform providers are
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

When the shared source calls something Uno doesn't have, or only stubs, this project
declares a stand-in under the same full name, which the compiler picks over the imported
one: `WinRTShims.cs` for CsWinRT's attributes, `GlobalizationShims.cs` for
`Windows.Globalization.Language.IsWellFormed` (Uno's throws `NotImplementedException`).
Delete a stand-in once Uno implements the real member.

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
| Implicit animations & transitions (`.Animate()`, `.LayoutAnimation()`, `.Stagger()`, Scale/Rotation/Opacity/Translation transitions, spring / natural-motion) | 🟡 | Uno doesn't implement `ImplicitAnimationCollection` or spring animations. Reactor checks once whether they exist: without them `.Animate()`, `.LayoutAnimation()` and `.Stagger()` don't animate (the change snaps), and a `Curve.Spring()` plays as a 300 ms decelerating ease. Enter/exit `.Transition()`s and `.Keyframes()` use explicit keyframe animations, which Uno implements. |
| RichTextBlock / rich inline text / Markdown rendering | 🟡 | Most `RichTextBlock` members are not implemented in Uno, so rich text renders incompletely. Markdown is source-shared from `Reactor.Advanced` and compiles, but renders through `RichTextBlock`, so its output is incomplete too. |
| RichEditBox | ✅ | Plain-text editing through `Document.GetText`/`SetText` and `TextChanged`. Verified on desktop, WebAssembly and Android. |
| Custom TitleBar (drag regions, back button, panes) | ❌ | Not implemented in Uno. |
| Virtualized lists: `LazyVStack` / `LazyHStack`, `ItemsRepeater`, `ItemsView` (incl. `LinedFlowLayout`) | ✅ | Uno's `ItemsRepeater` only accepts its own `IElementFactoryShim`, so on Uno Reactor's `ElementFactory<T>` implements it too (`ElementFactoryShim.cs`). |
| ListBox | ❌ | Uno's `ListBox` shows no items: its default template realizes nothing and it creates no `ListBoxItem` containers, so items can't be clicked or selected. `SelectedItems` isn't implemented either, so `OnSelectionChanged` can't work until Uno's `ListBox` does. Use `ListView` instead. |
| Specialty controls: ParallaxView, MapControl, SemanticZoom, AnnotatedScrollBar | ❌ | Not implemented in Uno. |
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
| Hot Reload (edit `Render()` while running) | ✅ desktop · wasm · Android · iOS | Reactor registers `[assembly: MetadataUpdateHandler]`, and Uno's own `HotReloadAgent` discovers and invokes **every** registered handler — so Reactor's re-render is driven by Uno's hot-reload pipeline on the targets `dotnet watch` alone can't reach. Needs no opt-in: Reactor's host connects the app to the Uno Dev Server itself. `UseState` survives; hook add/remove/reorder recovers by remounting. iOS verified on the Simulator only. See [Hot Reload](#hot-reload) below. |
| Declarative caption height (`TitleBar(...).Tall()`, `WindowSpec.TitleBarHeight`) | ✅ desktop | Backed by Uno's `AppWindowTitleBar.PreferredHeightOption` (Standard 32 / Tall 48 / Collapsed 0), which Uno implements for real. The WinUI `TitleBar` *control* is still an Uno stub, so only the caption half is visible today. |
| Window drag-move, aspect-ratio lock | ❌ | Still no-op stubs — **not yet audited** against Uno's API surface (multi-window, DPI, pickers and closing guards all turned out to be implementable, so these may be too). |
| Docking (dock manager, tab tear-off, floating windows, splitters) | ❌ | Excluded from the port entirely. |
| In-app devtools | ❌ | `DevtoolsEnabled` is `false`. |
| Charting / DataGrid / PropertyGrid | 🟡 | Source-shared from `Reactor.Advanced` and compiling on all four TFMs; render path shared with WinUI, but not yet runtime-verified on Skia. |

> The ❌ / 🟡 runtime rows line up with the `Uno0001` *not-implemented* build warnings: the code compiles, but those specific APIs no-op (or throw) on Uno. They don't affect the ✅ rows.

## Hot Reload

**Editing a component updates the running app, and its state survives.**
Reactor's [`HotReloadService`](../Reactor/Hosting/HotReloadService.cs), source-shared
from the Windows framework, is a standard `[assembly: MetadataUpdateHandler]`. On
each update it re-renders the whole tree with `force: true` (bypassing memo),
migrates hook cells whose types were edited, and treats a `HookOrderException` as
"the edit changed the hook shape": it drops that component's hook state and
remounts instead of showing the error overlay.

It works with whatever delivers the change to the running app:

- **`dotnet watch`**, on desktop. Verified; see [below](#from-the-command-line).
- **The Uno Dev Server**, started by the IDE or with `uno-devserver start`, on every
  target. Uno's `HotReloadAgent` calls every `MetadataUpdateHandler` in the app, so
  Reactor's re-render needs no Uno-specific handler. Verified on WebAssembly, an
  Android phone, the iOS Simulator and desktop.
- **Visual Studio**: expected, not tried.

Nothing has to be opted into. There is **no `HotReload` UnoFeature** (passing one
just warns `Unable to parse 'hotreload' to a known Uno Feature` and is ignored), and
Uno.Sdk references the Dev Server client in Debug builds only. A XAML app connects
to the Dev Server from its `App.xaml`'s generated code; a Reactor app has no
`App.xaml`, so Reactor's host makes the same `RemoteControlClient.Initialize` call
when it creates its `Application`, passing the root component type, whose assembly
carries the Dev Server's address. In Release there is nothing to call and nothing
happens.

Some of Uno's tooling doesn't understand a Reactor app yet. The ones you're most
likely to hit:

- **Uno reports each edit as failed**, although Reactor has re-rendered, because no
  window is registered with Uno's Hot Reload client. Register it in Debug builds.
- **The Uno App MCP doesn't see the app**, so an AI agent can edit the code but not
  drive the app or check an edit.
- **A desktop app started with `dotnet run` refuses every edit** without
  `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in its environment. IDEs and `dotnet watch`
  set it.

[HOT-RELOAD.md](HOT-RELOAD.md) has the full picture: support by launch method, how
Reactor and Uno split the work, and every [known issue](HOT-RELOAD.md#known-issues)
with its workaround. `.UseStudio()` isn't needed: it turns on Hot Design, Uno's
runtime *XAML* designer, which doesn't support Reactor apps yet.

### From the command line

The samples target four frameworks, so `dotnet watch` has to be told which one to
run. Override the target list from the command line:

```bash
cd samples/Uno/ReactorUnoCounter
dotnet watch --property:TargetFrameworks=net10.0-desktop
```

An edit to `Program.cs` then reports `🔥 C# and Razor changes applied`. The
override restores the head and `Reactor.Uno` for desktop only, so run a plain
`dotnet restore` before building another head with `--no-restore`. The
`Found project reference without a matching metadata reference` warning is benign.

`dotnet watch -f net10.0-desktop` also works on the samples as they ship (SDK
10.0.401), but it loads the head once for every target it lists (11 projects,
against 5 with the override), and that has failed every edit with
`CS0518: Predefined type '…' is not defined or imported`: on a head listing two or
three of the four targets, with the samples' earlier `Program.cs`. The override
has worked in every case tried. Without either, `dotnet watch` asks which target
to run (`--non-interactive` stops instead).

**With the Uno Dev Server instead**, which every target can use: start it with
`uno-devserver start` from a folder holding a solution, *before* building, since it
writes its port into the project's `.csproj.user` and the build stamps that port on
the app. On desktop, start the app with `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in its
environment. The samples README has the
[commands](../../samples/Uno/README.md#ios-simulator), and
[HOT-RELOAD.md](HOT-RELOAD.md#new-unoapp-reactor-apps-have-no-solution-file) covers
apps made from the template, which come without a solution.

## Known limitations / notes

- **Uno version.** Apps need Uno.Sdk **`7.0.0-dev.119`** or later, the first
  whose package starts Android apps from `Program.cs` (see
  [Quick start](#quick-start--a-new-app-from-the-template)); there is no Uno 6.x
  support. The port itself builds against the Uno **7.0** pre-release
  pinned in the repo-root `global.json`, which the projects and the file-based
  `Counter.cs` all read. Uno 7.0 renamed the runtime
  packages (`Uno.WinUI.Runtime.Skia.X11` → `Uno.WinUI.Runtime.X11`, …) and moved the
  Android entry types to `Uno.UI.Runtime.Android`. The runtime packages take their
  version from `$(UnoVersion)`, which **Uno.Sdk supplies**, so they track the SDK
  automatically — don't hardcode a version for them (it drifts and trips `NU1605`).
  They are `PrivateAssets="all"`: Reactor.Uno needs them only to compile its host
  bootstrap, and every Uno.Sdk app head references them itself, so the package
  doesn't list them as dependencies.
- **Apps made on an earlier preview.** Before `7.0.0-dev.119` the package had no
  Android entry, so `ReactorApp.Run` in `Program.cs` never ran on Android:
  - The `unoapp-reactor` template first shipped in `Uno.Templates`
    `7.0.0-dev.104`. Up to `7.0.0-dev.115` it gives the app its own Android
    `Application` in `Platforms/Android`.
  - An app that took the `Reactor` UnoFeature on Uno.Sdk `7.0.0-dev.97` to
    `7.0.0-dev.115` needs its own `NativeApplication` that returns
    `ReactorApp.CreateHost<TRoot>(...)` from `CreateHost`.

  Moving such an app to Uno.Sdk `7.0.0-dev.119` or later stops the build with
  `REACTORUNO0001`, whose message says what to delete or which property to set
  (see [The entry point](#the-entry-point)).
- **Don't name a file-based app's root component `App`** — Uno.Sdk generates an
  `App` type for single-project EXEs, which would clash. Use any other name
  (`CounterApp`, `MyApp`, …); `ReactorApp.Run<T>` doesn't care.
- **Hot Reload.** Edits apply, but some of Uno's tooling doesn't understand a
  Reactor app yet: Uno reports each edit as failed, and the Uno App MCP doesn't see
  the app. [HOT-RELOAD.md](HOT-RELOAD.md#known-issues) lists the known issues and
  their workarounds.
- See the **Feature support on Uno Skia** matrix above for what works, what's
  partial, and what's unsupported. The unsupported/partial rows surface as
  `Uno0001` *not-implemented* build warnings — runtime no-ops in Uno, not build
  breaks — and only affect those specific features.

## Mobile

The samples are single Uno projects whose `net10.0-android` and `net10.0-ios`
heads run as-is; no separate mobile head project is needed. Only the entry point
is platform-specific (see [The entry point](#the-entry-point)), and the framework code itself is
platform-agnostic `Microsoft.UI.Xaml`. Deploy commands are in
[samples/Uno](../../samples/Uno/README.md#android--ios).

### Safe area

On Android and iOS the app is kept clear of the status bar, navigation bar,
notch and display cutouts: Uno draws mobile apps edge-to-edge, so the host pads
its window root to the window's visible bounds. Component code needs nothing for
this. An app that draws under the bars itself picks the edges to keep padded when
it starts, e.g. `SafeAreaEdges.None` for fully edge-to-edge, or
`SafeAreaEdges.Bottom` to let a header run under the status bar:

```csharp
using Microsoft.UI.Reactor.Hosting;

ReactorApp.Run<CounterApp>("My App", configure: host => host.SafeAreaEdges = SafeAreaEdges.None);
```

On a padded edge, the bar shows the page background, not the app root's own
background. It doesn't move content out from under the soft keyboard.

Known mobile layout gap: on iOS the window doesn't fill the screen (black bars
above and below), so the padding hasn't been seen working there yet.
