# Hot Reload in Reactor on Uno Platform

Editing a component updates the running app, and the state held in its hooks survives.
This page lists where that works, what doesn't, and the known issues with their
workarounds. For the commands, see the port README's [Hot Reload](README.md#hot-reload)
section.

## Support

Hot Reload needs a Debug build. A Release build applies no edit.

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

- **`dotnet watch` updates Reactor apps on desktop**, unlike XAML Uno apps: Reactor
  re-renders the component tree when .NET applies a change.
- **With a debugger attached**, .NET accepts a change only through that debugger.
  Rider's debugger never calls Reactor's update
  ([RIDER-124189](https://youtrack.jetbrains.com/issue/RIDER-124189)). For Visual
  Studio and the Uno Platform debugger in VS Code, see Uno's
  [Hot Reload documentation](https://platform.uno/docs/articles/features/working-with-xaml-hot-reload.html).
- **From the command line on desktop**, the app needs `DOTNET_MODIFIABLE_ASSEMBLIES=debug`
  ([known issue](#desktop-apps-started-from-the-command-line-refuse-every-edit)).
- **The Uno Dev Server needs a signed-in Uno Platform account** for Hot Reload and the
  App MCP. Outside an IDE, nothing asks you to sign in: run `uno-devserver login` once.
- **Through the App MCP**, `app tree` shows only the root by default, because Reactor's
  controls count as library elements: pass `--detail full`.
- **To check an edit** from the command line, run
  `uno-devserver app hot-reload-status --file <path>`, then look at the app itself
  ([Success doesn't mean Reactor rendered](#success-doesnt-mean-reactor-rendered)).

What an edit keeps:

- `UseState`, `UseReducer`, `UseRef`, `UseMemo` and `UsePersisted` keep their values.
- A record or class held directly in `UseState`, `UseReducer` or `UseMemo` that gains
  or loses a field keeps the values of the fields it still has.
- A hook added after the existing ones starts with its initial value; the others keep
  theirs.
- An edit that changes the kind or type of hook at a position resets that component's
  hooks: its effects clean up and run again, and it re-renders, instead of showing an
  error.
- An edit inside a `UseMemo` factory shows once the memo's dependencies change.

## Doesn't work

- **Rider with the debugger**, on every target. Use Rider without the debugger.
- **VS Code with the debugger**, on desktop and WebAssembly. Use VS Code without the
  debugger.
- **Visual Studio without the debugger**, on Android and iOS. Start the app with the
  debugger.
- **`dotnet watch` on WebAssembly, Android and iOS.** Use an IDE or the Uno Dev Server.
- **Hot Design.** Hot Design, Uno's runtime XAML designer, doesn't support Reactor apps,
  and Hot Reload doesn't need it: don't call `UseStudio()`. Calling it can log
  *"Unable to find ServerProcessor via attribute or reflection"*. Apps from
  `unoapp-reactor` include the Hot Design package in Debug builds, which has no effect
  on Hot Reload. To leave it out, set `<UnoDisableHotDesign>true</UnoDisableHotDesign>`.

## Known issues

### Success doesn't mean Reactor rendered

Uno's Hot Reload status, as `uno-devserver app hot-reload-status` reports it, says
**Success** when Uno's own pass found nothing to replace.
An edit whose `Render()` throws still reports Success, while the app shows Reactor's
error screen with the exception.

**Workaround:** after an edit, look at the app, or check it with the App MCP's `find`,
`assert` or `screenshot`.

### Desktop apps started from the command line refuse every edit

`dotnet run`, or starting the built executable, doesn't set
`DOTNET_MODIFIABLE_ASSEMBLIES=debug`, unless a launch profile sets it. The app receives
each edit from the Dev Server and refuses it, and its log shows *"The assembly can not
be edited or changed"*. This affects every Uno desktop app.

**Workaround:** set the variable for the command that starts the app, or start it from
an IDE or with `dotnet watch`.

```bash
DOTNET_MODIFIABLE_ASSEMBLIES=debug dotnet run -f net10.0-desktop
```

```powershell
$env:DOTNET_MODIFIABLE_ASSEMBLIES = "debug"; dotnet run -f net10.0-desktop
```

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
Dev Server's. Edits to an app still apply, so no workaround is needed.
