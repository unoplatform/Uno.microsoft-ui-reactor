// Name disambiguation for the source-shared gallery pages.
//
// Every gallery page carries `using Microsoft.UI.Xaml.Controls;` alongside
// `using static Microsoft.UI.Reactor.Factories;`. On Uno that pulls Uno's own
// SelectionMode into scope next to Reactor's, and the shared source refers to
// the bare name — so pin it here rather than forking the pages.
//
// Reactor's Reactor.Uno library does exactly the same thing for the same reason
// (see src/Reactor.Uno/GlobalUsings.cs).

global using SelectionMode = Microsoft.UI.Reactor.Controls.SelectionMode;

// In Debug, Uno.Sdk adds Uno.UI.HotDesign, which brings in Uno.Toolkit and its
// implicit `using Uno.Toolkit.UI;`. From Uno 7.0 the toolkit has its own Flex
// enums, which collide with Reactor's in the pages that use FlexPanel.
global using FlexAlign = Microsoft.UI.Reactor.Layout.FlexAlign;
global using FlexDirection = Microsoft.UI.Reactor.Layout.FlexDirection;
global using FlexJustify = Microsoft.UI.Reactor.Layout.FlexJustify;
global using FlexWrap = Microsoft.UI.Reactor.Layout.FlexWrap;
