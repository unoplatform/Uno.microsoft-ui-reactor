// Stand-in for the Windows framework's ReactorHostControl, which the Uno port
// excludes (src/Reactor/Hosting/ReactorHostControl.cs).
//
// The shared Core/Diagnostics/ReactorDiagnostics.Hosts.cs exposes it as the type
// of the public ReactorHostInfo.HostControl, so the port must declare it to
// compile. Declaring it here rather than excluding that file keeps
// ReactorDiagnostics.GetHosts and the host registry (which ReactorSourceMap
// calls) shared verbatim.

using Microsoft.UI.Xaml.Controls;

namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// Placeholder for the Windows framework's embeddable Reactor host control (a
/// hybrid XAML "island"). Not available in the Uno port yet: it cannot be
/// constructed, so <c>ReactorHostInfo.HostControl</c> is always <c>null</c> and
/// <c>ReactorHostKind.HostControl</c> is never reported.
/// </summary>
public sealed class ReactorHostControl : ContentControl
{
    private ReactorHostControl() { }
}
