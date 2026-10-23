using Android.App;
using Android.Views;

namespace ReactorGalleryUno.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : Uno.UI.Runtime.Android.ApplicationActivity
{
}
