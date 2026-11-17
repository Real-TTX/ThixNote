using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatDesktop;
using ThixNote.Controls;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Dialogs;
using ThixNote.Pages;
using ThixNote.ViewModels;

namespace ThixNote;

/// <summary>
/// The main window. Everything works with the keyboard (Ctrl on Windows, Cmd on macOS):
///   N / T new note, O open, S save, W close the tab, Tab / Shift+Tab (or PageDown / PageUp) the next / previous tab,
///   F / K search, E preview, D favourite, L lock, B / I bold / italic, F2 rename, + / - / 0 zoom,
///   1 / 2 Alle Notizen / Favoriten, "," settings, Shift+L the note list, Shift+B fold the menu, Shift+K the compact view,
///   Esc ends the search.
/// The chrome is MatDesktop's: MatTitleBar (DESK-03) with the tabs, as in Notepad, and MatNav (DESK-04). In the compact view
/// and narrower than 900 px the window is a small editor like Notepad: no menu, the note list over the editor.
/// </summary>
public partial class MainWindow : MatWindow
{
    /// <summary>Below this width of the window ThixNote is always a small editor like Notepad.</summary>
    public const double CompactWidth = MatNav.NarrowWindow;

    private static readonly FilePickerFileType NoteFileType = new("Notizen") { Patterns = ["*.md", "*.txt", "*.txts"] };

    private readonly Lazy<SettingsPage> _settings;
    private readonly Lazy<AboutPage> _about = new(() => new AboutPage());
    private readonly DispatcherTimer _sizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly MatNavItem _navAll;
    private readonly MatNavItem _navFavorites;
    private readonly MatNavItem _navSettings;
    private readonly List<MatNavItem> _navFolders = [];
    private readonly IconLabel _newFolderLabel = new() { Icon = "plus", Text = "Neuer Ordner …" };
    private readonly IconLabel _notesFolderLabel = new() { Icon = "folder-open", Text = "Notizordner öffnen" };
    private bool _isCompact;
    private bool _isLaidOut;

