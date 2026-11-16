using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThixNote.Core.Business;
using ThixNote.Core.Models;

namespace ThixNote.App.ViewModels;

/// <summary>One open file (one tab). Saves automatically shortly after the last change.</summary>
public sealed partial class DocumentViewModel : ObservableObject
{
    private readonly DispatcherTimer autosaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly bool wasCreatedEmpty;
    private bool isLoading;
    private string savedTitle;

    [ObservableProperty] private string filePath;
    [ObservableProperty] private string title;
    [ObservableProperty] private string text = string.Empty;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLocked;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isPreview;
    [ObservableProperty] private string unlockPassword = string.Empty;
    [ObservableProperty] private string unlockError = string.Empty;
    [ObservableProperty] private string statusText = "Gespeichert";

    private DocumentViewModel(string path, bool wasCreatedEmpty)
    {
        filePath = path;
        title = savedTitle = Path.GetFileNameWithoutExtension(path);
        this.wasCreatedEmpty = wasCreatedEmpty;
        autosaveTimer.Tick += (_, _) => Save();
    }

    /// <summary>Raised after the file was renamed or converted (old path, new path).</summary>
    public event Action<DocumentViewModel, string, string>? PathChanged;

    public event Action<DocumentViewModel>? Saved;

    public NoteFileKind Kind => NoteLibrary.GetKind(FilePath);
    public bool IsEncrypted => Kind == NoteFileKind.Encrypted;
    public bool IsMarkdown => Kind != NoteFileKind.PlainText;
    public bool ShowEditor => !IsLocked;
    public bool ShowTextEditor => !IsLocked && !IsPreview;
    public bool ShowPreview => !IsLocked && IsPreview;
    public bool ShowMarkdownToolbar => ShowTextEditor && IsMarkdown;
    public string TabTitle => string.IsNullOrWhiteSpace(Title) ? "Unbenannt" : Title;
    public string FolderName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? string.Empty;
    public string WordCountText => $"{Text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Length} Wörter";

    public string KindText => Kind switch
    {
        NoteFileKind.Encrypted => "Verschlüsselt (.txts)",
        NoteFileKind.PlainText => "Text (.txt)",
        _ => "Markdown (.md)",
    };

    /// <summary>Opens a file. Encrypted files that are not unlocked yet open in the locked state.</summary>
    public static DocumentViewModel Open(string path, bool wasCreatedEmpty = false)
    {
        var document = new DocumentViewModel(Path.GetFullPath(path), wasCreatedEmpty);
        document.LoadContent();
        return document;
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
            RenameIfTitleChanged();
            NoteContent.Write(FilePath, Text);
            IsDirty = false;
            StatusText = "Gespeichert";
            Saved?.Invoke(this);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = "Speichern fehlgeschlagen: " + exception.Message;
            return false;
        }
    }

    /// <summary>Saves and closes. Notes that were created and never filled are removed again.</summary>
    public void Close()
    {
        Save();
        autosaveTimer.Stop();
        var isUntouchedNewNote = wasCreatedEmpty && Text.Trim().Length == 0 && Title.StartsWith(NoteLibrary.DefaultTitle, StringComparison.Ordinal);
        if (isUntouchedNewNote)
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
            FilePath = await Task.Run(() => NoteContent.Protect(oldPath, content, password));
            StatusText = "Mit Kennwort geschützt";
            PathChanged?.Invoke(this, oldPath, FilePath);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Unprotect()
    {
        Save();
        var oldPath = FilePath;
        FilePath = NoteContent.Unprotect(oldPath);
        StatusText = "Kennwortschutz entfernt";
        PathChanged?.Invoke(this, oldPath, FilePath);
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
        MarkDirty();
    }

    partial void OnTitleChanged(string value)
    {
        OnPropertyChanged(nameof(TabTitle));
        MarkDirty();
    }

    partial void OnFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IsEncrypted));
        OnPropertyChanged(nameof(IsMarkdown));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(ShowMarkdownToolbar));
    }

    partial void OnIsLockedChanged(bool value) => NotifyEditorVisibility();

    partial void OnIsPreviewChanged(bool value) => NotifyEditorVisibility();

    private void NotifyEditorVisibility()
    {
        OnPropertyChanged(nameof(ShowEditor));
        OnPropertyChanged(nameof(ShowTextEditor));
        OnPropertyChanged(nameof(ShowPreview));
        OnPropertyChanged(nameof(ShowMarkdownToolbar));
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
        if (LegacyNotes.TrySplit(content, out var legacyTitle, out var body))
        {
            // Notes from the first ThixNote build carry a front matter block: clean it up on first unlock.
            Text = body;
            Title = legacyTitle.Length > 0 ? legacyTitle : Title;
        }
        else
        {
            SetTextWithoutTracking(content);
        }

        IsLocked = false;
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

    private void RenameIfTitleChanged()
    {
        if (Title == savedTitle)
        {
            return;
        }

        var oldPath = FilePath;
        var newPath = NoteLibrary.Rename(oldPath, Title);
        savedTitle = Title;
        if (newPath == oldPath)
        {
            return;
        }

        FilePath = newPath;
        PathChanged?.Invoke(this, oldPath, newPath);
    }
}
