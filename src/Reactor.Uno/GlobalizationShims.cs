// Shim for a Windows.Globalization member the shared source calls but Uno only stubs.
//
// CalendarViewElement's descriptor (src/Reactor/Core/Element.cs) writes Language only when
// Windows.Globalization.Language.IsWellFormed accepts it. In Uno that method is a
// not-implemented stub that always throws, so mounting a CalendarView with a Language
// crashed (#2250). Declaring the type here, as WinRTShims.cs does for CsWinRT's
// attributes, keeps the port at zero conditionals in src/Reactor: a type declared in
// source wins over the imported one of the same name (CS0436, silenced in
// Reactor.Uno.csproj), so the shared call binds to this class. It has only what the
// shared source uses; a new use of Uno's Language there fails to compile instead of
// silently getting this one.
//
// Delete this file once Uno implements IsWellFormed.

using System;
using System.Globalization;

namespace Windows.Globalization;

/// <summary>
/// Uno-side stand-in for <c>Windows.Globalization.Language</c>, as far as the shared
/// source uses it.
/// </summary>
internal static class Language
{
    /// <summary>
    /// Whether .NET can build a <see cref="CultureInfo"/> for <paramref name="languageTag"/>,
    /// rather than WinRT's syntactic BCP-47 check: Uno turns the tag a CalendarView gets
    /// into a <see cref="CultureInfo"/>, so a tag that passes here can't throw there.
    /// Under ICU, .NET's default on every platform (Windows 10+ included), that is close
    /// to the syntactic check: a well-formed tag passes even without locale data
    /// ("qq-QQ"), and a few forms BCP-47 rejects do too ("en_US"). An app running with
    /// invariant globalization knows no culture, so there the Language is left unset
    /// instead.
    /// </summary>
    public static bool IsWellFormed(string languageTag)
    {
        if (string.IsNullOrEmpty(languageTag)) return false;

        try
        {
            _ = CultureInfo.GetCultureInfo(languageTag);
            return true;
        }
        catch (ArgumentException) // CultureNotFoundException
        {
            return false;
        }
    }
}
