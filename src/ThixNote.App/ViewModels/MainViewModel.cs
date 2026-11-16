using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ThixNote.Core.Business;
using ThixNote.Core.Models;

namespace ThixNote.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    /// <summary>Below this window width ThixNote becomes a small Notepad-like editor (library hidden, opens as overlay).</summary>
    private const double CompactWidth = 900;

    private const double LibraryWidth = 560;

    private static readonly NavigationItem AllNotesItem = new("Alle Notizen", NavigationKind.AllNotes);
    private static readonly NavigationItem FavoritesItem = new("Favoriten", NavigationKind.Favorites);

    private readonly AppSettings settings;
    private List<NoteFile> allNotes = [];
    private bool isSyncingSelection;

    [ObservableProperty] private NavigationItem? selectedView = AllNotesItem;
    [ObservableProperty] private NavigationItem? selectedFolder;
    [ObservableProperty] private LibraryItemViewModel? selectedLibraryItem;
    [ObservableProperty] private DocumentViewModel? activeDocument;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string listTitle = "Alle Notizen";
    [ObservableProperty] private string listSubtitle = string.Empty;
    [ObservableProperty] private string newFolderName = string.Empty;
    [ObservableProperty] private bool isCompact;
    [ObservableProperty] private bool isLibraryOpen;
    [ObservableProperty] private double libraryPaneWidth = LibraryWidth;
    [ObservableProperty] private bool showSidebarColumn = true;

    public MainViewModel()
    {
        settings = SettingsStore.Load();
        PrepareNotesFolder();
        isLibraryOpen = !settings.IsLibraryCollapsed;
        RefreshLibrary();
        RestoreSession();
    }

    public ObservableCollection<NavigationItem> Views { get; } = [AllNotesItem, FavoritesItem];
    public ObservableCollection<NavigationItem> Folders { get; } = [];
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    public string NotesFolder => settings.NotesFolder;
    public string VersionText => $"Version {VersionInfo.Current}";
    public double InitialWindowWidth => Math.Max(settings.WindowWidth, 420);
    public double InitialWindowHeight => Math.Max(settings.WindowHeight, 320);
    public SplitViewDisplayMode LibraryDisplayMode => IsCompact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
    public bool ShowEmptyState => ActiveDocument is null;
    public bool IsActiveFavorite => ActiveDocument is not null && IsFavorite(ActiveDocument.FilePath);

    // Dialogs are provided by the window; the view model only decides when they are needed.
    public Func<string, bool, Task<string?>>? PromptPassword { get; set; }
    public Func<string, Task>? ShowMessage { get; set; }
    public Func<string, Task<bool>>? Confirm { get; set; }
    public Func<string, string, Task<string?>>? PickExportPath { get; set; }
    public Func<Task<IReadOnlyList<string>>>? PickFilesToOpen { get; set; }

    public event Action? NewNoteCreated;

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

    /// <summary>Called by the window whenever its width changes.</summary>
    public void UpdateLayout(double windowWidth)
    {
        var compact = windowWidth < CompactWidth;
        if (compact != IsCompact)
        {
            IsCompact = compact;
            IsLibraryOpen = !compact && !settings.IsLibraryCollapsed;
            OnPropertyChanged(nameof(LibraryDisplayMode));
        }

        LibraryPaneWidth = compact ? Math.Min(LibraryWidth, windowWidth - 36) : LibraryWidth;
        ShowSidebarColumn = LibraryPaneWidth >= 520;
    }

    public void SaveSession(double windowWidth, double windowHeight)
    {
        foreach (var document in Documents)
        {
            document.Save();
        }

        settings.OpenFiles = Documents.Select(document => document.FilePath).ToList();
        settings.ActiveFile = ActiveDocument?.FilePath;
        settings.WindowWidth = windowWidth;
        settings.WindowHeight = windowHeight;
        SettingsStore.Save(settings);
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

    partial void OnSelectedViewChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedFolder = null;
        RefreshList();
    }

    partial void OnSelectedFolderChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        SelectedView = null;
        RefreshList();
    }

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
            IsLibraryOpen = false;
        }
    }

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsActiveFavorite));
        SyncListSelection();
    }

    [RelayCommand]
    private void NewNote()
    {
        var folder = SelectedFolder?.FolderPath ?? NotesFolder;
        var path = RunGuarded(() => NoteLibrary.CreateNote(folder));
        if (path is null)
        {
            return;
        }

        RefreshLibrary();
        OpenFile(path, wasCreatedEmpty: true);
        if (IsCompact)
        {
            IsLibraryOpen = false;
        }

        NewNoteCreated?.Invoke();
    }

    [RelayCommand]
    private async Task OpenFileFromDiskAsync()
    {
        if (PickFilesToOpen is null)
        {
            return;
        }

        OpenFiles(await PickFilesToOpen());
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
        if (ActiveDocument is null)
        {
            return;
        }

        var path = ActiveDocument.FilePath;
        var removedCount = settings.Favorites.RemoveAll(favorite => NoteLibrary.IsSamePath(favorite, path));
        if (removedCount == 0)
        {
            settings.Favorites.Add(path);
        }

        SettingsStore.Save(settings);
        OnPropertyChanged(nameof(IsActiveFavorite));
        RefreshList();
    }

    [RelayCommand]
    private async Task ProtectActiveDocumentAsync()
    {
        var document = ActiveDocument;
        if (document is null || document.IsEncrypted || PromptPassword is null)
        {
            return;
        }

        var password = await PromptPassword("Notiz mit Kennwort schützen", true);
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        try
        {
            await document.ProtectAsync(password);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
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

        if (await Confirm("Kennwortschutz entfernen? Die Notiz wird danach unverschlüsselt als .md gespeichert."))
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

        if (!await Confirm($"„{document.TabTitle}“ in den Papierkorb verschieben?"))
        {
            return;
        }

        document.Discard();
        if (await RunGuardedAsync(() => NoteLibrary.MoveToTrash(document.FilePath)))
        {
            settings.Favorites.RemoveAll(favorite => NoteLibrary.IsSamePath(favorite, document.FilePath));
            var index = Documents.IndexOf(document);
            Documents.Remove(document);
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
            RefreshLibrary();
        }
    }

    [RelayCommand]
    private void CreateFolder()
    {
        var name = NewFolderName.Trim();
        if (name.Length == 0)
        {
            return;
        }

        var path = RunGuarded(() => NoteLibrary.CreateFolder(NotesFolder, name));
        if (path is null)
        {
            return;
        }

        NewFolderName = string.Empty;
        ReloadFolders();
        SelectedFolder = Folders.FirstOrDefault(folder => NoteLibrary.IsSamePath(folder.FolderPath, path));
    }

    [RelayCommand]
    private void OpenNotesFolder() => RunGuarded(() => PlatformShell.OpenFolder(NotesFolder));

    [RelayCommand]
    private void RevealActiveDocument()
    {
        if (ActiveDocument is { } document)
        {
            RunGuarded(() => PlatformShell.RevealFile(document.FilePath));
        }
    }

    [RelayCommand]
    private void ToggleLibrary()
    {
        IsLibraryOpen = !IsLibraryOpen;
        if (!IsCompact)
        {
            settings.IsLibraryCollapsed = !IsLibraryOpen;
        }
    }

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
        foreach (var index in Enumerable.Range(0, settings.Favorites.Count))
        {
            if (NoteLibrary.IsSamePath(settings.Favorites[index], oldPath))
            {
                settings.Favorites[index] = newPath;
            }
        }

        SettingsStore.Save(settings);
        RefreshLibrary();
    }

    private void OnDocumentSaved(DocumentViewModel document)
    {
        var item = LibraryItems.FirstOrDefault(entry => NoteLibrary.IsSamePath(entry.FullPath, document.FilePath));
        if (item is not null)
        {
            item.File = NoteLibrary.ReadNoteFile(NotesFolder, document.FilePath);
        }
    }

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
            if (isFirstStart)
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
        var openFiles = settings.OpenFiles.Where(File.Exists).ToList();
        foreach (var path in openFiles)
        {
            OpenFile(path);
        }

        var active = Documents.FirstOrDefault(document => NoteLibrary.IsSamePath(document.FilePath, settings.ActiveFile));
        ActiveDocument = active ?? Documents.FirstOrDefault();
    }

    private void ReloadFolders()
    {
        var selectedPath = SelectedFolder?.FolderPath;
        Folders.Clear();
        foreach (var folder in NoteLibrary.ListFolders(NotesFolder))
        {
            Folders.Add(new NavigationItem(folder, NavigationKind.Folder, Path.Combine(NotesFolder, folder)));
        }

        if (selectedPath is not null)
        {
            SelectedFolder = Folders.FirstOrDefault(folder => NoteLibrary.IsSamePath(folder.FolderPath, selectedPath)) ?? null;
            if (SelectedFolder is null)
            {
                SelectedView = AllNotesItem;
            }
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

        ListTitle = SelectedView?.Label ?? SelectedFolder?.Label ?? "Notizen";
        ListSubtitle = items.Count == 1 ? "1 Notiz" : $"{items.Count} Notizen";
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
        if (SelectedView?.Kind == NavigationKind.Favorites)
        {
            return IsFavorite(note.FullPath);
        }

        if (SelectedFolder?.FolderPath is not { } folderPath)
        {
            return true;
        }

        var relative = Path.GetRelativePath(folderPath, note.FullPath);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private bool IsFavorite(string path) => settings.Favorites.Any(favorite => NoteLibrary.IsSamePath(favorite, path));

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
        "# Willkommen bei ThixNote\n\n" +
        "Jede Notiz ist eine ganz normale Datei in **Dokumente/ThixNote**. Der Dateiname ist der Titel.\n\n" +
        "> Schnell notieren. Nichts weiter.\n\n" +
        "- `.md` Markdown, `.txt` reiner Text, `.txts` mit Kennwort verschlüsselt\n" +
        "- Mehrere Notizen gleichzeitig in **Tabs** (Strg+N neu, Strg+W schließen, Strg+Tab wechseln)\n" +
        "- Fenster schmal ziehen: ThixNote wird zum kleinen Editor wie Notepad\n" +
        "- Schloss-Symbol: Notiz mit Kennwort schützen\n";
}
