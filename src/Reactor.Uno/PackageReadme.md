# Uno.Microsoft.UI.Reactor

[Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor), the functional, declarative UI framework for WinUI 3, on Uno Platform. You write components and hooks in C#, with no XAML, and the same code runs on desktop (Windows, macOS, Linux), WebAssembly, Android and iOS.

For a Windows App SDK app, use Microsoft's `Microsoft.UI.Reactor` package instead. This package doesn't target the Windows App SDK.

## Requirements

- The .NET 10 SDK.
- Uno.Sdk with the Skia renderer (`SkiaRenderer` in `UnoFeatures`). This version of the package is built against Uno.Sdk `7.0.0-dev.81`; use the same Uno.Sdk version in your app.

## A single-file app

Save this as `Counter.cs` and run `dotnet run Counter.cs`:

```csharp
#:sdk Uno.Sdk@7.0.0-dev.81
#:package Uno.Microsoft.UI.Reactor@[x.y.z]
#:property TargetFramework=net10.0-desktop
#:property OutputType=Exe
#:property UnoSingleProject=true
#:property UnoFeatures=SkiaRenderer
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

Replace `x.y.z` with this package's version. Keep the square brackets: they pin that exact version, while `@x.y.z` without them is only a minimum and can restore a different build.

Single-file apps run on desktop only for now.

## In an Uno project

Reference the package from a single Uno project that uses the Skia renderer:

```xml
<Project Sdk="Uno.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0-desktop;net10.0-browserwasm;net10.0-android;net10.0-ios</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <UnoSingleProject>true</UnoSingleProject>
    <UnoFeatures>SkiaRenderer</UnoFeatures>
    <!-- A Reactor app has no App.xaml, which Uno's iOS Hot Restart helper
         generator needs; without this, the iOS build fails with Uno0005. -->
    <UnoDisableHotRestartHelperGeneration>true</UnoDisableHotRestartHelperGeneration>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Uno.Microsoft.UI.Reactor" Version="x.y.z" />
  </ItemGroup>
</Project>
```

Pin Uno.Sdk to the same version in `global.json`, under `msbuild-sdks`.

Desktop and iOS start the app with `ReactorApp.Run<TRoot>(...)`, as above. WebAssembly and Android need a different entry point: the [counter sample](https://github.com/unoplatform/uno.microsoft-ui-reactor/tree/uno/samples/Uno/ReactorUnoCounter) shows all four, and it is the simplest project to start from.

## Learn more

- [Reactor on Uno Platform](https://github.com/unoplatform/uno.microsoft-ui-reactor/blob/uno/src/Reactor.Uno/README.md): what works on each target, and what Uno doesn't implement yet.
- [Reactor guide](https://github.com/microsoft/microsoft-ui-reactor/tree/main/docs/guide): components, hooks, layout and controls. It is written for Windows, but the API is the same.
