// Uno replacement for the Windows framework's WindowIcon
// (src/Reactor/Hosting/WindowIcon.cs), which the port excludes: its binary
// sources build Win32 HICONs and its Apply goes through AppWindow.SetIcon(IconId).
//
// The shared Core/V1Protocol/TitleBarIconDefault.cs reads WindowSpec.Icon and
// resolves it with TryResolvePath to give a TitleBar the window's icon, so this
// keeps the two file-backed sources and ports that resolver as-is. The binary
// factories (FromBytes / FromRgba) are left out: no Uno surface consumes them.

using System.Diagnostics;

namespace Microsoft.UI.Reactor;

/// <summary>
/// An icon file for <see cref="WindowSpec.Icon"/>. Mirrors the file-backed half of
/// the Windows framework's type: a filesystem path (<see cref="FromPath"/>) or an
/// <c>ms-appx:///</c> asset URI (<see cref="FromResource"/>).
/// </summary>
/// <remarks>
/// On Uno the declared icon reaches only the <c>TitleBar(...)</c> icon default, and is
/// not applied to the window caption. Uno's <c>TitleBar</c> control is itself not
/// implemented yet, so today the icon has no visible effect.
/// </remarks>
public sealed class WindowIcon
{
    private WindowIcon(string source, WindowIconKind kind)
    {
        Source = source;
        Kind = kind;
    }

    /// <summary>The path or resource URI this icon was constructed from.</summary>
    public string Source { get; }

    /// <summary>Which factory produced this icon.</summary>
    public WindowIconKind Kind { get; }

    /// <summary>True when constructed via <see cref="FromResource"/>.</summary>
    public bool IsResource => Kind == WindowIconKind.Resource;

    /// <summary>Always false on Uno, which has no binary icon sources.</summary>
    public bool IsBinary => Kind == WindowIconKind.Binary;

    /// <summary>
    /// Create an icon from a filesystem path (typically a <c>.ico</c>). A relative path
    /// means "beside the app". Throws on null/empty input.
    /// </summary>
    public static WindowIcon FromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("WindowIcon path must be non-empty.", nameof(path));
        return new WindowIcon(path, WindowIconKind.Path);
    }

    /// <summary>
    /// Create an icon from an app asset URI (e.g. <c>ms-appx:///Assets/AppIcon.ico</c>),
    /// mapped to the file under the app's base directory. Throws on null/empty input.
    /// </summary>
    public static WindowIcon FromResource(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            throw new ArgumentException("WindowIcon resource URI must be non-empty.", nameof(uri));
        return new WindowIcon(uri, WindowIconKind.Resource);
    }

    /// <summary>
    /// Resolves this icon to an existing file on disk. Same contract as the Windows
    /// framework: <c>false</c> when the source names no existing file, and <c>true</c>
    /// with the unverified source when the existence probe itself throws.
    /// </summary>
    internal bool TryResolvePath(out string resolved)
    {
        if (Kind == WindowIconKind.Resource)
        {
            if (!TryResolveResourceUri(Source, out resolved))
            {
                Debug.WriteLine(
                    $"[Reactor] WindowIcon.TryResolvePath: '{Source}' names no asset under {AppContext.BaseDirectory}.");
                return false;
            }
            return true;
        }

        if (!TryResolveExistingPath(Source, out resolved))
        {
            Debug.WriteLine($"[Reactor] WindowIcon.TryResolvePath: no icon file at '{Source}'.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Maps an <c>ms-appx:///Assets/App.ico</c> style URI onto an existing file under
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    internal static bool TryResolveResourceUri(string uri, out string resolved)
    {
        resolved = uri;
        const string scheme = "ms-appx:";
        if (string.IsNullOrEmpty(uri) ||
            !uri.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = uri.Substring(scheme.Length);

        // "ms-appx:///Assets/App.ico" has an empty authority; "ms-appx://pkg/Assets/App.ico"
        // names one. Drop the authority segment in both shapes.
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest.Substring(2);
            var slash = rest.IndexOf('/');
            rest = slash >= 0 ? rest.Substring(slash + 1) : string.Empty;
        }

        rest = rest.TrimStart('/');
        if (rest.Length == 0) return false;

        // Reject "..", rooted and drive-qualified segments before the join, so a
        // malformed source can never address a file outside BaseDirectory.
        if (Path.IsPathRooted(rest)
            || rest.Contains(':', StringComparison.Ordinal)
            || Array.IndexOf(rest.Split('/', '\\'), "..") >= 0)
        {
            Debug.WriteLine($"[Reactor] WindowIcon: '{uri}' is not an install-root-relative asset path.");
            return false;
        }

        try
        {
            var candidate = Path.Join(
                AppContext.BaseDirectory,
                rest.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(candidate)) return false;
            resolved = candidate;
            return true;
        }
        catch (Exception ex) when (IsPathProbeFailure(ex))
        {
            Debug.WriteLine($"[Reactor] WindowIcon: could not map '{uri}' to a path: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves a filesystem source to an existing absolute path, trying
    /// <see cref="AppContext.BaseDirectory"/> before the working directory.
    /// </summary>
    private static bool TryResolveExistingPath(string path, out string resolved)
    {
        resolved = path;
        try
        {
            if (Path.IsPathRooted(path))
                return File.Exists(path);

            var beside = Path.Join(AppContext.BaseDirectory, path);
            if (File.Exists(beside))
            {
                resolved = beside;
                return true;
            }

            if (File.Exists(path))
            {
                resolved = Path.GetFullPath(path);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (IsPathProbeFailure(ex))
        {
            Debug.WriteLine($"[Reactor] WindowIcon.TryResolveExistingPath: path probe failed for '{path}': {ex.Message}");
            resolved = path;
            return true;
        }
    }

    private static bool IsPathProbeFailure(Exception ex)
        => ex is ArgumentException
              or NotSupportedException
              or IOException
              or UnauthorizedAccessException
              or global::System.Security.SecurityException;
}
