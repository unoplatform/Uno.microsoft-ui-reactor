using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

/// <summary>
/// <c>--smoke</c>: the CI runtime check. Waits for the counter to render, clicks "+"
/// through its automation peer (no input injection, so it works on a headless
/// runner), and checks the re-render shows the new count. That exercises startup,
/// mount, a state update and the reconciler's patch on a live Uno window.
/// Exits 0 on success and 1 on failure or timeout.
/// On WebAssembly it is requested with <c>?smoke</c> in the page URL and does not exit:
/// the "[smoke] PASS" / "[smoke] FAIL" line on the browser console is the result,
/// which <c>smoke-wasm.ps1</c> reads from a headless browser.
/// </summary>
static class SmokeTest
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary><c>--smoke</c> on the command line, or <c>?smoke</c> in the page URL on WebAssembly.</summary>
    public static bool Requested(string[] args) =>
#if __WASM__
        RequestedByPageUrl();
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
                Finish(timer, 0, "PASS: clicking + re-rendered \"Count: 0\" as \"Count: 1\"");
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                Finish(timer, 1,
                    $"FAIL: timed out after {Timeout.TotalSeconds}s (clicked: {clicked}, count text: {count ?? "<not found>"})");
            }
        };
        timer.Start();
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