    public MainWindow()
    {
        InitializeComponent();
        UseOwnTitleBar();
        var settings = SettingsService.Get();
        Width = settings.WindowWidth;
        Height = settings.WindowHeight;

        Notes = new NotesPage();
        _settings = new(() => new SettingsPage(Notes.ViewModel));
        TitleBar.Logo = Ui.Logo;
        TitleBar.WordMark = Ui.WordMark();

        // The menu: the views, the folders, Settings below the line, the version at the foot (= About), folded or open as last time.
        _navAll = new MatNavItem("file-text", "Alle Notizen", Ui.Shortcut("1")) { IsCurrent = true };
        _navFavorites = new MatNavItem("star", "Favoriten", Ui.Shortcut("2"));
        _navSettings = new MatNavItem("settings", "Einstellungen", Ui.Shortcut(","));
        Sidebar.Items.AddRange([_navAll, _navFavorites]);
        Sidebar.Settings = _navSettings;
        Sidebar.Version = AppInfo.Version;
        Sidebar.Channel = AppInfo.Channel;
        Sidebar.TitleBar = TitleBar;
        Sidebar.IsFolded = settings.IsSidebarCollapsed;
        Sidebar.FoldShortcut = Ui.Shortcut("B", shift: true);
        Sidebar.Foot = BuildMenuFoot();
        Sidebar.ItemInvoked += (_, item) => OnNavInvoked(item);
        Sidebar.AboutRequested += (_, _) => ShowAbout();
        Sidebar.UpdateRequested += (_, _) => ShowSettings(SettingsPage.UpdatesTab);
        Sidebar.FoldChanged += (_, folded) =>
        {
            SettingsService.Remember(current => current with { IsSidebarCollapsed = folded });
            RefreshMenuFoot();
        };
        RefreshMenuFoot();
        RebuildFolders();

        var notes = Notes.ViewModel;
        notes.PromptPassword = (title, askTwice) => PasswordDialog.AskAsync(this, title, askTwice);
        notes.PromptText = (title, label, initial) => TextInputDialog.AskAsync(this, title, label, initial);
        notes.ShowMessage = message => ConfirmDialog.ShowMessageAsync(this, "ThixNote", message);
        notes.Confirm = (title, message, confirm, destructive) => ConfirmDialog.AskAsync(this, title, message, confirm, destructive);
        notes.PickExportPath = PickExportPathAsync;
        notes.PickFilesToOpen = PickFilesToOpenAsync;
        notes.FoldersChanged += RebuildFolders;
        notes.PropertyChanged += OnNotesPropertyChanged;
        Notes.SettingsRequested += (_, _) => ShowSettings();
        Notes.AboutRequested += (_, _) => ShowAbout();
        Notes.Tabs.TabList.SelectionChanged += (_, _) =>
        {
            if (PageHost.Content != Notes && notes.ActiveDocument is not null && Notes.Tabs.TabList.IsKeyboardFocusWithin)
            {
                ShowNotes();
            }
        };
        // A click on a tab – the chosen one too – leads back from the settings to the text (the compact view has no menu).
        Notes.Tabs.TabList.AddHandler(TappedEvent, (_, _) =>
        {
            if (PageHost.Content != Notes)
            {
                ShowNotes();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        PageHost.Content = Notes;

        TitleCenter.Content = Notes.Tabs; // the tabs in the title bar, as in Notepad

        _sizeTimer.Tick += (_, _) =>
        {
            _sizeTimer.Stop();
            if (WindowState == WindowState.Normal)
            {
                SettingsService.Remember(current => current with { WindowWidth = Width, WindowHeight = Height });
            }
        };
        Resized += (_, _) =>
        {
            _sizeTimer.Stop();
            _sizeTimer.Start();
        };
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);

        UpdateCoordinator.Changed += (_, _) => Ui.Post(RefreshUpdate);
        RefreshUpdate();
        RefreshTitle();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Activated += (_, _) => notes.RefreshLibrary();
        Closing += (_, _) => notes.SaveSession();
    }

    /// <summary>Set by App when ThixNote ends (update, setup): the window then closes without asking anything.</summary>
    public bool IsExiting { get; set; }

    public NotesPage Notes { get; }

    /// <summary>The window is a small editor like Notepad: the compact view, or narrower than 900 px.</summary>
    public bool IsCompact => _isCompact;

    public bool IsSidebarCollapsed => Sidebar.IsFolded;

    public void ToggleSidebar() => Sidebar.IsFolded = !Sidebar.IsFolded;

    /// <summary>The version at the foot of the menu (<c>Version 1.1.0 · release</c>, SETUP-07): it opens About.</summary>
    public Button VersionButton => Sidebar.VersionButton;

    public void ShowNotes()
    {
        Sidebar.Select(Notes.ViewModel.View.Kind switch
        {
            NotesViewKind.Favorites => _navFavorites,
            NotesViewKind.Folder => _navFolders.FirstOrDefault(item => NoteLibrary.IsSamePath(item.Tag as string, Notes.ViewModel.View.FolderPath)),
            _ => _navAll,
        });
        PageHost.Content = Notes;
        Notes.FocusEditor();
    }

    /// <summary>The settings, optionally on one tab (SettingsPage.UpdatesTab).</summary>
    public void ShowSettings(string? tab = null)
    {
        Sidebar.Select(_navSettings);
        PageHost.Content = _settings.Value;
        _settings.Value.ShowTab(tab);
    }

    public void ShowAbout()
    {
        Sidebar.Select(null);
        PageHost.Content = _about.Value;
    }

    /// <summary>Opens the search above the list (in the compact view the list opens over the editor for it).</summary>
    public void FocusSearch()
    {
        if (PageHost.Content != Notes)
        {
            ShowNotes();
        }

        Notes.ViewModel.IsListOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            Notes.SearchBox.Focus();
            Notes.SearchBox.SelectAll();
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Wide: menu, list and editor, the tabs in the title bar after the word mark. Compact (the compact view, or narrower than
    /// 900 px): Notepad – the logo and the tabs in the title bar, no menu, the list over the editor.
    /// </summary>
    private void ApplyLayout(double width)
    {
        var compact = Notes.ViewModel.IsCompactMode || width < CompactWidth;
        Notes.ApplyCompact(compact, width);
        if (compact == _isCompact && _isLaidOut)
        {
            return;
        }

        _isLaidOut = true;
        _isCompact = compact;
        Sidebar.IsVisible = !compact;
        Sidebar.TitleBar = compact ? null : TitleBar;
        TitleBar.IsNameVisible = !compact;
        TitleBar.BrandWidth = compact ? 56 : MatNav.WidthFor(Sidebar.IsFolded, width);
        if (!compact)
        {
            Sidebar.ApplyWindowWidth(width);
        }
    }

    private void OnNavInvoked(MatNavItem item)
    {
        if (item == _navSettings)
        {
            ShowSettings();
            return;
        }

        var notes = Notes.ViewModel;
        notes.SearchText = string.Empty;
        notes.View = item == _navFavorites ? NotesView.Favorites
            : item.Tag is string folder ? new NotesView(NotesViewKind.Folder, folder)
            : NotesView.All;
        if (!notes.IsListOpen)
        {
            notes.IsListOpen = true;
        }

        ShowNotes();
    }

    /// <summary>The folders of the library as items of the menu, after Alle Notizen and Favoriten.</summary>
    private void RebuildFolders()
    {
        var notes = Notes.ViewModel;
        foreach (var item in _navFolders)
        {
            Sidebar.Items.Remove(item);
        }

        _navFolders.Clear();
        foreach (var folder in notes.Folders)
        {
            var path = Path.Combine(notes.NotesFolder, folder);
            var item = new MatNavItem("folder", folder) { Tag = path };
            AutomationProperties.SetName(item, $"Ordner {folder}");
            _navFolders.Add(item);
        }

        Sidebar.Items.AddRange(_navFolders);
        if (PageHost.Content == Notes || PageHost.Content is null)
        {
            ShowNotesSelectionOnly();
        }
    }

    private void ShowNotesSelectionOnly() => Sidebar.Select(Notes.ViewModel.View.Kind switch
    {
        NotesViewKind.Favorites => _navFavorites,
        NotesViewKind.Folder => _navFolders.FirstOrDefault(item => NoteLibrary.IsSamePath(item.Tag as string, Notes.ViewModel.View.FolderPath)),
        _ => _navAll,
    });

    /// <summary>At the foot of the menu: a new folder and the notes folder in Explorer / Finder (icons only on the rail).</summary>
    private StackPanel BuildMenuFoot()
    {
        var notes = Notes.ViewModel;
        var newFolder = new Button { Content = _newFolderLabel, Theme = Ui.Theme("MatButton.Ghost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Command = notes.CreateFolderCommand };
        var openFolder = new Button { Content = _notesFolderLabel, Theme = Ui.Theme("MatButton.Ghost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Command = notes.OpenNotesFolderCommand };
        ToolTip.SetTip(newFolder, "Neuer Ordner");
        ToolTip.SetTip(openFolder, $"Notizordner öffnen ({notes.NotesFolder})");
        AutomationProperties.SetName(newFolder, "Neuer Ordner");
        AutomationProperties.SetName(openFolder, "Notizordner öffnen");
        return new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 8), Children = { newFolder, openFolder } };
    }

    private void RefreshMenuFoot() => _newFolderLabel.IsTextVisible = _notesFolderLabel.IsTextVisible = !Sidebar.IsFolded;

    /// <summary>A waiting update shows as a link above the version (SETUP-06); it leads to Einstellungen › Updates.</summary>
    private void RefreshUpdate() => Sidebar.UpdateVersion = UpdateCoordinator.AvailableVersion;

    private void OnNotesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotesViewModel.ActiveDocument))
        {
            RefreshTitle();
            if (Notes.ViewModel.ActiveDocument is { } document)
            {
                document.PropertyChanged -= OnActiveDocumentPropertyChanged;
                document.PropertyChanged += OnActiveDocumentPropertyChanged;
            }
        }
        else if (e.PropertyName is nameof(NotesViewModel.View))
        {
            ShowNotesSelectionOnly();
        }
        else if (e.PropertyName is nameof(NotesViewModel.IsCompactMode))
        {
            ApplyLayout(ClientSize.Width > 0 ? ClientSize.Width : Width);
            Notes.FocusEditor();
        }
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.TabTitle) && ReferenceEquals(sender, Notes.ViewModel.ActiveDocument))
        {
            RefreshTitle();
        }
    }

    /// <summary>The title of the window (task bar, Alt+Tab): the note and the app.</summary>
    private void RefreshTitle() => Title = Notes.ViewModel.ActiveDocument is { } document ? $"{document.TabTitle} – ThixNote" : "ThixNote";

    /// <summary>The shortcuts of the window, before any control sees the key (the editor would take Ctrl+B for itself).</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var notes = Notes.ViewModel;
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        var modifiers = e.KeyModifiers;

        if (e.Key == Key.Escape && modifiers == KeyModifiers.None)
        {
            if (Notes.SearchBox.IsFocused)
            {
                notes.SearchText = string.Empty;
                if (_isCompact)
                {
                    notes.IsListOpen = false;
                }

                Notes.FocusEditor();
                e.Handled = true;
            }
            else if (_isCompact && notes.IsListOpen)
            {
                notes.IsListOpen = false;
                Notes.FocusEditor();
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.F2 && modifiers == KeyModifiers.None)
        {
            notes.RenameActiveDocumentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+Tab is Ctrl on macOS too (like a browser).
        if (e.Key == Key.Tab && modifiers.HasFlag(KeyModifiers.Control))
        {
            (modifiers.HasFlag(KeyModifiers.Shift) ? notes.PreviousDocumentCommand : notes.NextDocumentCommand).Execute(null);
            e.Handled = true;
            return;
        }

        if (!modifiers.HasFlag(command) || modifiers.HasFlag(KeyModifiers.Alt))
        {
            return;
        }

        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        e.Handled = (e.Key, shift) switch
        {
            (Key.N or Key.T, false) => Run(notes.NewNoteCommand),
            (Key.O, false) => Run(notes.OpenFileFromDiskCommand),
            (Key.S, false) => Run(notes.SaveActiveDocumentCommand),
            (Key.W, false) => Run(notes.CloseDocumentCommand),
            (Key.D, false) => Run(notes.ToggleFavoriteCommand),
            (Key.E, false) => Act(() => notes.ActiveDocument?.TogglePreviewCommand.Execute(null)),
            (Key.L, false) => Act(() => notes.ActiveDocument?.LockCommand.Execute(null)),
            (Key.L, true) => Run(notes.ToggleListCommand),
            (Key.B, true) => Act(() => Sidebar.IsFolded = !Sidebar.IsFolded),
            (Key.K, true) => Run(notes.ToggleCompactModeCommand),
            (Key.B, false) => Act(() => Notes.WrapSelection("**", "**")),
            (Key.I, false) => Act(() => Notes.WrapSelection("*", "*")),
            (Key.F or Key.K, false) => Act(FocusSearch),
            (Key.PageDown, false) => Run(notes.NextDocumentCommand),
            (Key.PageUp, false) => Run(notes.PreviousDocumentCommand),
            (Key.OemPlus or Key.Add, _) => Run(notes.ZoomInCommand),
            (Key.OemMinus or Key.Subtract, false) => Run(notes.ZoomOutCommand),
            (Key.D0 or Key.NumPad0, false) => Run(notes.ZoomResetCommand),
            (Key.D1, false) => Act(() => OnNavInvoked(_navAll)),
            (Key.D2, false) => Act(() => OnNavInvoked(_navFavorites)),
            (Key.OemComma, false) => Act(() => ShowSettings()),
            _ => false,
        };
    }

    private static bool Run(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        return true;
    }

    private static bool Act(Action action)
    {
        action();
        return true;
    }

    private async Task<string?> PickExportPathAsync(string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Notiz exportieren",
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
        });
        return file?.TryGetLocalPath();
    }

    private async Task<IReadOnlyList<string>> PickFilesToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Notiz öffnen",
            AllowMultiple = true,
            FileTypeFilter = [NoteFileType, FilePickerFileTypes.All],
        });
        return files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
    }
}
