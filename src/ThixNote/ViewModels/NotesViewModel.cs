using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.ViewModels;

/// <summary>What the note list shows: every note, the favourites or one folder.</summary>
public sealed record NotesView(NotesViewKind Kind, string? FolderPath = null)
{
    public static readonly NotesView All = new(NotesViewKind.All);
    public static readonly NotesView Favorites = new(NotesViewKind.Favorites);
}

public enum NotesViewKind
{
    All,
    Favorites,
    Folder,
}

/// <summary>
/// The notes page: the list of the library (view, search) and the open documents (tabs). Files are read and written by
/// the business layer only (NoteLibrary, NoteContent); what ThixNote remembers (tabs, favourites) goes through
/// SettingsService.Remember. Dialogs are provided by the window; the view model only decides when they are needed.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject
{
    private List<NoteFile> allNotes = [];
    private bool isSyncingSelection;

    [ObservableProperty] private NotesView view = NotesView.All;
    [ObservableProperty] private LibraryItemViewModel? selectedLibraryItem;
    [ObservableProperty] private DocumentViewModel? activeDocument;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string listTitle = "Alle Notizen";
    [ObservableProperty] private string listSubtitle = string.Empty;

    /// <summary>ThixNote is a small editor like Notepad (no menu, the list over the editor): the compact view, or a narrow window.</summary>
    [ObservableProperty] private bool isCompact;

    /// <summary>The compact view was chosen (Ctrl/Cmd+Shift+K, remembered); MainWindow lays the window out for it.</summary>
    [ObservableProperty] private bool isCompactMode;

    /// <summary>The window is narrower than 900 px: the status bar leaves out the characters and the encoding.</summary>
    [ObservableProperty] private bool isNarrow;

    /// <summary>The list next to the editor (wide) or over it (compact) is shown.</summary>
    [ObservableProperty] private bool isListOpen;

    [ObservableProperty] private int zoom;
    [ObservableProperty] private bool wordWrap;

    public NotesViewModel()
    {
        var settings = SettingsService.Get();
        zoom = settings.EditorZoom;
        wordWrap = settings.WordWrap;
        isListOpen = !settings.IsListHidden;
        isCompactMode = settings.IsCompactMode;
        PrepareNotesFolder();
        RefreshLibrary();
        RestoreSession();
    }

    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>The folders of the library (relative names), for the menu.</summary>
    public IReadOnlyList<string> Folders { get; private set; } = [];

    public string NotesFolder => SettingsService.NotesFolder;
    public bool ShowEmptyState => ActiveDocument is null;
    public bool IsActiveFavorite => ActiveDocument is not null && IsFavorite(ActiveDocument.FilePath);
    public bool ShowListEmpty => LibraryItems.Count == 0;
    public string ListEmptyText => SearchText.Trim().Length > 0 ? "Keine Notiz passt zur Suche." : View.Kind == NotesViewKind.Favorites ? "Noch keine Favoriten. Strg+D merkt eine Notiz vor." : "Noch keine Notizen hier.";
    public double EditorFontSize => 15 * Zoom / 100.0;
    public string ZoomText => $"{Zoom} %";

    // Dialogs are provided by the window.
    public Func<string, bool, Task<string?>>? PromptPassword { get; set; }
    public Func<string, string, string, Task<string?>>? PromptText { get; set; }
    public Func<string, Task>? ShowMessage { get; set; }
    public Func<string, string, string, bool, Task<bool>>? Confirm { get; set; }
    public Func<string, string, Task<string?>>? PickExportPath { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickFilesToOpen { get; set; }

    /// <summary>The folders changed (the menu shows them).</summary>
    public event Action? FoldersChanged;

    /// <summary>A note was created or a tab chosen: the editor takes the focus.</summary>
    public event Action? EditorFocusRequested;

    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!NoteLibrary.IsSupportedFile(path))
            {
                _ = ReportAsync($"ThixNote öffnet nur .md, .txt und .txts: {Path.GetFileName(path)}");
                continue;
            }

            OpenFile(path);
        }
    }

    /// <summary>Saves every tab and remembers the session (closing, update).</summary>
    public void SaveSession()
    {
        foreach (var document in Documents)
        {
            document.Save();
        }

        RememberTabs();
    }

    public void RefreshLibrary()
    {
        ReloadFolders();
        try
        {
            allNotes = NoteLibrary.ListNotes(NotesFolder).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            allNotes = [];
            _ = ReportAsync(exception.Message);
        }

        RefreshList();
    }

    /// <summary>
    /// The sync with MatCloud changed notes in the folder: open tabs follow a rename or move and show the new text, a tab of a
    /// note that went into the trash closes, favourites keep pointing to their notes; then the list is read again. Notes with
    /// unsaved changes are never among them (the sync waits for them).
    /// </summary>
    public void ApplySyncChanges(IReadOnlyList<NoteChange> changes)
    {
        foreach (var change in changes.Where(change => change.OldPath is not null))
        {
            var document = Documents.FirstOrDefault(document => NoteLibrary.IsSamePath(document.FilePath, change.OldPath));
            if (document is null)
            {
                SettingsService.Remember(settings => settings with
                {
                    Favorites = change.NewPath is null
                        ? settings.Favorites.Where(favorite => !NoteLibrary.IsSamePath(favorite, change.OldPath)).ToList()
                        : settings.Favorites.Select(favorite => NoteLibrary.IsSamePath(favorite, change.OldPath) ? change.NewPath : favorite).ToList(),
                });
                continue;
            }

            if (change.NewPath is not null)
            {
                document.ApplySyncChange(change.NewPath, change.ContentChanged);
                continue;
            }

            if (document.IsDirty)
            {
                continue;
            }

            document.Discard();
            SettingsService.Remember(settings => settings with { Favorites = settings.Favorites.Where(favorite => !NoteLibrary.IsSamePath(favorite, change.OldPath)).ToList() });
            var index = Documents.IndexOf(document);
            Documents.Remove(document);
            if (ReferenceEquals(ActiveDocument, document))
            {
                ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
            }

            RememberTabs();
        }

        RefreshLibrary();
    }

    /// <summary>Another notes folder was chosen in the settings: the tabs stay, the list shows the new folder.</summary>
    public void ReloadNotesFolder()
    {
        View = NotesView.All;
        PrepareNotesFolder();
        RefreshLibrary();
    }

    partial void OnViewChanged(NotesView value) => RefreshList();

    partial void OnSearchTextChanged(string value) => RefreshList();

    partial void OnSelectedLibraryItemChanged(LibraryItemViewModel? value)
    {
        if (value is null || isSyncingSelection)
        {
            return;
        }

        OpenFile(value.FullPath);
        if (IsCompact)
        {
            IsListOpen = false;
        }

        EditorFocusRequested?.Invoke();
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsActiveFavorite));
        SyncListSelection();
        RememberTabs();
    }

    partial void OnIsCompactChanged(bool value) => IsListOpen = !value && !SettingsService.Get().IsListHidden;

    partial void OnIsCompactModeChanged(bool value) => SettingsService.Remember(settings => settings with { IsCompactMode = value });

    partial void OnZoomChanged(int value)
    {
        OnPropertyChanged(nameof(EditorFontSize));
        OnPropertyChanged(nameof(ZoomText));
        SettingsService.Remember(settings => settings with { EditorZoom = value });
    }

    partial void OnWordWrapChanged(bool value) => SettingsService.Remember(settings => settings with { WordWrap = value });

    [RelayCommand]
    private void NewNote()
    {
        var folder = View.Kind == NotesViewKind.Folder ? View.FolderPath! : NotesFolder;
        var path = RunGuarded(() => NoteLibrary.CreateNote(folder));
        if (path is null)
        {
            return;
        }

        RefreshLibrary();
        OpenFile(path, wasCreatedEmpty: true);
        if (IsCompact)
        {
            IsListOpen = false;
        }

        EditorFocusRequested?.Invoke();
    }

    [RelayCommand]
    private async Task OpenFileFromDiskAsync()
    {
        if (PickFilesToOpen is not null)
        {
            OpenFiles(await PickFilesToOpen());
        }
    }

    [RelayCommand]
    private void CloseDocument(DocumentViewModel? document)
    {
        document ??= ActiveDocument;
        if (document is null)
        {
            return;
        }

        var index = Documents.IndexOf(document);
        RunGuarded(document.Close);
        Documents.Remove(document);
        if (ReferenceEquals(ActiveDocument, document))
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }

        RememberTabs();
        RefreshLibrary();
    }

    [RelayCommand]
    private void NextDocument() => MoveActiveDocument(+1);

    [RelayCommand]
    private void PreviousDocument() => MoveActiveDocument(-1);

    [RelayCommand]
    private void SaveActiveDocument() => ActiveDocument?.Save();

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (ActiveDocument is not { } document)
        {
            return;
        }

        var path = document.FilePath;
        SettingsService.Remember(settings =>
        {
            var favorites = settings.Favorites.Where(favorite => !NoteLibrary.IsSamePath(favorite, path)).ToList();
            if (favorites.Count == settings.Favorites.Count)
            {
                favorites.Add(path);
            }

            return settings with { Favorites = favorites };
        });
        OnPropertyChanged(nameof(IsActiveFavorite));
        RefreshList();
    }

    [RelayCommand]
    private async Task RenameActiveDocumentAsync()
    {
        var document = ActiveDocument;
        if (document is null || PromptText is null)
        {
            return;
        }

        var title = await PromptText("Notiz umbenennen", "Titel", document.Title);
        if (string.IsNullOrWhiteSpace(title) || title.Trim() == document.Title)
        {
            return;
        }

        await RunGuardedAsync(() => document.Rename(title.Trim()));
    }

    [RelayCommand]
    private async Task ProtectActiveDocumentAsync()
    {
        var document = ActiveDocument;
        if (document is null || document.IsEncrypted || PromptPassword is null)
        {
            return;
        }

        var password = await PromptPassword("Mit Kennwort schützen", true);
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            await document.ProtectAsync(password);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await ReportAsync(exception.Message);
        }
    }

    [RelayCommand]
    private async Task UnprotectActiveDocumentAsync()
    {
        var document = ActiveDocument;
        if (document is null || !document.IsEncrypted || document.IsLocked || Confirm is null)
        {
            return;
        }

        if (await Confirm("Kennwortschutz entfernen?", "Die Notiz wird danach unverschlüsselt als Markdown (.md) gespeichert.", "Schutz entfernen", true))
        {
            await RunGuardedAsync(document.Unprotect);
        }
    }

    [RelayCommand]
    private async Task ExportAsync(string formatName)
    {
        var document = ActiveDocument;
        if (document is null || document.IsLocked || PickExportPath is null || !Enum.TryParse<ExportFormat>(formatName, out var format))
        {
            return;
        }

        document.Save();
        var extension = NoteExporter.FileExtensionFor(format);
        var targetPath = await PickExportPath(document.TabTitle + extension, extension);
        if (targetPath is null)
        {
            return;
        }

        await RunGuardedAsync(() =>
        {
            NoteExporter.ExportToFile(document.FilePath, document.Title, document.Text, format, targetPath);
            document.StatusText = $"Exportiert: {Path.GetFileName(targetPath)}";
        });
    }

    [RelayCommand]
    private async Task MoveActiveDocumentToTrashAsync()
    {
        var document = ActiveDocument;
        if (document is null || Confirm is null)
        {
            return;
        }

        if (!await Confirm("In den Papierkorb verschieben?", $"„{document.TabTitle}“ kommt in den Papierkorb und lässt sich von dort zurückholen.", "In den Papierkorb", true))
        {
            return;
        }

        document.Discard();
        if (await RunGuardedAsync(() => NoteLibrary.MoveToTrash(document.FilePath)))
        {
            SettingsService.Remember(settings => settings with { Favorites = settings.Favorites.Where(favorite => !NoteLibrary.IsSamePath(favorite, document.FilePath)).ToList() });
            var index = Documents.IndexOf(document);
            Documents.Remove(document);
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
            RefreshLibrary();
        }
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        if (PromptText is null)
        {
            return;
        }

        var name = (await PromptText("Neuer Ordner", "Name des Ordners", string.Empty))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        var path = RunGuarded(() => NoteLibrary.CreateFolder(NotesFolder, name));
        if (path is not null)
        {
            ReloadFolders();
            View = new NotesView(NotesViewKind.Folder, path);
        }
    }

    [RelayCommand]
    private void OpenNotesFolder()
    {
        Directory.CreateDirectory(NotesFolder);
        Ui.OpenExternal(NotesFolder);
    }

    [RelayCommand]
    private void RevealActiveDocument()
    {
        if (ActiveDocument is { } document)
        {
            Ui.RevealFile(document.FilePath);
        }
    }

    [RelayCommand]
    private void ToggleList()
    {
        IsListOpen = !IsListOpen;
        if (!IsCompact)
        {
            SettingsService.Remember(settings => settings with { IsListHidden = !IsListOpen });
        }
    }

    [RelayCommand]
    private void ToggleCompactMode() => IsCompactMode = !IsCompactMode;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(SettingsService.MaxZoom, Zoom + 10);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(SettingsService.MinZoom, Zoom - 10);

    [RelayCommand]
    private void ZoomReset() => Zoom = 100;

    [RelayCommand]
    private void ToggleWordWrap() => WordWrap = !WordWrap;

    private void OpenFile(string path, bool wasCreatedEmpty = false)
    {
        var existing = Documents.FirstOrDefault(document => NoteLibrary.IsSamePath(document.FilePath, path));
        if (existing is not null)
        {
            ActiveDocument = existing;
            return;
        }

        var document = RunGuarded(() => DocumentViewModel.Open(path, wasCreatedEmpty));
        if (document is null)
        {
            return;
        }

        document.PathChanged += OnDocumentPathChanged;
        document.Saved += OnDocumentSaved;
        Documents.Add(document);
        ActiveDocument = document;
    }

    private void OnDocumentPathChanged(DocumentViewModel document, string oldPath, string newPath)
    {
        SettingsService.Remember(settings => settings with
        {
            Favorites = settings.Favorites.Select(favorite => NoteLibrary.IsSamePath(favorite, oldPath) ? newPath : favorite).ToList(),
        });
        RememberTabs();
        RefreshLibrary();
    }

    private void OnDocumentSaved(DocumentViewModel document)
    {
        var item = LibraryItems.FirstOrDefault(entry => NoteLibrary.IsSamePath(entry.FullPath, document.FilePath));
        if (item is not null && File.Exists(document.FilePath))
        {
            item.File = NoteLibrary.ReadNoteFile(NotesFolder, document.FilePath);
        }
    }

    private void RememberTabs() => SettingsService.Remember(settings => settings with
    {
        OpenFiles = Documents.Select(document => document.FilePath).ToList(),
        ActiveFile = ActiveDocument?.FilePath,
    });

    private void MoveActiveDocument(int step)
    {
        if (Documents.Count < 2 || ActiveDocument is null)
        {
            return;
        }

        var index = (Documents.IndexOf(ActiveDocument) + step + Documents.Count) % Documents.Count;
        ActiveDocument = Documents[index];
    }

    private void PrepareNotesFolder()
    {
        try
        {
            var isFirstStart = NoteLibrary.EnsureNotesFolder(NotesFolder);
            LegacyNotes.MigrateFolder(NotesFolder);
            if (isFirstStart && !AppPaths.HasOverride)
            {
                NoteLibrary.CreateNote(NotesFolder, "Willkommen bei ThixNote", WelcomeText);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = ReportAsync(exception.Message);
        }
    }

    private void RestoreSession()
    {
        var settings = SettingsService.Get();
        foreach (var path in settings.OpenFiles.Where(File.Exists))
        {
            OpenFile(path);
        }

        var active = Documents.FirstOrDefault(document => NoteLibrary.IsSamePath(document.FilePath, settings.ActiveFile));
        ActiveDocument = active ?? Documents.FirstOrDefault();
    }

    private void ReloadFolders()
    {
        var folders = RunGuarded(() => NoteLibrary.ListFolders(NotesFolder)) ?? [];
        var changed = !folders.SequenceEqual(Folders);
        Folders = folders;
        if (View.Kind == NotesViewKind.Folder && !Directory.Exists(View.FolderPath))
        {
            View = NotesView.All;
        }

        if (changed)
        {
            FoldersChanged?.Invoke();
        }
    }

    private void RefreshList()
    {
        var query = SearchText.Trim();
        var items = allNotes
            .Where(IsInCurrentView)
            .Where(note => query.Length == 0 || Matches(note, query))
            .OrderByDescending(note => note.ModifiedUtc)
            .Select(note => new LibraryItemViewModel(note, IsFavorite(note.FullPath)))
            .ToList();

        isSyncingSelection = true;
        LibraryItems.Clear();
        items.ForEach(LibraryItems.Add);
        isSyncingSelection = false;

        ListTitle = query.Length > 0 ? "Suche" : View.Kind switch
        {
            NotesViewKind.Favorites => "Favoriten",
            NotesViewKind.Folder => Path.GetRelativePath(NotesFolder, View.FolderPath!),
            _ => "Alle Notizen",
        };
        ListSubtitle = items.Count == 1 ? "1 Notiz" : $"{items.Count} Notizen";
        OnPropertyChanged(nameof(ShowListEmpty));
        OnPropertyChanged(nameof(ListEmptyText));
        SyncListSelection();
    }

    private void SyncListSelection()
    {
        isSyncingSelection = true;
        SelectedLibraryItem = ActiveDocument is null
            ? null
            : LibraryItems.FirstOrDefault(item => NoteLibrary.IsSamePath(item.FullPath, ActiveDocument.FilePath));
        isSyncingSelection = false;
    }

    private bool IsInCurrentView(NoteFile note)
    {
        // A search looks through every note, whatever the menu shows.
        if (SearchText.Trim().Length > 0 || View.Kind == NotesViewKind.All)
        {
            return true;
        }

        if (View.Kind == NotesViewKind.Favorites)
        {
            return IsFavorite(note.FullPath);
        }

        var relative = Path.GetRelativePath(View.FolderPath!, note.FullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool IsFavorite(string path) => SettingsService.Get().Favorites.Any(favorite => NoteLibrary.IsSamePath(favorite, path));

    private static bool Matches(NoteFile note, string query)
        => note.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
           || note.Content.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private T? RunGuarded<T>(Func<T> action) where T : class
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ = ReportAsync(exception.Message);
            return null;
        }
    }

    private void RunGuarded(Action action) => RunGuarded<object>(() =>
    {
        action();
        return new object();
    });

    private async Task<bool> RunGuardedAsync(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            await ReportAsync(exception.Message);
            return false;
        }
    }

    private async Task ReportAsync(string message)
    {
        if (ShowMessage is not null)
        {
            await ShowMessage(message);
        }
    }

    private const string WelcomeText =
        "Jede Notiz ist eine ganz normale Datei in **Dokumente/ThixNote**. Der Dateiname ist der Titel – er steht im Tab, " +
        "umbenennen geht mit F2 oder einem Doppelklick auf den Tab.\n\n" +
        "> Schnell notieren. Nichts weiter.\n\n" +
        "- `.md` Markdown, `.txt` reiner Text, `.txts` mit Kennwort verschlüsselt\n" +
        "- Mehrere Notizen gleichzeitig in **Tabs** (Strg+N neu, Strg+W schließen, Strg+Tab wechseln)\n" +
        "- Eine neue Notiz heißt nach ihrer ersten Zeile, bis du sie umbenennst\n" +
        "- Fenster schmal ziehen: ThixNote wird zum kleinen Editor wie Notepad\n" +
        "- Strg+E zeigt die Vorschau, das Schloss schützt eine Notiz mit Kennwort\n";
}
