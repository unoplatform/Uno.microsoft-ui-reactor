using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.UI.Reactor.Animation;
using Microsoft.UI.Reactor.Controls.Validation;
using Microsoft.UI.Reactor.Core.Diagnostics;
using Microsoft.UI.Reactor.Core.V1Protocol;
using Microsoft.UI.Reactor.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUI = Microsoft.UI.Xaml.Controls;
using WinPrim = Microsoft.UI.Xaml.Controls.Primitives;
using WinShapes = Microsoft.UI.Xaml.Shapes;
using ReactorSourceMap = Microsoft.UI.Reactor.Diagnostics.ReactorSourceMap;

namespace Microsoft.UI.Reactor.Core;

/// <summary>
/// The reconciler diffs old and new element trees and patches the real WinUI control tree.
///
/// Split across partial classes, one file per concern, so unrelated changes don't collide:
///   - Reconciler.cs           — orchestration: Reconcile entry, components, children, CanUpdate
///   - Reconciler.Mount.cs     — Mount() dispatch + composition-primitive handlers
///   - Reconciler.Update.cs    — Update() dispatch + composition-primitive handlers
///   - Reconciler.*.cs         — one topic each (Modifiers, Keyframes, Transitions, ThemeBindings,
///                               Events, Unmount, ReactorState, ...); add code to the matching file
/// </summary>
public sealed partial class Reconciler : IDisposable
{
    private readonly Dictionary<UIElement, ComponentNode> _componentNodes = new();
    private readonly Dictionary<UIElement, ErrorBoundaryNode> _errorBoundaryNodes = new();
    // Test seam: boundaries still registered, so a teardown path that leaks one is observable.
    internal int ErrorBoundaryNodeCount => _errorBoundaryNodes.Count;
    // internal so NavigationHostLifecycle (V1-owned mount/update) can record and
    // look up per-instance navigation state; teardown stays here (Dispose loop +
    // CleanupNavigationHostNode).
    internal readonly Dictionary<UIElement, NavigationHostNode> _navigationHostNodes = new();
    // Internal so V1-owned lifecycle classes (e.g. LazyStackLifecycle) can rent
    // pooled controls and pass the pool into element factories.
    internal readonly ElementPool _pool = new();
    private readonly Dictionary<Type, ITypeRegistration> _typeRegistry = new();
    // Null when no caller-supplied logger and no devtools logger is published.
    // All call sites use null-conditional access so the M.E.Logging code path
    // doesn't run (and stays JIT-cold) for default apps.
    // Internal so V1-owned lifecycle classes can pass it into shared keyed-diff helpers.
    internal readonly ILogger? _logger;
    private readonly List<(string Key, UIElement Target)> _pendingConnectedAnimationStarts = new();
    private readonly ContextScope _contextScope = new();

    /// <summary>
    /// The host that owns this reconciler's content (<see cref="Microsoft.UI.Reactor.Hosting.ReactorHost"/>
    /// or <see cref="Microsoft.UI.Reactor.Hosting.ReactorHostControl"/>), or null for a bare
    /// reconciler (unit tests). Every component context it renders records it as its owner,
    /// including renders outside the host's render pass (virtualized rows); the picker methods
    /// resolve their owning window through it at call time.
    /// </summary>
    internal IContentOwner? Owner { get; set; }
    /// <summary>
    /// The XAML <see cref="Window"/> of the <see cref="Microsoft.UI.Reactor.Hosting.ReactorHost"/> that
    /// owns this reconciler, with or without a <see cref="Microsoft.UI.Reactor.ReactorWindow"/> (<see cref="Owner"/>); null for
    /// <see cref="Microsoft.UI.Reactor.Hosting.ReactorHostControl"/>. Lets a mounting
    /// <c>TitleBar</c> claim its own host's window instead of whichever host is ambient.
    /// </summary>
    internal Window? HostWindow { get; set; }

    // Issue #917 — withdraws an unmounting TitleBar from this reconciler's own window (not the
    // ambient host's). Skipped once the window is closing or disposed: ReactorWindow.Dispose
    // already drops its title-bar bookkeeping, and resetting the caption state of a window
    // being torn down would undo PrepareTitleBarForClose (issue #537).
    private void WithdrawTitleBar(WinUI.TitleBar unmounting)
    {
        if (HostWindow is null)
        {
            _embeddedTitleBars?.Remove(unmounting);
            WithdrawEmbeddedTitleBar(unmounting);
            return;
        }
        if (Owner?.ReactorWindow is { IsClosingOrDisposed: false } window)
            window.ClearTitleBarControl(unmounting);
    }

    // TitleBars in ReactorHostControl content, registered (once loaded) with the ReactorWindow
    // that contains them; released on unmount and on reconciler disposal. (issue #1396)
    private HashSet<WinUI.TitleBar>? _embeddedTitleBars;

    internal void TrackEmbeddedTitleBar(WinUI.TitleBar titleBar) => (_embeddedTitleBars ??= new()).Add(titleBar);

    private static void WithdrawEmbeddedTitleBar(WinUI.TitleBar titleBar)
    {
        if (TitleBarElement.ReleaseEmbedded(titleBar) is { IsClosingOrDisposed: false } window)
            window.ClearTitleBarControl(titleBar);
    }
    // Ancestor-aware effective theme for the subtree currently being mounted/updated,
    // threaded top-down through Mount/Update (an element's own RequestedTheme wins,
    // otherwise the subtree inherits its ancestor's). ElementTheme.Default means "no
    // Reactor-tree ancestor override" — resolution then falls back to the element's own
    // fe-based walk / app theme. At mount the element is not yet parented, so an
    // ancestor RequestedTheme is unreachable via fe's visual-tree walk; this field
    // carries the reconciler's top-down knowledge so ThemeRef-backed ResourceOverrides
    // resolve their concrete brush against the correct effective theme at mount.
    private ElementTheme _ambientRequestedTheme = ElementTheme.Default;

    /// <summary>
    /// Native parent of the host's content, also used when installing error fallbacks.
    /// </summary>
    internal DependencyObject? ThemeAnchor { get; set; }
    private ElementTheme _renderedHostTheme;
    private ElementTheme _realizationTheme;
    private int _realizationThemeDepth;
    private bool _realizationResourceRefresh;
    internal bool HostThemeChanged => InheritedRequestedTheme(ThemeAnchor) != _renderedHostTheme;

