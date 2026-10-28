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
// nothing happens; that is the same gate as Uno's generator.

using System;
using Microsoft.Extensions.Logging;

namespace Microsoft.UI.Reactor.Hosting;

internal static class DevServerConnection
{
    private const string ClientTypeName = "Uno.UI.RemoteControl.RemoteControlClient, Uno.UI.RemoteControl";

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

            // Initialize disposes a running client before starting a new one, so
            // leave an app that already called it alone.
            if (client.GetProperty("Instance")?.GetValue(null) is not null) return;

            client.GetMethod("Initialize", [typeof(Type)])?.Invoke(null, [appType]);
        }
        catch (Exception ex)
        {
            // A dev-time convenience must never stop the app from starting.
            ReactorApp.AppLogger?.LogWarning(ex, "Could not connect to the Uno Dev Server; Hot Reload is unavailable.");
        }
    }
}
