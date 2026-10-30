// Added to the app by Uno.Microsoft.UI.Reactor.targets unless ReactorAndroidEntry
// is false. Android starts from these two classes instead of Main; the Application runs
// Main for it (see Microsoft.UI.Reactor.Hosting.ReactorAndroidApplication).
//
// ReactorAndroidEntryConfig is generated next to it (obj/ReactorAndroidEntry.g.cs)
// from the app's Uno.Resizetizer items: the launcher icon from UnoIcon, and the
// splash theme from UnoSplashScreen, as the unoapp template's own Platforms/Android
// files set them.

#nullable enable

namespace Microsoft.UI.Reactor.AndroidEntry
{
    [global::Android.App.Application(
        Icon = ReactorAndroidEntryConfig.Icon,
        LargeHeap = true,
        HardwareAccelerated = true,
        Theme = ReactorAndroidEntryConfig.Theme)]
    internal sealed class ReactorEntryApplication : global::Microsoft.UI.Reactor.Hosting.ReactorAndroidApplication
    {
        public ReactorEntryApplication(global::System.IntPtr javaReference, global::Android.Runtime.JniHandleOwnership transfer)
            : base(javaReference, transfer)
        {
        }
    }

    [global::Android.App.Activity(
        MainLauncher = true,
        ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
        WindowSoftInputMode = global::Android.Views.SoftInput.AdjustNothing | global::Android.Views.SoftInput.StateHidden)]
    internal sealed class ReactorEntryActivity : global::Uno.UI.Runtime.Android.ApplicationActivity
    {
        protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
        {
            if (ReactorAndroidEntryConfig.HasSplashScreen)
            {
                global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);
            }

            base.OnCreate(savedInstanceState);
        }
    }
}
