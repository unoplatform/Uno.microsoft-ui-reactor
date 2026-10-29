# Uno.Microsoft.UI.Reactor

[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor), the functional, declarative UI framework for WinUI 3, on Uno Platform. You write components and hooks in C#, with no XAML, and the same code runs on desktop (Windows, macOS, Linux), WebAssembly, Android and iOS.

For a Windows App SDK app, use Microsoft's `Microsoft.UI.Reactor` package instead. This package doesn't target the Windows App SDK.

## Requirements

- The .NET SDK 10.0.400 or later.
- Uno.Sdk `7.0.0-dev.104` or later, with the Skia renderer. You don't reference this package yourself: add `Reactor` to `UnoFeatures` and the Uno.Sdk brings in the version it was released with.

## A new app

Install the Uno templates and create an app from `unoapp-reactor`:

```bash
dotnet new install Uno.Templates@7.0.0-dev.104
dotnet new unoapp-reactor -n MyApp
cd MyApp
dotnet run -f net10.0-desktop
```

Any later `Uno.Templates` works too; a plain `dotnet new install Uno.Templates` installs the latest stable templates, which don't have `unoapp-reactor` yet.

The app targets desktop, WebAssembly, Android and iOS. Pick fewer with `-platforms`, for example `-platforms desktop wasm`.

## A single-file app

Save this as `Counter.cs` and run `dotnet run Counter.cs`:

```csharp
#:sdk Uno.Sdk@7.0.0-dev.104
#:property TargetFramework=net10.0-desktop
#:property OutputType=Exe
#:property UnoSingleProject=true
#:property UnoFeatures=SkiaRenderer;Reactor
#:property PublishAot=false
#:property PackAsTool=false

using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run<CounterApp>("Counter", width: 480, height: 360);

class CounterApp : Component
{
    public override Element Render()
    {
        var (count, setCount) = UseState(0);

        return VStack(12,
            Heading($"Count: {count}"),
            HStack(8,
                Button("-", () => setCount(count - 1)),
                Button("+", () => setCount(count + 1))
            )
        ).Padding(24);
    }
}
```

The `#:sdk` line pins the Uno.Sdk, and with it the Reactor version: a lone file has no `global.json` to do it. Single-file apps run on desktop only for now.

## In an existing Uno project

Add `Reactor` to `UnoFeatures` in a single Uno project that uses the Skia renderer:

```xml
<UnoFeatures>
  SkiaRenderer;
  Reactor;
</UnoFeatures>
```

Pin Uno.Sdk `7.0.0-dev.104` or later in its `global.json`. The feature first shipped in `7.0.0-dev.97`; an Uno.Sdk older than that ignores it without a warning, and the build then fails with `CS0234` on `Microsoft.UI.Reactor`.

Remove the Windows target framework (`net10.0-windows10.0.x`) from the project: with `Reactor` in `UnoFeatures`, that target fails with `UNOB0031`, since this package doesn't target the Windows App SDK.

Desktop, WebAssembly and iOS start the app with `ReactorApp.Run<TRoot>(...)`, as above. Android has no console entry point, so it starts from an `Application` in `Platforms/Android` whose `CreateHost` returns `ReactorApp.CreateHost<TRoot>()`. The `unoapp-reactor` template generates both.

## Another Reactor version

To use a different version of this package than the one your Uno.Sdk brings in, set `UnoReactorVersion`: `<UnoReactorVersion>x.y.z</UnoReactorVersion>` in the project, or `#:property UnoReactorVersion=x.y.z` in a single file.

## Learn more

- [Reactor on Uno Platform](https://github.com/unoplatform/uno.microsoft-ui-reactor/blob/uno/src/Reactor.Uno/README.md): what works on each target, and what Uno doesn't implement yet.
- [Reactor guide](https://github.com/microsoft/microsoft-ui-reactor/tree/main/docs/guide): components, hooks, layout and controls. It is written for Windows, but the API is the same.
