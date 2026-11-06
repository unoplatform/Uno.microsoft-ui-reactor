// Connects the app to the Uno Dev Server, which is what delivers Hot Reload
// deltas on WebAssembly, Android, iOS, and desktop under VS Code / Rider.
//
// A XAML Uno app never does this by hand: Uno's XAML generator emits
// `RemoteControlClient.Initialize(GetType())` into its App.xaml's generated code.
// Reactor's Application is code-only (ReactorApplication) and lives in this
// library, so nothing emits that call and the app never connects. This makes it.
//
// RemoteControlClient ships in Uno.WinUI.DevServer, which Uno.Sdk references only
// in non-optimized builds, so this library can't reference it: it is looked up by
// name. A Release app doesn't carry the assembly, the lookup finds nothing, and
// nothing happens; that is the same gate as Uno's generator. The App MCP client
// and Uno's window registration are looked up the same way, for the same reason.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

namespace Microsoft.UI.Reactor.Hosting;

internal static class DevServerConnection
{
    private const string ClientTypeName = "Uno.UI.RemoteControl.RemoteControlClient, Uno.UI.RemoteControl";
    private const string WindowExtensionsTypeName = "Uno.UI.WindowExtensions, Uno.UI.RemoteControl";
    private const string AppMcpClientAssemblyName = "Uno.UI.App.Mcp.Client";

    /// <summary>
    /// Starts the Dev Server client for the app whose assembly holds
    /// <paramref name="appType"/>. The build stamps the Dev Server's address on the
    /// app assembly (<c>ServerEndpointAttribute</c>), so the type must come from the
    /// app, not from Reactor. No-op when the client isn't in the app, when no app
    /// type is known, or when the app already started it.
    /// </summary>
    public static void TryConnect(Type? appType)
    {
        if (appType is null) return;

        try
        {
            var client = Type.GetType(ClientTypeName, throwOnError: false);
            if (client is null) return;

            LoadAppMcpClient();

            // Initialize disposes a running client before starting a new one, so
            // leave an app that already called it alone.
            if (client.GetProperty("Instance")?.GetValue(null) is not null) return;

            if (client.GetMethod("Initialize", [typeof(Type)]) is not { } initialize)
            {
                Warn(null, "RemoteControlClient.Initialize(Type) was not found; Hot Reload is unavailable.");
                return;
            }

            initialize.Invoke(null, [appType]);
        }
        catch (Exception ex)
        {
            // A dev-time convenience must never stop the app from starting.
            Warn(ex, "Could not connect to the Uno Dev Server; Hot Reload is unavailable.");
        }
    }

    /// <summary>
    /// Gives Uno's Hot Reload the window it updates, as <c>UseStudio()</c> does in a
    /// XAML app. Without it, Uno reports every edit as failed, and the App MCP's
    /// requests time out. No-op when the Dev Server client isn't in the app.
    /// </summary>
    public static void TryEnableHotReload(Window window)
    {
        try
        {
            var extensions = Type.GetType(WindowExtensionsTypeName, throwOnError: false);
            if (extensions is null) return;

            if (extensions.GetMethod("EnableHotReload", [typeof(Window)]) is not { } enable)
            {
                Warn(null, "Uno.UI.WindowExtensions.EnableHotReload(Window) was not found; Uno will report every edit as failed.");
                return;
            }

            enable.Invoke(null, [window]);
        }
        catch (Exception ex)
        {
            // Same as TryConnect: this runs during launch and must never stop it.
            Warn(ex, "Could not register the window with Uno Hot Reload.");
        }
    }

    // The App MCP client registers with the Dev Server client from a module
    // initializer, which runs only once code touches its assembly. A XAML app does
    // that through its generated GlobalStaticResources; a Reactor app has none.
    // Caught on its own, so a broken App MCP can't keep the app off the Dev Server.
    private static void LoadAppMcpClient()
    {
        try
        {
            var assembly = Assembly.Load(AppMcpClientAssemblyName);
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        }
        catch (FileNotFoundException)
        {
            // Not in the app: UnoDisableMCPSupport, or not an Uno.Sdk app.
        }
        catch (Exception ex)
        {
            Warn(ex, "Could not load the Uno App MCP client; the App MCP won't see this app.");
        }
    }

    // Most Reactor apps don't set AppLogger, and they don't configure Uno's logging,
    // so fall back to stderr. This only runs in builds that carry the Dev Server.
    private static void Warn(Exception? ex, string message)
    {
        // Show what the reflected call threw, not the reflection wrapper.
        if (ex is TargetInvocationException { InnerException: { } inner }) ex = inner;

        if (ReactorApp.AppLogger is { } logger)
            logger.LogWarning(ex, "{Message}", message);
        else
            Console.Error.WriteLine(ex is null ? $"Reactor: {message}" : $"Reactor: {message} {ex}");
    }
}
