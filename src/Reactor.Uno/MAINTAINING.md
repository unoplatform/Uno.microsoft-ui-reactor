# Maintaining the Uno port

How this fork stays mergeable with Microsoft's repository: which files are ours,
and which workflows run here. For what the port is and how to use it, see the
[port README](README.md).

## What's ours, what's Microsoft's

This repository is a fork of
[microsoft/microsoft-ui-reactor](https://github.com/microsoft/microsoft-ui-reactor).
`upstream-sync.yml` brings Microsoft's latest `main` into `uno` every night
through a pull request, auto-merged when the merge is clean, so every line of
theirs we change has to be merged again, and can conflict, on every sync.

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
.github/scripts/uno/                 # scripts the Uno CI runs
.github/workflows/ci-uno.yml
.github/workflows/ci-uno-consume.yml
.github/workflows/conventional-commits.yml
.github/workflows/label-upstream-files.yml
.github/workflows/upstream-sync.yml
```

Everything else is Microsoft's. That includes the shared source the port
compiles (`src/Reactor/`, `src/Reactor.Advanced/`), the root build files
(`Directory.Build.props`, `Directory.Packages.props`, `Reactor.slnx`,
`global.json`), `AGENTS.md`, `docs/`, `tests/`, and their workflows in
`.github/workflows/`. Those workflows stay in the tree so the sync merges cleanly,
but they are switched off on this fork (see [below](#our-workflows-on-this-fork)).

A pull request into `uno` that changes any of Microsoft's files, of any type, gets
the `warning/touches-upstream` label (`label-upstream-files.yml`), and loses it once
it no longer does. The workflow keeps its own copy of the list above in `OURS`: a path
added here goes there too, or the workflow counts it as Microsoft's.

These Microsoft files are modified today. Add one only when the change can't live
under `src/Reactor.Uno/`, and say when it goes away. Remove an entry as soon as
you can do without it:

| File | Change | Why it isn't in our paths | Remove when |
|---|---|---|---|
| `README.md` | An Uno Platform section at the top | GitHub shows the root README on the fork's front page. | Never: the fork's front page has to say what it is. |
| `global.json` | An `msbuild-sdks` pin for `Uno.Sdk` | It's the port's only Uno.Sdk pin: `Reactor.Uno.slnx` sits at the root, and every Uno project and `Counter.cs` below it reads this file too. A second `global.json` under the port would win for builds started inside it and could drift from this one. No Windows project uses `Uno.Sdk`, so the pin doesn't affect the Windows build. | Never, while the port builds from this repo. |
| `global.json` | The .NET SDK floor raised from `10.0.100` to `10.0.400` | Reactor's source generators build against Roslyn 5.9, which SDKs before the 4xx band can't load: the compiler drops them (`CS9057`) and the build fails with `CS0759`. This one does affect the Windows build, which has the same need, so it could go to Microsoft's repository. | Microsoft's `global.json` asks for `10.0.400` or later. |
| `src/Reactor.Wrappers.Generator/Reactor.Wrappers.Generator.csproj` | `TargetFramework` added to `TreatAsLocalProperty` | Keeps the generator on `netstandard2.0` when a target framework comes in as a global property: from a file-based app's restore, or from `dotnet watch --property:TargetFrameworks=…` ([Hot Reload](README.md#from-the-command-line)). Without it the generator is evaluated as the app's target and restore fails with `NETSDK1139`. Changes nothing for a build that passes no target framework globally. | Microsoft's repository takes the change. |
| `src/Reactor/Core/Reconciler.Mount.cs` | `return null` → `return default` in a generic helper | Satisfies Uno's nullable analysis. Behaves the same on Windows, since `T` is constrained to a reference type. | Microsoft's repository takes the change, or Uno's nullable analysis accepts `return null` there. |

The shared source has no `#if REACTOR_UNO` blocks. A guard around
`Hyperlink.UnderlineStyle` was removed once Uno made `UnderlineStyleProperty` public
([unoplatform/uno#23652](https://github.com/unoplatform/uno/issues/23652),
`Uno.Sdk 6.7.0-dev.117`).

## Our workflows on this fork

Microsoft's workflows can't be switched off once and for all: GitHub only lists a
workflow after its trigger first fires, and every sync can bring new ones. So a
separate job in `upstream-sync.yml`, "Keep Microsoft's workflows off", runs with
each nightly sync (04:00 UTC). It lists the repository's active workflows and
switches off every one whose file name isn't in its `KEEP` list; GitHub's
built-in workflows (`dynamic/…`, such as Code Quality) are left alone. The run's
summary says how many it switched off.

That makes `KEEP` the list of workflows this fork runs:

- **A new workflow file of ours goes in `KEEP`** in the pull request that adds it,
  and in the "Ours" list above (and `OURS` in `label-upstream-files.yml`).
  Otherwise the next nightly run switches it off, and a switched-off workflow no
  longer runs on its triggers. Renaming one is the
  same: rename it in `KEEP` too.
- **Called workflows too** (`on: workflow_call`, such as `ci-uno-consume.yml`).
  GitHub doesn't document whether it lists them, nor whether a switched-off one
  still runs when another workflow calls it (a
  [community report](https://github.com/orgs/community/discussions/60389) says it
  does), and the summary would count it as one of Microsoft's.
- **The job only switches workflows off.** Adding a file to `KEEP` after the
  fact doesn't turn it back on: do that from the Actions tab, or with
  `gh workflow enable <file name> -R unoplatform/Uno.microsoft-ui-reactor`.
- **Until that pull request merges**, the nightly run reads `KEEP` from `uno`,
  which doesn't have the new file yet. If GitHub lists the file after a run on
  the pull request's branch, the job can switch it off before the merge. After
  merging, check `gh workflow list --all` and turn it back on if it shows as
  disabled.
- **To see what it would switch off** without changing anything, run "Sync
  upstream into uno" by hand with `dry_run` (`gh workflow run upstream-sync.yml
  -f dry_run=true`): it logs `would switch off <path>` for each, and the sync job
  makes no changes either.