    /// <summary>
    /// The nearest explicit <c>RequestedTheme</c> on <paramref name="start"/> or its visual
    /// ancestors, or Default. Covers themes set by Reactor on realized controls and themes
    /// an app sets outside the Reactor tree (for example on a window root).
    /// </summary>
    internal static ElementTheme InheritedRequestedTheme(DependencyObject? start)
    {
        for (var node = start; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { RequestedTheme: not ElementTheme.Default } themed)
                return themed.RequestedTheme;
        }
        return ElementTheme.Default;
    }

    /// <summary>
    /// Layout-time realization (an ItemsRepeater or ItemsView asking for a row) runs outside
    /// any root pass, so no ambient theme is threaded to the row. Seed it from the parented
    /// realization target so ThemeRef resource overrides on the still-unparented row resolve
    /// against the theme it will render under. A realization nested in a pass keeps the
    /// pass's ambient.
    /// </summary>
    internal RealizationThemeScope EnterRealizationTheme(DependencyObject? realizationParent, UIElement? reused = null)
    {
        var inherited = _debugReconcileDepth > 0
            ? _ambientRequestedTheme : InheritedRequestedTheme(realizationParent);
        bool refresh = reused is FrameworkElement reusedElement && TryGetReactorState(reusedElement, out var state)
            && state.RealizationTheme is { } previous && previous != inherited;
        var scope = new RealizationThemeScope(this, inherited);
        _realizationThemeDepth++;
        _realizationTheme = inherited;
        _ambientRequestedTheme = inherited;
        // A parked row missed the host refresh. Its unchanged children must not skip.
        if (refresh)
        {
            _realizationResourceRefresh = true;
            _resourceRefreshActive = true;
            _forceFullRenderActive = true;
        }
        return scope;
    }

    internal readonly struct RealizationThemeScope : IDisposable
    {
        private readonly Reconciler? _owner;
        private readonly ElementTheme _realization;
        private readonly ElementTheme _ambient;
        private readonly bool _realizationRefresh;
        private readonly bool _refreshActive;
        private readonly bool _forceActive;
        internal ElementTheme Theme { get; }

        internal RealizationThemeScope(Reconciler owner, ElementTheme theme)
        {
            _owner = owner;
            Theme = theme;
            _realization = owner._realizationTheme;
            _ambient = owner._ambientRequestedTheme;
            _realizationRefresh = owner._realizationResourceRefresh;
            _refreshActive = owner._resourceRefreshActive;
            _forceActive = owner._forceFullRenderActive;
        }

        public void Dispose()
        {
            if (_owner is null)
                return;
            _owner._realizationThemeDepth--;
            _owner._realizationTheme = _realization;
            _owner._ambientRequestedTheme = _ambient;
            _owner._realizationResourceRefresh = _realizationRefresh;
            _owner._resourceRefreshActive = _refreshActive;
            _owner._forceFullRenderActive = _forceActive;
        }
    }

    /// <summary>
    /// Supplies the owning host's effective <see cref="RenderErrorHandler"/> (issue #1291).
    /// Set by <c>ReactorHost</c> / <c>ReactorHostControl</c>; a reconciler without a host
    /// falls back to <see cref="ReactorApp.DefaultRenderErrorHandler"/>.
    /// </summary>
    internal Func<RenderErrorHandler?>? RenderErrorHandlerProvider { get; set; }

    private RenderErrorHandler? ResolveRenderErrorHandler() =>
        RenderErrorHandlerProvider is { } provider ? provider() : ReactorApp.DefaultRenderErrorHandler;

    // Nesting depth of the public Reconcile / Mount entry points (issue #1291). The outermost
    // entry — whichever a caller reaches first — opens a render-error propagation scope, so a
    // standalone caller (no host render loop) cannot leave propagation markers behind;
    // recursive mounts and nested passes just count.
    private int _entryDepth;

    private EntryFrame EnterFrame(string perfEntry, Element? element)
    {
        var scope = _entryDepth == 0 ? RenderErrorDispatch.EnterPropagationScope() : default;
        bool perfSpan = false;
        if (_entryDepth == 0)
        {
            _passId++;
            // The perf Reconcile span covers each outermost entry: a host pass's reconcile, and
            // a row an ItemsRepeater / ListView realizes or updates during layout, which runs
            // outside any host pass. Stopped in EntryFrame.Dispose, after the whole entry.
            if (Diagnostics.ReactorPerfTrace.PassEnabled)
            {
                Diagnostics.ReactorPerfTrace.ReconcileStart(perfEntry,
                    element is null ? "null" : Diagnostics.ComponentNames.For(element.GetType()));
                perfSpan = true;
                // Tally this entry's panel child edits for the Reconcile Stop.
                _perfEdits = default;
                _perfTally = true;
            }
        }
        _entryDepth++;
        return new EntryFrame(this, scope, perfSpan);
    }

    // The panel child edits made during the open perf Reconcile span (all containers, every
    // depth), written on its Stop. _perfTally is true only while that span is open, so with
    // no session listening ReconcileChildrenTraced reads one bool and counts nothing.
    private bool _perfTally;
    private PerfEdits _perfEdits;

    // Test-only: the tally must be closed between entries, or every child reconcile pays for it.
    internal bool PerfTallyOpenForTests => _perfTally;

    /// <summary>
    /// Counts controls this reconciler created, updated, removed or moved toward the open perf
    /// <c>ComponentRender</c> spans' <c>changes</c> (<see cref="Diagnostics.ComponentPerfTally"/>).
    /// One static gate read when nobody listens.
    /// </summary>
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CountPerfChanges(uint count = 1)
    {
        if (Diagnostics.ReactorPerfTrace.ComponentEnabled)
            Diagnostics.ComponentPerfTally.AddChanges(count);
    }

    /// <summary>
    /// Whether an in-place update changed what the control shows, for the perf <c>changes</c>
    /// count. <paramref name="shallowEqual"/> is <see cref="Element.ShallowEquals"/> of the two
    /// elements, which <see cref="Update"/> already computed. Counts a change when the effective
    /// modifiers differ in anything but callback identity
    /// (<see cref="Element.ModifiersVisiblyEqual"/>), or the element's own properties differ.
    /// Own properties ignore a container's or items host's children and items, which report
    /// their own edits: <see cref="Element.OwnPropsEqual"/> (the reconcile-highlight overlay's
    /// rule) plus the attached layout, theme bindings, resource overrides and animations it does
    /// not read (<see cref="Element.ExtrasEqual"/>). A type neither comparison knows counts as
    /// changed. A keyed <c>Memo</c> whose key held keeps its control (its inner update, if any,
    /// counts itself).
    /// </summary>
    internal static bool PerfUpdateChanged(Element oldEl, Element newEl,
        ElementModifiers? oldModifiers, ElementModifiers? modifiers, bool shallowEqual)
    {
        if (!Element.ModifiersVisiblyEqual(oldModifiers, modifiers)) return true;
        if (shallowEqual) return false;
        // Only a keyed Memo's factory output is stable under its key; the wrapper's own extras
        // (theme bindings, overrides, attached values) are applied on every update.
        if (oldEl is KeyedMemoElement && newEl is KeyedMemoElement) return !Element.ExtrasEqual(oldEl, newEl);
        if (Element.OwnPropsEqual(oldEl, newEl) && Element.ExtrasEqual(oldEl, newEl)) return false;
        // ShallowEquals also compares the element's own modifiers, callbacks included. The
        // effective modifiers are visibly equal (checked above), so when they differ in identity
        // only, compare the rest of the element without them.
        return ReferenceEquals(oldEl.Modifiers, newEl.Modifiers)
            || Element.ModifiersEqual(oldEl.Modifiers, newEl.Modifiers)
            || !Element.ShallowEquals(oldEl, newEl with { Modifiers = oldEl.Modifiers });
    }

    private struct PerfEdits
    {
        public uint Inserted, Removed, Moved, Replaced;

        public void Add(PanelChildCollection collection)
        {
            Inserted += collection.InsertCount;
            Removed += collection.RemoveCount;
            Moved += collection.MoveCount;
            Replaced += collection.ReplaceCount;
        }
    }

    // Identifies the outermost Reconcile/Mount pass in flight (advanced on entry at depth
    // 0). A virtualized host records the pass and the subtree theme it was reached with,
    // so a row realized re-entrantly later in the SAME pass can use that pending theme:
    // the native RequestedTheme modifier is only applied after children are dispatched,
    // so the host's ActualTheme still shows the previous theme at that point.
    private long _passId;

    private readonly struct EntryFrame(Reconciler owner, RenderErrorDispatch.PropagationScope scope, bool perfSpan) : IDisposable
    {
        public void Dispose()
        {
            scope.Dispose();
            owner._entryDepth--;
            if (perfSpan)
            {
                owner._perfTally = false;
                var edits = owner._perfEdits;
                Diagnostics.ReactorPerfTrace.ReconcileStop(edits.Inserted, edits.Removed, edits.Moved, edits.Replaced);
            }
        }
    }

    /// <summary>In-tree placeholder for a component whose render or effect flush threw.</summary>
    private Element BuildInTreeFallback(Exception ex, bool inEffects, string? componentName) =>
        RenderErrorDispatch.BuildInTreeFallback(
            ResolveRenderErrorHandler(),
            new RenderError(ex, inEffects ? RenderErrorSource.Effects : RenderErrorSource.ComponentRender,
                componentName, isHostLevel: false),
            _logger);
    /// <summary>
    /// Active rerender-callback depth. Throws past
    /// <see cref="MaxRerenderReentrancy"/> so a component that synchronously
    /// invokes its own <c>requestRerender</c> from inside <c>Render()</c> or
    /// effect cleanup fails fast instead of recursing to a stack overflow.
    /// TASK-059.
    /// </summary>
    [ThreadStatic]
    private static int t_rerenderDepth;
    /// <summary>Hard depth cap — see <see cref="t_rerenderDepth"/>.</summary>
    private const int MaxRerenderReentrancy = 50;

    /// <summary>
    /// Per-reconcile counters for diagnosing diff and mount/update volume.
    /// Reset before each top-level Reconcile() call; read afterward. Always
    /// populated (including Release builds) so ETW trace consumers can read
    /// them off the <c>ReconcileStop</c> event payload.
    /// </summary>
    public int DebugElementsDiffed;
    public int DebugElementsSkipped;
    public int DebugUIElementsCreated;
    public int DebugUIElementsModified;
    private int _debugReconcileDepth;

    /// <summary>
    /// Mounts a ContentDialog's body. A dialog opened later (deferred to the placeholder's
    /// Loaded, or on a state flip handled outside a render) mounts outside any owner scope; the
    /// placeholder's published <c>owner=</c> is the owner its body was declared under, so it is
    /// restored for the mount. Nested components still scope their own subtrees.
    /// </summary>
    internal UIElement? MountDialogContent(
        FrameworkElement placeholder,
        Element content,
        Action requestRerender,
        ElementTheme theme,
        V1Protocol.OverlayLifecycle.ContentDialogLifetime lifetime)
    {
        if (lifetime.Retired || IsDisposingContentDialogs) return null;

        var previousTheme = _ambientRequestedTheme;
        _ambientRequestedTheme = theme;
        UIElement? mounted;
        try
        {
            if (global::Microsoft.UI.Reactor.Hosting.ReactorFeatures.DevtoolsSupported && Diagnostics.ReactorSourcePublisher.IsEnabled
                && CurrentDiagnosticOwner is null
                && placeholder.GetValue(Diagnostics.ReactorDiagnostics.SourceProperty) is string published
                && Diagnostics.ReactorSourcePublisher.Owner(published) is { } owner)
                mounted = MountUnderOwner(content, requestRerender, owner);
            else
                mounted = Mount(content, requestRerender);
        }
        finally { _ambientRequestedTheme = previousTheme; }

        // A child setter can dispose the host while Mount is walking siblings.
        // Disposal drains the current component set, so tear down the completed
        // side-mounted subtree too; this also makes a later dialog.Content write a no-op.
        if (lifetime.Retired || IsDisposingContentDialogs)
        {
            if (mounted is not null) UnmountChild(mounted);
            return null;
        }
        return mounted;
    }

    private UIElement? MountUnderOwner(Element element, Action requestRerender, string owner)
    {
        var previous = _diagOwner;
        _diagOwner = owner;
        try { return Mount(element, requestRerender); }
        finally { _diagOwner = previous; }
    }

    private UIElement? ReconcileUnderOwner(
        Element? oldElement, Element? newElement, UIElement? existing, Action requestRerender, string owner)
    {
        var previous = _diagOwner;
        _diagOwner = owner;
        try { return Reconcile(oldElement, newElement, existing, requestRerender); }
        finally { _diagOwner = previous; }
    }

    // Hot reload signal: when set, the next top-level Reconcile() pass bypasses
    // Component memo (props/deps equality) so updated method bodies are picked up
    // even when props are unchanged. Cleared at the start of that pass.
    internal volatile bool ForceFullRenderPending;
    private bool _forceFullRenderActive;

    // Diagnostics only: the next top-level pass is the retry a host schedules after a
    // hot-reload hook-order recovery. That retry runs outside a hot-reload pass, so
    // without this ComponentRendered would attribute its renders to their ordinary
    // reasons, not "hotReload". Consumed by that pass; never changes render behaviour.
    internal volatile bool HotReloadRetryPending;
    private bool _hotReloadRetryActive;

    // True for the duration of a hot-reload force pass. Read by ChildReconciler's
    // positional fast path: while a force pass is active, untouched wrapper cells
    // (Component/Memo/Func) in a memoized range must still re-render through the
    // wrapper, so the structural skip must defer to the full walk (which honours
    // ForceRenderThroughWrapper per cell). A pure hot-reload force does NOT mark
    // any node SelfTriggered before the dirty-ancestor path is built, so the
    // IsOnDirtyAncestorPath gate alone does not cover this case.
    internal bool ForceFullRenderActive => _forceFullRenderActive;

    // Theme.NotifyResourcesChanged signal: the host's next root pass is a force pass that
    // ALSO declines every structural skip, not just the wrapper ones. A reference-equal
    // subtree (a reused or UseMemo'd element) is otherwise skipped wholesale, and its
    // ThemeRef modifiers / ThemeRef-backed resource overrides would never be re-applied
    // against the edited resources. Implies a force pass. Set from any thread; taken
    // atomically by BeginRootPass, so a request racing that pass is kept for the next one
    // rather than lost, and only the host's root pass can consume it.
    private int _resourceRefreshPending;
    private bool _resourceRefreshArmed;
    private bool _resourceRefreshActive;

    /// <summary>Requests a resource-refresh pass (see above). Thread-safe.</summary>
    internal void RequestResourceRefresh() => Interlocked.Exchange(ref _resourceRefreshPending, 1);

    /// <summary>
    /// Called by a host before its root render: moves a pending resource refresh onto that
    /// pass's <see cref="Reconcile"/>. Out-of-band top-level reconciles (ElementFactory
    /// realizing or refreshing a row) don't call it, so they can't consume the request.
    /// UI thread only; pair with <see cref="EndRootPass"/> in a finally.
    /// </summary>
    internal void BeginRootPass()
    {
        // Before Windows 10 2004 (19041) "Animation effects" has no change notification, and an
        // unchanged element skips its update, so each root pass polls the setting (issue #1702).
        if (!UiSettingsCapabilities.HasAnimationsEnabledChanged)
            global::Microsoft.UI.Reactor.Navigation.TransitionEngine.PollAnimationsEnabled();
        var pending = Interlocked.Exchange(ref _resourceRefreshPending, 0) != 0;
        _resourceRefreshArmed |= pending || HostThemeChanged;
    }

    /// <summary>
    /// Ends a host root pass. A request <see cref="BeginRootPass"/> armed that the pass never
    /// consumed (the render failed or returned before reconciling) goes back to pending.
    /// </summary>
    internal void EndRootPass()
    {
        if (!_resourceRefreshArmed) return;
        _resourceRefreshArmed = false;
        Interlocked.Exchange(ref _resourceRefreshPending, 1);
    }

    internal bool ResourceRefreshPendingForTest => Volatile.Read(ref _resourceRefreshPending) != 0;
    internal bool ResourceRefreshArmedForTest => _resourceRefreshArmed;

    // True only during a force pass, for the wrapper elements whose skip would prevent
    // ReconcileComponent from running — and for every element during a resource-refresh
    // pass. Used by Update() and ChildReconciler to bypass their structural-equality
    // short-circuits.
    internal bool ForceRenderThroughWrapper(Element el) =>
        _forceFullRenderActive
        && (_resourceRefreshActive || el is ComponentElement or MemoElement or FuncElement);

    // True for the duration of a resource-refresh pass. Read by the skip arms that are not
    // shallow-equality gates (a KeyedMemoElement with an unchanged key, an ItemsHost whose
    // item list is unchanged) so they still reconcile their element children.
    internal bool ResourceRefreshActive => _resourceRefreshActive;

    /// <summary>
    /// For a ListView/GridView whose item array was kept (same reference) during a resource
    /// refresh: reconcile each realized container's item in place, so theme values inside
    /// kept items are re-applied without swapping ItemsSource (which would reset selection
    /// and scroll). Unrealized items mount later against the already-cleared cache. Walks
    /// the realized panel children (O(realized)), as <c>RefreshRealizedContainers</c> does,
    /// rather than probing every index of a virtualized list.
    /// </summary>
    internal void RefreshRealizedItemContainers(
        WinUI.ListViewBase list, Element[] items, Action requestRerender)
    {
        if (!_resourceRefreshActive || list.ItemsPanelRoot is not { } panel) return;
        // Snapshot: reconciling may mount controls, and Children can't change mid-enumeration.
        var realized = panel.Children
            .OfType<WinUI.Primitives.SelectorItem>()
            .Select(static container => (Container: container, Shell: container.ContentTemplateRoot as WinUI.ContentControl))
            .Where(static c => c.Shell?.Content is UIElement)
            .ToList();
        foreach (var (container, cc) in realized)
        {
            var existing = (UIElement)cc!.Content;
            int i = list.IndexFromContainer(container);
            if (i < 0 || i >= items.Length) continue;
            var next = ReconcileV1Child(items[i], items[i], existing, requestRerender);
            if (next is not null && !ReferenceEquals(next, existing))
                cc.Content = next;
            // The item's own automation name can change in place too (a keyed memo whose output
            // reads a resource), so forward it after every reconcile, as RefreshRealizedContainers does.
            PropagateItemAutomationName(container, cc.Content as UIElement);
        }
    }

    // Set of realized UIElements that lie on the path from the root to a
    // ComponentNode whose <see cref="ComponentNode.SelfTriggered"/> is true.
    // Populated at the start of each top-level Reconcile pass by walking
    // up the visual tree from each self-triggered node, and consumed by
    // <see cref="Update"/> to bypass its shallow-equality short-circuit
    // so the render-pass can reach the self-triggered descendant.
    //
    // Without this, a setState inside a Component whose ancestor elements
    // are structurally unchanged (e.g. a dynamically-docked pane whose
    // wrapper Border/Provide chain has stable fields) gets its render
    // swallowed by the parent's shallow-equality skip — the new state
    // never lands in the visual tree. Reproduced by
    // NativeDocking_DynamicallyDockedContent_IsInteractive (counter
    // inside a docked Document — click fires, setCount marks
    // node.SelfTriggered, but the TextBlock never advances past 0).
    private HashSet<UIElement>? _dirtyAncestorPath;
    internal bool IsOnDirtyAncestorPath(UIElement? control) =>
        control is not null && _dirtyAncestorPath is { } set && set.Contains(control);

    // For each control on the dirty-ancestor path, the child (a UIElement) or children (a
    // List<UIElement>, when two self-triggered nodes share an ancestor) the path continues
    // through. Built by the same walk as _dirtyAncestorPath and cleared with it. It lets
    // ChildReconciler find which of a panel's children lead to pending work with one IndexOf
    // per dirty child, so its skip arms never have to read every skipped sibling to ask.
    private Dictionary<UIElement, object>? _dirtyPathChildren;

    /// <summary>
    /// Which children of <paramref name="parentControl"/>, held in
    /// <paramref name="children"/>, lie on the dirty-ancestor path — the children a reconcile of
    /// this container must descend into even when their elements are unchanged, because a
    /// component below them updated its own state.
    /// </summary>
    /// <remarks>
    /// Costs one field check when nothing in the pass self-triggered, and one hash lookup when
    /// something did but this container is not above it. Otherwise each dirty child is located
    /// with a single <see cref="IChildCollection.IndexOf"/>. When a dirty child can't be located
    /// in <paramref name="children"/> (a descriptor whose collection is not the parent's own
    /// <c>Children</c>), or the parent is unknown, the result asks the caller to test each
    /// skip-eligible child's control instead: slower, but it never skips a pending component.
    /// </remarks>
    internal DirtyChildIndices ResolveDirtyChildIndices(UIElement? parentControl, IChildCollection children)
    {
        if (_dirtyAncestorPath is not { Count: > 0 } path)
            return default;
        if (parentControl is null)
            return DirtyChildIndices.ProbeEachChild;
        if (!path.Contains(parentControl))
            return default;
        // On the path with no recorded child: the parent is itself a self-triggered control,
        // and nothing below it is pending.
        if (_dirtyPathChildren is null || !_dirtyPathChildren.TryGetValue(parentControl, out var recorded))
            return default;

        if (recorded is UIElement single)
        {
            int index = children.IndexOf(single);
            return index >= 0 ? DirtyChildIndices.At(index) : DirtyChildIndices.ProbeEachChild;
        }

        var many = (List<UIElement>)recorded;
        var indices = new int[many.Count];
        for (int k = 0; k < many.Count; k++)
        {
            int index = children.IndexOf(many[k]);
            if (index < 0)
                return DirtyChildIndices.ProbeEachChild;
            indices[k] = index;
        }
        return DirtyChildIndices.AtAll(indices);
    }

    private static void AddDirtyPathChild(Dictionary<UIElement, object> edges, UIElement parent, UIElement child)
    {
        if (!edges.TryGetValue(parent, out var existing))
        {
            edges[parent] = child;
            return;
        }
        if (existing is UIElement single)
        {
            if (!ReferenceEquals(single, child))
                edges[parent] = new List<UIElement>(2) { single, child };
            return;
        }
        var list = (List<UIElement>)existing;
        foreach (var known in list)
        {
            if (ReferenceEquals(known, child))
                return;
        }
        list.Add(child);
    }

    // ── Reconcile-highlight capture (gated by ReactorFeatureFlags.HighlightReconcileChanges) ──
    private List<UIElement>? _highlightMounted;
    private List<UIElement>? _highlightModified;

    /// <summary>
    /// UIElements that were newly mounted during the last top-level Reconcile pass.
    /// Only populated when <see cref="ReactorFeatureFlags.HighlightReconcileChanges"/> is true;
    /// returns an empty list otherwise so callers never see stale data after the flag is toggled off.
    /// </summary>
    public IReadOnlyList<UIElement> LastMountedElements =>
        ReactorFeatureFlags.HighlightReconcileChanges
            ? (IReadOnlyList<UIElement>?)_highlightMounted ?? Array.Empty<UIElement>()
            : Array.Empty<UIElement>();

    /// <summary>
    /// UIElements that were modified in-place during the last top-level Reconcile pass.
    /// Only populated when <see cref="ReactorFeatureFlags.HighlightReconcileChanges"/> is true;
    /// returns an empty list otherwise so callers never see stale data after the flag is toggled off.
    /// </summary>
    public IReadOnlyList<UIElement> LastModifiedElements =>
        ReactorFeatureFlags.HighlightReconcileChanges
            ? (IReadOnlyList<UIElement>?)_highlightModified ?? Array.Empty<UIElement>()
            : Array.Empty<UIElement>();

    /// <summary>
    /// The element pool used by this reconciler. Disable via Pool.Enabled = false
    /// to prevent recycled controls from retaining stale property state.
    /// </summary>
    public ElementPool Pool => _pool;

    /// <summary>
    /// EXP-2: When true, UpdateText uses bitmask diff (old vs new Element comparison)
    /// instead of reading WinUI control properties via COM interop to guard writes.
    /// </summary>
    private static volatile bool _enableBitmaskDiff;
    public static bool EnableBitmaskDiff
    {
        get => _enableBitmaskDiff;
        set => _enableBitmaskDiff = value;
    }

    /// <summary>
    /// Spec 047 §14 — the V1 protocol is the unconditional production path.
    /// The Phase 1 <c>UseV1Protocol</c> feature flag, its AppContext switch,
    /// and the descriptor-vs-handler A|B harness ctor were all removed in
    /// §4.6 once the legacy <c>MountXxx</c>/<c>UpdateXxx</c> switch was deleted
    /// (§4.5).
    ///
    /// Built-in V1 handlers register automatically into the internal
    /// <see cref="V1HandlerRegistry"/>; external authors add handlers via
    /// <see cref="RegisterHandler{TElement,TControl}"/> /
    /// <see cref="RegisterType{TElement,TControl}"/> (which populate
    /// <c>_typeRegistry</c>). Dispatch order is V1 → external → the four
    /// composition primitives that sit above the protocol.
    /// </summary>
    public Reconciler(ILogger? logger = null)
    {
        _logger = logger;
        // Registered before the first render, so a perf session already running (or started
        // later, e.g. by `winapp perf`) enables the provider. Once per process.
        Diagnostics.ReactorPerfTrace.EnsureRegistered();
        // Spec 048 §3.4 — no more bootstrap. Built-in handlers register
        // themselves lazily on first factory call (per-control Reg<>/
        // RegDecorator<> cctor latch in `Dsl.cs`), or callers register
        // explicitly via `ControlRegistry.Register<,>`. Constructing an
        // element record directly (e.g. `new TextBlockElement("hi")`)
        // without ever touching its factory or registering its handler
        // throws on first mount with a diagnostic pointing at issue #486.
    }



    /// <summary>
    /// Spec 047 §14 Phase 4 (4.0.5) — true if an element type already has a
    /// handler in either the V1 registry or the external type registry. Lets
    /// idempotent external registrars (e.g. <see cref="Hosting.XamlInterop.Register"/>)
    /// avoid the <see cref="EnsureRegistrableElementType"/> duplicate throw when
    /// V1 auto-registration already owns the type.
    /// </summary>
    internal bool IsElementTypeRegistered(Type elementType)
        => _v1Handlers.ContainsKey(elementType) || _typeRegistry.ContainsKey(elementType);

    // ── V1 handler registry (spec 047 §14 Phase 1, Q1.1) ──────────────
    // Keyed by exact element type, separate from _typeRegistry so that
    // external RegisterType callers and built-in V1 ports stay isolated.
    // Add is internal-only for Phase 1; the public author-facing surface
    // (`RegisterHandler<TElement,TControl>(IElementHandler<…>)`) lands in
    // sections 1.6 / 1.9.
    internal readonly V1HandlerRegistry _v1Handlers = new();

    /// <summary>
    /// Spec 048 §8 — arm 3 of the dispatch precedence: on a per-host
    /// <c>_v1Handlers</c> and per-host <c>_typeRegistry</c> miss, consult
    /// the global lazy <see cref="V1Protocol.ControlRegistry"/>. On a hit
    /// the factory is invoked exactly once and the resulting adapter is
    /// cached into per-host <c>_v1Handlers</c> so steady-state dispatch
    /// short-circuits in arm 1 (the existing fast per-host lookup) on the
    /// next call for this element type on this host.
    ///
    /// <para>Precedence enforced at the call sites (Mount / Update):
    /// (1) per-host <c>_v1Handlers</c> →
    /// (2) per-host <c>_typeRegistry</c> →
    /// (3) global <see cref="V1Protocol.ControlRegistry"/> →
    /// (4) composition-primitive switch.</para>
    /// </summary>
    /// <param name="elementType">Exact runtime element type
    /// (<c>element.GetType()</c>); not a base type. Dispatch is exact-match
    /// (spec 047 §13 Q17).</param>
    /// <param name="entry">On hit, the type-erased adapter to dispatch
    /// through (and now cached in <c>_v1Handlers</c>).</param>
    internal bool TryResolveFromControlRegistry(Type elementType, out IV1HandlerEntry entry)
    {
        if (V1Protocol.ControlRegistry.TryResolve(elementType, out var factory))
        {
            entry = factory();
            // _v1Handlers is UI-thread-only; the caller has already verified
            // there is no per-host entry for this element type, so the Add
            // can never see a duplicate it didn't itself just plant.
            _v1Handlers.Add(elementType, entry);
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>
    /// Associates a control with its current element via Tag.
    /// Only call for interactive controls that need the Tag-based event handler pattern.
    /// Layout-only controls (Border, StackPanel, TextBlock, etc.) should NOT set Tag
    /// to avoid expensive COM DependencyProperty calls on the hot path.
    /// </summary>
    /// <summary>
    /// A shared DataTemplate containing a ContentControl shell.
    /// Parsed once via XamlReader.Load, reused across all items controls (ListView, GridView, FlipView).
    /// </summary>
    internal static readonly Lazy<DataTemplate> SharedContentControlTemplate = new(() =>
        (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<ContentControl HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'/>" +
            "</DataTemplate>"));

    /// <summary>
    /// Spec 047 §14 Phase 3 finish — text-bound TreeView item template
    /// shared between the legacy <c>MountTreeView</c> arm and the
    /// <see cref="V1Protocol.TreeChildren{TElement,TControl}"/> strategy.
    /// In node mode the template's DataContext is <c>TreeViewNode</c>, so
    /// <c>{Binding Content.Content}</c> resolves <c>TreeViewNode.Content</c>
    /// (a <c>TreeViewNodeData</c>) → its <c>Content</c> (the display string).
    /// </summary>
    internal static readonly Lazy<DataTemplate> TreeViewTextItemTemplate = new(() =>
        (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock Text='{Binding Content.Content}'/>" +
            "</DataTemplate>"));

    /// <summary>
    /// Reconciles one child slot: brings <paramref name="existingControl"/>, the control mounted
    /// for <paramref name="oldElement"/>, up to date with <paramref name="newElement"/>, and returns
    /// the control the slot should now hold.
    /// </summary>
    /// <remarks>
    /// When <paramref name="newElement"/> has the element type and key of
    /// <paramref name="oldElement"/>, the control is patched in place and returned unchanged,
    /// unless the update had to build a new control. In every other case the old control is
    /// unmounted before the new one is returned: a changed element type or key, an update that
    /// built a new control, or a null or empty <paramref name="newElement"/>, which returns null.
    /// Without an <paramref name="existingControl"/> the new element is mounted. Put the result in
    /// the slot when it differs from <paramref name="existingControl"/>. A custom control updates
    /// the child elements it hosts with this method; in an <c>IElementHandler</c>,
    /// <c>UpdateContext.ReconcileChild</c> does the same.
    /// </remarks>
    // <snippet:reconciler-entry>
    public UIElement? Reconcile(
        Element? oldElement,
        Element? newElement,
        UIElement? existingControl,
        Action requestRerender)
    {
        // A top-level pass is an outermost Reactor frame for render-error propagation
        // (issue #1291); see EnterFrame. Nested passes and mounts just count.
        using var entryFrame = EnterFrame(Diagnostics.ReactorPerfTrace.ReconcileEntries.Reconcile, newElement);
        // Declared before the reconcile work so it is disposed after it: validation changes raised by mount,
        // update, or unmount are announced only once the whole pass has finished.
        using var validationScope = Controls.Validation.ValidationRenderScope.BeginReconcile();
        ReferenceDirtySet.BeginCommit();
        try
        {
        // Trace only top-level reconcile passes (depth == 0) to avoid flooding
        // the provider with per-subtree entries; nested Reconcile() calls during
        // the same pass don't emit their own start/stop. Gate the depth counter
        // and Start emit on IsEnabled so the disabled path pays nothing extra.
        // `traceEnabled` and `emitTrace` are tracked separately: every enabled
        // call must decrement the depth it incremented, but only the outermost
        // one emits. Decrementing on `emitTrace` alone would leak the counter
        // once a pass contained a nested reconcile, permanently suppressing
        // every later top-level trace.
        // <snippet:reconcile-trace-start>
        bool traceEnabled = Diagnostics.ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            Diagnostics.ReactorEventSource.Keywords.Reconcile);
        bool emitTrace = traceEnabled && _reconcileTraceDepth++ == 0;
        if (emitTrace)
        {
            Diagnostics.ReactorEventSource.Log.ReconcileStart(
                newElement?.GetType().Name ?? "null");
        }
        // </snippet:reconcile-trace-start>
        if (_debugReconcileDepth++ == 0)
        {
            DebugElementsDiffed = 0;
            DebugElementsSkipped = 0;
            DebugUIElementsCreated = 0;
            DebugUIElementsModified = 0;
            // Drop destination references a previous pass queued but never flushed. Every
            // shipped host flushes at the end of each render, but a pass that threw
            // mid-reconcile — or a Reconcile() caller that never flushes (tests,
            // embedders) — must not accumulate strong UIElement refs. A list clear, so
            // nothing here can throw and strand the depth counter incremented just above.
            _pendingConnectedAnimationStarts.Clear();
            _preparedConnectedAnimationKeys.Clear();
            if (ReactorFeatureFlags.HighlightReconcileChanges)
            {
                (_highlightMounted ??= new()).Clear();
                (_highlightModified ??= new()).Clear();
            }
            // Consume the hot-reload and resource-refresh signals exactly once per
            // top-level pass so every component re-runs Render() even when props/deps
            // are unchanged (and, for a resource refresh, no element is skipped).
            bool hostPass = _realizationThemeDepth == 0;
            var theme = hostPass ? InheritedRequestedTheme(ThemeAnchor) : _realizationTheme;
            _resourceRefreshActive = hostPass
                ? _resourceRefreshArmed || theme != _renderedHostTheme
                : _realizationResourceRefresh;
            if (hostPass)
                _resourceRefreshArmed = false;
            _forceFullRenderActive = ForceFullRenderPending || _resourceRefreshActive;
            ForceFullRenderPending = false;
            _hotReloadRetryActive = HotReloadRetryPending;
            HotReloadRetryPending = false;
            _ambientRequestedTheme = theme;

            // Build the dirty-ancestor path. For every component node
            // whose SelfTriggered is true, walk up the realized visual
            // tree and add each ancestor control. Consumed by Update's
            // shallow-equality short-circuit so the walk can reach the
            // self-triggered descendant even when its ancestor element
            // records are structurally unchanged.
            PopulateDirtyAncestorPath();
        }
        try {
        try
        {
            UIElement? result;
            if (newElement is null or EmptyElement)
            {
                if (existingControl is not null)
                    Unmount(existingControl);
                result = null;
            }
            else if (oldElement is null or EmptyElement || existingControl is null)
                result = Mount(newElement, requestRerender);
            else
                result = ReconcileImperative(oldElement, newElement, existingControl, requestRerender);
            if (_debugReconcileDepth == 1 && _realizationThemeDepth == 0)
                _renderedHostTheme = _ambientRequestedTheme;
            return result;
            // </snippet:reconciler-entry>
        }
        finally
        {
            // <snippet:reconcile-trace-stop>
            if (traceEnabled)
            {
                _reconcileTraceDepth--;
                if (emitTrace)
                {
                    Diagnostics.ReactorEventSource.Log.ReconcileStop(
                        DebugElementsDiffed, DebugElementsSkipped,
                        DebugUIElementsCreated, DebugUIElementsModified);
                }
            }
        // </snippet:reconcile-trace-stop>
        }
        } finally
        {
            if (--_debugReconcileDepth == 0)
            {
                _forceFullRenderActive = false;
                _resourceRefreshActive = false;
                _hotReloadRetryActive = false;
                _ambientRequestedTheme = ElementTheme.Default;
                _dirtyAncestorPath?.Clear();
                _dirtyPathChildren?.Clear();
            }
        }
        }
        finally
        {
            ReferenceDirtySet.EndCommitAndFlush();
        }
    }

    private void PopulateDirtyAncestorPath()
    {
        // Hot path — most renders have zero self-triggered nodes (the
        // pass was triggered by a prop change higher up). Avoid the
        // HashSet allocation entirely until we find one.
        HashSet<UIElement>? set = null;
        Dictionary<UIElement, object>? edges = null;
        foreach (var (control, node) in _componentNodes)
        {
            if (!node.SelfTriggered) continue;
            set ??= _dirtyAncestorPath ?? new HashSet<UIElement>();
            edges ??= _dirtyPathChildren ?? new Dictionary<UIElement, object>();
            // Add the control itself first — Update on the wrapper
            // element that owns this control needs to bypass too so it
            // reaches the Component's UpdateComponent path.
            set.Add(control);
            var child = control;
            var cursor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(control) as UIElement;
            while (cursor is not null)
            {
                // Record the edge before the early-out: a second self-triggered node joining
                // an already-walked path still enters it through a child of its own.
                AddDirtyPathChild(edges, cursor, child);
                if (!set.Add(cursor)) break; // already on a previously-walked path
                child = cursor;
                cursor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cursor) as UIElement;
            }
        }
        _dirtyAncestorPath = set;
        _dirtyPathChildren = edges;
    }

    // Tracks top-level Reconcile() entries so trace start/stop only fires once
    // per pass. Only mutated when the Reconcile keyword is enabled.
    private int _reconcileTraceDepth;

    // Test-only accessor (InternalsVisibleTo Reactor.Tests / Reactor.AppTests.Host).
    // The invariant is that this returns to 0 after every top-level pass; a
    // non-zero value between passes means a nested Reconcile incremented
    // without a matching decrement, which silently suppresses all later spans.
    internal int ReconcileTraceDepthForTests => _reconcileTraceDepth;

    /// <summary>
    /// Starts the <c>Microsoft-UI-Reactor-Perf</c> ComponentRender span for a component node
    /// when its keyword is on (an inactive span otherwise). Disposed at the end of the mount or
    /// update, so the span covers Render(), the effects flush and the subtree's reconcile — the
    /// component's inclusive cost, including the WinUI controls its subtree creates.
    /// </summary>
    private static Diagnostics.ReactorPerfTrace.ComponentSpan BeginPerfComponentSpan(
        ComponentNode node, Element element, string reason, bool memoMiss = false)
    {
        if (!Diagnostics.ReactorPerfTrace.ComponentEnabled) return default;
        return Diagnostics.ReactorPerfTrace.ComponentSpan.Begin(
            (ulong)node.EnsureDiagnosticId(), PerfComponentName(node, element), reason, element.CallSite, memoMiss);
    }

    /// <summary>
    /// A component node's name on the <c>Microsoft-UI-Reactor-Perf</c> spans: a class
    /// component's type name, or a function / memo component's render-function name
    /// (<see cref="Diagnostics.ComponentNames.ForRenderFunction(Element)"/>). The latter reads
    /// <c>Delegate.Method</c>, so it is resolved on the component's first traced render and
    /// kept for its lifetime, like its componentId.
    /// </summary>
    internal static string PerfComponentName(ComponentNode node, Element? element)
        => node.Component is null && element is FuncElement or MemoElement
            ? node.PerfName ??= Diagnostics.ComponentNames.ForRenderFunction(element)
            : Diagnostics.ComponentNames.For(node.Component, element);

    /// <summary>
    /// Emits <c>ReactorEventSource.ComponentRendered</c> for a component node and keeps
    /// the id → wrapper registry current. Only called when the event is enabled. A null
    /// <paramref name="wrapper"/> reports the render without touching the registry
    /// (a render that threw into an <c>ErrorBoundary</c>, whose wrapper is discarded or
    /// replaced by the boundary's fallback).
    /// </summary>
    private static void EmitComponentRendered(
        ComponentNode node, UIElement? wrapper, Element element, string reason, long startTimestamp)
    {
        long id = node.EnsureDiagnosticId();
        // Rechecks the live gate: the decision to trace was made before Render() ran, and a
        // listener can dispose itself from an earlier event's callback (for instance a
        // hot-reload hook-order retry's aborted attempt). Nothing is mapped while it is off.
        if (wrapper is not null && Diagnostics.ComponentRenderTrace.IsEnabled)
            Diagnostics.ComponentRenderControls.Registry.Track(id, wrapper, mapControlToId: true);
        Diagnostics.ReactorEventSource.Log.ComponentRendered(
            Diagnostics.ComponentNames.For(node.Component, element),
            id,
            reason,
            Diagnostics.ComponentRenderTrace.ElapsedMicroseconds(startTimestamp));
    }

    /// <summary>
    /// Maps a freshly mounted component's id to its wrapper (event enabled only). If an
    /// enclosing ErrorBoundary later discards the subtree, its rollback (unmount of completed
    /// subtrees, or the boundary mount journal) forgets the mapping again. Rechecks the live
    /// gate: a listener can be disabled from the event's own callback, after the mount
    /// decided to trace, and nothing is mapped while it is off.
    /// </summary>
    private static void TrackMountedComponent(ComponentNode node, UIElement wrapper)
    {
        if (Diagnostics.ComponentRenderTrace.IsEnabled)
            Diagnostics.ComponentRenderControls.Registry.Track(node.DiagnosticId, wrapper, mapControlToId: true);
    }

    /// <summary>
    /// <c>ReactorEventSource.RenderError</c> for a component whose Render() threw and was
    /// replaced by the error fallback. Shared by the mount and update paths so both name
    /// the component the same way (<see cref="Diagnostics.ComponentNames"/>). Only the
    /// component name and exception type are written: the sink redacts the message
    /// (TASK-064), so <paramref name="ex"/>'s virtual <c>Message</c> is never read here —
    /// an app exception whose <c>Message</c> override throws must not replace the original
    /// exception on this error path (the ErrorBoundary arms rethrow it unchanged).
    /// </summary>
    internal static void EmitRenderError(string componentName, Exception ex)
    {
        if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Error,
                Diagnostics.ReactorEventSource.Keywords.Errors))
        {
            Diagnostics.ReactorEventSource.Log.RenderError(componentName, ex.GetType().Name, string.Empty);
        }
    }

    /// <summary>
    /// <c>ReactorEventSource.CommitError</c> for a host-level failure outside any component's
    /// render or effects (<paramref name="phase"/> is <see cref="RenderErrorSource.Reconcile"/>
    /// or <see cref="RenderErrorSource.Cleanup"/>) that replaced the host's content with the
    /// error fallback. Names the host's root (<c>FuncElement</c> for a render-function root)
    /// and the exception type only — the message is never read, as for
    /// <see cref="EmitRenderError"/>.
    /// </summary>
    internal static void EmitCommitError(RenderErrorSource phase, Component? root, Exception ex)
    {
        if (Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Error,
                Diagnostics.ReactorEventSource.Keywords.Errors))
        {
            Diagnostics.ReactorEventSource.Log.CommitError(
                phase switch
                {
                    RenderErrorSource.Reconcile => nameof(RenderErrorSource.Reconcile),
                    RenderErrorSource.Cleanup => nameof(RenderErrorSource.Cleanup),
                    _ => phase.ToString(),
                },
                root is not null ? Diagnostics.ComponentNames.For(root, element: null) : nameof(FuncElement),
                ex.GetType().Name);
        }
    }

    private static void FlushEffectsTraced(RenderContext ctx, string? componentName)
    {
        // Fast path when the Render keyword is off: no Stopwatch, no event emit.
        if (!Diagnostics.ReactorEventSource.Log.IsEnabled(
                global::System.Diagnostics.Tracing.EventLevel.Informational,
                Diagnostics.ReactorEventSource.Keywords.Render))
        {
            ctx.FlushEffects();
            return;
        }

        var name = componentName ?? string.Empty;
        Diagnostics.ReactorEventSource.Log.EffectsFlushStart(name);
        var start = global::System.Diagnostics.Stopwatch.GetTimestamp();
        try { ctx.FlushEffects(); }
        finally
        {
            var us = (long)((global::System.Diagnostics.Stopwatch.GetTimestamp() - start)
                * 1_000_000.0 / global::System.Diagnostics.Stopwatch.Frequency);
            Diagnostics.ReactorEventSource.Log.EffectsFlushStop(name, us);
        }
    }

    /// <summary>
    /// The original C# imperative reconciliation path.
    /// </summary>
    private UIElement? ReconcileImperative(
        Element oldElement, Element newElement,
        UIElement existingControl, Action requestRerender)
    {
        // Contract: when we return a replacement (either from Update remounting or
        // from a type-change full remount), the caller must place it in the parent
        // collection — e.g. `g.Children[i] = replacement`. WinUI's indexer assignment
        // detaches the old control from its parent as part of the swap, so we must
        // leave the parent collection alone here. The UnmountAndPool path would
        // invoke ElementPool.Return.DetachFromParent, which synchronously removes
        // the old child from the parent's Children collection before the caller's
        // assignment runs; that shifts subsequent sibling indices and corrupts any
        // positional update loop that was iterating over the collection (see #34,
        // where DataGrid row cells were dropped off the end during inline-edit flips).
        //
        // Trade-off: the replaced control is no longer handed back to ElementPool.
        // Pooling is a performance optimization; correctness dominates here. Type
        // flips are uncommon in practice (inline edit transitions, ErrorBoundary
        // remounts) so the lost reuse is minor. A follow-up could reintroduce
        // pool-return by having the caller invoke a post-swap pool hook once the
        // old control is detached, but doing so safely across every Reconcile
        // caller is out of scope for this fix.
        if (CanUpdate(oldElement, newElement))
        {
            // An update that already unmounted the control it replaced (a target-wrapping flyout
            // whose Target changes element type) is not followed by a second unmount.
            var replacement = UpdateSlotChild(oldElement, newElement, existingControl, requestRerender, out var unmountedByUpdate);
            if (replacement is not null && replacement != existingControl && !unmountedByUpdate)
                Unmount(existingControl);
            return replacement ?? existingControl;
        }

        // Hot Reload: an edited component subclass mints a new Type token, so
        // CanUpdate is false above. Migrate the subtree in place (preserving
        // descendant state) instead of unmount/mount. Gated on IsHotReloadLive
        // (MetadataUpdater.IsSupported) so the reflection-bearing path is
        // statically dead under NativeAOT (spec 049 §7/§8).
        if (HotReloadService.IsHotReloadLive
            && TryHotReloadMigrateComponent(oldElement, newElement, existingControl, requestRerender))
            return existingControl;

        Unmount(existingControl);
        return Mount(newElement, requestRerender);
    }

    /// <summary>
    /// Issue #326 (pr-review H1) — adopt a freshly-mounted <paramref name="replacement"/>
    /// into an already-realized, still-parented wrapper so an out-of-band refresh
    /// (<see cref="ElementFactory{T}.RefreshRealizedItems"/>) whose row key changed
    /// (<see cref="CanUpdate"/> == false → <see cref="ReconcileImperative"/> returned a
    /// brand-new control) can reset per-item component state without reparenting.
    ///
    /// <para>The WinUI ItemsRepeater that owns realized rows is NOT a Panel, so its
    /// children cannot be replaced from managed code — there is no Children collection
    /// to assign into the way the normal Reconcile contract
    /// (<c>g.Children[i] = replacement</c>) or the framework's GetElement return-channel
    /// relies on. When both the realized control and the replacement are
    /// component-wrapper Borders (the dominant case: authors put per-item state in a
    /// Component and key it with <c>.WithKey($"{id}:{rev}")</c>), keep the parented
    /// wrapper in place and move the fresh component subtree plus its
    /// <see cref="_componentNodes"/> entry onto it. The discarded replacement wrapper is
    /// left empty for GC.</para>
    ///
    /// <para>Returns false when the shapes don't permit an in-place adopt (e.g. a root
    /// TYPE change where neither side is a component wrapper); the caller then falls
    /// back to tracking-only reconciliation.</para>
    /// </summary>
    internal bool TryAdoptRealizedReplacement(UIElement realized, UIElement replacement)
    {
        if (realized is not Border realizedWrapper || replacement is not Border replacementWrapper)
            return false;
        if (!_componentNodes.TryGetValue(replacement, out var freshNode))
            return false;

        // Migrate the fresh component node onto the still-parented wrapper. The old
        // node keyed on `realized` was already torn down + removed by the Unmount
        // inside ReconcileImperative; overwrite defensively regardless.
        _componentNodes.Remove(replacement);
        _componentNodes[realized] = freshNode;
        // Registry bookkeeping: while ComponentRendered is enabled the id moves onto the
        // realized wrapper. If the listener was disabled (possibly by the fresh mount's own
        // event callback), drop the mapping the mount gave the soon-discarded replacement
        // wrapper; the next traced render of this node re-tracks its id onto `realized`.
        if (freshNode.DiagnosticId != 0)
        {
            if (Diagnostics.ComponentRenderTrace.IsEnabled)
                Diagnostics.ComponentRenderControls.Registry.Track(freshNode.DiagnosticId, realized, mapControlToId: true);
            else
                Diagnostics.ComponentRenderControls.Registry.Forget(freshNode.DiagnosticId, replacement);
        }

        // Move the fresh visual subtree into the parented wrapper. Assigning
        // Border.Child detaches it from `replacementWrapper` first (and detaches the
        // old, already-unmounted subtree from `realizedWrapper`), leaving the
        // replacement wrapper empty + unreferenced.
        var freshChild = replacementWrapper.Child;
        replacementWrapper.Child = null;
        realizedWrapper.Child = freshChild;
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Component reconciliation
    // ════════════════════════════════════════════════════════════════════

    private void ReconcileComponent(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        if (!_componentNodes.TryGetValue(control, out var node))
        {
            _logger?.LogWarning("ReconcileComponent: component node not found for control — component will not update");
            return;
        }

        // ── Memo check: skip render if props/context unchanged and not self-triggered ──
        bool selfTriggered = node.SelfTriggered;
        node.SelfTriggered = false;

        // ComponentRendered bookkeeping: what forced or let this render through. Plain
        // locals holding interned constants, so they cost nothing when tracing is off.
        bool forcedRender = _forceFullRenderActive;
        string? memoReason = null;
        // Set when the reason classification below already read the event's gate, so
        // the render path reuses it instead of checking a second time.
        bool? traceEnabled = null;
        // A memo gate (class props / ShouldUpdate, Memo dependencies) was evaluated: a render that
        // follows is a memo miss on the perf provider, a skip a memo hit.
        bool memoGated = false;

        // Hot reload forces every component to re-run Render(); the new method
        // body lives only on the type, not in props/deps, so the memo gate
        // would otherwise skip it.
        if (_forceFullRenderActive)
            selfTriggered = true;

        if (!selfTriggered)
        {
            bool skipRender = false;

            if (node.Component is not null && newEl is ComponentElement newCompEl)
            {
                // Class component memo check
                var oldProps = node.PreviousProps;
                var newProps = newCompEl.Props;

                bool propsChanged;
                if (node.Component is IPropsReceiver)
                {
                    // Component<TProps>: delegate to ShouldUpdate(oldProps, newProps)
                    propsChanged = ShouldUpdateWithProps(node.Component, oldProps, newProps);
                }
                else
                {
                    // Propless Component: delegate to ShouldUpdate()
                    propsChanged = node.Component.ShouldUpdate();
                }

                bool contextChanged = HasConsumedContextChanged(node);
                skipRender = !propsChanged && !contextChanged;
                memoGated = true;
                // Props components: a props change wins, else it was the context.
                // Propless components (ShouldUpdate() true or a context change): the
                // context wins, else nothing could skip it.
                memoReason = node.Component is IPropsReceiver
                    ? propsChanged
                        ? Diagnostics.ComponentRenderTrace.Reasons.Props
                        : Diagnostics.ComponentRenderTrace.Reasons.Context
                    : contextChanged
                        ? Diagnostics.ComponentRenderTrace.Reasons.Context
                        : Diagnostics.ComponentRenderTrace.Reasons.Parent;
            }
            else if (node.Context is not null && newEl is MemoElement newMemo)
            {
                // MemoElement memo check
                var oldDeps = node.MemoDependencies;
                var newDeps = newMemo.Dependencies;
                bool depsChanged = oldDeps is null && newDeps is null
                    ? false // both null = render once, never re-render from parent
                    : oldDeps is null || newDeps is null || !RenderContext.DepsEqual(oldDeps, newDeps);
                bool contextChanged = HasConsumedContextChanged(node);
                skipRender = !depsChanged && !contextChanged;
                memoGated = true;
                memoReason = depsChanged
                    ? Diagnostics.ComponentRenderTrace.Reasons.Props
                    : Diagnostics.ComponentRenderTrace.Reasons.Context;
            }
            else if (newEl is FuncElement
                && ((traceEnabled = Diagnostics.ComponentRenderTrace.IsEnabled).Value
                    || Diagnostics.ReactorPerfTrace.ComponentEnabled)
                && HasConsumedContextChanged(node))
            {
                // No gate can skip a function component, so this only refines the
                // reported reason (and is only computed while a render event is traced).
                memoReason = Diagnostics.ComponentRenderTrace.Reasons.Context;
            }

            if (skipRender)
            {
                // A memo hit on the perf provider (the render it saved is not a span).
                if (Diagnostics.ReactorPerfTrace.ComponentEnabled)
                    Diagnostics.ReactorPerfTrace.ComponentSkip((ulong)node.EnsureDiagnosticId(), PerfComponentName(node, newEl));

                // Still update the element reference (modifiers may have changed on the ComponentElement itself)
                node.Element = newEl;

                // Stale-delegate guard (issue #151): even though we skip re-rendering,
                // refresh the live Props on the instance so any handler that reads
                // Props.X at *dispatch* time invokes the CURRENT delegate rather than the
                // one captured when the child last rendered. This is what makes an
                // always-equal callbacks slot (Callbacks<T>) safe: data fields drive the
                // memo decision, but the latest callbacks are still reachable off Props.
                // Mirrors the leaf-level Tag-trampoline refresh (Element.cs HasCallbacks).
                // The skipped data fields are equal by construction, so refreshing them is
                // a no-op for the rendered subtree; only the excluded slots (callbacks)
                // actually change. PreviousProps is kept in sync as the next baseline.
                if (node.Component is IPropsReceiver skipReceiver
                    && newEl is ComponentElement skipCompEl && skipCompEl.Props is not null)
                {
                    skipReceiver.SetProps(skipCompEl.Props);
                    node.PreviousProps = skipCompEl.Props;
                }
                return;
            }
        }

        // ── Render the component ──
        // Perf span: Render(), effects and the subtree reconcile below (closes on every exit).
        using var perfSpan = Diagnostics.ReactorPerfTrace.ComponentEnabled
            ? BeginPerfComponentSpan(node, newEl, Diagnostics.ComponentRenderTrace.ClassifyUpdate(
                forcedRender, HotReloadService.WithinUpdatePass || _hotReloadRetryActive, selfTriggered, memoReason),
                memoMiss: memoGated)
            : default;

        // Pass the component's own wrapped rerender to children so that child state
        // changes propagate SelfTriggered up through all component ancestors.
        var componentRerender = CreateComponentRerender(node, requestRerender);

        // Only compute component name + timestamps when the Render keyword is
        // enabled, so the disabled path avoids the reflection and Stopwatch work.
        bool traceRender = Diagnostics.ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            Diagnostics.ReactorEventSource.Keywords.Render);
        string? componentName = null;
        long renderStart = 0;
        if (traceRender)
        {
            componentName = Diagnostics.ComponentNames.For(node.Component, newEl);
            Diagnostics.ReactorEventSource.Log.ComponentRenderStart(
                componentName, selfTriggered ? "self" : "parent");
            renderStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
        }
        bool traceRendered = traceEnabled ?? Diagnostics.ComponentRenderTrace.IsEnabled;
        long renderedStart = traceRendered ? global::System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // The reason is fixed for this render (its inputs do not change below); computed
        // only while traced. renderReported: the fallback catch already emitted.
        string? renderedReason = traceRendered
            ? Diagnostics.ComponentRenderTrace.ClassifyUpdate(
                forcedRender, HotReloadService.WithinUpdatePass || _hotReloadRetryActive, selfTriggered, memoReason)
            : null;
        bool renderReported = false;

        Element newChildElement;
        // The RenderContext backing whichever branch we render (component or
        // func/memo). Used to recover an in-flight hook-order change during a
        // hot-reload pass: reset this context's hook state and re-render once.
        RenderContext? renderCtx = node.Component?.Context ?? node.Context;
        bool hotReloadRetried = false;
        bool inEffects = false;
        while (true)
        {
            inEffects = false;
            try
            {
                if (node.Component is not null)
                {
                    // Update props before re-rendering so the component sees fresh data
                    if (newEl is ComponentElement compEl && compEl.Props is not null
                        && node.Component is IPropsReceiver receiver)
                    {
                        receiver.SetProps(compEl.Props);
                    }

                    node.Component.Context.BeginRender(componentRerender, _contextScope, Owner);
                    using (ValidationRenderScope.Begin(ReadContext(ValidationContexts.Current)))
                    {
                        newChildElement = ValidationRenderScope.ApplyProvide(node.Component.Render());
                    }
                    inEffects = true;
                    FlushEffectsTraced(node.Component.Context, componentName);
                }
                else if (node.Context is not null && newEl is FuncElement func)
                {
                    node.Context.BeginRender(componentRerender, _contextScope, Owner);
                    using (ValidationRenderScope.Begin(ReadContext(ValidationContexts.Current)))
                    {
                        newChildElement = ValidationRenderScope.ApplyProvide(func.RenderFunc(node.Context));
                    }
                    inEffects = true;
                    FlushEffectsTraced(node.Context, componentName);
                }
                else if (node.Context is not null && newEl is MemoElement memo)
                {
                    node.Context.BeginRender(componentRerender, _contextScope, Owner);
                    using (ValidationRenderScope.Begin(ReadContext(ValidationContexts.Current)))
                    {
                        newChildElement = ValidationRenderScope.ApplyProvide(memo.RenderFunc(node.Context));
                    }
                    inEffects = true;
                    FlushEffectsTraced(node.Context, componentName);
                }
                else
                {
                    if (traceRender)
                        Diagnostics.ReactorEventSource.Log.ComponentRenderStop(componentName!, 0);
                    return;
                }
            }
            // Tree-wide hot-reload hook-order recovery: when an edit reorders or
            // retypes hooks in a *non-root* child, its Render() throws here.
            // Inside a hot-reload pass we reset that child's hook state and
            // re-render once (matching the root recovery in the host), so the
            // edit applies and the subtree's descendant state is preserved,
            // instead of falling through to the error-fallback path below.
            catch (HookOrderException ex) when (!hotReloadRetried
                && renderCtx is not null
                && HotReloadService.WithinUpdatePass)
            {
                // The aborted attempt still ran Render(); report it before logging, so a
                // slow logger is not charged to it.
                if (traceRendered)
                    EmitComponentRendered(node, null, newEl, Diagnostics.ComponentRenderTrace.Reasons.HotReload, renderedStart);
                _logger?.LogWarning(ex,
                    "Hot reload: hook order/type changed in child component — " +
                    "resetting state and re-rendering: {ComponentName}",
                    componentName ?? Diagnostics.ComponentNames.For(node.Component, newEl));
                hotReloadRetried = true;
                renderCtx.ResetForHotReload();
                // Time the retry on its own (it reports itself after the loop), starting
                // after the reset ran the effect cleanups.
                if (traceRendered)
                    renderedStart = global::System.Diagnostics.Stopwatch.GetTimestamp();
                continue;
            }
            // Inside an ErrorBoundary the exception propagates to the boundary, which
            // renders its fallback. Name the failing component here, at the throw site —
            // the boundary itself cannot know which descendant threw — so a listener sees
            // the error even though the app recovered. Rethrown unchanged. No OOM/SO carve-out:
            // the boundary catches every exception, so every one it recovers from is reported.
            // Outside a boundary, OOM/SO skip the fallback arm below and propagate to the
            // host, which also recovers — so they are reported here too. An exception the app
            // declined via RenderError.Propagate() was already reported where it was thrown,
            // so it passes through unreported (issue #1291).
            catch (Exception ex) when ((_errorBoundaryDepth > 0 || ex is OutOfMemoryException or StackOverflowException)
                && !RenderErrorDispatch.IsPropagating(ex))
            {
                // The render still happened: report it, as the trailing arm does for the rest.
                if (traceRendered)
                    EmitComponentRendered(node, null, newEl, renderedReason!, renderedStart);
                EmitRenderError(componentName ?? Diagnostics.ComponentNames.For(node.Component, newEl), ex);
                throw;
            }
            catch (Exception ex) when (_errorBoundaryDepth == 0 && ex is not OutOfMemoryException and not StackOverflowException && !RenderErrorDispatch.IsPropagating(ex))
            {
                // Report before building the fallback: its time is not this render's, and
                // a fallback that throws must not swallow the event.
                if (traceRendered)
                {
                    EmitComponentRendered(node, control, newEl, renderedReason!, renderedStart);
                    renderReported = true;
                }
                var failedName = componentName ?? Diagnostics.ComponentNames.For(node.Component, newEl);
                _logger?.LogError(ex, "Component Render() threw: {ComponentName}", failedName);
                // Before the fallback: the app's handler may call Propagate(), which throws.
                EmitRenderError(failedName, ex);
                newChildElement = BuildInTreeFallback(ex, inEffects, node.Component?.GetType().Name);
            }
            // Every other exception propagates (to an enclosing ErrorBoundary, or a fatal
            // one to the host); the render still happened, so report it and rethrow.
            catch (Exception) when (traceRendered)
            {
                EmitComponentRendered(node, null, newEl, renderedReason!, renderedStart);
                throw;
            }
            break;
        }

        if (traceRender)
        {
            var renderElapsedUs = (long)((global::System.Diagnostics.Stopwatch.GetTimestamp() - renderStart)
                * 1_000_000.0 / global::System.Diagnostics.Stopwatch.Frequency);
            Diagnostics.ReactorEventSource.Log.ComponentRenderStop(componentName!, renderElapsedUs);
        }
        if (traceRendered && !renderReported)
            EmitComponentRendered(node, control, newEl, renderedReason!, renderedStart);

        // Dereference the Border wrapper to get the actual child control.
        // Each component is wrapped in a Border as an identity anchor, so we
        // reconcile the child inside the wrapper, not the wrapper itself.
        var existingChild = (control as Border)?.Child;
        var newControl = global::Microsoft.UI.Reactor.Hosting.ReactorFeatures.DevtoolsSupported && Diagnostics.ReactorSourcePublisher.IsEnabled
            ? ReconcileUnderOwner(node.RenderedElement, newChildElement, existingChild, componentRerender,
                Diagnostics.ReactorSourceFormat.ComponentName(node.Component, newEl))
            : Reconcile(node.RenderedElement, newChildElement, existingChild, componentRerender);
        if (control is Border border)
        {
            if (newControl != existingChild)
                border.Child = newControl; // handles both replacement and null (child removed)
        }

        node.RenderedElement = newChildElement;
        node.Element = newEl;
        // Store current props for next memo comparison
        if (newEl is ComponentElement compEl2)
            node.PreviousProps = compEl2.Props;
        else if (newEl is MemoElement memoEl)
            node.MemoDependencies = memoEl.Dependencies;
    }

    /// <summary>
    /// Creates a rerender callback that marks the component node as self-triggered
    /// before invoking the parent requestRerender, so the memo check is bypassed.
    /// Captures the node directly to avoid accessing _componentNodes from background threads.
    /// </summary>
    private Action CreateComponentRerender(ComponentNode node, Action requestRerender)
    {
        return () =>
        {
            // SECURITY (TASK-059): bound rerender re-entrancy. A component
            // that calls its own requestRerender synchronously from Render()
            // or an effect's cleanup would otherwise blow the stack.
            if (t_rerenderDepth >= MaxRerenderReentrancy)
                throw new InvalidOperationException(
                    "Render loop detected: requestRerender re-entered more than " +
                    $"{MaxRerenderReentrancy} times. Likely cause: setState called " +
                    "synchronously inside Render() or effect cleanup.");

            // SECURITY (TASK-063): if invoked off the UI thread (e.g.,
            // setState(threadSafe:true) firing from a worker, or a worker
            // that owns its own DispatcherQueue), marshal onto the UI
            // dispatcher so we don't race the reconciler and ElementPool from
            // a background thread. That is this reconciler's owning thread
            // (its host's), not the process-wide one: hosts on a second UI
            // thread must render on theirs. A non-null GetForCurrentThread()
            // doesn't imply UI affinity — the only authoritative check is
            // HasThreadAccess on the UI DQ.
            var uiDq = OwningDispatcher ?? Microsoft.UI.Reactor.ReactorApp.UIDispatcher;
            bool onUiThread = false;
            if (uiDq is not null)
            {
                try { onUiThread = uiDq.HasThreadAccess; }
                catch { onUiThread = false; }
            }

            if (!onUiThread && uiDq is not null)
            {
                uiDq.TryEnqueue(() =>
                {
                    node.SelfTriggered = true;
                    InvokeRerenderTracked(requestRerender, node);
                });
                return;
            }

            // Either we're on the UI thread, or no UI DQ has been captured
            // (test/headless host) — run inline.
            node.SelfTriggered = true;
            InvokeRerenderTracked(requestRerender, node);
        };
    }

    private static void InvokeRerenderTracked(Action requestRerender, ComponentNode requester)
    {
        t_rerenderDepth++;
        // The perf RenderPass names the component that asked: the innermost wrapper claims the
        // thread before the request climbs through its ancestors' wrappers to the host.
        try { Diagnostics.RenderPassRequests.InvokeAs(requester, requestRerender); }
        finally { t_rerenderDepth--; }
    }

    /// <summary>
    /// Checks whether any context consumed by a component has changed since the last render.
    /// </summary>
    private bool HasConsumedContextChanged(ComponentNode node)
    {
        var renderCtx = node.Component?.Context ?? node.Context;
        if (renderCtx is null) return false;

        foreach (var ctxHook in renderCtx.ContextHooks)
        {
            var currentValue = _contextScope.Read(ctxHook.Context);
            if (!Equals(currentValue, ctxHook.LastValue))
                return true;
        }
        return false;
    }

    internal bool HasConsumedContextChangedInSubtree(UIElement control)
    {
        if (_componentNodes.TryGetValue(control, out var node)
            && HasConsumedContextChanged(node))
            return true;

        // A virtualized host has no realized consumer for rows it has yet to realize,
        // so its own snapshot stands in for them: if the providers above it changed,
        // decline the skip so the Update path re-snapshots before the next realization.
        if (TryGetContextCapture(control) is { } capture
            && (capture.IsStale(_contextScope) || capture.ThemeGeneration != _themeGeneration))
            return true;

        bool found = false;
        ForEachReactorChildControl(control, child =>
        {
            if (HasConsumedContextChangedInSubtree(child))
            {
                found = true;
                return false;
            }

            return true;
        });

        return found;
    }

    internal bool ShouldDeclineSkip(UIElement control)
        => HasActiveContextValues && HasConsumedContextChangedInSubtree(control);

    /// <summary>
    /// Calls ShouldUpdate(oldProps, newProps) on a Component&lt;TProps&gt; via interface dispatch.
    /// </summary>
    private static bool ShouldUpdateWithProps(Component component, object? oldProps, object? newProps)
    {
        if (component is IPropsComparable comparable)
            return comparable.CompareProps(oldProps, newProps);

        // Fallback: always re-render
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Children reconciliation (keyed LIS + positional)
    // ════════════════════════════════════════════════════════════════════

    private void ReconcileChildren(
        Element[] oldChildren, Element[] newChildren,
        WinUI.Panel panel, Action requestRerender)
        => ReconcileChildrenTraced(oldChildren, newChildren, new PanelChildCollection(panel), requestRerender, panel);

    // Spec 047 §14 — keyed-reconcile entrypoint for the V1 descriptor Panel<>
    // strategy. The descriptor resolves the live collection as a
    // UIElementCollection (not a WinUI.Panel), so this overload wraps that
    // collection directly. Behavior — including the diagnostics ChildReconcile
    // event scope — is identical to the WinUI.Panel-based ReconcileChildren so
    // descriptor panels match the legacy hand-coded Update* methods byte-for-byte.
    internal void ReconcilePanelChildrenInto(
        Element[] oldChildren, Element[] newChildren,
        UIElementCollection collection, Action requestRerender,
        UIElement? parentControl = null)
        => ReconcileChildrenTraced(oldChildren, newChildren, new PanelChildCollection(collection), requestRerender, parentControl);

    private void ReconcileChildrenTraced(
        Element[] oldChildren, Element[] newChildren,
        PanelChildCollection childCollection, Action requestRerender, UIElement? parentControl)
    {
        // Skip the try/finally and event emits when neither provider wants the span.
        bool traceSource = Diagnostics.ReactorEventSource.Log.IsEnabled(
            global::System.Diagnostics.Tracing.EventLevel.Informational,
            Diagnostics.ReactorEventSource.Keywords.Reconcile);
        bool tracePerf = Diagnostics.ReactorPerfTrace.ChildrenEnabled;
        if (!traceSource && !tracePerf)
        {
            if (!_perfTally)
            {
                ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender, parentControl);
                CountPerfMoves(childCollection);
                return;
            }
            // Only the enclosing Reconcile span's edit tally wants this container's counts.
            try { ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender, parentControl); }
            finally { _perfEdits.Add(childCollection); }
            CountPerfMoves(childCollection);
            return;
        }
        if (traceSource)
            Diagnostics.ReactorEventSource.Log.ChildReconcileStart(oldChildren.Length, newChildren.Length);
        if (tracePerf)
            Diagnostics.ReactorPerfTrace.ChildReconcileStart((uint)oldChildren.Length, (uint)newChildren.Length);
        try { ChildReconciler.Reconcile(oldChildren, newChildren, childCollection, this, requestRerender, parentControl); }
        finally
        {
            if (traceSource)
                Diagnostics.ReactorEventSource.Log.ChildReconcileStop();
            // The counts are this container's own collection edits; a nested container's edits
            // are reported by its own (nested) span.
            if (tracePerf)
                Diagnostics.ReactorPerfTrace.ChildReconcileStop(
                    childCollection.InsertCount, childCollection.RemoveCount,
                    childCollection.MoveCount, childCollection.ReplaceCount);
            if (_perfTally)
                _perfEdits.Add(childCollection);
        }
        CountPerfMoves(childCollection);
    }

    // A repositioned child is a change the mount and unmount counts do not see (inserts,
    // removals and replacements mount or unmount a control, which counts it).
    private static void CountPerfMoves(PanelChildCollection childCollection)
    {
        if (childCollection.MoveCount != 0)
            CountPerfChanges(childCollection.MoveCount);
    }

    /// <summary>
    /// Low-level update of one child control, for callers that have already checked the new
    /// element can update it in place. Code outside the framework should call
    /// <see cref="Reconcile"/>, which makes that check and unmounts a control it replaces.
    /// </summary>
    /// <remarks>
    /// Patches <paramref name="control"/> to match <paramref name="newEl"/> and returns null, or
    /// returns a new control when the update had to build one. The caller then unmounts
    /// <paramref name="control"/> with <see cref="UnmountChild"/> and puts the new control in its
    /// slot, in that order, as <see cref="Reconcile"/> does: unmounting reads state from the control
    /// while it is still in the tree, such as a connected-animation snapshot. It doesn't check that
    /// <paramref name="newEl"/> has the element type and key of <paramref name="oldEl"/>: a changed
    /// type throws <c>InvalidCastException</c> for a built-in control. The analyzer rule
    /// <c>REACTOR_LIFECYCLE_003</c> flags calls to this method and offers to switch them to
    /// <see cref="Reconcile"/>.
    /// </remarks>
    public UIElement? UpdateChild(Element oldEl, Element newEl, UIElement control, Action requestRerender)
    {
        return Update(oldEl, newEl, control, requestRerender);
    }

    /// <summary>
    /// <see cref="Update"/> for a hosted child slot (dialog/popup/flyout/tooltip content, tree and
    /// list item containers) whose owner swaps a replacement in. When the update hands back a
    /// different control, the old one is unmounted here (unless the update already did) so its
    /// effects, refs and unmount callbacks don't outlive it. Returns the replacement, or null
    /// when the existing control was kept: a drop-in for <see cref="Update"/> at those sites.
    /// </summary>
    internal UIElement? UpdateHostedChild(Element oldEl, Element newEl, UIElement existing, Action requestRerender)
    {
        var replacement = UpdateSlotChild(oldEl, newEl, existing, requestRerender, out var unmountedByUpdate);
        if (replacement is null || IsSameControl(replacement, existing)) return null;
        if (!unmountedByUpdate) Unmount(existing);
        return replacement;
    }

    /// <summary>
    /// <see cref="UpdateChild"/> for a caller that unmounts a replaced control: reports whether
    /// the update already unmounted it (e.g. a same-key Memo remounting its output during a
    /// resource refresh), so the caller doesn't unmount it twice.
    /// </summary>
    internal UIElement? UpdateChildTracked(Element oldEl, Element newEl, UIElement control, Action requestRerender,
        out bool unmountedByUpdate, int childIndex = -1)
    {
        // The child's new position among its panel's children, for a parent .Stagger() (RefreshStaggerDelay).
        var previousIndex = _updatingChildIndex;
        _updatingChildIndex = childIndex;
        try { return UpdateSlotChild(oldEl, newEl, control, requestRerender, out unmountedByUpdate); }
        finally { _updatingChildIndex = previousIndex; }
    }

    // ════════════════════════════════════════════════════════════════════
    //  CanUpdate
    // ════════════════════════════════════════════════════════════════════

    internal bool CanUpdate(Element oldEl, Element newEl)
    {
        if (oldEl.GetType() != newEl.GetType()) return false;
        if (oldEl.Key != newEl.Key) return false;
        if (oldEl is ComponentElement oldComp && newEl is ComponentElement newComp)
            return oldComp.ComponentType == newComp.ComponentType;
        if (oldEl is XamlHostElement oldHost && newEl is XamlHostElement newHost)
            return oldHost.TypeKey == newHost.TypeKey;
        // Issue #327 — a directly-reconciled KeyedMemoElement (used outside a virtualized
        // ElementFactory, which resolves it to its inner before reconcile) can update in place
        // only when its MemoKey is unchanged: same key ⇒ identical factory output by contract ⇒
        // the existing inner control is already correct (the KeyedMemoElement arm in
        // Reconciler.Update then skips). A changed key means different content, so return false to
        // take the standard replace path (unmount + fresh mount of the new factory output) rather
        // than re-invoking the OLD factory at update time to reconstruct the old tree.
        if (oldEl is KeyedMemoElement oldKm && newEl is KeyedMemoElement newKm)
            return Equals(oldKm.MemoKey, newKm.MemoKey);
        // MemoElement can always update to MemoElement (same type check above handles it)
        return true;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Hot Reload — subtree migration on component type identity change
    //  (spec 049 §7 / Phase 3)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// When a <see cref="Component"/> subclass is edited under Hot Reload the
    /// runtime mints a <em>new</em> <see cref="Type"/> token, so
    /// <see cref="CanUpdate"/> returns false and the steady-state path would
    /// unmount the whole subtree — discarding every descendant's
    /// <c>UseState</c>. This migrates the node in place instead: it constructs
    /// the post-edit component via the element's factory closure (so
    /// parameterized constructors work — spec §7 Q2), copies surviving instance
    /// fields old → new via <see cref="Microsoft.UI.Reactor.Hosting.ReactorHotReloadCopier"/>,
    /// transfers the live <see cref="RenderContext"/> (hooks + cleanups stay
    /// alive), swaps <see cref="ComponentNode.Component"/>, and re-renders
    /// through the normal <see cref="ReconcileComponent"/> path so prop-set,
    /// Phase-1 hook-order recovery, child reconciliation, tracing and node
    /// bookkeeping all run exactly as a regular update would. The wrapper
    /// <see cref="UIElement"/> is preserved, so <see cref="FrameworkElement"/>
    /// identity (and any running compositor animations on descendant visuals)
    /// survives the edit.
    ///
    /// <para>Returns false — and the caller falls through to unmount/mount —
    /// when the elements are not both class <see cref="ComponentElement"/>s of
    /// the same <c>FullName</c>+assembly, when the keys differ, when the node is
    /// not found, or when the post-edit instance cannot be constructed (no
    /// factory and no parameterless ctor). Only ever reached inside a hot-reload
    /// pass, so it is statically dead under NativeAOT (spec §8).</para>
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reachable only via HotReloadService.IsHotReloadLive; dead under NativeAOT (spec 049 §8).")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Reachable only via HotReloadService.IsHotReloadLive; dead under NativeAOT (spec 049 §8).")]
    internal bool TryHotReloadMigrateComponent(Element oldEl, Element newEl, UIElement existingControl, Action requestRerender)
    {
        // Only a class ComponentElement whose Type token changed but whose
        // logical identity (FullName + assembly) and key are unchanged is an
        // edit we migrate. Anything else (different component, different key,
        // a base-class swap that changes the name) is a genuine identity
        // change the author asked to discard.
        if (oldEl is not ComponentElement oldComp || newEl is not ComponentElement newComp)
            return false;
        if (oldEl.Key != newEl.Key) return false;

        Type oldType = oldComp.ComponentType;
        Type newType = newComp.ComponentType;
        if (oldType.FullName is null || oldType.FullName != newType.FullName) return false;
        if (oldType.Assembly.GetName().Name != newType.Assembly.GetName().Name) return false;

        if (!_componentNodes.TryGetValue(existingControl, out var node) || node.Component is null)
            return false;

        Component newComponent;
        try
        {
            newComponent = newComp.CreateInstance();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Factory closure threw or no usable constructor — let the caller
            // unmount/mount so the edit still takes effect (state is lost). A
            // clean recreate is strictly better than migrating onto a
            // half-built instance. Fatal exceptions deliberately propagate.
            return false;
        }

        Component oldComponent = node.Component;

        // Copy surviving instance fields old → new (added fields keep their
        // default, removed fields drop, native/visual handles are block-listed).
        // Contain reflection faults on pathological user fields so a copy failure
        // falls back to unmount/mount rather than aborting the hot-reload pass —
        // a clean recreate beats proceeding with a partially-migrated instance.
        try
        {
            Microsoft.UI.Reactor.Hosting.ReactorHotReloadCopier.TryMigrate(
                oldComponent, newComponent,
                new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Field copy faulted midway: discard the partially-migrated instance
            // and recreate the component cleanly. Fatal exceptions propagate.
            return false;
        }

        // Transfer the live render context explicitly (deterministic regardless
        // of copier field-copy order) so hooks and pending cleanups survive.
        // KNOWN LIMITATION (spec 049 §7): UseEffect cleanup closures captured the
        // OLD instance; effects whose deps are unchanged do not re-run, so their
        // cleanups still reference the pre-edit instance until deps change or the
        // subtree unmounts. This matches React's component-identity semantics
        // (preserved state ⇒ effects don't re-fire); most effects close over
        // state values rather than `this`, so this is benign in practice.
        newComponent.Context = oldComponent.Context;

        // Swap the instance and force a render past the memo gate, then delegate
        // to the normal component reconcile path. ReconcileComponent re-sets
        // props from newEl, applies Phase-1 hook-order recovery if the edit
        // changed the hook shape, reconciles the child element against the
        // preserved wrapper, and updates node.Element / node.PreviousProps.
        node.Component = newComponent;
        node.SelfTriggered = true;
        ReconcileComponent(oldEl, newEl, existingControl, requestRerender);

        // Spec 010 — the wrapper is PRESERVED across the migration (that is the whole
        // point), so its back-pointer still names the pre-edit element. Refresh it, or
        // the reported location survives an edit that moved the call site — which is
        // exactly the staleness this route exists to avoid. ReconcileComponent is called
        // directly here rather than through UpdateComponent, so it does not inherit that
        // path's refresh.
        if (existingControl is FrameworkElement migratedFe)
            SetElementTagIfNeeded(migratedFe, newEl);
        if (global::Microsoft.UI.Reactor.Hosting.ReactorFeatures.DevtoolsSupported && Diagnostics.ReactorSourcePublisher.IsEnabled)
            PublishSource(existingControl, newEl);
        Diagnostics.ReactorEventSource.Log.HotReloadStateMigrated(newType.FullName ?? newType.Name);
        return true;
    }

    /// <summary>
    /// Spec 047 §14 Phase 1 — child-slot reconciliation used by the V1
    /// <c>ChildrenStrategy</c> dispatch (SingleContent, NamedSlots). Mirrors
    /// legacy <c>ReconcileChild</c> semantics: structural <c>Update</c> when
    /// <see cref="CanUpdate"/> matches, fresh <c>Mount</c> otherwise, and
    /// <c>Unmount</c> when the slot is being cleared. Returns the
    /// <see cref="UIElement"/> the caller should write back into the slot
    /// (or null when the slot should be cleared).
    ///
    /// <para>When the update returns a new control for the slot, the control it
    /// replaced is unmounted here, as a panel's <c>ChildReconciler</c> and the
    /// component path (<see cref="ReconcileImperative"/>) do. The caller only swaps
    /// the slot, so without this the old subtree's effect cleanups, refs,
    /// <c>.OnUnmount</c> actions and handler unmounts never ran. An update that
    /// returns the control it was handed patched it in place, and an update that
    /// already unmounted the control it replaced is not followed by a second
    /// unmount.</para>
    ///
    /// <para>This exists because the naive replace in early Phase 1
    /// (<c>MountChild</c> + <c>SetChild</c> without comparing old vs new)
    /// destroys descendant state slots on every parent re-render — see the
    /// DynDock / KLR_FlexColumn regressions in
    /// <c>docs/specs/tasks/047-extensible-control-model-phase1-implementation.md</c>
    /// (Phase 1 known defects).</para>
    /// </summary>
    internal UIElement? ReconcileV1Child(Element? oldChild, Element? newChild, UIElement? existing, Action requestRerender)
    {
        if (newChild is null)
        {
            if (existing is not null) Unmount(existing);
            return null;
        }
        if (oldChild is not null && existing is not null && CanUpdate(oldChild, newChild))
        {
            var replacement = UpdateSlotChild(oldChild, newChild, existing, requestRerender, out var unmountedByUpdate);
            if (replacement is null || IsSameControl(replacement, existing))
                return existing;
            if (!unmountedByUpdate)
                Unmount(existing);
            return replacement;
        }
        // Hot Reload component-identity migration (spec 049 §7) — preserve the
        // child subtree's state across an edit instead of unmount/mount.
        if (oldChild is not null && existing is not null
            && HotReloadService.IsHotReloadLive
            && TryHotReloadMigrateComponent(oldChild, newChild, existing, requestRerender))
            return existing;
        if (existing is not null) Unmount(existing);
        return Mount(newChild, requestRerender);
    }

    // The controls the slot updates now running are working on, outermost first, and whether each
    // has been unmounted while its update ran. ReconcileV1Child and ReconcileImperative (behind the
    // public Reconcile) run their updates in these frames. A target-wrapping flyout whose Target
    // changes element type unmounts the old Target itself before it returns the new one
    // (OverlayLifecycle.UpdateFlyoutElement and its MenuFlyout / CommandBarFlyout twins), and a
    // second unmount would run the old subtree's handler and registered unmount callbacks again.
    // Slot updates nest, and a handler deep inside one can unmount a control an outer slot is
    // updating, so every frame watching the unmounted control is marked, not just the innermost.
    private UIElement?[] _slotUpdateControls = Array.Empty<UIElement?>();
    private bool[] _slotUpdateUnmounted = Array.Empty<bool>();
    private int _slotUpdateDepth;

    private UIElement? UpdateSlotChild(Element oldChild, Element newChild, UIElement existing,
        Action requestRerender, out bool unmountedByUpdate)
    {
        // Every access goes through the fields: a nested frame may grow the arrays.
        int frame = _slotUpdateDepth;
        if (frame == _slotUpdateControls.Length)
        {
            int size = frame == 0 ? 8 : frame * 2;
            Array.Resize(ref _slotUpdateControls, size);
            Array.Resize(ref _slotUpdateUnmounted, size);
        }
        _slotUpdateControls[frame] = existing;
        _slotUpdateUnmounted[frame] = false;
        _slotUpdateDepth = frame + 1;
        try
        {
            var replacement = Update(oldChild, newChild, existing, requestRerender);
            unmountedByUpdate = _slotUpdateUnmounted[frame];
            return replacement;
        }
        finally
        {
            _slotUpdateControls[frame] = null;
            _slotUpdateDepth = frame;
        }
    }

    // Matched by reference, not IsSameControl. A frame holds the control it watches, and while a
    // managed wrapper is alive CsWinRT hands back that same wrapper for its native control, so every
    // path that reaches the control to unmount it (the reference an update was handed, or a walk of
    // the visual tree) arrives with this reference. IsSameControl's ReactorState compare is for an
    // update's result, which user code chooses. Here it would add native reads to every unmount
    // inside a slot update, which is nearly every unmount, and attach a ReactorState to a watched
    // control that has none.
    private void NoteUnmountDuringSlotUpdate(UIElement control)
    {
        for (int i = 0; i < _slotUpdateDepth; i++)
        {
            if (ReferenceEquals(_slotUpdateControls[i], control))
                _slotUpdateUnmounted[i] = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Shared helpers (used by Mount + Update)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Spec 047 §14 Phase 1 (1.3) — promoted from internal. Runs each setter
    /// inside the §8.2 echo-suppression scope so user-authored
    /// <c>.Set(c =&gt; c.IsOn = true)</c> writes don't echo through the
    /// engine-wired OnXChanged callback. Provisional API;
    /// see <c>REACTOR_V1_PREVIEW</c>.
    /// </summary>
    public static void ApplySetters<T>(Action<T>[] setters, T control) where T : class
    {
        if (setters.Length == 0) return;
        // §8.2 — run setters inside the echo-suppression scope so that a
        // user-authored `.Set(c => c.IsOn = true)` write does not echo back
        // through the OnXChanged callback the engine already wired. Only
        // enters scope when ReactorState already exists; value-bearing mounts
        // call SetElementTag before ApplySetters, so the state is present for
        // every control that can fire a suppressed change event. Bare leaves
        // (TextBlock, RichTextBlock) skip the scope and pay no allocation.
        ReactorState? state =
            control is FrameworkElement fe
            && fe.GetValue(ReactorAttached.StateProperty) is ReactorState rs
                ? rs : null;
        if (state is not null) state.EchoSuppressScopeDepth++;
        // A setter's opacity write replaces a pending opacity keyframe, as a modifier's does (#1669).
        var opacityProbe = control is UIElement probed ? new OpacityRestartProbe(probed) : default;
        try
        {
            foreach (var setter in setters) setter(control);
            opacityProbe.Complete();
        }
        // Spec 066 §9 row 1: name the modifier when WinUI rejects a facade write after the
        // handoff. The filter returns false, so the exception is unchanged.
        catch (UnauthorizedAccessException) when (FacadeConflictDiagnostic.ReportRejectedSetter(control))
        {
            throw;
        }
        finally
        {
            if (state is not null) state.EchoSuppressScopeDepth--;
        }
    }

<<<<<<< HEAD
    // WPF/WinUI auto-populate UIA Name from a control's string Content through the
    // automation peer, but AutomationProperties.GetName on the raw element only
    // returns an explicitly-set attached property. UIA clients that read the attached
    // property directly (our own tree walker, Appium's AutomationName getter, some
    // screen readers that probe before invoking the peer) see an empty string when
    // the author never set .AutomationName(). Mirroring the caption into the
    // attached property at mount makes both lookup paths agree, so
    // click { selector: "[name='+ 1']" } and a screen reader saying "+ 1" both work
    // without the author having to say so twice. Skips when the author already set
    // an AutomationName via modifier or setter — explicit always wins.
    /// <summary>
    /// Spec 047 §14 Phase 1 (1.3) — promoted from internal. Mirrors a control's
    /// caption into <c>AutomationProperties.Name</c> when the author has not set
    /// one explicitly, so UIA clients that read the attached property directly
    /// (not via the peer) still find the name. Provisional API;
    /// see <c>REACTOR_V1_PREVIEW</c>.
    /// </summary>
    public static void ApplyDefaultAutomationName(FrameworkElement fe, string? caption)
    {
        if (fe is null) return;
        if (DefaultAutomationNameFromCaption(caption) is not { } trimmed) return;
        var existing = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe);
        if (!string.IsNullOrEmpty(existing)) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, trimmed);
    }

    /// <summary>
    /// The name <see cref="ApplyDefaultAutomationName"/> / <see cref="UpdateDefaultAutomationName"/>
    /// write for <paramref name="caption"/> (null when they write nothing). Shared with
    /// <c>ReactorDiagnostics.GetAppliedProperties</c> so the reported default cannot drift
    /// from the one applied.
    /// </summary>
    internal static string? DefaultAutomationNameFromCaption(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return null;
        return caption.Length > 100 ? caption.Substring(0, 100) : caption;
    }

    // Update variant: a label change ("+ 1" → "+ 2") should update UIA Name as
    // long as the author didn't override it. We can't distinguish "author set
    // it to the previous caption" from "our default set it" without tracking
    // provenance, so the rule is: overwrite when the current value is empty or
    // equals the previous caption — any other value means the author intervened
    // (via modifier or setter) and we leave it alone.
    /// <summary>
    /// Spec 047 §14 Phase 1 (1.3) — promoted from internal. Update-time pair
    /// for <see cref="ApplyDefaultAutomationName"/>; overwrites the
    /// automation Name only when it matches the previous caption (i.e. the
    /// author hasn't intervened). Provisional API; see <c>REACTOR_V1_PREVIEW</c>.
    /// </summary>
    public static void UpdateDefaultAutomationName(FrameworkElement fe, string? oldCaption, string? newCaption)
    {
        if (fe is null) return;
        if (DefaultAutomationNameFromCaption(newCaption) is not { } trimmed) return;
        var current = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe);
        if (!MayReplaceDefaultAutomationName(current, oldCaption)) return;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, trimmed);
    }

    /// <summary>
    /// True when the live name is Reactor's own default for <paramref name="oldCaption"/> (or
    /// empty), so a caption change may replace it. The default for a long caption is its
    /// truncated form (<see cref="DefaultAutomationNameFromCaption"/>), so both the full and the
    /// truncated caption count as Reactor's; any other value is the author's and is kept.
    /// </summary>
    internal static bool MayReplaceDefaultAutomationName(string? current, string? oldCaption)
    {
        if (string.IsNullOrEmpty(current)) return true;
        if (oldCaption is null) return false;
        return string.Equals(current, oldCaption, StringComparison.Ordinal)
            || string.Equals(current, DefaultAutomationNameFromCaption(oldCaption), StringComparison.Ordinal);
    }

    internal static string? ExtractElementCaption(Element? element) => element switch
    {
        TextBlockElement te => te.Content,
        null => null,
        _ => null,
    };

    // Resolves the caption-bearing string for controls whose visible text is
    // implicit (Button's Label, CheckBox's Label, TextBlock's Content, …). Returns
    // null for elements without a natural caption so ApplyDefaultAutomationName
    // leaves them alone. Kept alongside the helper so adding new caption-bearing
    // controls is one place to change.
    internal static string? ResolveCaptionForElement(Element element) => element switch
    {
        TextBlockElement te => te.Content,
        ButtonElement be => be.Label ?? ExtractElementCaption(be.ContentElement),
        HyperlinkButtonElement hle => hle.Content,
        RepeatButtonElement rbe => rbe.Label,
        ToggleButtonElement tbe => tbe.Label,
        DropDownButtonElement dde => dde.Label,
        SplitButtonElement sbe => sbe.Label,
        ToggleSplitButtonElement tsbe => tsbe.Label,
        CheckBoxElement cbe => cbe.Label,
        RadioButtonElement rbe => rbe.Label,
        ToggleSwitchElement tse => tse.Header as string ?? tse.OnContent ?? tse.OffContent,
        TextBoxElement tfe => tfe.Header as string ?? tfe.PlaceholderText,
        _ => null,
    };

    internal static void ApplyTransitions(UIElement uie, ImplicitTransitions? implicitT, ThemeTransitions? themeT)
    {
        if (implicitT is not null)
        {
            try
            {
                if (implicitT.Opacity is not null)
                    uie.OpacityTransition = implicitT.Opacity;
                if (implicitT.Rotation is not null)
                    uie.RotationTransition = implicitT.Rotation;
                if (implicitT.Scale is not null)
                    uie.ScaleTransition = implicitT.Scale;
                if (implicitT.Translation is not null)
                    uie.TranslationTransition = implicitT.Translation;
            }
            catch (UnauthorizedAccessException)
            {
                // WinUI blocks XAML implicit transition APIs once GetElementVisual()
                // has been called on the element (e.g., from .Animate(), enter transitions,
                // or a previous owner via the pool). Fall back to compositor implicit
                // animations which always work.
                ApplyTransitionsViaCompositor(uie, implicitT);
            }
            if (implicitT.Background is not null)
            {
                switch (uie)
                {
                    case WinUI.Grid g: g.BackgroundTransition = implicitT.Background; break;
                    case WinUI.StackPanel sp: sp.BackgroundTransition = implicitT.Background; break;
                    case WinUI.ContentPresenter cp: cp.BackgroundTransition = implicitT.Background; break;
                }
            }
        }

        if (themeT?.Children is { } children)
        {
            var tc = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            foreach (var t in children) tc.Add(t);
            switch (uie)
            {
                case WinUI.StackPanel sp: sp.ChildrenTransitions = tc; break;
                case WinUI.Grid g: g.ChildrenTransitions = tc; break;
                case WinUI.Canvas c: c.ChildrenTransitions = tc; break;
                case WinUI.Border b: b.ChildTransitions = tc; break;
                case WinUI.ContentPresenter cp: cp.ContentTransitions = tc; break;
                case WinUI.ContentControl cc: cc.ContentTransitions = tc; break;
            }
        }

        if (themeT?.ItemContainer is { } itemTransitions)
        {
            var tc = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            foreach (var t in itemTransitions) tc.Add(t);
            if (uie is WinUI.ListViewBase lvb)
                lvb.ItemContainerTransitions = tc;
        }
    }

    /// <summary>
    /// Fallback: applies XAML-style implicit transitions via compositor ImplicitAnimationCollection
    /// when the XAML API is blocked by a prior GetElementVisual() call.
    /// </summary>
    private static void ApplyTransitionsViaCompositor(UIElement uie, ImplicitTransitions implicitT)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        if (!AnimationCapabilities.SupportsImplicitAnimations(compositor)) return; // the properties snap
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();

        if (implicitT.Opacity is not null)
        {
            var dur = implicitT.Opacity.Duration;
            implicitAnimations["Opacity"] = AnimationHelper.CreateScalarImplicitAnimation(
                compositor, "Opacity", new Animation.LinearCurve(dur));
        }
        if (implicitT.Rotation is not null)
        {
            var dur = implicitT.Rotation.Duration;
            implicitAnimations["RotationAngle"] = AnimationHelper.CreateScalarImplicitAnimation(
                compositor, "RotationAngle", new Animation.LinearCurve(dur));
        }
        if (implicitT.Scale is not null)
        {
            var dur = implicitT.Scale.Duration;
            implicitAnimations["Scale"] = AnimationHelper.CreateVector3ImplicitAnimation(
                compositor, "Scale", new Animation.LinearCurve(dur));
        }
        if (implicitT.Translation is not null)
        {
            var dur = implicitT.Translation.Duration;
            implicitAnimations["Offset"] = AnimationHelper.CreateVector3ImplicitAnimation(
                compositor, "Offset", new Animation.LinearCurve(dur));
        }

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Sets up Composition-layer implicit animations on the element's Visual so that
    /// layout-driven Offset (and optionally Size) changes animate smoothly.
    /// Runs entirely on the Composition thread — zero managed-code callbacks during animation.
    /// </summary>
    internal static void ApplyLayoutAnimation(UIElement uie, LayoutAnimationConfig config)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        if (!AnimationCapabilities.SupportsImplicitAnimations(compositor)) return; // layout changes snap

        var implicitAnimations = compositor.CreateImplicitAnimationCollection();

        if (config.AnimateOffset)
        {
            if (config.UseSpring)
            {
                var spring = compositor.CreateSpringVector3Animation();
                spring.DampingRatio = config.DampingRatio;
                spring.Period = TimeSpan.FromSeconds(config.Period);
                spring.Target = "Offset";
                implicitAnimations["Offset"] = spring;
            }
            else
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = config.Duration;
                anim.Target = "Offset";
                implicitAnimations["Offset"] = anim;
            }
        }

        if (config.AnimateSize)
        {
            if (config.UseSpring)
            {
                var spring = compositor.CreateSpringVector2Animation();
                spring.DampingRatio = config.DampingRatio;
                spring.Period = TimeSpan.FromSeconds(config.Period);
                spring.Target = "Size";
                implicitAnimations["Size"] = spring;
            }
            else
            {
                var anim = compositor.CreateVector2KeyFrameAnimation();
                anim.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
                anim.Duration = config.Duration;
                anim.Target = "Size";
                implicitAnimations["Size"] = anim;
            }
        }

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Clears Composition-layer implicit animations from an element's Visual.
    /// Called when an element previously had LayoutAnimation but no longer does
    /// (e.g., after a state change removes the modifier, or a pooled control is reused).
    /// </summary>
    internal static void ClearLayoutAnimation(UIElement uie)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        if (!AnimationCapabilities.SupportsImplicitAnimations(visual.Compositor)) return;
        visual.ImplicitAnimations = null;
    }

    // ════════════════════════════════════════════════════════════════
    //  Property animation (.Animate() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates ImplicitAnimationCollection entries on the element's composition Visual
    /// for the targeted properties using the specified Curve. Merges with existing
    /// layout animation entries (Offset/Size) to avoid overwriting each other.
    /// </summary>
    internal static void ApplyPropertyAnimation(UIElement uie, AnimationConfig config, LayoutAnimationConfig? layoutConfig)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        if (!AnimationCapabilities.SupportsImplicitAnimations(compositor)) return; // the properties snap

        // Start from existing implicit animations (layout may have set Offset/Size)
        // or create a new collection
        var implicitAnimations = visual.ImplicitAnimations ?? compositor.CreateImplicitAnimationCollection();

        var props = config.Properties;
        var curve = config.Curve;

        if (props.HasFlag(AnimateProperty.Opacity))
            implicitAnimations["Opacity"] = AnimationHelper.CreateScalarImplicitAnimation(compositor, "Opacity", curve);

        if (props.HasFlag(AnimateProperty.Offset))
        {
            // Only add Offset if layout animation hasn't already claimed it
            if (layoutConfig is null || !layoutConfig.AnimateOffset)
                implicitAnimations["Offset"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "Offset", curve);
        }

        if (props.HasFlag(AnimateProperty.Scale))
            implicitAnimations["Scale"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "Scale", curve);

        if (props.HasFlag(AnimateProperty.Rotation))
            implicitAnimations["RotationAngle"] = AnimationHelper.CreateScalarImplicitAnimation(compositor, "RotationAngle", curve);

        if (props.HasFlag(AnimateProperty.CenterPoint))
            implicitAnimations["CenterPoint"] = AnimationHelper.CreateVector3ImplicitAnimation(compositor, "CenterPoint", curve);

        visual.ImplicitAnimations = implicitAnimations;
    }

    /// <summary>
    /// Clears property animation entries from an element's Visual's ImplicitAnimationCollection.
    /// Preserves layout animation entries if they exist.
    /// </summary>
    internal static void ClearPropertyAnimation(UIElement uie, LayoutAnimationConfig? layoutConfig)
    {
        if (layoutConfig is null)
        {
            // No layout animation either — clear everything
            var visual = ElementCompositionPreview.GetElementVisual(uie);
            if (AnimationCapabilities.SupportsImplicitAnimations(visual.Compositor))
                visual.ImplicitAnimations = null;
        }
        // If layout animation exists, ApplyLayoutAnimation will recreate the collection
        // with just layout entries on next update
    }

    // ════════════════════════════════════════════════════════════════
    //  Enter/exit transitions (.Transition() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies enter transition on mount: sets initial visual state then animates to final state.
    /// </summary>
    internal static void ApplyEnterTransition(UIElement uie, ElementTransition transition, int staggerIndex = 0, TimeSpan staggerDelay = default)
    {
        var enter = transition.GetEnterTransition();
        if (enter is null) return;
        ElementPool.MarkCompositorTainted(uie);

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var curve = transition.Curve ?? Curve.Ease(300, Easing.Decelerate);

        // Override with ambient scope if present
        if (AnimationScope.HasScope && AnimationScope.Current is not null)
            curve = AnimationScope.Current;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var delay = staggerIndex > 0 ? TimeSpan.FromTicks(staggerDelay.Ticks * staggerIndex) : TimeSpan.Zero;

        ApplyTransitionAnimations(uie, visual, compositor, enter, curve, isEnter: true, delay);

        batch.End();
    }

    /// <summary>
    /// Applies exit transition before unmount: animates out, then invokes onComplete to remove/pool.
    /// </summary>
    internal void ApplyExitTransition(UIElement uie, ElementTransition transition, Action onComplete, int staggerIndex = 0, TimeSpan staggerDelay = default)
    {
        var exit = transition.GetExitTransition();
        if (exit is null) { onComplete(); return; }
        ElementPool.MarkCompositorTainted(uie);

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var curve = transition.Curve ?? Curve.Ease(300, Easing.Decelerate);

        if (AnimationScope.HasScope && AnimationScope.Current is not null)
            curve = AnimationScope.Current;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var delay = staggerIndex > 0 ? TimeSpan.FromTicks(staggerDelay.Ticks * staggerIndex) : TimeSpan.Zero;

        ApplyTransitionAnimations(uie, visual, compositor, exit, curve, isEnter: false, delay);

        batch.End();
        batch.Completed += (_, _) =>
        {
            // Reset visual state after exit
            visual.Opacity = 1;
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            onComplete();
        };
    }

    private static void ApplyTransitionAnimations(
        UIElement uie, Visual visual, Compositor compositor,
        Animation.Transition transition, Curve curve, bool isEnter, TimeSpan delay)
    {
        switch (transition)
        {
            case FadeTransition:
                ApplyFadeTransition(uie, compositor, curve, isEnter, delay);
                break;
            case SlideTransition slide:
                ApplySlideTransition(uie, visual, compositor, slide.Edge, curve, isEnter, delay);
                break;
            case ScaleTransition scale:
                ApplyScaleTransition(uie, visual, compositor, scale.From, curve, isEnter, delay);
                break;
            case CombinedTransition combined:
                ApplyTransitionAnimations(uie, visual, compositor, combined.First, curve, isEnter, delay);
                ApplyTransitionAnimations(uie, visual, compositor, combined.Second, curve, isEnter, delay);
                break;
            case AsymmetricTransition asym:
                var inner = isEnter ? asym.EnterTransition : asym.ExitTransition;
                if (inner is not null)
                    ApplyTransitionAnimations(uie, visual, compositor, inner, curve, isEnter, delay);
                break;
            case DirectionalTransition dir:
                var dirInner = isEnter ? dir.EnterTransition : dir.ExitTransition;
                if (dirInner is not null)
                    ApplyTransitionAnimations(uie, visual, compositor, dirInner, curve, isEnter, delay);
                break;
        }
    }

    private static void ApplyFadeTransition(UIElement uie, Compositor compositor, Curve curve, bool isEnter, TimeSpan delay)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        if (isEnter)
        {
            visual.Opacity = 0;
            var anim = AnimationHelper.CreateScalarTargetAnimation(compositor, 1.0f, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Opacity", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateScalarTargetAnimation(compositor, 0f, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Opacity", anim);
        }
    }

    private static void ApplySlideTransition(UIElement uie, Visual visual, Compositor compositor, Edge edge, Curve curve, bool isEnter, TimeSpan delay)
    {
        var slideDistance = 40f;
        var offset = edge switch
        {
            Edge.Left => new Vector3(-slideDistance, 0, 0),
            Edge.Top => new Vector3(0, -slideDistance, 0),
            Edge.Right => new Vector3(slideDistance, 0, 0),
            Edge.Bottom => new Vector3(0, slideDistance, 0),
            _ => new Vector3(0, slideDistance, 0),
        };

        if (isEnter)
        {
            visual.Offset = offset;
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, Vector3.Zero, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Offset", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, offset, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Offset", anim);
        }
    }

    private static void ApplyScaleTransition(UIElement uie, Visual visual, Compositor compositor, float from, Curve curve, bool isEnter, TimeSpan delay)
    {
        if (isEnter)
        {
            visual.Scale = new Vector3(from, from, 1f);
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, Vector3.One, curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Scale", anim);
        }
        else
        {
            var anim = AnimationHelper.CreateVector3TargetAnimation(compositor, new Vector3(from, from, 1f), curve);
            if (delay > TimeSpan.Zero) AnimationHelper.SetDelay(anim, delay);
            visual.StartAnimation("Scale", anim);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Interaction states (.InteractionStates() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// State tracking for elements with InteractionStates — stores current state and cached animations.
    /// Uses a static dictionary keyed by UIElement.
    /// </summary>
    private static readonly Dictionary<UIElement, InteractionStateTracker> _interactionTrackers = new();

    // Test-only accessor (InternalsVisibleTo Reactor.AppTests.Host).
    internal static int InteractionTrackerCountForTests => _interactionTrackers.Count;

    // Test-only: drives a hover-in / hover-out transition without synthesizing pointer input.
    internal static void SimulatePointerOverForTests(UIElement uie, bool isOver)
    {
        if (_interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, isOver ? InteractionState.PointerOver : InteractionState.Normal);
    }

    private sealed class InteractionStateTracker
    {
        public InteractionState CurrentState;
        public InteractionStatesConfig Config = null!;
        public Brush? NormalBackground;
        public Brush? NormalForeground;
        public Brush? NormalBorderBrush;
    }

    private enum InteractionState { Normal, PointerOver, Pressed, Focused }

    /// <summary>
    /// Sets up or updates InteractionStates on an element. Registers pointer event handlers
    /// on first setup; updates cached config on subsequent calls.
    /// </summary>
    internal static void ApplyInteractionStates(UIElement uie, InteractionStatesConfig config)
    {
        ElementPool.MarkCompositorTainted(uie);
        if (!_interactionTrackers.TryGetValue(uie, out var tracker))
        {
            tracker = new InteractionStateTracker();
            _interactionTrackers[uie] = tracker;

            // Capture normal brush values
            if (uie is FrameworkElement fe)
            {
                tracker.NormalBackground = fe switch
                {
                    WinUI.Panel p => p.Background,
                    WinUI.Control c => c.Background,
                    WinUI.Border b => b.Background,
                    _ => null,
                };
                tracker.NormalForeground = fe switch
                {
                    WinUI.Control c => c.Foreground,
                    TextBlock tb => tb.Foreground,
                    _ => null,
                };
                tracker.NormalBorderBrush = fe switch
                {
                    WinUI.Control c => c.BorderBrush,
                    WinUI.Border b => b.BorderBrush,
                    _ => null,
                };
            }

            // Register handlers
            uie.PointerEntered += OnInteractionPointerEntered;
            uie.PointerExited += OnInteractionPointerExited;
            uie.PointerPressed += OnInteractionPointerPressed;
            uie.PointerReleased += OnInteractionPointerReleased;
            uie.PointerCaptureLost += OnInteractionPointerCaptureLost;
            uie.GotFocus += OnInteractionGotFocus;
            uie.LostFocus += OnInteractionLostFocus;
        }

        tracker.Config = config;
    }

    /// <summary>
    /// Removes InteractionStates from an element, unregistering handlers and clearing state.
    /// </summary>
    internal static void ClearInteractionStates(UIElement uie)
    {
        if (!_interactionTrackers.Remove(uie)) return;

        uie.PointerEntered -= OnInteractionPointerEntered;
        uie.PointerExited -= OnInteractionPointerExited;
        uie.PointerPressed -= OnInteractionPointerPressed;
        uie.PointerReleased -= OnInteractionPointerReleased;
        uie.PointerCaptureLost -= OnInteractionPointerCaptureLost;
        uie.GotFocus -= OnInteractionGotFocus;
        uie.LostFocus -= OnInteractionLostFocus;
    }

    private static void OnInteractionPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.PointerOver);
    }

    private static void OnInteractionPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void OnInteractionPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Pressed);
    }

    private static void OnInteractionPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
        {
            // Released goes back to PointerOver (pointer is still over the element)
            TransitionToState(uie, tracker, InteractionState.PointerOver);
        }
    }

    private static void OnInteractionPointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void OnInteractionGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker)
            && tracker.Config.Focused is not null)
            TransitionToState(uie, tracker, InteractionState.Focused);
    }

    private static void OnInteractionLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement uie && _interactionTrackers.TryGetValue(uie, out var tracker))
            TransitionToState(uie, tracker, InteractionState.Normal);
    }

    private static void TransitionToState(UIElement uie, InteractionStateTracker tracker, InteractionState newState)
    {
        if (tracker.CurrentState == newState) return;
        tracker.CurrentState = newState;

        var config = tracker.Config;
        var curve = config.Curve ?? Curve.Ease(200, Easing.Standard);

        // Resolve effective state values (Pressed inherits from PointerOver)
        var values = newState switch
        {
            InteractionState.PointerOver => config.PointerOver,
            InteractionState.Pressed => MergePressed(config.PointerOver, config.Pressed),
            InteractionState.Focused => config.Focused,
            _ => null, // Normal
        };

        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        // Compositor properties — animate via Visual
        var targetOpacity = values?.Opacity ?? 1.0f;
        var opacityAnim = AnimationHelper.CreateScalarTargetAnimation(compositor, targetOpacity, curve);
        visual.StartAnimation("Opacity", opacityAnim);

        if (values?.Scale.HasValue == true || values?.ScaleV.HasValue == true || newState == InteractionState.Normal)
        {
            var targetScale = values?.ScaleV ?? (values?.Scale.HasValue == true ? new Vector3(values.Scale.Value, values.Scale.Value, 1f) : Vector3.One);
            var scaleAnim = AnimationHelper.CreateVector3TargetAnimation(compositor, targetScale, curve);
            visual.StartAnimation("Scale", scaleAnim);
        }

        if (values?.Translation.HasValue == true || newState == InteractionState.Normal)
        {
            // Animate Translation, not Offset: on a XAML element's handoff visual, Offset is the
            // layout position, so animating it to zero pulls the element to its parent's origin.
            ElementCompositionPreview.SetIsTranslationEnabled(uie, true);
            var targetTranslation = values?.Translation ?? Vector3.Zero;
            var translationAnim = AnimationHelper.CreateVector3TargetAnimation(compositor, targetTranslation, curve);
            visual.StartAnimation("Translation", translationAnim);
        }

        if (values?.Rotation.HasValue == true || newState == InteractionState.Normal)
        {
            var targetRotation = values?.Rotation ?? 0f;
            var rotationAnim = AnimationHelper.CreateScalarTargetAnimation(compositor, targetRotation, curve);
            visual.StartAnimation("RotationAngle", rotationAnim);
        }

        // Brush properties — direct set
        if (uie is FrameworkElement fe)
        {
            var bg = values?.Background ?? tracker.NormalBackground;
            if (bg is not null)
            {
                if (fe is WinUI.Panel p) p.Background = bg;
                else if (fe is WinUI.Control c) c.Background = bg;
                else if (fe is WinUI.Border b) b.Background = bg;
            }

            var fg = values?.Foreground ?? tracker.NormalForeground;
            if (fg is not null)
            {
                if (fe is WinUI.Control c) c.Foreground = fg;
                else if (fe is TextBlock tb) tb.Foreground = fg;
            }

            var bb = values?.BorderBrush ?? tracker.NormalBorderBrush;
            if (bb is not null)
            {
                if (fe is WinUI.Control c) c.BorderBrush = bb;
                else if (fe is WinUI.Border b) b.BorderBrush = bb;
            }
        }
    }

    private static InteractionStateValues? MergePressed(InteractionStateValues? pointerOver, InteractionStateValues? pressed)
    {
        if (pressed is null) return pointerOver;
        if (pointerOver is null) return pressed;

        // Pressed inherits unoverridden values from PointerOver
        return new InteractionStateValues(
            Opacity: pressed.Opacity ?? pointerOver.Opacity,
            Scale: pressed.Scale ?? pointerOver.Scale,
            ScaleV: pressed.ScaleV ?? pointerOver.ScaleV,
            Translation: pressed.Translation ?? pointerOver.Translation,
            Rotation: pressed.Rotation ?? pointerOver.Rotation,
            Background: pressed.Background ?? pointerOver.Background,
            Foreground: pressed.Foreground ?? pointerOver.Foreground,
            BorderBrush: pressed.BorderBrush ?? pointerOver.BorderBrush);
    }

    // ════════════════════════════════════════════════════════════════
    //  Stagger animation (DelayTime on child animations)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies stagger delays to an element's Visual's implicit animations.
    /// Called after layout/property animations are set up on children.
    /// </summary>
    internal static void ApplyStaggerDelays(UIElement parent, StaggerConfig config)
    {
        if (parent is not WinUI.Panel panel) return;

        for (int i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            var visual = ElementCompositionPreview.GetElementVisual(child);
            if (!AnimationCapabilities.SupportsImplicitAnimations(visual.Compositor)) return;
            if (visual.ImplicitAnimations is null) continue;

            var delay = TimeSpan.FromTicks(config.Delay.Ticks * i);
            foreach (var key in new[] { "Offset", "Opacity", "Scale", "RotationAngle", "Size", "CenterPoint" })
            {
                try
                {
                    var anim = visual.ImplicitAnimations[key];
                    if (anim is KeyFrameAnimation kfa) kfa.DelayTime = delay;
                    else if (anim is SpringScalarNaturalMotionAnimation ssa) ssa.DelayTime = delay;
                    else if (anim is SpringVector3NaturalMotionAnimation sva) sva.DelayTime = delay;
                }
                catch { /* Key not present in collection — skip */ }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Keyframe animation (.Keyframes() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks previous trigger values for keyframe animations, keyed by (UIElement, name).
    /// </summary>
    private static readonly Dictionary<(UIElement, string), object?> _keyframeTriggerValues = new();

    /// <summary>
    /// Checks trigger values and starts keyframe animations when they change.
    /// </summary>
    internal static void ApplyKeyframeAnimations(UIElement uie, KeyframeEntry[] entries)
    {
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;

        foreach (var entry in entries)
        {
            var key = (uie, entry.Name);
            var changed = false;

            if (_keyframeTriggerValues.TryGetValue(key, out var prevTrigger))
                changed = !Equals(prevTrigger, entry.Trigger);
            else
                changed = true; // First mount

            _keyframeTriggerValues[key] = entry.Trigger;

            if (!changed) continue;

            var def = entry.Definition;
            var group = compositor.CreateAnimationGroup();

            // Create per-property keyframe animations
            bool hasOpacity = false, hasScale = false, hasTranslation = false, hasRotation = false;
            foreach (var kf in def.Keyframes)
            {
                if (kf.Opacity.HasValue) hasOpacity = true;
                if (kf.Scale.HasValue) hasScale = true;
                if (kf.Translation.HasValue) hasTranslation = true;
                if (kf.Rotation.HasValue) hasRotation = true;
            }

            if (hasOpacity)
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "Opacity";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Opacity.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Opacity.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Opacity.Value);
                }
                group.Add(anim);
            }

            if (hasScale)
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "Scale";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Scale.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Scale.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Scale.Value);
                }
                group.Add(anim);
            }

            if (hasTranslation)
            {
                var anim = compositor.CreateVector3KeyFrameAnimation();
                anim.Duration = def.Duration;
                // Translation, not Offset: Offset is the layout position on a XAML handoff visual (#1432)
                ElementCompositionPreview.SetIsTranslationEnabled(uie, true);
                anim.Target = "Translation";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Translation.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Translation.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Translation.Value);
                }
                group.Add(anim);
            }

            if (hasRotation)
            {
                var anim = compositor.CreateScalarKeyFrameAnimation();
                anim.Duration = def.Duration;
                anim.Target = "RotationAngle";
                if (def.Loop) anim.IterationBehavior = AnimationIterationBehavior.Forever;
                foreach (var kf in def.Keyframes)
                {
                    if (!kf.Rotation.HasValue) continue;
                    if (kf.Easing.HasValue)
                    {
                        var e = kf.Easing.Value;
                        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(e.X1, e.Y1), new Vector2(e.X2, e.Y2));
                        anim.InsertKeyFrame(kf.Progress, kf.Rotation.Value, easing);
                    }
                    else
                        anim.InsertKeyFrame(kf.Progress, kf.Rotation.Value);
                }
                group.Add(anim);
            }

            // Start the animation group via Visual
            foreach (CompositionAnimation anim in group)
                visual.StartAnimation(anim.Target, anim);
        }
    }

    internal static void ClearKeyframeAnimations(UIElement uie, KeyframeEntry[] entries)
    {
        foreach (var entry in entries)
            _keyframeTriggerValues.Remove((uie, entry.Name));

        // Stop composition animations so they don't keep running on pooled/unmounted controls.
        // We stop all four possible targets since KeyframeAnimationDef doesn't track which
        // targets were started — StopAnimation is a no-op if no animation is running on that property.
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(uie);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Translation");
            visual.StopAnimation("RotationAngle");
        }
        catch { /* No compositor (e.g. unit tests) */ }
    }

    // ════════════════════════════════════════════════════════════════
    //  Scroll-linked expression animation (.ScrollLinked() modifier)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies scroll-linked expression animations to an element's Visual.
    /// </summary>
    internal static void ApplyScrollAnimation(UIElement uie, ScrollAnimationConfig config)
    {
        if (config.ScrollViewer is null) return;
        ElementPool.MarkCompositorTainted(uie);
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        var compositor = visual.Compositor;
        var scrollPropertySet = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(config.ScrollViewer);

        foreach (var expr in config.Expressions)
        {
            var animation = compositor.CreateExpressionAnimation(expr.Expression);
            animation.SetReferenceParameter("scroll", scrollPropertySet);
            visual.StartAnimation(expr.Property, animation);
        }
    }

    internal static void ClearScrollAnimation(UIElement uie, ScrollAnimationConfig config)
    {
        var visual = ElementCompositionPreview.GetElementVisual(uie);
        foreach (var expr in config.Expressions)
            visual.StopAnimation(expr.Property);
    }

    // ════════════════════════════════════════════════════════════════
    //  Connected animations (cross-container transitions)
    // ════════════════════════════════════════════════════════════════

    // Test-only seam (InternalsVisibleTo Reactor.Tests / Reactor.AppTests.Host): counts
    // connected animations that actually STARTED (ConnectedAnimation.TryStart returned
    // true). A selftest asserts this increments across a source→destination transition;
    // the settled visual tree looks identical whether or not the animation ran, so the
    // start count is the only non-vacuous oracle available to an automated test.
    internal int ConnectedAnimationStartCount;

    // Test-only seam, same visibility: counts unmounting sources that were NOT prepared
    // because WinUI had not yet rendered a frame since their key was last prepared (see
    // PrepareConnectedAnimationSource). The settled tree is the same either way.
    internal int ConnectedAnimationPreparationsSkipped;

    // Keys this reconcile pass actually published a snapshot for. The flush resolves
    // queued destinations only against these, because ConnectedAnimationService is
    // view-wide and unclaimed preparations are deliberately left to expire on their own
    // (~1s): in principle a LATER render mounting the same key could consume one of those
    // and animate unrelated UI from wherever the old source sat.
    //
    // Carried as defence-in-depth, and honestly so: I could not build a NON-VACUOUS
    // regression test for it. A fixture that unmounts a keyed source in one pass and
    // mounts the same key in a later pass reads green with this guard disabled, i.e. WinUI
    // did not hand the stale snapshot over anyway — so the hazard may be theoretical in
    // this WinUI build. The guard is a strict narrowing (it can only prevent starts, never
    // cause one) and costs a hash lookup, so it stays; but it is NOT test-proven, and a
    // future change here should not assume a test would catch a regression.
    //
    // Bookkeeping only. Nothing is invoked on the service, so an unclaimed preparation is
    // never cancelled; see PrepareConnectedAnimationSource for why a cancel can crash.
    private readonly HashSet<string> _preparedConnectedAnimationKeys = new(StringComparer.Ordinal);

    private readonly Dictionary<UIElement, int> _navigationConnectedElements = new();
    internal void ClaimNavigationConnectedElement(UIElement element) =>
        _navigationConnectedElements[element] = _navigationConnectedElements.GetValueOrDefault(element) + 1;
    internal void ReleaseNavigationConnectedElement(UIElement element)
    {
        if (_navigationConnectedElements.GetValueOrDefault(element) <= 1) _navigationConnectedElements.Remove(element);
        else _navigationConnectedElements[element]--;
    }

    /// <summary>
    /// Publishes the source snapshot for an unmounting element, so a destination mounting
    /// under the same key in this pass can travel from it.
    /// </summary>
    /// <remarks>
    /// Preparation is speculative: every unmounting element carrying a key gets a
    /// snapshot, because the reconciler cannot know which sibling the user's interaction
    /// designated as the source. Collapsing a list of N keyed rows to one detail element
    /// therefore leaves N-1 preparations that nothing claims, and WinUI keeps each of
    /// those composited at its source's old position until it times out — roughly a
    /// second of the old list ghosting over the new view.
    ///
    /// <para>That is a known cosmetic wart, deliberately left alone, because WinUI faults
    /// (0xC0000005 at <c>CConnectedAnimationService::PreCommit</c>) when a preparation is
    /// cancelled before it has rendered a frame and its source left the tree by itself.
    /// The source is removed in the same pass it is prepared in, so WinUI retains it for
    /// that frame's commit, and only the preparation gives it a composition node; a cancel
    /// takes the node away and the commit dereferences null (issue #1152). An earlier
    /// revision withdrew unclaimed preparations with <c>ConnectedAnimation.Cancel()</c> at
    /// flush time and crashed this way; <c>ConnectedAnimation_OrphanOnlyPassDoesNotCrash</c>
    /// pins the no-crash behaviour.</para>
    ///
    /// <para>Preparing a key that is still in use cancels its earlier animation the same way.
    /// That happens when two passes run before WinUI renders a frame and both unmount an
    /// element with the key, so a key is not prepared again until WinUI has rendered a frame
    /// since its last preparation (<see cref="ConnectedAnimationFrameGate"/>). The skipped
    /// source does not animate, and the earlier preparation stands. In the round trip that
    /// reaches this (Go, Back, Go before one frame), the skipped source was mounted after
    /// the last frame, so it never reached the screen.
    /// <c>ConnectedAnimation_RepreparedBeforeFrameDoesNotCrash</c> pins this.</para>
    ///
    /// <para>Not covered: WinUI also cancels a started animation when its destination leaves
    /// the tree. A pass that removes a destination before WinUI has rendered a frame since
    /// its animation started can still fault.</para>
    /// </remarks>
    private void PrepareConnectedAnimationSource(string key, UIElement control)
    {
        if (_navigationConnectedElements.ContainsKey(control)) return;
        if (ConnectedAnimationFrameGate.IsAwaitingFrame(key))
        {
            ConnectedAnimationPreparationsSkipped++;
            return;
        }

        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(key, control);
            _preparedConnectedAnimationKeys.Add(key);
            ConnectedAnimationFrameGate.Prepared(key);
        }
        catch (global::System.Runtime.InteropServices.COMException ex) when (Diagnostics.HResults.IsTeardownReentry(ex.HResult))
        {
            Diagnostics.DiagnosticLog.SwallowedError(
                Diagnostics.LogCategory.Reactor, "Reconciler.PrepareConnectedAnimationSource", ex);
        }
    }

    /// <summary>
    /// Queues a connected animation start for an element that was just mounted.
    /// Called from Mount() whenever the element carries a ConnectedAnimationKey.
    /// </summary>
    /// <remarks>
    /// The key is resolved to a <see cref="ConnectedAnimation"/> in
    /// <see cref="FlushConnectedAnimations"/>, NOT here. Mount runs before the unmount
    /// that publishes the source snapshot: the child-reconciler's type-mismatch arm
    /// mounts the replacement first and only then calls
    /// <see cref="ReplaceChildWithExitTransition"/>, which unmounts the old control (and
    /// the keyed/LIS arms mount inserts before removing leftovers). Looking the key up
    /// at mount time therefore observed the service *before*
    /// <c>PrepareToAnimate</c> had run for it, returned null, and silently dropped the
    /// animation — the destination simply appeared at its final position. Deferring the
    /// lookup to the flush, which runs after the whole reconcile pass has completed,
    /// makes the source snapshot visible to the destination regardless of the order the
    /// reconciler happened to visit the two subtrees in.
    /// </remarks>
    internal void QueueConnectedAnimationStart(UIElement target, string key)
        => _pendingConnectedAnimationStarts.Add((key, target));

    /// <summary>
    /// Starts all queued connected animations. Call AFTER the new tree has been attached
    /// to the visual tree (e.g., after Window.Content = newControl).
    /// </summary>
    /// <remarks>
    /// Source and destination must appear in the SAME reconcile pass — that is the shape
    /// Reactor's model produces, since a single state change swaps both subtrees.
    /// </remarks>
    public void FlushConnectedAnimations()
    {
        if (_pendingConnectedAnimationStarts.Count == 0)
            return;

        try
        {
            var service = ConnectedAnimationService.GetForCurrentView();

            foreach (var (key, target) in _pendingConnectedAnimationStarts)
            {
                if (_navigationConnectedElements.ContainsKey(target)) continue;
                try
                {
                    // Scoped to this pass on purpose: the service is view-wide and holds
                    // unclaimed snapshots for ~1s, so an unscoped lookup could let a
                    // later render travel from a stale one. Defence-in-depth — see the
                    // note on _preparedConnectedAnimationKeys about the missing test.
                    if (!_preparedConnectedAnimationKeys.Contains(key))
                        continue;

                    if (service.GetAnimation(key) is { } anim && anim.TryStart(target))
                        ConnectedAnimationStartCount++;
                }
                catch (global::System.Runtime.InteropServices.COMException ex) when (Diagnostics.HResults.IsTeardownReentry(ex.HResult))
                {
                    Diagnostics.DiagnosticLog.SwallowedError(
                        Diagnostics.LogCategory.Reactor, "Reconciler.FlushConnectedAnimations.start", ex);
                }
            }
        }
        catch (global::System.Runtime.InteropServices.COMException ex) when (Diagnostics.HResults.IsTeardownReentry(ex.HResult))
        {
            Diagnostics.DiagnosticLog.SwallowedError(
                Diagnostics.LogCategory.Reactor, "Reconciler.FlushConnectedAnimations", ex);
        }

        _pendingConnectedAnimationStarts.Clear();
        _preparedConnectedAnimationKeys.Clear();
    }

    internal void ApplyModifiers(FrameworkElement fe, ElementModifiers m, Action requestRerender)
        => ApplyModifiers(fe, null, m, requestRerender);

    private static void ApplyDragAttached(FrameworkElement fe, DragAttached? attached)
        => DragAttachedProperties.SetIsDragEnabled(fe, attached?.IsEnabled);

    internal void ApplyModifiers(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m, Action requestRerender,
        bool keepRemovedBackground = false)
    {
        // Guard each property: only call into WinUI when the value actually changed.
        // Each WinUI property set is a managed→native interop call, so avoiding
        // unnecessary sets is critical for large element counts.

        // RequestedTheme must be set BEFORE ApplyThemeBindings so that ThemeRef
        // bindings resolve against the correct theme variant.
        if (m.RequestedTheme.HasValue && m.RequestedTheme != oldM?.RequestedTheme)
            fe.RequestedTheme = m.RequestedTheme.Value;
        else if (!m.RequestedTheme.HasValue && oldM?.RequestedTheme.HasValue == true)
            fe.ClearValue(FrameworkElement.RequestedThemeProperty);

        // Apply physical margin, then overlay logical (BiDi-aware) inline margin.
        // The previous render's physical margin is only carried forward as the *base*
        // for that overlay — folding it into resolvedMargin unconditionally would make
        // the value non-null whenever the last render had a margin, which left the unset
        // arm below unreachable and meant `.Margin(x)` → no margin reset nothing (#952).
        var resolvedMargin = m.Margin;
        if (m.MarginInlineStart.HasValue || m.MarginInlineEnd.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            var baseMargin = resolvedMargin ?? oldM?.Margin ?? fe.Margin;
            var left = isRtl ? (m.MarginInlineEnd ?? baseMargin.Left) : (m.MarginInlineStart ?? baseMargin.Left);
            var right = isRtl ? (m.MarginInlineStart ?? baseMargin.Right) : (m.MarginInlineEnd ?? baseMargin.Right);
            resolvedMargin = new Thickness(left, baseMargin.Top, right, baseMargin.Bottom);
        }
        // Unset goes through ClearValue, never through "write the DP default". A local
        // value outranks every Style setter in WinUI's precedence order, so assigning the
        // default would not restore the styled value — it would permanently override it
        // (issue #952). Same rule for every reset arm below.
        // The value guard compares against the previous *physical* margin, so dropping or
        // changing an inline edge while the physical margin stays put would resolve back to
        // the old value and skip the write, stranding the previous render's inline edge on
        // the control. An inline delta is a change in its own right.
        var marginInlineChanged = m.MarginInlineStart != oldM?.MarginInlineStart
            || m.MarginInlineEnd != oldM?.MarginInlineEnd;
        if (resolvedMargin.HasValue && (resolvedMargin != oldM?.Margin || marginInlineChanged)) fe.Margin = resolvedMargin.Value;
        else if (!resolvedMargin.HasValue && (oldM?.Margin.HasValue == true || oldM?.MarginInlineStart.HasValue == true || oldM?.MarginInlineEnd.HasValue == true))
            fe.ClearValue(FrameworkElement.MarginProperty);

        // Apply physical padding, then overlay logical (BiDi-aware) inline padding.
        //
        // `resolvedPadding` falls back to the OLD padding so the inline overlay has a base for
        // the edges it does not own. That fallback is not evidence the author still wants
        // padding this render, so the reset arm below cannot test it: `m.Padding ?? oldM?.Padding`
        // is null only when oldM had no padding either, which made the old
        // `!resolvedPadding.HasValue && oldM?.Padding.HasValue == true` guard a contradiction —
        // dead code that left a dropped `.Padding(...)` stuck on the control forever. `wantsPadding`
        // is the honest question, and `hadPadding` covers the inline-only case (oldM.Padding null
        // but an overlay was written) that `oldM?.Padding.HasValue` missed.
        var wantsPadding = m.Padding.HasValue || m.PaddingInlineStart.HasValue || m.PaddingInlineEnd.HasValue;
        var hadPadding = oldM is not null && (oldM.Padding.HasValue || oldM.PaddingInlineStart.HasValue || oldM.PaddingInlineEnd.HasValue);
        var resolvedPadding = m.Padding ?? oldM?.Padding;
        if (m.PaddingInlineStart.HasValue || m.PaddingInlineEnd.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            var basePad = resolvedPadding ?? fe switch
            {
                WinUI.Control pc => pc.Padding,
                WinUI.Border pb => pb.Padding,
                WinUI.StackPanel psp => psp.Padding,
                WinUI.Grid pg => pg.Padding,
                WinUI.RelativePanel prp => prp.Padding,
                WinUI.TextBlock ptb => ptb.Padding,
                _ => new Thickness(),
            };
            var left = isRtl ? (m.PaddingInlineEnd ?? basePad.Left) : (m.PaddingInlineStart ?? basePad.Left);
            var right = isRtl ? (m.PaddingInlineStart ?? basePad.Right) : (m.PaddingInlineEnd ?? basePad.Right);
            resolvedPadding = new Thickness(left, basePad.Top, right, basePad.Bottom);
        }
        // Same inline-delta rule as Margin above.
        var paddingInlineChanged = m.PaddingInlineStart != oldM?.PaddingInlineStart
            || m.PaddingInlineEnd != oldM?.PaddingInlineEnd;
        if (resolvedPadding.HasValue && (resolvedPadding != oldM?.Padding || paddingInlineChanged))
        {
            if (fe is WinUI.Control padCtrl) padCtrl.Padding = resolvedPadding.Value;
            else if (fe is WinUI.Border padBdr) padBdr.Padding = resolvedPadding.Value;
            else if (fe is WinUI.StackPanel padSp) padSp.Padding = resolvedPadding.Value;
            else if (fe is WinUI.Grid padGrid) padGrid.Padding = resolvedPadding.Value;
            else if (fe is WinUI.RelativePanel padRelative) padRelative.Padding = resolvedPadding.Value;
            else if (fe is WinUI.TextBlock padTb) padTb.Padding = resolvedPadding.Value;
        }
        else if (!wantsPadding && hadPadding)
        {
            // Clear rather than write a local `new Thickness(0)`: a zero local wins over the
            // control's style/template, so a Button that dropped `.Padding(...)` would flush to
            // nothing instead of returning to its themed padding. This is the `Padding` half of
            // issue #952; the sizing/alignment arms below now follow the same rule.
            if (fe is WinUI.Control padCtrl) padCtrl.ClearValue(WinUI.Control.PaddingProperty);
            else if (fe is WinUI.Border padBdr) padBdr.ClearValue(WinUI.Border.PaddingProperty);
            else if (fe is WinUI.StackPanel padSp) padSp.ClearValue(WinUI.StackPanel.PaddingProperty);
            else if (fe is WinUI.Grid padGrid) padGrid.ClearValue(WinUI.Grid.PaddingProperty);
            else if (fe is WinUI.RelativePanel padRelative) padRelative.ClearValue(WinUI.RelativePanel.PaddingProperty);
            else if (fe is WinUI.TextBlock padTb) padTb.ClearValue(WinUI.TextBlock.PaddingProperty);
        }
        if (m.Width.HasValue && m.Width != oldM?.Width) fe.Width = m.Width.Value;
        else if (!m.Width.HasValue && oldM?.Width.HasValue == true) fe.ClearValue(FrameworkElement.WidthProperty);
        if (m.Height.HasValue && m.Height != oldM?.Height) fe.Height = m.Height.Value;
        else if (!m.Height.HasValue && oldM?.Height.HasValue == true) fe.ClearValue(FrameworkElement.HeightProperty);
        if (m.MinWidth.HasValue && m.MinWidth != oldM?.MinWidth) fe.MinWidth = m.MinWidth.Value;
        else if (!m.MinWidth.HasValue && oldM?.MinWidth.HasValue == true) fe.ClearValue(FrameworkElement.MinWidthProperty);
        if (m.MinHeight.HasValue && m.MinHeight != oldM?.MinHeight) fe.MinHeight = m.MinHeight.Value;
        else if (!m.MinHeight.HasValue && oldM?.MinHeight.HasValue == true) fe.ClearValue(FrameworkElement.MinHeightProperty);
        if (m.MaxWidth.HasValue && m.MaxWidth != oldM?.MaxWidth) fe.MaxWidth = m.MaxWidth.Value;
        else if (!m.MaxWidth.HasValue && oldM?.MaxWidth.HasValue == true) fe.ClearValue(FrameworkElement.MaxWidthProperty);
        if (m.MaxHeight.HasValue && m.MaxHeight != oldM?.MaxHeight) fe.MaxHeight = m.MaxHeight.Value;
        else if (!m.MaxHeight.HasValue && oldM?.MaxHeight.HasValue == true) fe.ClearValue(FrameworkElement.MaxHeightProperty);
        if (m.HorizontalAlignment.HasValue && m.HorizontalAlignment != oldM?.HorizontalAlignment) fe.HorizontalAlignment = m.HorizontalAlignment.Value;
        else if (!m.HorizontalAlignment.HasValue && oldM?.HorizontalAlignment.HasValue == true) fe.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        if (m.VerticalAlignment.HasValue && m.VerticalAlignment != oldM?.VerticalAlignment) fe.VerticalAlignment = m.VerticalAlignment.Value;
        else if (!m.VerticalAlignment.HasValue && oldM?.VerticalAlignment.HasValue == true) fe.ClearValue(FrameworkElement.VerticalAlignmentProperty);
        if (fe is WinUI.Control contentAlignmentControl)
        {
            if (m.HorizontalContentAlignment.HasValue && m.HorizontalContentAlignment != oldM?.HorizontalContentAlignment)
                contentAlignmentControl.HorizontalContentAlignment = m.HorizontalContentAlignment.Value;
            else if (!m.HorizontalContentAlignment.HasValue && oldM?.HorizontalContentAlignment.HasValue == true)
                contentAlignmentControl.ClearValue(WinUI.Control.HorizontalContentAlignmentProperty);

            if (m.VerticalContentAlignment.HasValue && m.VerticalContentAlignment != oldM?.VerticalContentAlignment)
                contentAlignmentControl.VerticalContentAlignment = m.VerticalContentAlignment.Value;
            else if (!m.VerticalContentAlignment.HasValue && oldM?.VerticalContentAlignment.HasValue == true)
                contentAlignmentControl.ClearValue(WinUI.Control.VerticalContentAlignmentProperty);
        }
        if (m.Opacity.HasValue && m.Opacity != oldM?.Opacity)
            AnimationHelper.SetOrAnimate(fe, "Opacity", (float)m.Opacity.Value);
        else if (!m.Opacity.HasValue && oldM?.Opacity.HasValue == true)
            // Note: if the set arm started a compositor animation on Visual.Opacity,
            // clearing the XAML DP does not cancel it — neither did the previous
            // `fe.Opacity = 1.0`, so this is not a regression.
            fe.ClearValue(UIElement.OpacityProperty);
        // Transform family — Scale / Rotation / Translation / CenterPoint deliberately have
        // NO unset arm yet (issue #1001). These are XAML facade properties whose live value
        // lives on the element's composition visual: when a curve is ambient the set path
        // above calls visual.StartAnimation, which outranks the DP and leaves no local value
        // for ClearValue to release. Undoing that needs a StopAnimation companion and a
        // selftest that exercises the animated path, so it is tracked separately rather than
        // given the mechanical arm the plain-DP modifiers below get.
        // ModifierUnsetClearValueTests.MissingUnsetArmExceptions records the same four names.
        if (m.Scale.HasValue && m.Scale != oldM?.Scale)
            AnimationHelper.SetOrAnimateVector3(fe, "Scale", m.Scale.Value);
        if (m.Rotation.HasValue && m.Rotation != oldM?.Rotation)
            AnimationHelper.SetOrAnimate(fe, "Rotation", m.Rotation.Value);
        if (m.Translation.HasValue && m.Translation != oldM?.Translation)
            AnimationHelper.SetOrAnimateVector3(fe, "Translation", m.Translation.Value);
        if (m.CenterPoint.HasValue && m.CenterPoint != oldM?.CenterPoint)
            AnimationHelper.SetOrAnimateVector3(fe, "CenterPoint", m.CenterPoint.Value);
        if (m.IsVisible.HasValue && m.IsVisible != oldM?.IsVisible)
            fe.Visibility = m.IsVisible.Value ? Visibility.Visible : Visibility.Collapsed;
        else if (!m.IsVisible.HasValue && oldM?.IsVisible.HasValue == true)
            fe.ClearValue(UIElement.VisibilityProperty);
        if (m.RichToolTip is not null)
        {
            var oldTipEl = oldM?.RichToolTip;
            var existingTip = WinUI.ToolTipService.GetToolTip(fe) as UIElement;
            if (oldTipEl is not null && existingTip is not null && CanUpdate(oldTipEl, m.RichToolTip))
            {
                var replacement = UpdateHostedChild(oldTipEl, m.RichToolTip, existingTip, requestRerender);
                if (replacement is not null)
                    WinUI.ToolTipService.SetToolTip(fe, replacement);
            }
            else
            {
                WinUI.ToolTipService.SetToolTip(fe, Mount(m.RichToolTip, requestRerender));
            }
        }
        else if (m.ToolTip is not null && m.ToolTip != oldM?.ToolTip)
            WinUI.ToolTipService.SetToolTip(fe, m.ToolTip);
        else if (m.RichToolTip is null && m.ToolTip is null && (oldM?.RichToolTip is not null || oldM?.ToolTip is not null))
            fe.ClearValue(WinUI.ToolTipService.ToolTipProperty);

        if (m.ToolTipPlacement.HasValue && m.ToolTipPlacement != oldM?.ToolTipPlacement)
            WinUI.ToolTipService.SetPlacement(fe, m.ToolTipPlacement.Value);
        else if (!m.ToolTipPlacement.HasValue && oldM?.ToolTipPlacement.HasValue == true)
            fe.ClearValue(WinUI.ToolTipService.PlacementProperty);

        if (m.AttachedFlyout is not null)
            ApplyFlyoutAttachment(fe, oldM?.AttachedFlyout, m.AttachedFlyout, requestRerender);

        if (m.ContextFlyout is not null)
        {
            var oldContextEl = oldM?.ContextFlyout;
            if (oldContextEl is not null && fe.ContextFlyout is WinPrim.FlyoutBase existingCtx)
                UpdateFlyoutInPlace(existingCtx, oldContextEl, m.ContextFlyout, requestRerender);
            else
                fe.ContextFlyout = CreateFlyoutFromElement(m.ContextFlyout, requestRerender);
        }
        else if (oldM?.ContextFlyout is not null)
            fe.ClearValue(UIElement.ContextFlyoutProperty);

        // IsEnabled (on Control)
        if (m.IsEnabled.HasValue && m.IsEnabled != oldM?.IsEnabled && fe is WinUI.Control enCtrl)
            enCtrl.IsEnabled = m.IsEnabled.Value;
        else if (!m.IsEnabled.HasValue && oldM?.IsEnabled.HasValue == true && fe is WinUI.Control enCtrl2)
            enCtrl2.ClearValue(WinUI.Control.IsEnabledProperty);

        // CornerRadius (on Control, Border, Grid, StackPanel, and RelativePanel)
        if (m.CornerRadius.HasValue && m.CornerRadius != oldM?.CornerRadius)
        {
            if (fe is WinUI.Control crCtrl) crCtrl.CornerRadius = m.CornerRadius.Value;
            else if (fe is WinUI.Border crBdr) crBdr.CornerRadius = m.CornerRadius.Value;
            else if (fe is WinUI.Grid crGrid) crGrid.CornerRadius = m.CornerRadius.Value;
            else if (fe is WinUI.StackPanel crStack) crStack.CornerRadius = m.CornerRadius.Value;
            else if (fe is WinUI.RelativePanel crRelative) crRelative.CornerRadius = m.CornerRadius.Value;
        }
        else if (!m.CornerRadius.HasValue && oldM?.CornerRadius.HasValue == true)
        {
            if (fe is WinUI.Control crCtrl) crCtrl.ClearValue(WinUI.Control.CornerRadiusProperty);
            else if (fe is WinUI.Border crBdr) crBdr.ClearValue(WinUI.Border.CornerRadiusProperty);
            else if (fe is WinUI.Grid crGrid) crGrid.ClearValue(WinUI.Grid.CornerRadiusProperty);
            else if (fe is WinUI.StackPanel crStack) crStack.ClearValue(WinUI.StackPanel.CornerRadiusProperty);
            else if (fe is WinUI.RelativePanel crRelative) crRelative.ClearValue(WinUI.RelativePanel.CornerRadiusProperty);
        }

        // BorderBrush / BorderThickness (on Control and Border)
        if (m.BorderBrush is not null && !ReferenceEquals(m.BorderBrush, oldM?.BorderBrush))
        {
            if (fe is WinUI.Control bbCtrl) bbCtrl.BorderBrush = m.BorderBrush;
            else if (fe is WinUI.Border bbBdr) bbBdr.BorderBrush = m.BorderBrush;
        }
        else if (m.BorderBrush is null && oldM?.BorderBrush is not null)
        {
            if (fe is WinUI.Control bbCtrl) bbCtrl.ClearValue(WinUI.Control.BorderBrushProperty);
            else if (fe is WinUI.Border bbBdr) bbBdr.ClearValue(WinUI.Border.BorderBrushProperty);
        }
        // Apply physical border thickness, then overlay logical (BiDi-aware) inline border
        var resolvedBorder = m.BorderThickness;
        if (m.BorderInlineStart.HasValue)
        {
            var isRtl = fe.FlowDirection == FlowDirection.RightToLeft;
            // Same base rule as Margin/Padding: the previous render's physical thickness, not
            // the live control value, which already carries the last render's inline overlay.
            var baseBorder = resolvedBorder ?? oldM?.BorderThickness ?? (fe is WinUI.Control bc ? bc.BorderThickness : fe is WinUI.Border bb ? bb.BorderThickness : new Thickness());
            var inlineStartThickness = m.BorderInlineStart.Value;
            if (isRtl)
                resolvedBorder = new Thickness(baseBorder.Left, baseBorder.Top, inlineStartThickness.Left, baseBorder.Bottom);
            else
                resolvedBorder = new Thickness(inlineStartThickness.Left, baseBorder.Top, baseBorder.Right, baseBorder.Bottom);
        }
        // Same inline-delta rule as Margin/Padding above; the reset arm likewise has to fire
        // when the old bag carried only an inline border, or that edge is never released.
        var borderInlineChanged = m.BorderInlineStart != oldM?.BorderInlineStart;
        if (resolvedBorder.HasValue && (resolvedBorder != oldM?.BorderThickness || borderInlineChanged))
        {
            if (fe is WinUI.Control btCtrl) btCtrl.BorderThickness = resolvedBorder.Value;
            else if (fe is WinUI.Border btBdr) btBdr.BorderThickness = resolvedBorder.Value;
        }
        else if (!resolvedBorder.HasValue && (oldM?.BorderThickness.HasValue == true || oldM?.BorderInlineStart.HasValue == true))
        {
            if (fe is WinUI.Control btCtrl) btCtrl.ClearValue(WinUI.Control.BorderThicknessProperty);
            else if (fe is WinUI.Border btBdr) btBdr.ClearValue(WinUI.Border.BorderThicknessProperty);
        }

        // Background (Panel, Control, or Border)
        if (m.Background is not null && !ReferenceEquals(m.Background, oldM?.Background))
        {
            if (fe is WinUI.Panel panel) panel.Background = m.Background;
            else if (fe is WinUI.Control ctrl2) ctrl2.Background = m.Background;
            else if (fe is WinUI.Border bdr) bdr.Background = m.Background;
        }
        else if (m.Background is null && oldM?.Background is not null && !keepRemovedBackground)
        {
            if (fe is WinUI.Panel panel) panel.ClearValue(WinUI.Panel.BackgroundProperty);
            else if (fe is WinUI.Control ctrl2) ctrl2.ClearValue(WinUI.Control.BackgroundProperty);
            else if (fe is WinUI.Border bdr) bdr.ClearValue(WinUI.Border.BackgroundProperty);
        }

        // Foreground (Control or TextBlock)
        if (m.Foreground is not null && !ReferenceEquals(m.Foreground, oldM?.Foreground))
        {
            if (fe is WinUI.Control fgCtrl) fgCtrl.Foreground = m.Foreground;
            else if (fe is TextBlock fgTb) fgTb.Foreground = m.Foreground;
        }
        else if (m.Foreground is null && oldM?.Foreground is not null)
        {
            if (fe is WinUI.Control fgCtrl) fgCtrl.ClearValue(WinUI.Control.ForegroundProperty);
            else if (fe is TextBlock fgTb) fgTb.ClearValue(TextBlock.ForegroundProperty);
        }

        // AutomationProperties.Name
        if (m.AutomationName is not null && m.AutomationName != oldM?.AutomationName)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fe, m.AutomationName);
        else if (m.AutomationName is null && oldM?.AutomationName is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty);

        // AutomationProperties.AutomationId
        if (m.AutomationId is not null && m.AutomationId != oldM?.AutomationId)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(fe, m.AutomationId);
        else if (m.AutomationId is null && oldM?.AutomationId is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AutomationIdProperty);

        // TitleBar.IsDragRegion (attached; inert outside a TitleBar). WinApp SDK ≥ 2.1.3.
        if (m.IsDragRegion.HasValue && m.IsDragRegion != oldM?.IsDragRegion)
            WinUI.TitleBar.SetIsDragRegion(fe, m.IsDragRegion.Value);
        else if (!m.IsDragRegion.HasValue && oldM?.IsDragRegion.HasValue == true)
            fe.ClearValue(WinUI.TitleBar.IsDragRegionProperty);

        // ElementSoundMode (on Control, not FrameworkElement)
        // The reset arm re-tests `fe is Control` under its own pattern variable rather than
        // reusing `ctrl`: reaching the `else` only proves the whole set condition was false,
        // which a non-Control satisfies while still having the modifier set. Without the
        // re-test a Border that carries `.SoundMode(...)` would take the reset branch and
        // clear a property it never had. Same shape as IsEnabled above (issue #986).
        if (m.ElementSoundMode.HasValue && m.ElementSoundMode != oldM?.ElementSoundMode && fe is WinUI.Control ctrl)
            ctrl.ElementSoundMode = m.ElementSoundMode.Value;
        else if (!m.ElementSoundMode.HasValue && oldM?.ElementSoundMode.HasValue == true && fe is WinUI.Control ctrlSnd)
            ctrlSnd.ClearValue(WinUI.Control.ElementSoundModeProperty);

        // ── Accessibility — Tier 1 (inline properties) ──────────────
        if (m.HeadingLevel.HasValue && m.HeadingLevel != oldM?.HeadingLevel)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(fe, m.HeadingLevel.Value);
        else if (!m.HeadingLevel.HasValue && oldM?.HeadingLevel.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HeadingLevelProperty);

        if (m.IsTabStop.HasValue && m.IsTabStop != oldM?.IsTabStop)
            fe.IsTabStop = m.IsTabStop.Value;
        else if (!m.IsTabStop.HasValue && oldM?.IsTabStop.HasValue == true)
            fe.ClearValue(UIElement.IsTabStopProperty);

        if (m.IsHitTestVisible.HasValue && m.IsHitTestVisible != oldM?.IsHitTestVisible)
            fe.IsHitTestVisible = m.IsHitTestVisible.Value;
        else if (!m.IsHitTestVisible.HasValue && oldM?.IsHitTestVisible.HasValue == true)
            fe.ClearValue(UIElement.IsHitTestVisibleProperty);

        if (m.TabIndex.HasValue && m.TabIndex != oldM?.TabIndex && fe is WinUI.Control tabIdxCtrl)
            tabIdxCtrl.TabIndex = m.TabIndex.Value;
        else if (!m.TabIndex.HasValue && oldM?.TabIndex.HasValue == true && fe is WinUI.Control tabIdxCtrl2)
            tabIdxCtrl2.ClearValue(WinUI.Control.TabIndexProperty);

        if (m.AccessKey is not null && m.AccessKey != oldM?.AccessKey)
            fe.AccessKey = m.AccessKey;
        else if (m.AccessKey is null && oldM?.AccessKey is not null)
            fe.ClearValue(UIElement.AccessKeyProperty);

        if (m.XYFocusKeyboardNavigation.HasValue && m.XYFocusKeyboardNavigation != oldM?.XYFocusKeyboardNavigation)
            fe.XYFocusKeyboardNavigation = m.XYFocusKeyboardNavigation.Value;
        else if (!m.XYFocusKeyboardNavigation.HasValue && oldM?.XYFocusKeyboardNavigation.HasValue == true)
            fe.ClearValue(UIElement.XYFocusKeyboardNavigationProperty);

        // ── Accessibility — Tier 2/3 (lazy sub-record) ─────────────
        var a11y = m.Accessibility;
        var oldA11y = oldM?.Accessibility;
        if (a11y is not null || oldA11y is not null)
            ApplyAccessibilityModifiers(fe, oldA11y, a11y);

        // ── Typography (FontFamily, FontSize, FontWeight) ──────────
        if (m.FontFamily is not null && !ReferenceEquals(m.FontFamily, oldM?.FontFamily))
        {
            if (fe is WinUI.Control ffCtrl) ffCtrl.FontFamily = m.FontFamily;
            else if (fe is TextBlock ffTb) ffTb.FontFamily = m.FontFamily;
        }
        else if (m.FontFamily is null && oldM?.FontFamily is not null)
        {
            if (fe is WinUI.Control ffCtrl) ffCtrl.ClearValue(WinUI.Control.FontFamilyProperty);
            else if (fe is TextBlock ffTb) ffTb.ClearValue(TextBlock.FontFamilyProperty);
        }
        if (m.FontSize.HasValue && m.FontSize != oldM?.FontSize)
        {
            if (fe is WinUI.Control fsCtrl) fsCtrl.FontSize = m.FontSize.Value;
            else if (fe is TextBlock fsTb) fsTb.FontSize = m.FontSize.Value;
        }
        else if (!m.FontSize.HasValue && oldM?.FontSize.HasValue == true)
        {
            if (fe is WinUI.Control fsCtrl) fsCtrl.ClearValue(WinUI.Control.FontSizeProperty);
            else if (fe is TextBlock fsTb) fsTb.ClearValue(TextBlock.FontSizeProperty);
        }
        if (m.FontWeight.HasValue && m.FontWeight != oldM?.FontWeight)
        {
            if (fe is WinUI.Control fwCtrl) fwCtrl.FontWeight = m.FontWeight.Value;
            else if (fe is TextBlock fwTb) fwTb.FontWeight = m.FontWeight.Value;
        }
        else if (!m.FontWeight.HasValue && oldM?.FontWeight.HasValue == true)
        {
            if (fe is WinUI.Control fwCtrl) fwCtrl.ClearValue(WinUI.Control.FontWeightProperty);
            else if (fe is TextBlock fwTb) fwTb.ClearValue(TextBlock.FontWeightProperty);
        }

        // ── Declarative event handlers ────────────────────────────
        // Detach previous handler (if any) before attaching new one.
        // Handlers are stored in Tag via a wrapper so we can find them for detach.
        ApplyEventHandlers(fe, oldM, m);

        // Gesture recognizers (.OnPan / .OnPinch / .OnRotate)
        ApplyGestureHandlers(fe, oldM, m);

        // Drag-and-drop (.OnDragStart / .OnDrop / .OnDragEnter / .OnDragOver / .OnDragLeave)
        ApplyDragDropHandlers(fe, oldM, m);

        // OnMountAction — only run on initial mount (oldM is null)
        if (m.OnMountAction is not null && oldM is null)
            m.OnMountAction(fe);

        // OnUpdateAction — the update-time counterpart, run on every in-place
        // update (oldM is not null) but never at mount. Runs after child
        // reconciliation (ApplyModifiers is invoked post-dispatch in the update
        // path), so descendants realized during this update are already in the
        // visual tree. Chart label a11y uses it to re-hide newly realized
        // focusable descendants that OnMountAction (mount-only) would miss (#162).
        if (m.OnUpdateAction is not null && oldM is not null)
            m.OnUpdateAction(fe);

        // OnUnmountAction — captured here (authoritative modifier set) so it fires
        // reliably at teardown regardless of the tagged element's identity. Keep the
        // latest action across re-renders; don't clear an inner target's registration
        // when a shared-control wrapper drops its own callback. Owner identity prevents
        // a retained control from firing a disposed reconciler's callback in a new host.
        // Known limitation: a shared control has one slot, so a wrapper's callback
        // replaces the target's. If the wrapper later drops it while the target update is
        // shallow-skipped, nothing re-registers the target's callback and it is lost.
        if (m.OnUnmountAction is not null)
        {
            var callbackState = GetOrCreateReactorState(fe);
            callbackState.UnmountAction = m.OnUnmountAction;
            callbackState.UnmountActionOwner = _unmountActionOwner;
            _boundaryMountJournal?.Add(fe);
        }
        else if (oldM?.OnUnmountAction is not null
            && TryGetReactorState(fe, out var unmountState)
            && ReferenceEquals(unmountState.UnmountActionOwner, _unmountActionOwner)
            && ReferenceEquals(unmountState.UnmountAction, oldM.OnUnmountAction))
        {
            unmountState.UnmountAction = null;
            unmountState.UnmountActionOwner = null;
        }

        // Element ref — populate on mount/update so imperative APIs (FocusManager.Focus)
        // can target the mounted control. Writing on every update is cheap (single field
        // write) and keeps the ref fresh when the pool recycles elements. When the ref is
        // swapped or removed (oldM.Ref differs from m.Ref), clear the old cell first so it
        // doesn't dangle pointing at a control it no longer represents (CR-001).
        var oldRef = oldM?.Ref;
        var newRef = m.Ref;
        if (oldRef is not null && !ReferenceEquals(oldRef, newRef))
            oldRef.SetCurrent(null);
        if (newRef is not null)
        {
            newRef.SetCurrent(fe);
            AssertTypedRefMatch(newRef, fe);
        }

        ApplyModifierReferenceEdges(fe, oldM, m);
    }

    private static void ApplyModifierReferenceEdges(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m)
    {
        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_LabeledBy,
            m.Accessibility?.LabeledByRef,
            oldM?.Accessibility?.LabeledByRef,
            static (c, target) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(c, target));

        WireModifierReferenceList(
            fe,
            ReferenceSlots.ModifierRef_DescribedBy,
            m.Accessibility?.DescribedByRefs,
            oldM?.Accessibility?.DescribedByRefs,
            static c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetDescribedBy(c));

        WireModifierReferenceList(
            fe,
            ReferenceSlots.ModifierRef_FlowsTo,
            m.Accessibility?.FlowsToRefs,
            oldM?.Accessibility?.FlowsToRefs,
            static c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetFlowsTo(c));

        WireModifierReferenceList(
            fe,
            ReferenceSlots.ModifierRef_FlowsFrom,
            m.Accessibility?.FlowsFromRefs,
            oldM?.Accessibility?.FlowsFromRefs,
            static c => Microsoft.UI.Xaml.Automation.AutomationProperties.GetFlowsFrom(c));

        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_XYFocusUp,
            m.XYFocusUpRef,
            oldM?.XYFocusUpRef,
            static (c, target) => c.XYFocusUp = target);

        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_XYFocusDown,
            m.XYFocusDownRef,
            oldM?.XYFocusDownRef,
            static (c, target) => c.XYFocusDown = target);

        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_XYFocusLeft,
            m.XYFocusLeftRef,
            oldM?.XYFocusLeftRef,
            static (c, target) => c.XYFocusLeft = target);

        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_XYFocusRight,
            m.XYFocusRightRef,
            oldM?.XYFocusRightRef,
            static (c, target) => c.XYFocusRight = target);

        // ToolTipService.PlacementTarget takes a UIElement, so an unset target
        // has to go through ClearValue rather than a null assignment.
        WireModifierScalarReference(
            fe,
            ReferenceSlots.ModifierRef_ToolTipPlacementTarget,
            m.ToolTipPlacementTargetRef,
            oldM?.ToolTipPlacementTargetRef,
            static (c, target) =>
            {
                if (target is not null) WinUI.ToolTipService.SetPlacementTarget(c, target);
                else c.ClearValue(WinUI.ToolTipService.PlacementTargetProperty);
            });
    }

    private static void WireModifierScalarReference(
        FrameworkElement fe,
        int slot,
        Microsoft.UI.Reactor.Input.ElementRef? current,
        Microsoft.UI.Reactor.Input.ElementRef? old,
        Action<FrameworkElement, FrameworkElement?> apply)
    {
        if (current is not null || old is not null)
            WireReferenceEdge(fe, slot, current, apply);
    }

    private static void WireModifierReferenceList(
        FrameworkElement fe,
        int slot,
        IReadOnlyList<Microsoft.UI.Reactor.Input.ElementRef>? current,
        IReadOnlyList<Microsoft.UI.Reactor.Input.ElementRef>? old,
        Func<FrameworkElement, IList<DependencyObject>> getDestination)
    {
        if (current is null && old is null) return;

        List<Microsoft.UI.Reactor.Input.ElementRef>? cells = null;
        if (current is not null)
        {
            cells = new(current.Count);
            foreach (var r in current)
                if (r is not null)
                    cells.Add(r);
        }

        WireReferenceListEdge(
            fe,
            slot,
            cells,
            ctrl =>
            {
                var dst = getDestination(ctrl);
                dst.Clear();
                if (cells is null) return;

                foreach (var cell in cells)
                {
                    if (cell.Current is FrameworkElement target)
                        dst.Add(target);
                }
            },
            clearTarget: ctrl => getDestination(ctrl).Clear());
    }

    [global::System.Diagnostics.Conditional("DEBUG")]
    private static void AssertTypedRefMatch(Microsoft.UI.Reactor.Input.ElementRef r, FrameworkElement fe)
    {
        // Spec 033 §3 — typed refs (ElementRef<T>) record their expected concrete
        // type on the inner untyped ref. When a typed ref is bound to an element
        // that is not assignable to T, fail loudly under DEBUG so the bug is caught
        // during dev-loop. RELEASE builds keep the silent-null behavior of the
        // typed wrapper's `Current` property — no perf cost on the hot path.
        var expected = r.ExpectedType;
        if (expected is null) return;
        if (!expected.IsInstanceOfType(fe))
        {
            global::System.Diagnostics.Debug.Fail(
                $"ElementRef<{expected.Name}> attached to a {fe.GetType().Name}. " +
                "Use ElementRef<" + fe.GetType().Name + "> or untyped ElementRef.");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Declarative event handler management
    // ════════════════════════════════════════════════════════════════
    //  Accessibility modifiers (Tier 2/3 sub-record)
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Applies the Tier 2/3 accessibility sub-record.
    /// </summary>
    /// <remarks>
    /// Called whenever <em>either</em> bag is non-null, which includes the case where the whole
    /// sub-record was dropped this render (<paramref name="a"/> null, <paramref name="oldA"/>
    /// not). That is why there is no <c>a is null</c> early return any more: it swallowed
    /// exactly the transition it needed to handle, so dropping <c>.HelpText(...)</c> — or the
    /// entire accessibility bag — left every automation property pinned on the control forever
    /// (issue #986). Every property is therefore null-tolerant on <paramref name="a"/> — set
    /// arms guard with <c>a is not null &amp;&amp; a.X…</c>, reset arms read <c>a?.X</c> — so the
    /// dropped-bag case takes the same reset path as dropping one modifier from a surviving bag.
    /// <para>
    /// Resets go through <c>ClearValue</c>, never through writing the property's default: a
    /// local value outranks every <c>Style</c> setter in WinUI's precedence order, so the
    /// assignment would pin the default rather than hand the property back (issue #952).
    /// </para>
    /// </remarks>
    private static void ApplyAccessibilityModifiers(FrameworkElement fe, AccessibilityModifiers? oldA, AccessibilityModifiers? a)
    {
        if (a is null && oldA is null) return;

        if (a is not null && a.HelpText is not null && a.HelpText != oldA?.HelpText)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(fe, a.HelpText);
        else if (a?.HelpText is null && oldA?.HelpText is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.HelpTextProperty);

        if (a is not null && a.FullDescription is not null && a.FullDescription != oldA?.FullDescription)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetFullDescription(fe, a.FullDescription);
        else if (a?.FullDescription is null && oldA?.FullDescription is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.FullDescriptionProperty);

        if (a is not null && a.LandmarkType.HasValue && a.LandmarkType != oldA?.LandmarkType)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLandmarkType(fe, a.LandmarkType.Value);
        else if (a?.LandmarkType.HasValue != true && oldA?.LandmarkType.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LandmarkTypeProperty);

        if (a is not null && a.AccessibilityView.HasValue && a.AccessibilityView != oldA?.AccessibilityView)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(fe, a.AccessibilityView.Value);
        else if (a?.AccessibilityView.HasValue != true && oldA?.AccessibilityView.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.AccessibilityViewProperty);

        if (a is not null && a.IsRequiredForForm.HasValue && a.IsRequiredForForm != oldA?.IsRequiredForForm)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetIsRequiredForForm(fe, a.IsRequiredForForm.Value);
        else if (a?.IsRequiredForForm.HasValue != true && oldA?.IsRequiredForForm.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.IsRequiredForFormProperty);

        if (a is not null && a.LiveSetting.HasValue && a.LiveSetting != oldA?.LiveSetting)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(fe, a.LiveSetting.Value);
        else if (a?.LiveSetting.HasValue != true && oldA?.LiveSetting.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LiveSettingProperty);

        if (a is not null && a.PositionInSet.HasValue && a.PositionInSet != oldA?.PositionInSet)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetPositionInSet(fe, a.PositionInSet.Value);
        else if (a?.PositionInSet.HasValue != true && oldA?.PositionInSet.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.PositionInSetProperty);

        if (a is not null && a.SizeOfSet.HasValue && a.SizeOfSet != oldA?.SizeOfSet)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetSizeOfSet(fe, a.SizeOfSet.Value);
        else if (a?.SizeOfSet.HasValue != true && oldA?.SizeOfSet.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.SizeOfSetProperty);

        if (a is not null && a.Level.HasValue && a.Level != oldA?.Level)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLevel(fe, a.Level.Value);
        else if (a?.Level.HasValue != true && oldA?.Level.HasValue == true)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LevelProperty);

        if (a is not null && a.ItemStatus is not null && a.ItemStatus != oldA?.ItemStatus)
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(fe, a.ItemStatus);
        else if (a?.ItemStatus is null && oldA?.ItemStatus is not null)
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.ItemStatusProperty);

        if (a is not null && a.TabFocusNavigation.HasValue && a.TabFocusNavigation != oldA?.TabFocusNavigation)
            fe.TabFocusNavigation = a.TabFocusNavigation.Value;
        else if (a?.TabFocusNavigation.HasValue != true && oldA?.TabFocusNavigation.HasValue == true)
            fe.ClearValue(UIElement.TabFocusNavigationProperty);

        // LabeledBy — resolve AutomationId string to the target element in the visual tree.
        // During mount the element may not be in the visual tree yet (XamlRoot is null),
        // so defer resolution to the Loaded event if needed.
        if (a is not null && a.LabeledBy is not null && a.LabeledBy != oldA?.LabeledBy)
        {
            var target = FindByAutomationId(fe, a.LabeledBy);
            if (target is not null)
            {
                // Resolved synchronously — retire any deferred request from an
                // earlier render so its parked handler can't overwrite this one.
                // TryGet, not GetOrCreate: with no state there is no parked handler
                // and nothing to retire, and this is the common path — allocating and
                // attaching a ReactorState here would cost one per element that ever
                // resolves a LabeledBy synchronously.
                if (TryGetReactorState(fe, out var resolvedState))
                    resolvedState.PendingLabeledBy = null;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(fe, target);
            }
            else
            {
                // Element not yet in visual tree — defer until Loaded. The handler
                // outlives this render, so publish the request first and have the
                // handler re-check it: if LabeledBy is dropped or retargeted before
                // Loaded fires, the stale handler must no-op rather than stomp the
                // reset arm below (issue #986).
                var labelId = a.LabeledBy;
                GetOrCreateReactorState(fe).PendingLabeledBy = labelId;
                void OnLoaded(object sender, RoutedEventArgs _)
                {
                    fe.Loaded -= OnLoaded;
                    if (!TryGetReactorState(fe, out var pending) || pending.PendingLabeledBy != labelId)
                        return;
                    var deferred = FindByAutomationId(fe, labelId);
                    if (deferred is not null)
                    {
                        pending.PendingLabeledBy = null;
                        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLabeledBy(fe, deferred);
                    }
                    // Not found: the request stays recorded as pending (still unresolved), so
                    // ReactorDiagnostics.GetReferenceEdges keeps reporting it. Nothing retries it
                    // here; a later render that changes or drops the id, or pool return, retires it.
                }
                fe.Loaded += OnLoaded;
            }
        }
        else if (a?.LabeledBy is null && oldA?.LabeledBy is not null)
        {
            // Cancel a still-parked deferred resolution before clearing, or it would
            // re-apply the dropped label the moment the element loads.
            if (TryGetReactorState(fe, out var pendingState))
                pendingState.PendingLabeledBy = null;
            fe.ClearValue(Microsoft.UI.Xaml.Automation.AutomationProperties.LabeledByProperty);
        }
    }

    /// <summary>
    /// Walks the visual tree from <paramref name="element"/>'s XamlRoot to find
    /// the first UIElement whose AutomationProperties.AutomationId matches <paramref name="automationId"/>.
    /// </summary>
    private static UIElement? FindByAutomationId(FrameworkElement element, string automationId)
    {
        var root = element.XamlRoot?.Content;
        if (root is null) return null;
        return WalkForAutomationId(root, automationId);
    }

    private static UIElement? WalkForAutomationId(DependencyObject node, string automationId)
    {
        if (node is UIElement uie
            && Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(uie) == automationId)
            return uie;

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i);
            var found = WalkForAutomationId(child, automationId);
            if (found is not null) return found;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Tracks the currently-attached event handlers on a FrameworkElement so they
    /// can be detached before new ones are attached. Stored as the element's Tag
    /// (or alongside it in a wrapper if Tag is already used for pool identity).
    /// </summary>
    /// <summary>
    /// Per-element handler state. Holds the <b>current</b> user delegate for each event
    /// plus a bit tracking whether the stable trampoline has been attached yet. The
    /// trampoline reads from the mutable <c>Current*</c> field when it fires, so updating
    /// a handler just swaps the field — no WinUI subscribe/unsubscribe churn.
    /// </summary>
    internal sealed class ModifierEventHandlerState
    {
        // Current user handlers (mutable; null means "no-op")
        public Action<object, SizeChangedEventArgs>? CurrentSizeChanged;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerPressed;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerMoved;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerReleased;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerEntered;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerExited;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerCanceled;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerCaptureLost;
        public Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? CurrentPointerWheelChanged;
        public Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? CurrentTapped;
        public Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? CurrentDoubleTapped;
        public Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? CurrentRightTapped;
        public Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? CurrentHolding;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentKeyDown;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentKeyUp;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentPreviewKeyDown;
        public Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? CurrentPreviewKeyUp;
        public Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? CurrentCharacterReceived;
        public Action<object, RoutedEventArgs>? CurrentGotFocus;
        public Action<object, RoutedEventArgs>? CurrentLostFocus;
        public Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? CurrentAccessKeyDisplayRequested;

        // Stable trampoline delegates — captured for reference-equality detach (never used)
        // and to prevent GC collection of the compiler-generated closure.
        public SizeChangedEventHandler? SizeChangedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerPressedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerMovedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerReleasedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerEnteredTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerExitedTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerCanceledTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerCaptureLostTrampoline;
        public Microsoft.UI.Xaml.Input.PointerEventHandler? PointerWheelChangedTrampoline;
        public Microsoft.UI.Xaml.Input.TappedEventHandler? TappedTrampoline;
        public Microsoft.UI.Xaml.Input.DoubleTappedEventHandler? DoubleTappedTrampoline;
        public Microsoft.UI.Xaml.Input.RightTappedEventHandler? RightTappedTrampoline;
        public Microsoft.UI.Xaml.Input.HoldingEventHandler? HoldingTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? KeyDownTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? KeyUpTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? PreviewKeyDownTrampoline;
        public Microsoft.UI.Xaml.Input.KeyEventHandler? PreviewKeyUpTrampoline;
        public global::Windows.Foundation.TypedEventHandler<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? CharacterReceivedTrampoline;
        public RoutedEventHandler? GotFocusTrampoline;
        public RoutedEventHandler? LostFocusTrampoline;
        public global::Windows.Foundation.TypedEventHandler<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? AccessKeyDisplayRequestedTrampoline;

        /// <summary>
        /// Null out every Current* user delegate so trampolines already attached
        /// on the native event source become no-ops. Trampoline delegate fields
        /// are left intact — they're rooted by WinUI's subscription list and
        /// can't be detached here without access to the native element.
        /// </summary>
        public void ClearCurrentHandlers()
        {
            CurrentSizeChanged = null;
            CurrentPointerPressed = null;
            CurrentPointerMoved = null;
            CurrentPointerReleased = null;
            CurrentPointerEntered = null;
            CurrentPointerExited = null;
            CurrentPointerCanceled = null;
            CurrentPointerCaptureLost = null;
            CurrentPointerWheelChanged = null;
            CurrentTapped = null;
            CurrentDoubleTapped = null;
            CurrentRightTapped = null;
            CurrentHolding = null;
            CurrentKeyDown = null;
            CurrentKeyUp = null;
            CurrentPreviewKeyDown = null;
            CurrentPreviewKeyUp = null;
            CurrentCharacterReceived = null;
            CurrentGotFocus = null;
            CurrentLostFocus = null;
            CurrentAccessKeyDisplayRequested = null;
        }
    }

    // ModifierEventHandlerState lives on the ReactorState wrapper attached to the
    // native DependencyObject via ReactorAttached.StateProperty. That keys
    // on native identity, so two RCWs pointing at the same element share one
    // ModifierEventHandlerState and one set of trampolines — fixing issue #86.
    private static ModifierEventHandlerState GetOrCreateModifierState(FrameworkElement fe)
    {
        var state = GetOrCreateReactorState(fe);
        return state.Modifiers ??= new ModifierEventHandlerState();
    }

    private static bool HasAnyEventHandler(ElementModifiers? m)
    {
        if (m is null) return false;
        return m.OnSizeChanged is not null
            || m.OnPointerPressed is not null || m.OnPointerMoved is not null || m.OnPointerReleased is not null
            || m.OnPointerEntered is not null || m.OnPointerExited is not null || m.OnPointerCanceled is not null
            || m.OnPointerCaptureLost is not null || m.OnPointerWheelChanged is not null
            || m.OnTapped is not null || m.OnDoubleTapped is not null || m.OnRightTapped is not null || m.OnHolding is not null
            || m.OnKeyDown is not null || m.OnKeyUp is not null
            || m.OnPreviewKeyDown is not null || m.OnPreviewKeyUp is not null
            || m.OnCharacterReceived is not null
            || m.OnGotFocus is not null || m.OnLostFocus is not null
            || m.OnAccessKeyDisplayRequested is not null;
    }

    private static bool HasAnyPointerHandler(ElementModifiers m)
    {
        return m.OnPointerPressed is not null || m.OnPointerMoved is not null || m.OnPointerReleased is not null
            || m.OnPointerEntered is not null || m.OnPointerExited is not null || m.OnPointerCanceled is not null
            || m.OnPointerCaptureLost is not null || m.OnPointerWheelChanged is not null
            || m.OnTapped is not null || m.OnDoubleTapped is not null || m.OnRightTapped is not null || m.OnHolding is not null;
    }

    private static void ApplyEventHandlers(FrameworkElement fe, ElementModifiers? oldM, ElementModifiers m)
    {
        // Fast path: nothing to do
        if (!HasAnyEventHandler(m) && !HasAnyEventHandler(oldM)) return;

        var state = GetOrCreateModifierState(fe);

        // Trampoline pattern: each Ensure* helper points the current-handler field at
        // the new delegate and, if the trampoline isn't attached yet, attaches it once.
        // Subsequent renders that just hand us a fresh closure touch only the field —
        // no add_/remove_ COM traffic on the underlying WinUI event.
        EnsureSizeChangedSubscribed(fe, state, m.OnSizeChanged);
        EnsurePointerPressedSubscribed(fe, state, m.OnPointerPressed);
        EnsurePointerMovedSubscribed(fe, state, m.OnPointerMoved);
        EnsurePointerReleasedSubscribed(fe, state, m.OnPointerReleased);
        EnsurePointerEnteredSubscribed(fe, state, m.OnPointerEntered);
        EnsurePointerExitedSubscribed(fe, state, m.OnPointerExited);
        EnsurePointerCanceledSubscribed(fe, state, m.OnPointerCanceled);
        EnsurePointerCaptureLostSubscribed(fe, state, m.OnPointerCaptureLost);
        EnsurePointerWheelChangedSubscribed(fe, state, m.OnPointerWheelChanged);
        EnsureTappedSubscribed(fe, state, m.OnTapped, oldM?.OnTapped);
        EnsureDoubleTappedSubscribed(fe, state, m.OnDoubleTapped, oldM?.OnDoubleTapped);
        EnsureRightTappedSubscribed(fe, state, m.OnRightTapped, oldM?.OnRightTapped);
        EnsureHoldingSubscribed(fe, state, m.OnHolding, oldM?.OnHolding);
        EnsureKeyDownSubscribed(fe, state, m.OnKeyDown);
        EnsureKeyUpSubscribed(fe, state, m.OnKeyUp);
        EnsurePreviewKeyDownSubscribed(fe, state, m.OnPreviewKeyDown);
        EnsurePreviewKeyUpSubscribed(fe, state, m.OnPreviewKeyUp);
        EnsureCharacterReceivedSubscribed(fe, state, m.OnCharacterReceived);
        EnsureGotFocusSubscribed(fe, state, m.OnGotFocus);
        EnsureLostFocusSubscribed(fe, state, m.OnLostFocus);
        EnsureAccessKeyDisplayRequestedSubscribed(fe, state, m.OnAccessKeyDisplayRequested);

        // Shape auto-fill: Shape subclasses need a non-null Fill to hit-test pointer events.
        // If any pointer-family handler is attached and Fill is null, set transparent brush.
        if (fe is Microsoft.UI.Xaml.Shapes.Shape shape && shape.Fill is null && HasAnyPointerHandler(m))
        {
            shape.Fill = new SolidColorBrush(global::Microsoft.UI.Colors.Transparent);
        }
    }

    // ── Trampoline Ensure* helpers ──────────────────────────────────────
    // Each helper:
    //   1. Updates state.Current<Event> to the new user handler (may be null).
    //   2. On first non-null handler, allocates the stable trampoline, attaches
    //      it to the WinUI event, emits reactor:event.reattach once.
    //   3. Never detaches — the trampoline stays bound for the element's lifetime.
    //      When the user handler becomes null again, the trampoline dispatches no-op.

    // <snippet:event-trampoline>
    private static void EnsureSizeChangedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, SizeChangedEventArgs>? handler)
    {
        state.CurrentSizeChanged = handler;
        if (state.SizeChangedTrampoline is null && handler is not null)
        {
            state.SizeChangedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("SizeChanged");
                state.CurrentSizeChanged?.Invoke(s!, e);
            };
            fe.SizeChanged += state.SizeChangedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("SizeChanged", fe.GetType().Name);
        }
    }
    // </snippet:event-trampoline>

    private static void EnsurePointerPressedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerPressed = handler;
        if (state.PointerPressedTrampoline is null && handler is not null)
        {
            state.PointerPressedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerPressed");
                state.CurrentPointerPressed?.Invoke(s!, e);
            };
            fe.PointerPressed += state.PointerPressedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerPressed", fe.GetType().Name);
        }
    }

    private static void EnsurePointerMovedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerMoved = handler;
        if (state.PointerMovedTrampoline is null && handler is not null)
        {
            state.PointerMovedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerMoved");
                state.CurrentPointerMoved?.Invoke(s!, e);
            };
            fe.PointerMoved += state.PointerMovedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerMoved", fe.GetType().Name);
        }
    }

    private static void EnsurePointerReleasedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerReleased = handler;
        if (state.PointerReleasedTrampoline is null && handler is not null)
        {
            state.PointerReleasedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerReleased");
                state.CurrentPointerReleased?.Invoke(s!, e);
            };
            fe.PointerReleased += state.PointerReleasedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerReleased", fe.GetType().Name);
        }
    }

    private static void EnsurePointerEnteredSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerEntered = handler;
        if (state.PointerEnteredTrampoline is null && handler is not null)
        {
            state.PointerEnteredTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerEntered");
                state.CurrentPointerEntered?.Invoke(s!, e);
            };
            fe.PointerEntered += state.PointerEnteredTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerEntered", fe.GetType().Name);
        }
    }

    private static void EnsurePointerExitedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerExited = handler;
        if (state.PointerExitedTrampoline is null && handler is not null)
        {
            state.PointerExitedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerExited");
                state.CurrentPointerExited?.Invoke(s!, e);
            };
            fe.PointerExited += state.PointerExitedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerExited", fe.GetType().Name);
        }
    }

    private static void EnsurePointerCanceledSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerCanceled = handler;
        if (state.PointerCanceledTrampoline is null && handler is not null)
        {
            state.PointerCanceledTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerCanceled");
                state.CurrentPointerCanceled?.Invoke(s!, e);
            };
            fe.PointerCanceled += state.PointerCanceledTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerCanceled", fe.GetType().Name);
        }
    }

    private static void EnsurePointerCaptureLostSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerCaptureLost = handler;
        if (state.PointerCaptureLostTrampoline is null && handler is not null)
        {
            state.PointerCaptureLostTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerCaptureLost");
                state.CurrentPointerCaptureLost?.Invoke(s!, e);
            };
            fe.PointerCaptureLost += state.PointerCaptureLostTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerCaptureLost", fe.GetType().Name);
        }
    }

    private static void EnsurePointerWheelChangedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs>? handler)
    {
        state.CurrentPointerWheelChanged = handler;
        if (state.PointerWheelChangedTrampoline is null && handler is not null)
        {
            state.PointerWheelChangedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PointerWheelChanged");
                state.CurrentPointerWheelChanged?.Invoke(s!, e);
            };
            fe.PointerWheelChanged += state.PointerWheelChangedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PointerWheelChanged", fe.GetType().Name);
        }
    }

    private static void EnsureTappedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentTapped = handler;
        if (state.TappedTrampoline is null && handler is not null)
        {
            state.TappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("Tapped");
                state.CurrentTapped?.Invoke(s!, e);
            };
            fe.Tapped += state.TappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("Tapped", fe.GetType().Name);
        }
        if (handler is not null)
        {
            fe.IsTapEnabled = true;
            // Make tappable elements keyboard-accessible and visible in UIA tree
            if (fe is Microsoft.UI.Xaml.Controls.Control ctrl && !ctrl.IsTabStop)
                ctrl.IsTabStop = true;
        }
        else if (oldHandler is not null)
        {
            fe.IsTapEnabled = false;
        }
    }

    private static void EnsureDoubleTappedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentDoubleTapped = handler;
        if (state.DoubleTappedTrampoline is null && handler is not null)
        {
            state.DoubleTappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("DoubleTapped");
                state.CurrentDoubleTapped?.Invoke(s!, e);
            };
            fe.DoubleTapped += state.DoubleTappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("DoubleTapped", fe.GetType().Name);
        }
        if (handler is not null) fe.IsDoubleTapEnabled = true;
        else if (oldHandler is not null) fe.IsDoubleTapEnabled = false;
    }

    private static void EnsureRightTappedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs>? oldHandler)
    {
        state.CurrentRightTapped = handler;
        if (state.RightTappedTrampoline is null && handler is not null)
        {
            state.RightTappedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("RightTapped");
                state.CurrentRightTapped?.Invoke(s!, e);
            };
            fe.RightTapped += state.RightTappedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("RightTapped", fe.GetType().Name);
        }
        if (handler is not null) fe.IsRightTapEnabled = true;
        else if (oldHandler is not null) fe.IsRightTapEnabled = false;
    }

    private static void EnsureHoldingSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? handler, Action<object, Microsoft.UI.Xaml.Input.HoldingRoutedEventArgs>? oldHandler)
    {
        state.CurrentHolding = handler;
        if (state.HoldingTrampoline is null && handler is not null)
        {
            state.HoldingTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("Holding");
                state.CurrentHolding?.Invoke(s!, e);
            };
            fe.Holding += state.HoldingTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("Holding", fe.GetType().Name);
        }
        if (handler is not null) fe.IsHoldingEnabled = true;
        else if (oldHandler is not null) fe.IsHoldingEnabled = false;
    }

    private static void EnsureKeyDownSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentKeyDown = handler;
        if (state.KeyDownTrampoline is null && handler is not null)
        {
            state.KeyDownTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("KeyDown");
                state.CurrentKeyDown?.Invoke(s!, e);
            };
            fe.KeyDown += state.KeyDownTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("KeyDown", fe.GetType().Name);
        }
    }

    private static void EnsureKeyUpSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentKeyUp = handler;
        if (state.KeyUpTrampoline is null && handler is not null)
        {
            state.KeyUpTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("KeyUp");
                state.CurrentKeyUp?.Invoke(s!, e);
            };
            fe.KeyUp += state.KeyUpTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("KeyUp", fe.GetType().Name);
        }
    }

    private static void EnsurePreviewKeyDownSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentPreviewKeyDown = handler;
        if (state.PreviewKeyDownTrampoline is null && handler is not null)
        {
            state.PreviewKeyDownTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PreviewKeyDown");
                state.CurrentPreviewKeyDown?.Invoke(s!, e);
            };
            fe.PreviewKeyDown += state.PreviewKeyDownTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PreviewKeyDown", fe.GetType().Name);
        }
    }

    private static void EnsurePreviewKeyUpSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs>? handler)
    {
        state.CurrentPreviewKeyUp = handler;
        if (state.PreviewKeyUpTrampoline is null && handler is not null)
        {
            state.PreviewKeyUpTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("PreviewKeyUp");
                state.CurrentPreviewKeyUp?.Invoke(s!, e);
            };
            fe.PreviewKeyUp += state.PreviewKeyUpTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("PreviewKeyUp", fe.GetType().Name);
        }
    }

    private static void EnsureCharacterReceivedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<UIElement, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs>? handler)
    {
        state.CurrentCharacterReceived = handler;
        if (state.CharacterReceivedTrampoline is null && handler is not null)
        {
            state.CharacterReceivedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("CharacterReceived");
                state.CurrentCharacterReceived?.Invoke(s, e);
            };
            fe.CharacterReceived += state.CharacterReceivedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("CharacterReceived", fe.GetType().Name);
        }
    }

    private static void EnsureGotFocusSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, RoutedEventArgs>? handler)
    {
        state.CurrentGotFocus = handler;
        if (state.GotFocusTrampoline is null && handler is not null)
        {
            state.GotFocusTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("GotFocus");
                state.CurrentGotFocus?.Invoke(s!, e);
            };
            fe.GotFocus += state.GotFocusTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("GotFocus", fe.GetType().Name);
        }
    }

    private static void EnsureLostFocusSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<object, RoutedEventArgs>? handler)
    {
        state.CurrentLostFocus = handler;
        if (state.LostFocusTrampoline is null && handler is not null)
        {
            state.LostFocusTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("LostFocus");
                state.CurrentLostFocus?.Invoke(s!, e);
            };
            fe.LostFocus += state.LostFocusTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("LostFocus", fe.GetType().Name);
        }
    }

    private static void EnsureAccessKeyDisplayRequestedSubscribed(FrameworkElement fe, ModifierEventHandlerState state, Action<UIElement, Microsoft.UI.Xaml.Input.AccessKeyDisplayRequestedEventArgs>? handler)
    {
        state.CurrentAccessKeyDisplayRequested = handler;
        if (state.AccessKeyDisplayRequestedTrampoline is null && handler is not null)
        {
            state.AccessKeyDisplayRequestedTrampoline = (s, e) =>
            {
                Diagnostics.ReactorEventSource.Log.EventTrampolineDispatch("AccessKeyDisplayRequested");
                state.CurrentAccessKeyDisplayRequested?.Invoke(s!, e);
            };
            fe.AccessKeyDisplayRequested += state.AccessKeyDisplayRequestedTrampoline;
            Diagnostics.ReactorEventSource.Log.EventTrampolineAttached("AccessKeyDisplayRequested", fe.GetType().Name);
        }
    }

    /// <summary>
    /// Applies ThemeRef bindings by setting properties through WinUI's {ThemeResource}
    /// mechanism. Builds a local Style with ThemeResource setters and applies it to the
    /// element. WinUI then handles theme-reactive resolution natively — the live
    /// {ThemeResource} Style setter re-resolves on the control's effective-theme change,
    /// which tracks the per-element/inherited <c>ActualTheme</c> (an ancestor
    /// <c>RequestedTheme</c> override included), not just the app-level theme. This native
    /// self-heal is why a ThemeBindings-only element is safe to structurally skip
    /// (#758; empirically verified in <c>ThemeBindingsSkipSelfHealFixtures</c> across
    /// immediate and inherited ancestor <c>RequestedTheme</c> toggles). Contrast a
    /// <c>ResourceOverrides.ThemeRef</c>, which resolves to a CONCRETE brush at reconcile
    /// and does NOT self-heal (so it stays theme-sensitive for the skip gate).
    /// </summary>
    /// <summary>
    /// Spec 047 §14 Phase 1 (1.3) — promoted from private (per audit
    /// existing-api-surface.md). Applies <see cref="ThemeRef"/> bindings as
    /// a synthesized <c>{ThemeResource}</c>-driven Style. Provisional API;
    /// see <c>REACTOR_V1_PREVIEW</c>.
    /// </summary>
    public static void ApplyThemeBindings(FrameworkElement fe, IReadOnlyDictionary<string, ThemeRef> bindings)
    {
        var targetType = GetStyleTargetType(fe);
        if (targetType is null) return;

        var cacheKey = BuildCacheKey(targetType, bindings);

        // Cache hit: reuse the previously compiled Style
        if (_styleCache.TryGetValue(cacheKey, out var cachedStyle))
        {
            ApplyStyleToElement(fe, cachedStyle);
            return;
        }

        // Cache miss: build XAML, parse, and cache
        var setters = new global::System.Text.StringBuilder();
        foreach (var (property, themeRef) in bindings)
        {
            var dp = GetDependencyPropertyName(fe, property);
            if (dp is null) continue;
            var escapedResourceKey = global::System.Security.SecurityElement.Escape(themeRef.ResourceKey);
            setters.Append($"<Setter Property='{dp}' Value='{{ThemeResource {escapedResourceKey}}}'/>");
        }

        if (setters.Length == 0) return;

        try
        {
            var xaml =
                $"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{targetType}'>" +
                setters.ToString() +
                "</Style>";
            var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
            _styleCache.TryAdd(cacheKey, style);
            ApplyStyleToElement(fe, style);
        }
        catch (Exception ex)
        {
            Microsoft.UI.Reactor.Core.Diagnostics.DiagnosticLog.SwallowedError(
                Microsoft.UI.Reactor.Core.Diagnostics.LogCategory.Theme,
                "ApplyThemeBindings",
                ex);
        }
    }

    /// <summary>
    /// Issue #522 — counterpart to <see cref="ApplyThemeBindings"/>. When an
    /// element on a recycled control transitions from having
    /// <see cref="Element.ThemeBindings"/> to none, drop the previously
    /// applied themed Style so e.g. the red Foreground from an Error arm
    /// does not bleed into a subsequent Loading / Data render.
    ///
    /// <para>Ownership is tracked via the
    /// <c>ReactorAppliedThemeStyleProperty</c> attached DP, which stores the
    /// Style instance we wrote on the most recent <see cref="ApplyThemeBindings"/>
    /// call. <see cref="DependencyObject.ClearValue(DependencyProperty)"/>
    /// on <see cref="FrameworkElement.StyleProperty"/> fires only when
    /// <c>fe.Style</c> still references the same instance — preserving an
    /// author Setter (e.g. <c>.Set(c =&gt; c.Style = mine)</c>) that may have
    /// replaced our Style in between.</para>
    ///
    /// <para>This avoids per-FrameworkElement tracking via
    /// <c>ConditionalWeakTable</c> (fragile under RCW churn) and is robust to
    /// the global <c>_styleCache</c> being cleared between apply and clear —
    /// the marker holds the actual Style reference, not a cache lookup key.</para>
    /// </summary>
    internal static void ClearThemeBindings(FrameworkElement fe)
    {
        if (fe.GetValue(ReactorAppliedThemeStyleProperty) is not Style ownedStyle)
            return;
        if (ReferenceEquals(fe.Style, ownedStyle))
            fe.ClearValue(FrameworkElement.StyleProperty);
        fe.ClearValue(ReactorAppliedThemeStyleProperty);
    }

    /// <summary>
    /// Applies a cached style to an element. Clears any existing style first to
    /// force WinUI to re-evaluate <c>{ThemeResource}</c> setters against the
    /// element's current effective theme (which may have changed due to a parent's
    /// <see cref="FrameworkElement.RequestedTheme"/> override).
    /// </summary>
    private static void ApplyStyleToElement(FrameworkElement fe, Style cachedStyle)
    {
        // Clearing first forces WinUI to process the subsequent set as a
        // genuine change, re-resolving {ThemeResource} values. Without this,
        // re-applying the same cached Style reference is a no-op.
        if (fe.Style is not null)
            fe.Style = null;
        fe.Style = cachedStyle;

        // Track ownership for the symmetric ClearThemeBindings path (issue
        // #522). Always overwrite so an A → B ThemeBindings transition
        // updates the marker to the new Style.
        fe.SetValue(ReactorAppliedThemeStyleProperty, cachedStyle);
    }

    private static string? GetStyleTargetType(FrameworkElement fe) => fe switch
    {
        WinUI.Border => "Border",
        WinUI.StackPanel => "StackPanel",
        WinUI.Grid => "Grid",
        WinUI.Button => "Button",
        WinUI.TextBox => "TextBox",
        TextBlock => "TextBlock",
        WinUI.ContentControl => "ContentControl",
        WinUI.Panel => "Panel",
        WinUI.Control => "Control",
        _ => fe.GetType().Name,
    };

    private static string? GetDependencyPropertyName(FrameworkElement fe, string property)
    {
        if (property == "Background" && (fe is WinUI.Panel || fe is WinUI.Control || fe is WinUI.Border))
            return "Background";
        if (property == "Foreground" && (fe is WinUI.Control || fe is TextBlock))
            return "Foreground";
        if (property == "BorderBrush" && (fe is WinUI.Control || fe is WinUI.Border))
            return "BorderBrush";
        return null;
    }

    // ── Lightweight Styling: per-control resource overrides ────────────────

    /// <summary>
    /// Removes every <c>Resources</c> key this control's resource overrides wrote, leaving keys
    /// set outside Reactor in place. <see cref="ElementPool.CleanElement"/> calls it on pool
    /// return, because mount applies overrides only for an element that declares some.
    /// </summary>
    internal static void RemoveManagedResourceKeys(FrameworkElement fe)
        => ApplyResourceOverrides(fe, null, null);

    /// <summary>
    /// Applies per-control resource overrides (lightweight styling) to a
    /// <see cref="FrameworkElement"/>. Literal values are set directly;
    /// <see cref="ThemeRef"/>-based values are resolved from
    /// <c>Application.Current.Resources</c> using the element's own effective theme.
    /// </summary>
    /// <remarks>
    /// Binary-compatible original 3-arg surface (Spec 047 §14 Phase 1 (1.3), promoted
    /// from private per audit existing-api-surface.md). Delegates to the 4-arg overload
    /// with <see cref="ElementTheme.Default"/>; the reconciler uses the 4-arg form to
    /// pass an explicit ancestor-aware theme (needed at mount, before the control is
    /// parented). Provisional API; see <c>REACTOR_V1_PREVIEW</c>.
    /// </remarks>
    public static void ApplyResourceOverrides(
        FrameworkElement fe,
        Microsoft.UI.Reactor.Elements.ResourceOverrides? oldOverrides,
        Microsoft.UI.Reactor.Elements.ResourceOverrides? newOverrides)
        => ApplyResourceOverrides(fe, oldOverrides, newOverrides, ElementTheme.Default);

    /// <summary>
    /// Applies per-control resource overrides (lightweight styling), resolving
    /// <see cref="ThemeRef"/>-backed values against an explicit
    /// <paramref name="effectiveTheme"/>. Provisional API (Spec 047 §14 Phase 1);
    /// see <c>REACTOR_V1_PREVIEW</c>.
    /// </summary>
    /// <param name="fe">The control whose <c>Resources</c> receive the overrides.</param>
    /// <param name="oldOverrides">The previously-applied overrides, or null at mount.</param>
    /// <param name="newOverrides">The overrides to apply, or null to clear managed keys.</param>
    /// <param name="effectiveTheme">
    /// The ancestor-aware effective theme threaded by the reconciler. When non-Default,
    /// ThemeRef-backed overrides resolve their concrete brush against it via
    /// <see cref="ThemeRef.Resolve(string, bool)"/> — necessary at mount, where the
    /// element is not yet parented and so an ancestor <c>RequestedTheme</c> is
    /// unreachable through <paramref name="fe"/>'s own visual-tree walk. Pass
    /// <see cref="ElementTheme.Default"/> to fall back to fe-based resolution
    /// (for example, callers of the 3-arg overload).
    /// </param>
    public static void ApplyResourceOverrides(
        FrameworkElement fe,
        Microsoft.UI.Reactor.Elements.ResourceOverrides? oldOverrides,
        Microsoft.UI.Reactor.Elements.ResourceOverrides? newOverrides,
        ElementTheme effectiveTheme)
    {
        // Keys Reactor has set on this element, as opposed to keys set by XAML or other
        // sources: only these are removed when the overrides change. They live on the
        // native object (ReactorState.ManagedResourceKeys), because the managed wrapper can
        // be collected and re-projected between renders. A removal-only call on a control
        // that never got state has nothing of ours to strip, so it skips creating one.
        HashSet<string> managed;
        if (newOverrides is not null)
            managed = GetOrCreateReactorState(fe).ManagedResourceKeys ??= new HashSet<string>();
        else if (TryGetReactorState(fe, out var existing) && existing.ManagedResourceKeys is { } keys)
            managed = keys;
        else
            return;

        // Remove managed keys that are no longer present in the new overrides.
        // Issue #675 — drive removal off `managed` vs `newOverrides`, independent of
        // `oldOverrides` nullness. A control can carry Reactor-managed keys that did
        // NOT arrive via `oldOverrides` (e.g. a pooled/reused control, or the Mount
        // path which always passes oldOverrides==null): gating removal on
        // `oldOverrides is not null` would leak those stale keys into fe.Resources.
        // Removal only strips managed keys absent from newOverrides; the apply loop
        // below re-adds the current keys, so this never fights the apply path. At a
        // truly-fresh mount `managed` is empty and this is a no-op.
        if (managed.Count > 0)
        {
            var newKeys = newOverrides?.AllKeys.ToHashSet() ?? new HashSet<string>();
            foreach (var key in managed.ToArray())
            {
                if (!newKeys.Contains(key))
                {
                    fe.Resources?.Remove(key);
                    managed.Remove(key);
                }
            }
        }

        if (newOverrides is null) return;

        fe.Resources ??= new ResourceDictionary();

        // Apply literal resources
        foreach (var (key, value) in newOverrides.Literals)
        {
            fe.Resources[key] = value;
            managed.Add(key);
        }

        // Apply ThemeRef resources (resolved from Application.Current.Resources).
        // When the reconciler threaded an explicit ancestor-aware effective theme,
        // resolve against it: at mount fe is not yet parented, so its own visual-tree
        // walk cannot see an ancestor RequestedTheme and would otherwise fall back to
        // the app theme (the mount-theme bug). When no ambient theme is supplied
        // (Default — for example, direct public-API callers), keep the fe-based resolution.
        foreach (var (key, themeRef) in newOverrides.ThemeRefs)
        {
            var resolved = effectiveTheme != ElementTheme.Default
                ? ThemeRef.Resolve(themeRef.ResourceKey, effectiveTheme == ElementTheme.Dark)
                : ThemeRef.Resolve(themeRef.ResourceKey, fe);
            if (resolved is not null)
            {
                fe.Resources[key] = resolved;
                managed.Add(key);
            }
            else if (!newOverrides.Literals.ContainsKey(key) && managed.Remove(key))
            {
                // The source resource went away (e.g. a dictionary removed before
                // Theme.NotifyResourcesChanged): drop the brush this override wrote earlier
                // instead of leaving it stale. A literal for the same key, written above, stays.
                fe.Resources.Remove(key);
            }
        }
    }

    /// <summary>
    /// Sets or updates the flyout on a control. On first mount, creates a new flyout.
    /// On update, reconciles the content inside the existing flyout to keep it open.
    /// </summary>
    private void ApplyFlyoutAttachment(FrameworkElement fe, Element? oldFlyoutEl, Element newFlyoutEl, Action requestRerender)
    {
        // Read the existing flyout from whichever slot SetFlyoutOnControl writes to.
        var existingFlyout = GetFlyoutOnControl(fe);

        // If we have an existing flyout and old element, try to update in place
        if (oldFlyoutEl is not null && existingFlyout is not null)
        {
            UpdateFlyoutInPlace(existingFlyout, oldFlyoutEl, newFlyoutEl, requestRerender);
            return;
        }

        // First mount — create new flyout
        var flyout = CreateFlyoutFromElement(newFlyoutEl, requestRerender);
        if (flyout is null) return;

        SetFlyoutOnControl(fe, flyout);
    }

    /// <summary>
    /// Updates the content inside an existing flyout without replacing the flyout object.
    /// This keeps the flyout open while its content changes.
    /// </summary>
    private void UpdateFlyoutInPlace(WinPrim.FlyoutBase existingFlyout, Element oldEl, Element newEl, Action requestRerender)
    {
        // ContentFlyout → reconcile child content inside the existing Flyout
        if (newEl is ContentFlyoutElement newCf && existingFlyout is WinUI.Flyout flyout)
        {
            var oldContent = oldEl is ContentFlyoutElement oldCf ? oldCf.Content : null;
            if (oldContent is not null && flyout.Content is UIElement existingContent && CanUpdate(oldContent, newCf.Content))
            {
                var replacement = UpdateHostedChild(oldContent, newCf.Content, existingContent, requestRerender);
                if (replacement is not null && !ReferenceEquals(flyout.Content, replacement))
                    flyout.Content = replacement;
            }
            else
            {
                // Type changed — remount content
                flyout.Content = Mount(newCf.Content, requestRerender);
            }
            FlyoutPlacement.Apply(flyout, newCf.Placement);
            return;
        }

        // MenuFlyout → recreate items (lightweight, no open-state issue)
        if (newEl is MenuFlyoutContentElement newMf && existingFlyout is WinUI.MenuFlyout menuFlyout)
        {
            menuFlyout.Items.Clear();
            foreach (var item in newMf.Items) menuFlyout.Items.Add(MenuCommandFactory.CreateMenuFlyoutItem(item, this));
            FlyoutPlacement.Apply(menuFlyout, newMf.Placement);
            return;
        }

        // Fallback: plain element → reconcile inside existing Flyout
        if (existingFlyout is WinUI.Flyout plainFlyout && plainFlyout.Content is UIElement existingCtrl)
        {
            if (CanUpdate(oldEl, newEl))
            {
                var replacement = UpdateHostedChild(oldEl, newEl, existingCtrl, requestRerender);
                if (replacement is not null && !ReferenceEquals(plainFlyout.Content, replacement))
                    plainFlyout.Content = replacement;
            }
            else
            {
                plainFlyout.Content = Mount(newEl, requestRerender);
            }
        }
    }

    /// <summary>
    /// Which slot a flyout occupies on a given target control. Button-family targets
    /// get the control's own <c>Flyout</c> property, which WinUI opens on click; every
    /// other target falls back to <c>FlyoutBase.AttachedFlyout</c> metadata, which only
    /// opens via an explicit <c>ShowAttachedFlyout</c> call.
    /// </summary>
    internal enum FlyoutSlot
    {
        Attached,
        Button,
        SplitButton,
    }

    /// <summary>
    /// Single source of truth for the flyout-slot rule shared by
    /// <see cref="SetFlyoutOnControl"/> and <see cref="GetFlyoutOnControl"/>. Writers and
    /// readers that disagree on the slot silently lose the flyout, so both go through here.
    /// </summary>
    internal static FlyoutSlot ResolveFlyoutSlot(Type targetType)
    {
        // Check SplitButton before Button (SplitButton doesn't inherit from Button,
        // but DropDownButton does, so the Button catch-all handles it).
        if (typeof(WinUI.SplitButton).IsAssignableFrom(targetType)) return FlyoutSlot.SplitButton;
        if (typeof(WinUI.Button).IsAssignableFrom(targetType)) return FlyoutSlot.Button;  // AppBarButton, DropDownButton inherit from Button
        return FlyoutSlot.Attached;
    }

    internal void SetFlyoutOnControl(FrameworkElement fe, WinPrim.FlyoutBase flyout)
    {
        switch (ResolveFlyoutSlot(fe.GetType()))
        {
            case FlyoutSlot.SplitButton: ((WinUI.SplitButton)fe).Flyout = flyout; break;
            case FlyoutSlot.Button: ((WinUI.Button)fe).Flyout = flyout; break;
            default: WinPrim.FlyoutBase.SetAttachedFlyout(fe, flyout); break;
        }
    }

    /// <summary>
    /// Reads back the flyout <see cref="SetFlyoutOnControl"/> installed. Update paths must
    /// use this rather than <c>GetAttachedFlyout</c> — a Button/SplitButton target holds its
    /// flyout in the control's own slot, so an attached-only lookup returns null and the
    /// caller creates a duplicate flyout the user can never see.
    /// </summary>
    internal static WinPrim.FlyoutBase? GetFlyoutOnControl(FrameworkElement fe) =>
        ResolveFlyoutSlot(fe.GetType()) switch
        {
            FlyoutSlot.SplitButton => ((WinUI.SplitButton)fe).Flyout,
            FlyoutSlot.Button => ((WinUI.Button)fe).Flyout,
            _ => WinPrim.FlyoutBase.GetAttachedFlyout(fe),
        };

    /// <summary>
    /// Detaches whatever <see cref="SetFlyoutOnControl"/> installed, so a pooled/reused target
    /// retains no stale flyout. Clears the same slot the writer and reader agree on.
    /// </summary>
    internal static void ClearFlyoutOnControl(FrameworkElement fe)
    {
        switch (ResolveFlyoutSlot(fe.GetType()))
        {
            case FlyoutSlot.SplitButton: ((WinUI.SplitButton)fe).Flyout = null; break;
            case FlyoutSlot.Button: ((WinUI.Button)fe).Flyout = null; break;
            default: WinPrim.FlyoutBase.SetAttachedFlyout(fe, null); break;
        }
    }

    /// <summary>
    /// Creates a WinUI FlyoutBase from a Reactor element descriptor.
    /// Recognizes ContentFlyoutElement and MenuFlyoutContentElement for configured flyouts,
    /// and falls back to wrapping plain elements in a basic Flyout.
    /// Used by both ApplyModifiers (for .WithFlyout()/.WithContextFlyout()) and
    /// button mount methods (for direct Flyout parameter).
    /// </summary>
    internal WinPrim.FlyoutBase? CreateFlyoutFromElement(Element flyoutEl, Action requestRerender)
    {
        switch (flyoutEl)
        {
            case ContentFlyoutElement cf:
            {
                var content = Mount(cf.Content, requestRerender);
                if (content is null) return null;
                var contentFlyout = new WinUI.Flyout { Content = content };
                FlyoutPlacement.Apply(contentFlyout, cf.Placement);
                return contentFlyout;
            }
            case MenuFlyoutContentElement mf:
            {
                var menuFlyout = new WinUI.MenuFlyout();
                // Placement is routed through FlyoutPlacement so Auto is never written
                // (WinUI's FlyoutBase validator rejects it — see FlyoutPlacement).
                FlyoutPlacement.Apply(menuFlyout, mf.Placement);
                foreach (var item in mf.Items) menuFlyout.Items.Add(MenuCommandFactory.CreateMenuFlyoutItem(item, this));
                return menuFlyout;
            }
            default:
            {
                var content = Mount(flyoutEl, requestRerender);
                return content is not null ? new WinUI.Flyout { Content = content } : null;
            }
        }
    }

    /// <summary>
    /// Spec 047 §14 Phase 3-final — descriptor-facing sibling of
    /// <see cref="CreateFlyoutFromElement"/>. Same shape as
    /// <see cref="IconResolver.ResolveIconForDescriptor"/>: a thin forwarder that tolerates
    /// <see langword="null"/> input so a <c>.OneWayBridged</c> entry on a
    /// button-family descriptor can wire
    /// <c>(c, v, rec, rr) =&gt; c.Flyout = rec.CreateFlyoutForDescriptor(v, rr)</c>
    /// without an upstream null guard.
    /// </summary>
    internal WinPrim.FlyoutBase? CreateFlyoutForDescriptor(Element? flyoutEl, Action requestRerender)
        => flyoutEl is null ? null : CreateFlyoutFromElement(flyoutEl, requestRerender);

