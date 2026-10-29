// Android default entry: runs the app's Main from the Android Application.
//
// Android never calls Main: ART instantiates the Application class named in the
// manifest, and Uno runs the host its NativeApplication.CreateHost returns. This
// base class closes that gap. Its CreateHost invokes the app assembly's entry point
// (the top-level statements in Program.cs), where ReactorApp.Run records its options
// and returns on Android, then builds the host from those options. The app keeps one
// Program.cs for every target, with no `#if __ANDROID__`.
//
// Uno.Microsoft.UI.Reactor.targets compiles the app as an Exe (so it has an entry
// point) and injects the [Application] + [Activity] subclasses unless
// ReactorAndroidEntry is false.

#if __ANDROID__
using System;
using System.Reflection;
using Android.Runtime;
using Uno.UI.Hosting;

namespace Microsoft.UI.Reactor.Hosting;

/// <summary>
/// An Android <c>Application</c> that starts a Reactor app from its <c>Main</c>, so
/// <see cref="ReactorApp.Run{TRoot}"/> works on Android like on the other targets.
/// </summary>
/// <remarks>
/// Not used directly: the build adds the <c>[Application]</c> and launcher
/// <c>[Activity]</c> for you (<c>ReactorAndroidEntry</c>, on by default). An app that
/// sets <c>ReactorAndroidEntry</c> to <c>false</c> brings its own <c>NativeApplication</c>
/// and calls <see cref="ReactorApp.CreateHost{TRoot}"/> from it instead.
/// </remarks>
public abstract class ReactorAndroidApplication : Uno.UI.Runtime.Android.NativeApplication
{
    protected ReactorAndroidApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override UnoPlatformHost CreateHost()
    {
        var entryPoint = GetType().Assembly.EntryPoint
            ?? throw new InvalidOperationException(
                $"'{GetType().Assembly.GetName().Name}' has no entry point, so Reactor can't start it on Android. " +
                "Call ReactorApp.Run from the app's top-level statements, or set ReactorAndroidEntry to false " +
                "and start the app from your own NativeApplication with ReactorApp.CreateHost.");

        ReactorApp.Options = new ReactorAppOptions();
        var args = entryPoint.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
        entryPoint.Invoke(null, BindingFlags.DoNotWrapExceptions, binder: null, args, culture: null);

        var options = ReactorApp.Options;
        if (options.RootFactory is null && options.RootRenderFunc is null)
        {
            throw new InvalidOperationException(
                "The app's entry point returned without calling ReactorApp.Run, so there is nothing to start on Android.");
        }

        return UnoBootstrap.BuildHost();
    }
}
#endif
