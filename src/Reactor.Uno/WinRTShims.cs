// Shims for CsWinRT types the shared source references but Uno does not have.
//
// On Windows, Reactor is compiled against the C#/WinRT projection, so WinUI types
// like Microsoft.UI.Xaml.Controls.Page carry [WinRT.WindowsRuntimeTypeAttribute]
// and live in the native WinRT type system. Uno has no projection at all — every
// type, WinUI's included, is an ordinary managed type.
//
// Declaring the attribute here rather than guarding the shared source with
// #if REACTOR_UNO keeps the port at *zero* conditionals in src/Reactor, and the
// behaviour is right by construction: nothing in an Uno app is WinRT-projected,
// so `IsDefined(typeof(WindowsRuntimeTypeAttribute), ...)` is correctly always
// false and FrameNavigation falls through to asking the XAML type resolver —
// which is the whole point of that check.

namespace WinRT;

/// <summary>
/// Uno-side stand-in for CsWinRT's marker attribute. Deliberately never applied
/// to anything: on Uno there are no WinRT-projected types.
/// </summary>
[global::System.AttributeUsage(
    global::System.AttributeTargets.Class
    | global::System.AttributeTargets.Interface
    | global::System.AttributeTargets.Struct
    | global::System.AttributeTargets.Enum
    | global::System.AttributeTargets.Delegate,
    Inherited = false)]
internal sealed class WindowsRuntimeTypeAttribute : global::System.Attribute
{
    public WindowsRuntimeTypeAttribute() { }

    public WindowsRuntimeTypeAttribute(string sourceMetadata) => SourceMetadata = sourceMetadata;

    public string? SourceMetadata { get; }
}

/// <summary>
/// Uno-side stand-in for CsWinRT's binding-metadata attribute, which the shared
/// <c>TreeViewNodeData</c> carries so its <c>{Binding}</c> survives NativeAOT
/// trimming on Windows. Inert here: Uno's binding engine resolves properties
/// directly, with no CsWinRT <c>ICustomPropertyProvider</c> hop to generate.
/// </summary>
[global::System.AttributeUsage(
    global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct,
    Inherited = false)]
internal sealed class GeneratedBindableCustomPropertyAttribute : global::System.Attribute
{
    public GeneratedBindableCustomPropertyAttribute() { }

    public GeneratedBindableCustomPropertyAttribute(
        string[] propertyNames, global::System.Type[] indexerPropertyTypes)
    {
        PropertyNames = propertyNames;
        IndexerPropertyTypes = indexerPropertyTypes;
    }

    public string[]? PropertyNames { get; }

    public global::System.Type[]? IndexerPropertyTypes { get; }
}
