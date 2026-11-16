using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ThixNote.App.ViewModels;

namespace ThixNote.App.Views;

public sealed partial class MainWindow : Window
{
    private static readonly FilePickerFileType NoteFileType = new("Notizen") { Patterns = ["*.md", "*.txt", "*.txts"] };

    private MainViewModel? viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ConnectViewModel();
        Closing += (_, _) => viewModel?.SaveSession(Bounds.Width, Bounds.Height);
        Activated += (_, _) => viewModel?.RefreshLibrary();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            viewModel?.UpdateLayout(Bounds.Width);
        }
    }

    private void ConnectViewModel()
    {
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.NewNoteCreated -= FocusTitle;
        }

        viewModel = DataContext as MainViewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PromptPassword = (title, askTwice) => new PasswordDialog(title, askTwice).ShowDialog<string?>(this);
        viewModel.ShowMessage = message => new MessageDialog("ThixNote", message, showCancel: false).ShowDialog<bool>(this);
        viewModel.Confirm = message => new MessageDialog("ThixNote", message, showCancel: true).ShowDialog<bool>(this);
        viewModel.PickExportPath = PickExportPathAsync;
        viewModel.PickFilesToOpen = PickFilesToOpenAsync;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.NewNoteCreated += FocusTitle;
        RegisterShortcuts(viewModel);
    }

    /// <summary>Keyboard shortcuts use Ctrl on Windows and Cmd on macOS.</summary>
    private void RegisterShortcuts(MainViewModel model)
    {
        var command = Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        KeyBindings.Clear();
        AddShortcut(Key.N, command, model.NewNoteCommand);
        AddShortcut(Key.T, command, model.NewNoteCommand);
        AddShortcut(Key.O, command, model.OpenFileFromDiskCommand);
        AddShortcut(Key.S, command, model.SaveActiveDocumentCommand);
        AddShortcut(Key.W, command, model.CloseDocumentCommand);
        AddShortcut(Key.Tab, KeyModifiers.Control, model.NextDocumentCommand);
        AddShortcut(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift, model.PreviousDocumentCommand);
        AddShortcut(Key.PageDown, KeyModifiers.Control, model.NextDocumentCommand);
        AddShortcut(Key.PageUp, KeyModifiers.Control, model.PreviousDocumentCommand);
        AddShortcut(Key.D, command, model.ToggleFavoriteCommand);
        AddShortcut(Key.Back, command | KeyModifiers.Shift, model.ToggleLibraryCommand);
        AddShortcut(Key.OemBackslash, command, model.ToggleLibraryCommand);
        AddShortcut(Key.F, command, new RelayCommand(FocusSearch));
        AddShortcut(Key.K, command, new RelayCommand(FocusSearch));
        AddShortcut(Key.E, command, new RelayCommand(() => model.ActiveDocument?.TogglePreviewCommand.Execute(null)));
        AddShortcut(Key.L, command, new RelayCommand(() => model.ActiveDocument?.LockCommand.Execute(null)));
        AddShortcut(Key.B, command, new RelayCommand(() => WrapSelection("**", "**")));
        AddShortcut(Key.I, command, new RelayCommand(() => WrapSelection("*", "*")));
    }

    private void AddShortcut(Key key, KeyModifiers modifiers, System.Windows.Input.ICommand command)
        => KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = command });

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ActiveDocument) || viewModel?.ActiveDocument is not { } document)
        {
            return;
        }

        DocumentScroller.Offset = default;
        Dispatcher.UIThread.Post(() =>
        {
            TabsList.ContainerFromItem(document)?.BringIntoView();
            if (document.IsLocked)
            {
                UnlockBox.Focus();
                return;
            }

            BodyEditor.Focus();
        }, DispatcherPriority.Background);
    }

    private void FocusTitle()
        => Dispatcher.UIThread.Post(() =>
        {
            TitleEditor.Focus();
            TitleEditor.SelectAll();
        }, DispatcherPriority.Background);

    private void FocusSearch()
    {
        if (viewModel is { IsLibraryOpen: false })
        {
            viewModel.ToggleLibraryCommand.Execute(null);
        }

        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Background);
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

    private static void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        viewModel?.OpenFiles(paths);
    }

    // Markdown formatting helpers for the toolbar.
    private void OnHeadingClick(object? sender, RoutedEventArgs e) => PrefixLine("# ");
    private void OnQuoteClick(object? sender, RoutedEventArgs e) => PrefixLine("> ");
    private void OnListClick(object? sender, RoutedEventArgs e) => PrefixLine("- ");
    private void OnCheckboxClick(object? sender, RoutedEventArgs e) => PrefixLine("- [ ] ");
    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelection("**", "**");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelection("*", "*");
    private void OnLinkClick(object? sender, RoutedEventArgs e) => WrapSelection("[", "](https://)");

    private void WrapSelection(string before, string after)
    {
        if (!BodyEditor.IsEffectivelyVisible)
        {
            return;
        }

        var text = BodyEditor.Text ?? string.Empty;
        var start = Math.Min(BodyEditor.SelectionStart, BodyEditor.SelectionEnd);
        var end = Math.Max(BodyEditor.SelectionStart, BodyEditor.SelectionEnd);
        BodyEditor.Text = text[..start] + before + text[start..end] + after + text[end..];
        BodyEditor.SelectionStart = start + before.Length;
        BodyEditor.SelectionEnd = end + before.Length;
        BodyEditor.Focus();
    }

    private void PrefixLine(string prefix)
    {
        var text = BodyEditor.Text ?? string.Empty;
        var caret = Math.Min(BodyEditor.CaretIndex, text.Length);
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        BodyEditor.Text = text.Insert(lineStart, prefix);
        BodyEditor.CaretIndex = caret + prefix.Length;
        BodyEditor.Focus();
    }
}
