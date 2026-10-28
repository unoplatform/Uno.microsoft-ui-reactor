using System;
using Android.Runtime;
using Microsoft.UI.Reactor;
using Uno.UI.Hosting;
using WinUIGalleryReactor;

namespace ReactorGalleryUno.Droid;

[global::Android.App.ApplicationAttribute(
    Label = "@string/ApplicationName",
    Icon = "@mipmap/icon",
    LargeHeap = true,
    HardwareAccelerated = true,
    Theme = "@style/Theme.App.Starting"
)]
public class Application : Uno.UI.Runtime.Android.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override UnoPlatformHost CreateHost() =>
        ReactorApp.CreateHost<GalleryShell>("Reactor Gallery (Uno)");
}
