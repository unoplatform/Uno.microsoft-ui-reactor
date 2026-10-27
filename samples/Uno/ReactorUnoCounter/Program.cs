using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using static Microsoft.UI.Reactor.Factories;

// Reactor owns the Uno Application + window + render loop. This single call is
// the whole entry point — the same shape as the Windows/WinUI ReactorApp.Run,
// and it is what desktop, WebAssembly AND iOS use (Uno gives the Apple heads the
// same host-builder shape as desktop; on WebAssembly Run starts the app and returns,
// since the browser thread can't block).
//
// Android differs: it has no console entry point at all — the OS starts the
// Application in Platforms/Android/, whose CreateHost calls ReactorApp.CreateHost<CounterApp>().
#if __ANDROID__
// intentionally empty — see Platforms/Android/
#else
// `--smoke` (`?smoke` in the page URL on WebAssembly) is the CI runtime check:
// click "+" once, verify the re-render, exit.
ReactorApp.Run<CounterApp>("Reactor Counter (Uno)", width: 480, height: 360,
    configure: SmokeTest.Requested(args) ? SmokeTest.Start : null);
#endif

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
