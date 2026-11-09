// Uno stand-in for Windows App SDK's Microsoft.Windows.Storage.Pickers, which the shared
// Core/Pickers.cs is written against. Uno doesn't implement that namespace, so these wrap
// the WinRT Windows.Storage.Pickers that Uno does implement. Mirrors only the members the
// shared source uses; options with no Uno equivalent are kept but have no effect.

using Microsoft.UI;
using WinRTPickers = global::Windows.Storage.Pickers;

namespace Microsoft.Windows.Storage.Pickers;

/// <summary>Where a picker suggests starting. Values match Windows App SDK.</summary>
public enum PickerLocationId
{
    DocumentsLibrary = 0,
    ComputerFolder = 1,
    Desktop = 2,
    Downloads = 3,
    MusicLibrary = 5,
    PicturesLibrary = 6,
    VideosLibrary = 7,
    Objects3D = 8,
    Unspecified = 9,
}

/// <summary>How a picker shows its items.</summary>
public enum PickerViewMode
{
    List = 0,
    Thumbnail = 1,
}

/// <summary>A picked file. On WebAssembly with the browser's File System Access API, <see cref="Path"/> is only the file name.</summary>
public sealed class PickFileResult
{
    internal PickFileResult(string path) => Path = path;

    public string Path { get; }
}

/// <summary>A picked folder. On WebAssembly with the browser's File System Access API, <see cref="Path"/> is only the folder name.</summary>
public sealed class PickFolderResult
{
    internal PickFolderResult(string path) => Path = path;

    public string Path { get; }
}

/// <summary>Uno stand-in for Windows App SDK's FileOpenPicker.</summary>
/// <remarks><see cref="FileTypeChoices"/> is flattened into one filter; <see cref="Title"/>, <see cref="SuggestedFolder"/>, <see cref="SuggestedStartFolder"/> and <see cref="InitialFileTypeIndex"/> have no effect.</remarks>
public sealed class FileOpenPicker
{
    public FileOpenPicker(WindowId windowId)
    {
    }

    public PickerViewMode ViewMode { get; set; }

    public PickerLocationId SuggestedStartLocation { get; set; }

    public string CommitButtonText { get; set; } = "";

    public string SettingsIdentifier { get; set; } = "";

    public string Title { get; set; } = "";

    public string SuggestedFolder { get; set; } = "";

    public string SuggestedStartFolder { get; set; } = "";

    public int InitialFileTypeIndex { get; set; }

    public IList<string> FileTypeFilter { get; } = new List<string>();

    public IDictionary<string, IList<string>> FileTypeChoices { get; } = new Dictionary<string, IList<string>>();

    public async Task<PickFileResult?> PickSingleFileAsync()
        => await CreatePicker().PickSingleFileAsync() is { } file ? new PickFileResult(file.Path) : null;

    public async Task<IReadOnlyList<PickFileResult>> PickMultipleFilesAsync()
    {
        var files = await CreatePicker().PickMultipleFilesAsync();
        return files is null ? [] : files.Select(f => new PickFileResult(f.Path)).ToList();
    }

    private WinRTPickers.FileOpenPicker CreatePicker()
    {
        var picker = new WinRTPickers.FileOpenPicker
        {
            ViewMode = (WinRTPickers.PickerViewMode)ViewMode,
            SuggestedStartLocation = PickerMapping.ToWinRT(SuggestedStartLocation),
            CommitButtonText = CommitButtonText,
            SettingsIdentifier = SettingsIdentifier,
        };
        // Windows App SDK shows all files for an empty filter; Uno throws.
        var filters = FileTypeChoices.Count > 0 ? FileTypeChoices.Values.SelectMany(e => e) : FileTypeFilter;
        foreach (var filter in filters.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            picker.FileTypeFilter.Add(filter);
        }
        if (picker.FileTypeFilter.Count == 0)
        {
            picker.FileTypeFilter.Add("*");
        }
        return picker;
    }
}

/// <summary>Uno stand-in for Windows App SDK's FolderPicker.</summary>
/// <remarks><see cref="PickMultipleFoldersAsync"/> picks a single folder: Uno's picker has no multiple-folder mode.</remarks>
public sealed class FolderPicker
{
    public FolderPicker(WindowId windowId)
    {
    }

    public PickerViewMode ViewMode { get; set; }

    public PickerLocationId SuggestedStartLocation { get; set; }

    public string CommitButtonText { get; set; } = "";

    public string SettingsIdentifier { get; set; } = "";

    public string Title { get; set; } = "";

    public string SuggestedFolder { get; set; } = "";

    public string SuggestedStartFolder { get; set; } = "";

    public async Task<PickFolderResult?> PickSingleFolderAsync()
        => await CreatePicker().PickSingleFolderAsync() is { } folder ? new PickFolderResult(folder.Path) : null;

    public async Task<IReadOnlyList<PickFolderResult>> PickMultipleFoldersAsync()
        => await PickSingleFolderAsync() is { } folder ? [folder] : [];

    private WinRTPickers.FolderPicker CreatePicker()
    {
        var picker = new WinRTPickers.FolderPicker
        {
            ViewMode = (WinRTPickers.PickerViewMode)ViewMode,
            SuggestedStartLocation = PickerMapping.ToWinRT(SuggestedStartLocation),
            CommitButtonText = CommitButtonText,
            SettingsIdentifier = SettingsIdentifier,
        };
        picker.FileTypeFilter.Add("*");
        return picker;
    }
}

/// <summary>Uno stand-in for Windows App SDK's FileSavePicker.</summary>
/// <remarks><see cref="Title"/>, <see cref="SuggestedFolder"/>, <see cref="SuggestedStartFolder"/>, <see cref="InitialFileTypeIndex"/> and <see cref="ShowOverwritePrompt"/> have no effect.</remarks>
public sealed class FileSavePicker
{
    public FileSavePicker(WindowId windowId)
    {
    }

    public PickerLocationId SuggestedStartLocation { get; set; }

    public string CommitButtonText { get; set; } = "";

    public string SettingsIdentifier { get; set; } = "";

    public string Title { get; set; } = "";

    public string SuggestedFileName { get; set; } = "";

    public string SuggestedFolder { get; set; } = "";

    public string SuggestedStartFolder { get; set; } = "";

    public string DefaultFileExtension { get; set; } = "";

    public int InitialFileTypeIndex { get; set; }

    public bool ShowOverwritePrompt { get; set; } = true;

    public IDictionary<string, IList<string>> FileTypeChoices { get; } = new Dictionary<string, IList<string>>();

    public async Task<PickFileResult?> PickSaveFileAsync()
    {
        var picker = new WinRTPickers.FileSavePicker
        {
            SuggestedStartLocation = PickerMapping.ToWinRT(SuggestedStartLocation),
            CommitButtonText = CommitButtonText,
            SettingsIdentifier = SettingsIdentifier,
            SuggestedFileName = SuggestedFileName,
            DefaultFileExtension = DefaultFileExtension,
        };
        foreach (var (label, extensions) in FileTypeChoices)
        {
            picker.FileTypeChoices.Add(label, extensions);
        }
        return await picker.PickSaveFileAsync() is { } file ? new PickFileResult(file.Path) : null;
    }
}

internal static class PickerMapping
{
    // Same numeric values in both enums, minus WinRT's HomeGroup (4).
    public static WinRTPickers.PickerLocationId ToWinRT(PickerLocationId location)
        => (WinRTPickers.PickerLocationId)(int)location;
}
