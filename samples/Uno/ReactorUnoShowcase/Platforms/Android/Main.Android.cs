using System;
using Android.Runtime;
using Microsoft.UI.Reactor;
using Uno.UI.Hosting;

namespace ReactorUnoShowcase.Droid;

[global::Android.App.ApplicationAttribute(
    Label = "@string/ApplicationName",
    LargeHeap = true,
    HardwareAccelerated = true,
    Theme = "@style/AppTheme"
)]
public class Application : Uno.UI.Runtime.Android.NativeApplication
{
    public Application(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override UnoPlatformHost CreateHost() =>
        ReactorApp.CreateHost<Showcase>("Reactor Showcase (Uno)");
}
