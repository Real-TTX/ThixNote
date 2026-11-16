using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatDesktop;
using ThixNote.ViewModels;

namespace ThixNote.Pages;

/// <summary>
/// The notes page: the list and the editor (Notepad's layout: tabs, the formatting bar, the text, the status bar). The
/// formatting buttons put Markdown around the selection or in front of the line; the editor reports the caret for the
/// status bar. In a narrow window the list opens over the editor and the tabs move into the title bar (MainWindow).
/// </summary>
public partial class NotesPage : UserControl
{
    public const double ListWidth = 340;

    private DocumentViewModel? _document;

    public NotesPage()
        : this(new NotesViewModel())
    {
    }

    public NotesPage(NotesViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Tabs = new TabStrip { DataContext = viewModel };
        TabHost.Content = Tabs;
        Preview.MarkdownStyleName = "FluentTheme";
        ListPane.Width = ListWidth;
        Root.ColumnDefinitions[0].Width = GridLength.Auto;

        ToolTip.SetTip(NewNoteButton, $"Neue Notiz ({Ui.Shortcut("N")})");
        ToolTip.SetTip(ListToggle, $"Notizliste ein- und ausblenden ({Ui.Shortcut("L", shift: true)})");
        ToolTip.SetTip(BoldButton, $"Fett ({Ui.Shortcut("B")})");
        ToolTip.SetTip(ItalicButton, $"Kursiv ({Ui.Shortcut("I")})");
        ToolTip.SetTip(PreviewButton, $"Vorschau / Bearbeiten ({Ui.Shortcut("E")})");
        ToolTip.SetTip(FavoriteButton, $"Favorit ({Ui.Shortcut("D")})");
        RenameItem.InputGesture = new KeyGesture(Key.F2);
        SaveItem.InputGesture = Gesture(Key.S);
        OpenItem.InputGesture = Gesture(Key.O);
        LockNowItem.InputGesture = Gesture(Key.L);
        ZoomInItem.InputGesture = Gesture(Key.OemPlus);
        ZoomOutItem.InputGesture = Gesture(Key.OemMinus);
        ZoomResetItem.InputGesture = Gesture(Key.D0);
        CloseTabItem.InputGesture = Gesture(Key.W);
        SettingsItem.InputGesture = Gesture(Key.OemComma);

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.EditorFocusRequested += FocusEditor;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty)
            {
                _document?.SetCaret(Editor.CaretIndex);
            }
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        ApplyWordWrap();
        Attach(viewModel.ActiveDocument);
        RefreshFavorite();
        AttachedToVisualTree += (_, _) => FocusEditor(); // at start: the text of the restored tab (or its password)
    }

    /// <summary>"Einstellungen …" and "Über ThixNote" of the more menu (in a narrow window the menu at the left is not there).</summary>
    public event EventHandler? SettingsRequested;

    public event EventHandler? AboutRequested;

    public NotesViewModel ViewModel { get; }

    /// <summary>The tabs; MainWindow moves them into the title bar in a narrow window.</summary>
    public TabStrip Tabs { get; }

    /// <summary>The text area (tests, focus).</summary>
    public TextBox EditorBox => Editor;

    public ListBox Notes => NoteList;

    /// <summary>The search field of the list (shown in a narrow window, where the title bar holds the tabs).</summary>
    public TextBox CompactSearch => ListSearch;

    /// <summary>Wide: the list beside the editor, the tabs above it. Narrow (Notepad): the list over the editor, the tabs elsewhere.</summary>
    public void ApplyCompact(bool compact, double windowWidth)
    {
        ViewModel.IsCompact = compact;
        Grid.SetColumn(ListPane, compact ? 1 : 0);
        ListPane.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        ListPane.Width = compact ? Math.Min(ListWidth, Math.Max(240, windowWidth - 48)) : ListWidth;
        TabBar.IsVisible = !compact;
        if (compact)
        {
            TabHost.Content = null;
        }
        else if (Tabs.Parent is null)
        {
            TabHost.Content = Tabs;
        }
    }

    public void FocusEditor() => Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel.ActiveDocument is { IsLocked: true })
        {
            UnlockBox.Focus();
        }
        else if (Editor.IsEffectivelyVisible)
        {
            Editor.Focus();
        }
    }, DispatcherPriority.Background);

    /// <summary>Strg+B / Strg+I and the bar: Markdown around the selection (only for a Markdown note in the editor).</summary>
    public void WrapSelection(string before, string after)
    {
        if (!CanFormat())
        {
            return;
        }

        var text = Editor.Text ?? string.Empty;
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        Editor.Text = text[..start] + before + text[start..end] + after + text[end..];
        Editor.SelectionStart = start + before.Length;
        Editor.SelectionEnd = end + before.Length;
        Editor.Focus();
    }

    /// <summary>A prefix for the line of the caret ("# ", "- ", "> "); a heading replaces the heading the line had.</summary>
    public void PrefixLine(string prefix)
    {
        if (!CanFormat())
        {
            return;
        }

        var text = Editor.Text ?? string.Empty;
        var caret = Math.Min(Editor.CaretIndex, text.Length);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var existing = 0;
        if (prefix.StartsWith('#'))
        {
            while (lineStart + existing < text.Length && text[lineStart + existing] == '#')
            {
                existing++;
            }

            if (existing > 0 && lineStart + existing < text.Length && text[lineStart + existing] == ' ')
            {
                existing++;
            }
        }

        Editor.Text = text[..lineStart] + prefix + text[(lineStart + existing)..];
        Editor.CaretIndex = Math.Max(lineStart, caret - existing) + prefix.Length;
        Editor.Focus();
    }

    private static KeyGesture Gesture(Key key) => new(key, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private bool CanFormat() => ViewModel.ActiveDocument is { CanFormat: true } && Editor.IsEffectivelyVisible;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NotesViewModel.ActiveDocument):
                Attach(ViewModel.ActiveDocument);
                RefreshFavorite();
                FocusEditor();
                break;
            case nameof(NotesViewModel.IsActiveFavorite):
                RefreshFavorite();
                break;
            case nameof(NotesViewModel.WordWrap):
                ApplyWordWrap();
                break;
        }
    }

    private void Attach(DocumentViewModel? document)
    {
        if (_document is not null)
        {
            _document.PropertyChanged -= OnDocumentPropertyChanged;
        }

        _document = document;
        if (document is not null)
        {
            document.PropertyChanged += OnDocumentPropertyChanged;
            Dispatcher.UIThread.Post(() => document.SetCaret(Editor.CaretIndex), DispatcherPriority.Background);
        }
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Locked: the password field; unlocked: the text.
        if (e.PropertyName == nameof(DocumentViewModel.IsLocked))
        {
            FocusEditor();
        }
    }

    private void RefreshFavorite()
    {
        var favorite = ViewModel.IsActiveFavorite;
        FavoriteIcon.IsFilled = favorite;
        FavoriteIcon.Use(MatIcon.ForegroundProperty, favorite ? "Mat.AccentText" : "Mat.TextMuted");
    }

    private void ApplyWordWrap() => Editor.TextWrapping = ViewModel.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

    private static void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        ViewModel.OpenFiles(paths);
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OnAboutClick(object? sender, RoutedEventArgs e) => AboutRequested?.Invoke(this, EventArgs.Empty);
    private void OnHeading1Click(object? sender, RoutedEventArgs e) => PrefixLine("# ");
    private void OnHeading2Click(object? sender, RoutedEventArgs e) => PrefixLine("## ");
    private void OnHeading3Click(object? sender, RoutedEventArgs e) => PrefixLine("### ");
    private void OnQuoteClick(object? sender, RoutedEventArgs e) => PrefixLine("> ");
    private void OnCodeBlockClick(object? sender, RoutedEventArgs e) => WrapSelection("```\n", "\n```");
    private void OnRuleClick(object? sender, RoutedEventArgs e) => WrapSelection("\n---\n", string.Empty);
    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelection("**", "**");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelection("*", "*");
    private void OnStrikeClick(object? sender, RoutedEventArgs e) => WrapSelection("~~", "~~");
    private void OnListClick(object? sender, RoutedEventArgs e) => PrefixLine("- ");
    private void OnNumberedClick(object? sender, RoutedEventArgs e) => PrefixLine("1. ");
    private void OnTaskClick(object? sender, RoutedEventArgs e) => PrefixLine("- [ ] ");
    private void OnLinkClick(object? sender, RoutedEventArgs e) => WrapSelection("[", "](https://)");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => WrapSelection("`", "`");
}
