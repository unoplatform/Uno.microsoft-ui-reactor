// Uno host-builder bootstrap + code-only Application subclass.
//
// Uno hosts every head behind UnoPlatformHostBuilder (Uno.UI.Hosting). Each
// platform provider's Use*() extension lives in a runtime package that only its
// own TFM references, so the providers are picked per-TFM: wasm gets
// UseWebAssembly(), iOS UseAppleUIKit(), Android UseAndroid(), and desktop the
// X11/Framebuffer/macOS/Win32 providers (the builder picks the one available at
// runtime).

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Reactor.Hosting;
using Uno.UI.Hosting;

namespace Microsoft.UI.Reactor;

internal static class UnoBootstrap
{
    public static void Run()
    {
#if __WASM__
        // The browser thread can't block, so Uno's host.Run() throws on WebAssembly.
        // Start the host and return instead: Uno.Wasm.Bootstrap calls Main through
        // runMain, which keeps the runtime alive after Main returns, and the host's
        // run loop never completes. Nobody awaits the task, so report its faults here
        // or they are lost.
        RunAsync().ContinueWith(
            static t => ReportStartupFailure(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
#elif __ANDROID__
        // Android never calls Main on its own. When it runs, ReactorAndroidApplication
        // invoked it to read the options ReactorApp.Run just recorded, and builds and
        // runs the host itself once Main returns.
#else
        BuildHost().Run();
#endif
    }

    public static Task RunAsync() => BuildHost().RunAsync();

#if __WASM__
    private static void ReportStartupFailure(Exception ex)
    {
        if (!ReactorApplication.ReportUnhandled(ex))
            Console.Error.WriteLine($"Reactor app failed to start: {ex}");
    }
#endif

    internal static UnoPlatformHost BuildHost()
    {
        var builder = UnoPlatformHostBuilder.Create()
            .App(() => new ReactorApplication());

#if __WASM__
        builder = builder.UseWebAssembly();
#elif __IOS__ || __MACCATALYST__ || __TVOS__
        // Apple heads DO start from a real Main, and Uno gives them the same
        // host-builder shape as desktop — so ReactorApp.Run works there
        // unchanged, with UIKit as the platform provider.
        builder = builder.UseAppleUIKit();
#elif __ANDROID__
        // Android has no console entry point: the OS instantiates the head's
        // NativeApplication, whose CreateHost override calls ReactorApp.CreateHost
        // (or, with ReactorAndroidEntry, runs Main through ReactorAndroidApplication),
        // and Uno runs the host it returns.
        builder = builder.UseAndroid();
#else
        builder = builder
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32();
#endif

        return builder.Build();
    }
}

/// <summary>
/// The Uno <see cref="Application"/> that hosts a Reactor tree. Created by
/// <see cref="UnoBootstrap"/>; reads startup config from <see cref="ReactorApp.Options"/>.
/// </summary>
internal sealed class ReactorApplication : Application
{
    private ReactorWindow? _primary;

    // Where a XAML app's generated InitializeComponent connects to the Dev Server.
    public ReactorApplication() => DevServerConnection.TryConnect(ReactorApp.Options.AppType);

    /// <summary>
    /// The shared unhandled-exception path: logs through <see cref="ReactorApp.AppLogger"/>
    /// and asks <see cref="ReactorApp.OnUnhandledException"/>. Also used by the shared
    /// <c>RenderErrorDispatch</c> for render errors routed here by
    /// <c>RenderError.Propagate</c>. Mirrors the Windows framework.
    /// </summary>
    internal static bool ReportUnhandled(Exception ex) => ReportUnhandled(ex, ReactorApp.OnUnhandledException);

    private static bool ReportUnhandled(Exception ex, Func<Exception, bool>? callback)
    {
        ReactorApp.AppLogger?.LogError(ex, "UnhandledException: {ExceptionType}: {ExceptionMessage}", ex.GetType().Name, ex.Message);
        return callback is not null && callback(ex);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Process-wide unhandled-exception routing. Mirrors the Windows host:
        // log, then let an app-supplied hook decide whether to swallow. Unknown
        // exceptions are left unhandled so the app crashes with a useful error
        // rather than limping along corrupt.
        UnhandledException += (_, e) =>
        {
            // A render error the app asked to propagate (RenderError.Propagate) was already
            // offered to OnUnhandledException, which declined it; don't ask twice.
            if (Microsoft.UI.Reactor.Core.RenderErrorDispatch.TryConsumeDeclined(e.Exception))
                return;
            // Read the callback once: a one-shot callback that unregisters itself before
            // returning true must still have its decision applied.
            var callback = ReactorApp.OnUnhandledException;
            bool handled = ReportUnhandled(e.Exception, callback);
            if (callback is not null)
                e.Handled = handled;
        };

        // Marshal cross-thread setState callbacks back onto the UI thread.
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq is not null)
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(dq));
        }

        // WinUI control templates/styles.
        try { Resources.MergedDictionaries.Add(new XamlControlsResources()); }
        catch { /* resources already present / headless */ }

        var opts = ReactorApp.Options;

        var spec = new WindowSpec
        {
            Title = opts.WindowTitle,
            Width = opts.WindowWidth,
            Height = opts.WindowHeight,
            FullScreen = opts.FullScreen,
        };

        // The primary window goes through the same construction path as every
        // secondary window opened later via ReactorApp.OpenWindow / UseOpenWindow.
        _primary = ReactorApp.OpenWindowCore(
            spec, opts.RootFactory, opts.RootRenderFunc, opts.Configure);
    }
}
