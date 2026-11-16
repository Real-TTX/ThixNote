using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThixNote.Core.Business;
using ThixNote.Core.Models;

namespace ThixNote.ViewModels;

/// <summary>
/// One open file (one tab). The file name is the title: it lives in the tab, not in the text (rename: F2 or a double click
/// on the tab). A new note takes its first line as its title until it is renamed by hand. Saves shortly after the last change.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    private const int AutoTitleLength = 60;

    private static readonly Regex OpenTask = new(@"^(\s*[-*+]\s)\[ \]\s", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex DoneTask = new(@"^(\s*[-*+]\s)\[[xX]\]\s", RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly DispatcherTimer autosaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly bool wasCreatedEmpty;
    private bool isLoading;
    private bool hasAutoTitle;

    [ObservableProperty] private string filePath;
    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLocked;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isPreview;
    [ObservableProperty] private string unlockPassword = string.Empty;
    [ObservableProperty] private string unlockError = string.Empty;
    [ObservableProperty] private string statusText = "Gespeichert";
    [ObservableProperty] private int line = 1;
    [ObservableProperty] private int column = 1;

    private DocumentViewModel(string path, bool wasCreatedEmpty)
    {
        filePath = path;
        this.wasCreatedEmpty = wasCreatedEmpty;
        hasAutoTitle = wasCreatedEmpty && Title.StartsWith(NoteLibrary.DefaultTitle, StringComparison.Ordinal);
        autosaveTimer.Tick += (_, _) => Save();
    }

    /// <summary>Raised after the file was renamed or converted (old path, new path).</summary>
    public event Action<DocumentViewModel, string, string>? PathChanged;

    public event Action<DocumentViewModel>? Saved;

    public string Title => Path.GetFileNameWithoutExtension(FilePath);
    public NoteFileKind Kind => NoteLibrary.GetKind(FilePath);
    public bool IsEncrypted => Kind == NoteFileKind.Encrypted;
    public bool IsMarkdown => Kind != NoteFileKind.PlainText;
    public bool ShowTextEditor => !IsLocked && !IsPreview;
    public bool ShowPreview => !IsLocked && IsPreview;

    /// <summary>The formatting bar acts on Markdown in the editor (not on plain text, not in the preview).</summary>
    public bool CanFormat => ShowTextEditor && IsMarkdown;

    public bool HasUnlockError => UnlockError.Length > 0;

    public string TabTitle => string.IsNullOrWhiteSpace(Title) ? "Unbenannt" : Title;
    public string FolderName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? string.Empty;
    public int WordCount => Text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
    public string WordCountText => WordCount == 1 ? "1 Wort" : $"{WordCount} Wörter";
    public string CharacterCountText => Text.Length == 1 ? "1 Zeichen" : $"{Text.Length} Zeichen";
    public string PositionText => $"Zeile {Line}, Spalte {Column}";

    public string KindText => Kind switch
    {
        NoteFileKind.Encrypted => "Verschlüsselt",
        NoteFileKind.PlainText => "Text",
        _ => "Markdown",
    };

    /// <summary>
    /// The text for the preview: task lists drawn as boxes (the preview has no check boxes of its own). ☒ for a done task: the
    /// ticked ballot box (U+2611) is drawn as a coloured emoji by the system fonts.
    /// </summary>
    public string PreviewText => DoneTask.Replace(OpenTask.Replace(Text, "${1}☐ "), "${1}☒ ");

    /// <summary>Opens a file. Encrypted files that are not unlocked yet open in the locked state.</summary>
    public static DocumentViewModel Open(string path, bool wasCreatedEmpty = false)
    {
        var document = new DocumentViewModel(Path.GetFullPath(path), wasCreatedEmpty);
        document.LoadContent();
        return document;
    }

    /// <summary>Where the caret is (the editor reports it): line and column for the status bar.</summary>
    public void SetCaret(int caretIndex)
    {
        var index = Math.Clamp(caretIndex, 0, Text.Length);
        var lineStart = index == 0 ? 0 : Text.LastIndexOf('\n', index - 1) + 1;
        Line = 1 + Text.AsSpan(0, lineStart).Count('\n');
        Column = 1 + index - lineStart;
    }

    public bool Save()
    {
        autosaveTimer.Stop();
        if (!IsDirty || IsLocked)
        {
            return true;
        }

        try
        {
            NoteContent.Write(FilePath, Text);
            IsDirty = false;
            StatusText = "Gespeichert";
            TakeTitleFromFirstLine();
            Saved?.Invoke(this);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = "Speichern fehlgeschlagen: " + exception.Message;
            return false;
        }
    }

    /// <summary>Renames the file (the title); from now on the first line no longer names a new note.</summary>
    public void Rename(string newTitle)
    {
        Save();
        hasAutoTitle = false;
        MoveTo(NoteLibrary.Rename(FilePath, newTitle));
    }

    /// <summary>Saves and closes. Notes that were created and never filled are removed again.</summary>
    public void Close()
    {
        Save();
        autosaveTimer.Stop();
        if (wasCreatedEmpty && hasAutoTitle && Text.Trim().Length == 0)
        {
            NoteLibrary.DeleteIfEmpty(FilePath);
        }
    }

    public void Discard() => autosaveTimer.Stop();

    public async Task ProtectAsync(string password)
    {
        Save();
        var oldPath = FilePath;
        var content = Text;
        IsBusy = true;
        try
        {
            var newPath = await Task.Run(() => NoteContent.Protect(oldPath, content, password));
            MoveTo(newPath);
            StatusText = "Mit Kennwort geschützt";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Unprotect()
    {
        Save();
        MoveTo(NoteContent.Unprotect(FilePath));
        StatusText = "Kennwortschutz entfernt";
    }

    [RelayCommand]
    private void Lock()
    {
        if (!IsEncrypted || IsLocked)
        {
            return;
        }

        Save();
        NoteContent.Lock(FilePath);
        SetTextWithoutTracking(string.Empty);
        IsLocked = true;
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (UnlockPassword.Length == 0 || IsBusy)
        {
            return;
        }

        IsBusy = true;
        UnlockError = string.Empty;
        try
        {
            var path = FilePath;
            var password = UnlockPassword;
            var content = await Task.Run(() => NoteContent.Unlock(path, password));
            ApplyUnlockedContent(content);
        }
        catch (InvalidPasswordException exception)
        {
            UnlockError = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or InvalidNoteFileException or UnauthorizedAccessException)
        {
            UnlockError = exception.Message;
        }
        finally
        {
            UnlockPassword = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TogglePreview()
    {
        if (IsMarkdown && !IsLocked)
        {
            IsPreview = !IsPreview;
        }
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(WordCountText));
        OnPropertyChanged(nameof(CharacterCountText));
        OnPropertyChanged(nameof(PreviewText));
        MarkDirty();
    }

    partial void OnUnlockErrorChanged(string value) => OnPropertyChanged(nameof(HasUnlockError));

    partial void OnLineChanged(int value) => OnPropertyChanged(nameof(PositionText));

    partial void OnColumnChanged(int value) => OnPropertyChanged(nameof(PositionText));

    partial void OnFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsEncrypted));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(CanFormat));
    }

    partial void OnIsLockedChanged(bool value) => NotifyEditorVisibility();

    partial void OnIsPreviewChanged(bool value) => NotifyEditorVisibility();

    private void NotifyEditorVisibility()
    {
        OnPropertyChanged(nameof(ShowTextEditor));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(CanFormat));
    }

    /// <summary>A new note is named after its first line ("Neue Notiz" until there is one), until the user renames it.</summary>
    private void TakeTitleFromFirstLine()
    {
        if (!hasAutoTitle)
        {
            return;
        }

        var firstLine = Text.Split('\n').Select(line => line.Trim().TrimStart('#', '>', '-', '*', ' ').Trim()).FirstOrDefault(line => line.Length > 0);
        if (firstLine is null)
        {
            return;
        }

        var title = NoteLibrary.SanitizeTitle(firstLine.Length > AutoTitleLength ? firstLine[..AutoTitleLength].TrimEnd() : firstLine);
        if (title.Length == 0 || title == Title)
        {
            return;
        }

        try
        {
            MoveTo(NoteLibrary.Rename(FilePath, title));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            hasAutoTitle = false; // leave the name as it is rather than trying on every save
        }
    }

    private void MoveTo(string newPath)
    {
        var oldPath = FilePath;
        if (newPath == oldPath)
        {
            return;
        }

        FilePath = newPath;
        PathChanged?.Invoke(this, oldPath, newPath);
    }

    private void LoadContent()
    {
        if (IsEncrypted && !EncryptionSession.IsUnlocked(FilePath))
        {
            IsLocked = true;
            return;
        }

        SetTextWithoutTracking(NoteContent.Read(FilePath));
    }

    private void ApplyUnlockedContent(string content)
    {
        IsLocked = false;
        if (LegacyNotes.TrySplit(content, out var legacyTitle, out var body))
        {
            // Notes of the very first version (then still QuickNote) carry a front matter block: clean it up on first unlock.
            Text = body;
            if (legacyTitle.Length > 0)
            {
                Save();
                MoveTo(NoteLibrary.Rename(FilePath, legacyTitle));
            }
        }
        else
        {
            SetTextWithoutTracking(content);
        }
    }

    private void SetTextWithoutTracking(string content)
    {
        isLoading = true;
        Text = content;
        isLoading = false;
    }

    private void MarkDirty()
    {
        if (isLoading)
        {
            return;
        }

        IsDirty = true;
        StatusText = "Nicht gespeichert …";
        autosaveTimer.Stop();
        autosaveTimer.Start();
    }
}
