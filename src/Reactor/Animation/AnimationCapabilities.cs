using Microsoft.UI.Composition;

namespace Microsoft.UI.Reactor.Animation;

/// <summary>
/// Which compositor animation features the running platform implements, probed once.
/// Every probe succeeds on Windows. Uno's Skia compositor (the Uno port) throws
/// <see cref="NotImplementedException"/> from <c>CreateImplicitAnimationCollection</c>,
/// <c>CompositionObject.ImplicitAnimations</c> and the <c>CreateSpring*Animation</c>
/// factories, which took down the whole render on the first <c>.Animate()</c>,
/// <c>.LayoutAnimation()</c>, <c>.Stagger()</c> or <c>Curve.Spring()</c>. Callers skip
/// implicit animations (the property snaps) and turn springs into an ease when a probe
/// fails, and pick the real APIs up as soon as the platform implements them.
/// </summary>
/// <remarks>
/// A workaround for the Uno port, to remove once Uno ships the APIs and the port's
/// Uno.Sdk pin includes them. Each probe names the Uno issue it waits on. Removing a
/// probe means removing its guards at every call site (Reconciler, AnimationHelper,
/// TransitionEngine), so look up its references first.
/// </remarks>
internal static class AnimationCapabilities
{
    private static bool? _implicitAnimations;
    private static bool? _springs;

    /// <summary>Stand-in for a spring where springs aren't implemented: a decelerating ease.</summary>
    internal static readonly EaseCurve SpringFallback = new(TimeSpan.FromMilliseconds(300), Easing.Decelerate);

    /// <summary><c>ImplicitAnimationCollection</c> and <c>CompositionObject.ImplicitAnimations</c>.</summary>
    /// <remarks>
    /// Remove once Uno implements them:
    /// <see href="https://github.com/unoplatform/uno-private/issues/1898"/>.
    /// </remarks>
    internal static bool SupportsImplicitAnimations(Compositor compositor)
        => _implicitAnimations ??= Probe(() => compositor.CreateImplicitAnimationCollection());

    /// <summary>The spring natural-motion animations.</summary>
    /// <remarks>
    /// Remove (with <see cref="Resolve"/> and <see cref="SpringFallback"/>) once Uno implements
    /// springs, <see href="https://github.com/unoplatform/uno-private/issues/1891"/>, AND its
    /// <c>CompositionScopedBatch</c> tracks them,
    /// <see href="https://github.com/unoplatform/uno-private/issues/1901"/>. Until both ship, the
    /// batch only waits for keyframe animations, so exit transitions and the spring slide
    /// navigation would finish before their springs settle. This probe only checks the first,
    /// so it turns real springs on as soon as #1891 ships.
    /// </remarks>
    internal static bool SupportsSprings(Compositor compositor)
        => _springs ??= Probe(() => compositor.CreateSpringScalarAnimation());

    /// <summary><paramref name="curve"/>, or <see cref="SpringFallback"/> for a spring the platform can't run.</summary>
    internal static Curve Resolve(Curve curve, Compositor compositor)
        => curve is SpringCurve && !SupportsSprings(compositor) ? SpringFallback : curve;

    private static bool Probe(Func<object> create)
    {
        try
        {
            create();
            return true;
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }
}
