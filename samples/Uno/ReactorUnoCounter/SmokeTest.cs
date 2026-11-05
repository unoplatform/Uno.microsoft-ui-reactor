using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation.Metadata;

/// <summary>
/// <c>--smoke</c>: the CI runtime check. Waits for the counter to render, clicks "+"
/// through its automation peer (no input injection, so it works on a headless
/// runner), and checks the re-render shows the new count. That exercises startup,
/// mount, a state update and the reconciler's patch on a live Uno window. Then it awaits
/// on the UI thread and checks the continuation comes back there, which needs the
/// SynchronizationContext Uno installs (a stub one dropped it, uno-private#2250).
/// Exits 0 on success and 1 on failure or timeout.
/// On WebAssembly it is requested with <c>?smoke</c> in the page URL and does not exit:
/// the "[smoke] PASS" / "[smoke] FAIL" line on the browser console is the result,
/// which <c>.github/scripts/uno/smoke-wasm.ps1</c> reads from a headless browser.
/// On Android, Main gets no arguments, so it is requested with the environment variable
/// <c>REACTOR_SMOKE=1</c>; the result is the same line in logcat, which
/// <c>.github/scripts/uno/smoke-android.ps1</c> reads from an emulator.
/// The run also turns on <see cref="ApiInformation.IsFailWhenNotImplemented"/> for its
/// whole length, so an API Uno has only as a stub fails the smoke test instead of
/// silently doing nothing.
/// </summary>
static class SmokeTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The <c>configure</c> callback for <c>ReactorApp.Run</c>: <see cref="Start"/> when the
    /// smoke test is <see cref="Requested"/>, otherwise <c>null</c>.
    /// </summary>
    public static Action<ReactorHost>? Arm(string[] args)
    {
        if (!Requested(args)) return null;

        // Must be set before Run: Uno checks it when a stub is called, and OnLaunched is
        // where Reactor's hosting calls the most Uno APIs. A stub that throws there stops
        // startup before Start's timer runs, so there is no FAIL line: desktop and
        // WebAssembly give no result and time out, and Android crashes, which the smoke
        // script reports.
        ApiInformation.IsFailWhenNotImplemented = true;
        return Start;
    }

    /// <summary>
    /// <c>--smoke</c> on the command line, <c>?smoke</c> in the page URL on WebAssembly,
    /// or <c>REACTOR_SMOKE=1</c> on Android.
    /// </summary>
    public static bool Requested(string[] args) =>
#if __WASM__
        RequestedByPageUrl();
#elif __ANDROID__
        Environment.GetEnvironmentVariable("REACTOR_SMOKE") == "1";
#else
        Array.IndexOf(args, "--smoke") >= 0;
#endif

#if __WASM__
    // OperatingSystem.IsBrowser() is for CA1416, which doesn't count Uno's "BrowserWasm" as "browser".
    static bool RequestedByPageUrl() =>
        OperatingSystem.IsBrowser()
        && System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis
            .GetPropertyAsJSObject("location")?.GetPropertyAsString("search")?.Contains("smoke") == true;
#endif

    public static void Start(ReactorHost host)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var clicked = false;

        // Start runs inside OnLaunched, after Reactor's hosting has set up, so this await
        // captures the SynchronizationContext the hosting leaves in place, the one input
        // handlers outside Uno's dispatcher callbacks see. The timer below can't check it:
        // Uno runs every DispatcherQueue callback, ticks included, under its own context.
        var resumedOnUiThread = ResumesOnUiThread();

        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (_, _) =>
        {
            var root = host.Window.Content;
            var count = root is null ? null : Find<TextBlock>(root, t => t.Text.StartsWith("Count:"))?.Text;

            if (!clicked)
            {
                var plus = root is null ? null : Find<Button>(root, b => b.Content as string == "+");
                if (count == "Count: 0" && plus is not null)
                {
                    new ButtonAutomationPeer(plus).Invoke();
                    clicked = true;
                    return;
                }
            }
            else if (count == "Count: 1")
            {
                // If the continuation was dropped, this never completes and the deadline
                // below reports it.
                if (resumedOnUiThread.IsCompleted)
                {
                    if (resumedOnUiThread.Result)
                        Finish(timer, 0, "PASS: clicking + re-rendered \"Count: 0\" as \"Count: 1\", and an await on the UI thread resumed there");
                    else
                        Finish(timer, 1, "FAIL: an await on the UI thread resumed on another thread (no SynchronizationContext?)");
                    return;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                var awaited = resumedOnUiThread.IsCompleted ? "resumed" : "never resumed";
                Finish(timer, 1,
                    $"FAIL: timed out after {Timeout.TotalSeconds}s (clicked: {clicked}, count text: {count ?? "<not found>"}, await on the UI thread: {awaited})");
            }
        };
        timer.Start();
    }

    // Task.Yield posts the continuation to SynchronizationContext.Current, so this completes
    // with true only if that context exists and posts to this thread.
    static async Task<bool> ResumesOnUiThread()
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        await Task.Yield();
        return queue.HasThreadAccess;
    }

    static void Finish(DispatcherQueueTimer timer, int exitCode, string message)
    {
        timer.Stop();
        Console.WriteLine($"[smoke] {message}");
#if !__WASM__
        Environment.Exit(exitCode);
#endif
    }

    static T? Find<T>(DependencyObject node, Func<T, bool> match) where T : DependencyObject
    {
        if (node is T candidate && match(candidate)) return candidate;

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < children; i++)
        {
            if (Find(VisualTreeHelper.GetChild(node, i), match) is { } found) return found;
        }
        return null;
    }
}
