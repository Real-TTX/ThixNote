using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ThixNote.Core.Models;

namespace ThixNote.App.ViewModels;

/// <summary>Entry in the note list of the library.</summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    private static readonly CultureInfo German = new("de-DE");
    private static readonly Regex MarkdownSymbols = new(@"\*\*|__|`|\*(?=\S)|(?<=\S)\*", RegexOptions.Compiled);

    [ObservableProperty] private NoteFile file;
    [ObservableProperty] private bool isFavorite;

    public LibraryItemViewModel(NoteFile file, bool isFavorite)
    {
        this.file = file;
        this.isFavorite = isFavorite;
    }

    public string FullPath => File.FullPath;
    public string Title => File.Title;
    public bool IsEncrypted => File.Kind == NoteFileKind.Encrypted;
    public string FolderText => File.FolderName;
    public bool HasFolder => File.FolderName.Length > 0;

    public string PreviewText => IsEncrypted
        ? "Mit Kennwort geschützt"
        : BuildPreview(File.Content);

    public string DateText
    {
        get
        {
            var local = File.ModifiedUtc.ToLocalTime();
            return local.Date == DateTime.Today ? local.ToString("HH:mm", German) : local.ToString("d. MMMM yyyy", German);
        }
    }

    partial void OnFileChanged(NoteFile value) => OnPropertyChanged(string.Empty);

    private static string BuildPreview(string content)
    {
        var lines = content.Split('\n')
            .Select(line => line.Trim().TrimStart('#', '>', '-', '*', ' ').Replace("[ ]", "☐").Replace("[x]", "☑"))
            .Where(line => line.Length > 0)
            .Take(3);
        var preview = MarkdownSymbols.Replace(string.Join(" ", lines), string.Empty);
        return preview.Length == 0 ? "Leer" : preview.Length > 140 ? preview[..140] + "…" : preview;
    }
}

/// <summary>Entry in the sidebar navigation (a smart view or a folder).</summary>
public sealed record NavigationItem(string Label, NavigationKind Kind, string? FolderPath = null)
{
    public bool IsAllNotes => Kind == NavigationKind.AllNotes;
    public bool IsFavorites => Kind == NavigationKind.Favorites;
    public bool IsFolder => Kind == NavigationKind.Folder;
}

public enum NavigationKind
{
    AllNotes,
    Favorites,
    Folder,
}
