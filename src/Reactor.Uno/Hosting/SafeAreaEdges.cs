using System;

namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// The window edges whose system UI (status bar, navigation bar, notch, display
/// cutouts) the app is kept clear of. See <see cref="ReactorHost.SafeAreaEdges"/>.
/// </summary>
[Flags]
public enum SafeAreaEdges
{
    /// <summary>Edge-to-edge: the app draws under all system UI.</summary>
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
    All = Top | Bottom | Left | Right,
}
