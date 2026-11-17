using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixNote.Controls;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;
using ThixNote.ViewModels;

namespace ThixNote.Pages;

/// <summary>
/// The settings. Every change goes through the business layer (SettingsService, UpdateService), which checks values and
/// policies; an error is shown at its field or above the tabs.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string UpdatesTab = "updates";
    public const string AdvancedTab = "advanced";

    private readonly NotesViewModel _notes;
    private bool _isLoading;

    public SettingsPage()
        : this(new NotesViewModel())
    {
    }

    public SettingsPage(NotesViewModel notes)
    {
        _notes = notes;
        InitializeComponent();
        ThemeChoice.SelectionChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { Theme = (ThemeChoice)Math.Max(0, ThemeChoice.SelectedIndex) }));
        WordWrapSwitch.IsCheckedChanged += (_, _) => Apply(() => _notes.WordWrap = WordWrapSwitch.IsChecked == true);
        CompactModeSwitch.IsCheckedChanged += (_, _) => Apply(() => _notes.IsCompactMode = CompactModeSwitch.IsChecked == true);
        CompactModeRow.Description += $" ({Ui.Shortcut("K", shift: true)}).";
        CheckUpdatesSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { CheckForUpdates = CheckUpdatesSwitch.IsChecked == true }));
        AutoInstallSwitch.IsCheckedChanged += (_, _) => Apply(() => SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = AutoInstallSwitch.IsChecked == true }));
        ToolTip.SetTip(ZoomOutButton, $"Verkleinern ({Ui.Shortcut("-")})");
        ToolTip.SetTip(ZoomInButton, $"Vergrößern ({Ui.Shortcut("+")})");
        _notes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NotesViewModel.Zoom) or nameof(NotesViewModel.WordWrap) or nameof(NotesViewModel.IsCompactMode))
            {
                Ui.Post(Load);
            }
        };
        UpdateCoordinator.Changed += (_, _) => Ui.Post(() => ShowUpdateResult(UpdateService.LastResult));
        AttachedToVisualTree += (_, _) => Load();
        Load();
    }

    /// <summary>Opens a tab (null: leave it where it is).</summary>
    public void ShowTab(string? tab)
    {
        Tabs.SelectedItem = tab switch
        {
            UpdatesTab => UpdatesTabItem,
            AdvancedTab => AdvancedTabItem,
            GeneralTab => GeneralTabItem,
            _ => Tabs.SelectedItem,
        };
    }

    private void Load()
    {
        _isLoading = true;
        try
        {
            var settings = SettingsService.Get();
            var locked = AccessPolicy.IsConfigurationLocked;
            PolicyNote.IsVisible = locked || AccessPolicy.AreUpdatesDisabled;
            ThemeChoice.SelectedIndex = (int)settings.Theme;
            WordWrapSwitch.IsChecked = _notes.WordWrap;
            CompactModeSwitch.IsChecked = _notes.IsCompactMode;
            ZoomText.Text = _notes.ZoomText;
            NotesFolderRow.Description = SettingsService.NotesFolder;
            ThemeChoice.IsEnabled = ChangeFolderButton.IsEnabled = !locked;

            InstalledVersionText.Text = MatVersion.Format(AppInfo.Version, AppInfo.Channel);
            ChannelText.Text = AppInfo.Channel switch
            {
                "release" => "Release (main)",
                "nightly" => "Nightly (dev)",
                _ => "Lokaler Build – keine Updates",
            };
            CheckUpdatesSwitch.IsChecked = settings.CheckForUpdates;
            AutoInstallSwitch.IsChecked = settings.InstallUpdatesAutomatically;
            LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
            CheckUpdatesSwitch.IsEnabled = AutoInstallSwitch.IsEnabled = UpdateService.CanUpdate && !locked;
            if (!UpdateService.CanUpdate)
            {
                UpdateMessage.Show(AccessPolicy.AreUpdatesDisabled
                    ? "Updates wurden von deiner Organisation abgeschaltet."
                    : MatVersion.LocalNote(AppInfo.Channel) ?? "Lokaler Build – keine Updates.", InfoSeverity.Info);
            }
            else
            {
                ShowUpdateResult(UpdateService.LastResult);
            }

            DataFolderRow.Description = AppPaths.DataDirectory;
            LogFolderRow.Description = AppPaths.LogDirectory;
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Runs a change through the business layer; its error shows at its field (or above the tabs).</summary>
    private void Apply(Action change)
    {
        if (_isLoading)
        {
            return;
        }

        PageMessage.Hide();
        FormErrors.ShowError(this, null, "");
        try
        {
            change();
        }
        catch (BusinessException exception)
        {
            if (!FormErrors.ShowError(this, exception.Field, exception.Message))
            {
                PageMessage.Show(exception.Message, InfoSeverity.Error);
            }

            Load();
        }
    }

    private void ShowUpdateResult(UpdateCheckResult? result)
    {
        if (result is null || !UpdateService.CanUpdate)
        {
            return;
        }

        InstallUpdateButton.IsVisible = result.IsUpdateAvailable;
        var waiting = UpdateService.Downloaded is { } downloaded && downloaded.Manifest.BuildNumber == result.Manifest?.BuildNumber;
        UpdateMessage.Show(
            waiting ? $"{result.Message} Sie ist geladen und wird installiert, wenn du ThixNote schließt." : result.Message,
            result.IsUpdateAvailable ? InfoSeverity.Success : InfoSeverity.Info);
        LastCheckText.Text = UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–";
    }

    private async void OnChangeFolderClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var start = await storage.TryGetFolderFromPathAsync(SettingsService.NotesFolder);
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Notizordner wählen", SuggestedStartLocation = start });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder)
        {
            return;
        }

        Apply(() =>
        {
            SettingsService.SetNotesFolder(folder);
            _notes.ReloadNotesFolder();
        });
        Load();
    }

    private void OnOpenNotesFolderClick(object? sender, RoutedEventArgs e) => _notes.OpenNotesFolderCommand.Execute(null);

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => _notes.ZoomInCommand.Execute(null);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => _notes.ZoomOutCommand.Execute(null);

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) => _notes.ZoomResetCommand.Execute(null);

    private async void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            ShowUpdateResult(await UpdateCoordinator.CheckAsync());
        }
        catch (BusinessException exception)
        {
            UpdateMessage.Show(exception.Message, InfoSeverity.Error);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = UpdateService.CanUpdate;
        }
    }

    private async void OnInstallUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (UpdateService.LastResult?.Manifest is not { } manifest)
        {
            return;
        }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        UpdateProgress.IsVisible = true;
        try
        {
            await UpdateCoordinator.InstallNowAsync(manifest, new Progress<double>(value => UpdateProgress.Value = value));
        }
        catch (Exception exception) when (exception is BusinessException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            UpdateMessage.Show($"Das Update ist fehlgeschlagen: {exception.Message}", InfoSeverity.Error);
            InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
            UpdateProgress.IsVisible = false;
        }
    }

    private void OnReleasePageClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(UpdateService.ReleasePageUrl(AppInfo.IsLocalBuild ? "release" : AppInfo.Channel));

    private void OnOpenDataFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Ui.OpenExternal(AppPaths.DataDirectory);
    }

    private void OnOpenLogFolderClick(object? sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.OpenExternal(AppPaths.LogDirectory);
    }
}
