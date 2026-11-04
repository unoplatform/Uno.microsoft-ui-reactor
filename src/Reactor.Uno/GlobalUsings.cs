// Disambiguation aliases for the Uno port.
//
// Uno defines ElementFactoryGetArgs / ElementFactoryRecycleArgs in BOTH
// Microsoft.UI.Xaml and Microsoft.UI.Xaml.Controls, which makes the unqualified
// name in Core/ElementFactory.cs (it imports both namespaces) ambiguous. Pin it to
// the Controls types: those are the args of IElementFactoryShim, the only factory
// interface Uno's ItemsRepeater calls, so the shared GetElement / RecycleElement
// implement the shim as-is (the Microsoft.UI.Xaml ones are Skia stubs whose getters
// throw). See ElementFactoryShim.cs. Once Uno fixes that
// (https://github.com/unoplatform/uno-private/issues/2561), point these at
// Microsoft.UI.Xaml instead: the name stays ambiguous while Uno ships both.
global using ElementFactoryGetArgs = Microsoft.UI.Xaml.Controls.ElementFactoryGetArgs;
global using ElementFactoryRecycleArgs = Microsoft.UI.Xaml.Controls.ElementFactoryRecycleArgs;

// Uno's implicit `Microsoft.UI.Xaml.Controls` global using cannot be removed via
// <Using Remove> (Uno injects it from the SDK targets). The only bare-`SelectionMode`
// *type* reference in shared source is DataGridFactories.cs, which wants Reactor's
// enum; the WinUI list controls use control-specific enums (ListViewSelectionMode,
// …), so pinning the bare name to Reactor's type is safe project-wide.
global using SelectionMode = Microsoft.UI.Reactor.Controls.SelectionMode;
