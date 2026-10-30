using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Hosting;
using WinUIGalleryReactor;

// The Uno gallery head.
//
// Mirrors samples/ReactorGallery/Program.cs minus the two Windows-only steps:
// there is no `reactor-gallery://` single-instance redirection or HKCU protocol
// registration (see Shims/GalleryDeepLinkShims.cs), and no docking interop to
// register because that subsystem is not part of the Uno port.
//
// As on Windows, no explicit width/height: the gallery is content-heavy and
// benefits from the OS-chosen default window size.
ReactorApp.Run<GalleryShell>(
    "Reactor Gallery (Uno)",
    configure: static host => XamlInterop.Register(host.Reconciler));