=======
>>>>>>> upstream/main
    // ── Enum conversions removed — Reactor now uses WinUI types directly ──

    /// <summary>
    /// Tracks the state of a mounted component in the tree.
    /// </summary>
    internal class ComponentNode
    {
        /// <summary>The class-based Component instance (null for function components).</summary>
        public Component? Component { get; set; }
        /// <summary>The RenderContext for function components (null for class components).</summary>
        public RenderContext? Context { get; set; }
        /// <summary>The element tree output from the last Render() call.</summary>
        public Element? RenderedElement { get; set; }
        /// <summary>The ComponentElement or FuncElement that created this node.</summary>
        public Element? Element { get; set; }
        /// <summary>Previous props for memo comparison (class components only).</summary>
        public object? PreviousProps { get; set; }
        /// <summary>Dependencies from the last MemoElement render (null = render once).</summary>
        public object?[]? MemoDependencies { get; set; }
        /// <summary>Set to true when a self-triggered re-render is queued (UseState setter).
        /// Accessed from background threads (UseState callbacks) — use volatile field.</summary>
        private volatile bool _selfTriggered;
        public bool SelfTriggered { get => _selfTriggered; set => _selfTriggered = value; }
        /// <summary>
        /// <c>componentId</c> of this node in <c>ReactorEventSource.ComponentRendered</c> and the
        /// <c>Microsoft-UI-Reactor-Perf</c> events; 0 until first assigned
        /// (<see cref="EnsureDiagnosticId"/>). Lives on the node's <see cref="RenderContext"/>,
        /// so a state change the component makes reports the same id.
        /// </summary>
        public long DiagnosticId => (Component?.Context ?? Context)?.DiagnosticId ?? 0;

        /// <summary><see cref="DiagnosticId"/>, assigning it on first use.</summary>
        public long EnsureDiagnosticId()
            => (Component?.Context ?? Context)?.EnsureDiagnosticId() ?? 0;

        /// <summary>
        /// A function or memo component's name on the perf spans, resolved on its first traced
        /// render (see <see cref="PerfComponentName"/>); null until then and for class components.
        /// </summary>
        public string? PerfName { get; set; }
    }

    /// <summary>
    /// Tracks the state of a mounted ErrorBoundary in the tree.
    /// </summary>
    internal class ErrorBoundaryNode
    {
        public Element ChildElement { get; set; } = null!;
        public Element? RenderedElement { get; set; }
        public Exception? CaughtException { get; set; }
        public Func<Exception, Element> Fallback { get; set; } = null!;
    }

    /// <summary>
    /// Tags the <c>Border</c> wrapper of every mounted component boundary with its element.
    /// Called on this reconciler's UI thread when <see cref="global::Microsoft.UI.Reactor.Diagnostics.ReactorSourceMap.Enabled"/>
    /// turns on, so boundaries mounted while it was off become discoverable too — including
    /// cached subtrees that later renders skip without ever reaching <see cref="NeedsTag"/>.
    /// Walks <c>_componentNodes</c>, which holds every mounted Component/Func/Memo.
    /// Only untagged wrappers are filled in: a wrapper that already carries an element keeps
    /// it, because an outer decorator (a <c>Flyout</c> on a component target, for example)
    /// re-tags the wrapper with its own element and resolves its callbacks and unmount
    /// cleanup through that tag.
    /// </summary>
    internal void TagComponentBoundaries()
    {
        foreach (var (wrapper, node) in _componentNodes)
        {
            if (wrapper is not FrameworkElement fe || node.Element is not { } element || !IsComponentBoundary(element))
                continue;
            if (fe.GetValue(ReactorAttached.StateProperty) is ReactorState { Element: not null })
                continue;
            SetElementTagIfNeeded(fe, element);
        }
    }

    /// <summary>
    /// Hot Reload (spec 049 §6 step 3). Invokes <paramref name="action"/> once for
    /// every live <see cref="RenderContext"/> tracked by this reconciler — both
    /// function-component contexts (<see cref="ComponentNode.Context"/>) and
    /// class-component contexts (<see cref="Component.Context"/>). The host calls
    /// this at the start of a hot-reload pass to migrate hook state tree-wide
    /// before any component re-renders. The root component's context is NOT in
    /// <c>_componentNodes</c>, so the host migrates it separately.
    /// </summary>
    internal void ForEachLiveContext(Action<RenderContext> action)
    {
        foreach (var node in _componentNodes.Values)
        {
            if (node.Context is { } funcCtx) action(funcCtx);
            if (node.Component?.Context is { } classCtx) action(classCtx);
        }
    }

    /// <summary>
    /// Drops the <c>ComponentRendered</c> id mapping of every component this reconciler
    /// tracks, for teardown paths that discard the tree without unmounting it
    /// (<see cref="Dispose"/>, a host replacing its content with the error panel).
    /// Cheap when tracing was never on: no id was ever issued, so nothing is touched.
    /// </summary>
    internal void ForgetComponentDiagnostics()
    {
        foreach (var (control, node) in _componentNodes)
        {
            if (node.DiagnosticId != 0)
                Diagnostics.ComponentRenderControls.Registry.Forget(node.DiagnosticId, control);
        }
    }

    internal HashSet<V1Protocol.OverlayLifecycle.ContentDialogLifetime>? ContentDialogLifetimes;
    internal bool IsDisposingContentDialogs { get; private set; }

    internal void RetireContentDialogs()
    {
        if (IsDisposingContentDialogs) return;
        IsDisposingContentDialogs = true;
        if (ContentDialogLifetimes is not { Count: > 0 } lifetimes) return;
        foreach (var lifetime in lifetimes.ToArray())
            lifetime.Retire();
        ContentDialogLifetimes = null;
    }

    public void Dispose()
    {
        global::System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingPropagation;
        // Standalone disposal is its own outermost frame (issue #1291).
        using (RenderErrorDispatch.EnterPropagationScope())
            pendingPropagation = DisposeCollectingPropagation();
        pendingPropagation?.Throw();
    }

    /// <summary>
    /// Disposes and returns, rather than throws, a cleanup exception the app asked to
    /// propagate. Hosts call this inside their own propagation scope so the root's and the
    /// reconciler's cleanups share one "only the first propagation is rethrown" decision,
    /// and the host finishes its own teardown before rethrowing.
    /// </summary>
    internal global::System.Runtime.ExceptionServices.ExceptionDispatchInfo? DisposeCollectingPropagation()
    {
        RetireContentDialogs();
        // Hosts dispose through here rather than Dispose(); a disposed reconciler's nodes are dead.
        _disposedForDiagnostics = true;
        // Issue #1291: with a RenderErrorHandler configured, every effect cleanup runs and
        // each failure is reported (Source = Cleanup). With no handler the first throwing
        // cleanup escapes as before.
        // The tree is dropped without unmounting it; its ComponentRendered ids must stop resolving.
        ForgetComponentDiagnostics();
        global::System.Runtime.ExceptionServices.ExceptionDispatchInfo? pendingPropagation = null;
        // Resolved per failure (a cleanup may change the handler), not once per batch.
        Func<RenderErrorHandler?> cleanupHandler = ResolveRenderErrorHandler;
        foreach (var node in _componentNodes.Values)
        {
            var name = node.Component?.GetType().Name;
            RenderErrorDispatch.RunCleanups(node.Context, cleanupHandler, name, isHostLevel: false, _logger, ref pendingPropagation);
            RenderErrorDispatch.RunCleanups(node.Component?.Context, cleanupHandler, name, isHostLevel: false, _logger, ref pendingPropagation);
        }
        _componentNodes.Clear();
        _errorBoundaryNodes.Clear();
        foreach (var node in _navigationHostNodes.Values)
        {
            node.TransitionGeneration++;
            node.ConnectedOperation?.Dispose();
            node.ConnectedOperation = null;
            if (node.RouteChangedHandler is not null)
                node.Handle.RouteChanged -= node.RouteChangedHandler;
            node.Handle.Detach();
            if (node.CurrentChildControl is not null)
                Navigation.ConnectedNavigationOperation.UnmountPage(this, node.CurrentChildControl);
            node.Cache?.Clear();
        }
        _navigationHostNodes.Clear();
        CancelOpacityRestarts();
        _pool.Clear();
        // The tree is dropped without unmounting, so a TitleBar this (ReactorHostControl)
        // reconciler registered with its containing window would otherwise stay registered
        // there for the window's lifetime. (issue #1396)
        if (_embeddedTitleBars is { } embedded)
        {
            _embeddedTitleBars = null;
            foreach (var bar in embedded)
                WithdrawEmbeddedTitleBar(bar);
        }
        return pendingPropagation;
    }
}
