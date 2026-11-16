using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using ThixNote.Core.Models;

namespace ThixNote.ViewModels;

/// <summary>A row of the note list: title, the first words, when and where.</summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
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
    public bool IsPlainText => File.Kind == NoteFileKind.PlainText;
    public string FolderText => File.FolderName;
    public bool HasFolder => File.FolderName.Length > 0;

    public string PreviewText => IsEncrypted
        ? "Mit Kennwort geschützt"
        : BuildPreview(File.Content);

    /// <summary>"14:28", "Gestern, 17:56", "12.11., 09:14" – and the folder, if the note is in one.</summary>
    public string MetaText => HasFolder ? $"{Ui.FormatTime(File.ModifiedUtc)} · {FolderText}" : Ui.FormatTime(File.ModifiedUtc);

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
