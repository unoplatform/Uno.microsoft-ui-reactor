namespace Microsoft.UI.Reactor.Core;

// Uno ships two element-factory families. Microsoft.UI.Xaml.IElementFactory (what the
// shared ElementFactory<T> declares) is a not-implemented stub on Skia: its args' getters
// throw. Uno's ItemsRepeater only accepts Microsoft.UI.Xaml.Controls.IElementFactoryShim:
// anything else throws ArgumentException("ItemTemplate") when it is assigned, which took
// down every LazyVStack / LazyHStack / ItemsRepeater, and left ItemsView empty (it catches
// the throw).
//
// GlobalUsings.cs points the bare ElementFactoryGetArgs / ElementFactoryRecycleArgs names
// at the shim's args types, so the shared GetElement / RecycleElement implement the shim
// below without touching shared source. The stub interface stays declared, so
// ItemsView.ItemTemplate (typed IElementFactory) and the shared `is IElementFactory`
// checks keep compiling; its members are implemented explicitly here and are unreachable,
// since its args can't carry data on Skia.
public sealed partial class ElementFactory<T> : Microsoft.UI.Xaml.Controls.IElementFactoryShim
{
    Microsoft.UI.Xaml.UIElement Microsoft.UI.Xaml.IElementFactory.GetElement(
        Microsoft.UI.Xaml.ElementFactoryGetArgs args)
        => throw new NotSupportedException("Uno calls the factory through IElementFactoryShim.");

    void Microsoft.UI.Xaml.IElementFactory.RecycleElement(
        Microsoft.UI.Xaml.ElementFactoryRecycleArgs args)
        => throw new NotSupportedException("Uno calls the factory through IElementFactoryShim.");
}
