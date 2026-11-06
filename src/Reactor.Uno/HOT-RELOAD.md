# Hot Reload in Reactor on Uno Platform

Editing a component updates the running app, and the state held in its hooks survives.
This page lists where that works, what doesn't, and the known issues with their
workarounds. For the commands, see the port README's [Hot Reload](README.md#hot-reload)
section and the samples' [Hot Reload](../../samples/Uno/README.md#hot-reload) commands.

## Support

Hot Reload needs a Debug build. Release builds don't have it.

| How the app is started | Desktop | WebAssembly | Android, iOS |
|---|:---:|:---:|:---:|
| `dotnet watch` | ✅ | ❌ | ❌ |
| Visual Studio, with the debugger | ☑️ | ☑️ | ☑️ |
| Visual Studio, without the debugger | ☑️ | ☑️ | ❌ |
| VS Code or Rider, without the debugger | ☑️ | ✅ | ✅ |
| VS Code, with the debugger | ❌ | ❌ | ☑️ |
| Rider, with the debugger | ❌ | ❌ | ❌ |
| Command line: `uno-devserver start`, then start the app | ✅ | ☑️ | ✅ iOS Simulator · ☑️ Android |
| An AI agent, through the Uno App MCP | ✅ [with workarounds](#the-app-mcp-doesnt-see-a-reactor-app) | ☑️ | ☑️ |

✅ verified · ☑️ expected from the code, not tried · ❌ doesn't work

- **VS Code and Rider without the debugger** were verified on WebAssembly, an Android
  phone and the iOS Simulator.
- **With a debugger attached**, .NET accepts a change only through that debugger:
  - Rider never calls Reactor's update
    ([RIDER-124189](https://youtrack.jetbrains.com/issue/RIDER-124189)).
  - VS Code's debugger applies changes on Android and iOS only.
  - Visual Studio applies changes to Android and iOS only with its debugger attached.

  See Uno's
  [Hot Reload documentation](https://platform.uno/docs/articles/features/working-with-xaml-hot-reload.html).
- **From the command line on desktop**, the app needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug`
  ([known issue](#desktop-apps-started-from-the-command-line-refuse-every-edit)).

What an edit keeps:

- `UseState`, `UseReducer`, `UseRef` and `UseMemo` keep their values. A record or
  class stored in a hook that gains or loses a field keeps the values of the fields it
  still has.
- Adding, removing or reordering hook calls resets that component's hooks and remounts
  it, instead of showing an error.

## Doesn't work

- **Rider with the debugger**, on every target. Use Rider without the debugger.
- **`dotnet watch` on WebAssembly, Android and iOS.** Use an IDE or the Uno Dev Server.
- **Hot Design.** Hot Design, Uno's runtime XAML designer, doesn't support Reactor apps.
  `UseStudio()`, which turns it on, logs *"Unable to find ServerProcessor via attribute
  or reflection"*, and its Themes panel stays unavailable. Hot Reload doesn't need it:
  don't call `UseStudio()`. Apps from `unoapp-reactor` keep the Hot Design package in
  Debug builds, which does no harm while nothing calls `UseStudio()`. To leave it out,
  set `<UnoDisableHotDesign>true</UnoDisableHotDesign>`.

## Known issues

Remove an entry when its fix ships.

### Uno reports every Reactor edit as failed

The edit applies and Reactor re-renders, but Uno marks the operation **Failed**,
because no window is registered with Uno's Hot Reload. Uno's tooling reads that
result: the IDE's Hot Reload status, and the App MCP's `hot-reload-status`. The app
logs:

```text
Unable to access Dispatcher/DispatcherQueue in order to invoke UpdateVisualTree. Make sure you have enabled hot-reload (Window.UseStudio()) in app startup.
```

**Fix:** [#35](https://github.com/unoplatform/Uno.microsoft-ui-reactor/pull/35) makes
Reactor register its window. Until a Reactor package with it ships, register the window
in Debug builds from `ReactorApp.Run`'s `configure` callback:

```csharp
#if DEBUG
using Uno.UI;
#endif

ReactorApp.Run<MainComponent>("MyApp", configure: host =>
{
#if DEBUG
#pragma warning disable UNO0008 // Obsolete in favor of UseStudio(), which also turns on Hot Design
    host.Window.EnableHotReload();
#pragma warning restore UNO0008
#endif
});
```

Call it as an extension method, as above. When Hot Design is in the build, writing
`Uno.UI.WindowExtensions.EnableHotReload(…)` fails with `CS0433`.

### Success doesn't mean Reactor rendered

Uno's **Success** only means Uno's own pass found nothing to replace. An edit whose
`Render()` throws still reports Success, while the app shows Reactor's error screen.

**Workaround:** after an edit, check the UI itself, for example with the App MCP's
`find`, `assert` or `screenshot`.

### The App MCP doesn't see a Reactor app

Every App MCP command (`uno-devserver app info`, `screenshot`, `find`, …) answers
*"No connected app instance"*. An AI agent can edit the code but can't see or drive the
app.

**Fix:** two parts.
- **App MCP:** Uno.UI.App.Mcp **2.0.0-dev.20** fixes the client. Uno.Sdk
  7.0.0-dev.177 and later bring it.
- **Reactor:** [#35](https://github.com/unoplatform/Uno.microsoft-ui-reactor/pull/35)
  loads the client and registers the window.

Until a Reactor package with #35 ships, make both calls in Debug builds, from
`ReactorApp.Run`'s `configure` callback:

```csharp
#if DEBUG
using Uno.UI;
#endif

ReactorApp.Run<MainComponent>("MyApp", configure: host =>
{
#if DEBUG
    MyApp.GlobalStaticResources.Initialize(); // MyApp: the app's root namespace
#pragma warning disable UNO0008
    host.Window.EnableHotReload();
#pragma warning restore UNO0008
#endif
});
```

On an Uno.Sdk older than 7.0.0-dev.177, also set the App MCP version in the app
project:

```xml
<UnoAppMcpVersion>2.0.0-dev.20</UnoAppMcpVersion>
```

With these, every App MCP app tool works on desktop. `app tree` shows only the root by
default, because Reactor's controls count as library elements: pass `--detail full`.

### Desktop apps started from the command line refuse every edit

`dotnet run`, or starting the built executable, doesn't set
`DOTNET_MODIFIABLE_ASSEMBLIES=debug`. The app receives each edit from the Dev Server and
refuses it: *"The assembly can not be edited or changed"*. This affects every Uno
desktop app.

**Workaround:** set `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in the environment before
starting the app, or start it from an IDE or with `dotnet watch`.

### New `unoapp-reactor` apps have no solution file

Without a solution, `uno-devserver start` doesn't write its port into the project's
`.csproj.user`, so the app has no Dev Server to connect to.

**Workaround:** add a solution before starting the Dev Server:

```bash
dotnet new sln --format slnx
dotnet sln add MyApp.csproj
```

### `uno-devserver app` starts a second Dev Server

If `uno-devserver start` ran in a folder with no solution file, a later
`uno-devserver app …` command starts a second Dev Server, then reports *"No connected
app instance"* although the app is connected to the first one. This affects any Uno app.

**Workaround:** run `uno-devserver start` in a folder that has a solution file.

### Every edit fails when a target's workload is missing

On a machine without the Android or iOS workload, the Dev Server keeps those targets in
its workspace. Every edit then fails with `ENC1001 … Image is too small`. This affects
any Uno app with several targets, including the samples
([unoplatform/uno#23716](https://github.com/unoplatform/uno/issues/23716)).

**Workaround:** install the workloads, or limit `TargetFrameworks` to the target you run,
both in the project and on the build command line
(`-p:TargetFrameworks=net10.0-desktop`).

### Reactor's source generator doesn't load in the Dev Server

The Dev Server logs *"Analyzer 'Reactor.Wrappers.Generator' failed to load in the
hot-reload workspace"*, because the generator is built against a newer Roslyn than the
Dev Server's. Edits to an app still apply. Edits to `Reactor.Uno`'s own source, when
it's a project reference as in the samples, are expected to miss the generated code.

## Not tried yet

- **`dotnet watch` while a Dev Server runs.** An app built with a Dev Server port
  connects to it too, so both would deliver every edit. Close the IDE or run
  `uno-devserver stop` before using `dotnet watch`.
- **File-based apps.** `dotnet run Counter.cs` has no `.csproj.user` for the Dev Server's
  port, so it's expected to get no Uno Hot Reload. `dotnet watch` should still work on
  desktop.
- **XAML hosted in a Reactor tree** (`XamlPageElement`, a XAML `UserControl`).
- **#35 on WebAssembly, Android and iOS**, and with `UseStudio()` called in `configure`.

Verified on Windows 11, between 2026-10-30 and 2026-11-06. Runs used the samples and new
`unoapp-reactor` apps (Uno.Templates 7.0.0-dev.161, and 7.0.0-dev.177 for the App MCP),
unless a row above says otherwise.
