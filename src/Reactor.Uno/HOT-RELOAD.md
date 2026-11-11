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
| An AI agent, through the Uno App MCP | ✅ | ☑️ | ☑️ |

✅ works · ☑️ expected, not tested · ❌ doesn't work

- **With a debugger attached**, .NET accepts a change only through that debugger:
  - Rider never calls Reactor's update
    ([RIDER-124189](https://youtrack.jetbrains.com/issue/RIDER-124189)).
  - VS Code's debugger applies changes on Android and iOS only.
  - Visual Studio applies changes to Android and iOS only with its debugger attached.

  See Uno's
  [Hot Reload documentation](https://platform.uno/docs/articles/features/working-with-xaml-hot-reload.html).
- **`dotnet watch` updates Reactor apps on desktop**, unlike XAML Uno apps: Reactor
  re-renders the component itself when .NET applies a change.
- **From the command line on desktop**, the app needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug`
  ([known issue](#desktop-apps-started-from-the-command-line-refuse-every-edit)).
- **Through the App MCP**, `app tree` shows only the root by default, because Reactor's
  controls count as library elements: pass `--detail full`.
- **The App MCP's app tools need a signed-in Uno Platform account.** Outside an IDE,
  nothing asks you to sign in: run `uno-devserver login` once.

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

### Success doesn't mean Reactor rendered

Uno's **Success** only means Uno's own pass found nothing to replace. An edit whose
`Render()` throws still reports Success, while the app shows Reactor's error screen.

**Workaround:** after an edit, check the UI itself, for example with the App MCP's
`find`, `assert` or `screenshot`.

### Desktop apps started from the command line refuse every edit

`dotnet run`, or starting the built executable, doesn't set
`DOTNET_MODIFIABLE_ASSEMBLIES=debug`. The app receives each edit from the Dev Server and
refuses it: *"The assembly can not be edited or changed"*. This affects every Uno
desktop app.

**Workaround:** set `DOTNET_MODIFIABLE_ASSEMBLIES=debug` in the environment before
starting the app, or start it from an IDE or with `dotnet watch`.

### `dotnet watch` and the Dev Server both deliver each edit

An app built while a Dev Server runs has the Dev Server's port, and connects to it even
when `dotnet watch` starts it. Both then send every edit to the app.

**Workaround:** close the IDE, or run `uno-devserver stop`, before using `dotnet watch`.

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
Dev Server's. Edits to an app still apply.
