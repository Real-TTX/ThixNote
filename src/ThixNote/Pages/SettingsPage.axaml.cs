using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MatDesktop;
using ThixNote.Controls;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;
using ThixNote.Dialogs;
using ThixNote.ViewModels;

namespace ThixNote.Pages;

/// <summary>
/// The settings. Every change goes through the business layer (SettingsService, SyncService, UpdateService), which checks
/// values and policies; an error is shown at its field or above the tabs.
/// </summary>
public partial class SettingsPage : UserControl
{
    public const string GeneralTab = "general";
    public const string CloudTab = "cloud";
    public const string UpdatesTab = "updates";
    public const string AdvancedTab = "advanced";

    private readonly NotesViewModel _notes;
    private bool _isLoading;

    /// <summary>The sign-in in the browser that waits for its code to be confirmed (Abbrechen cancels it).</summary>
    private CancellationTokenSource? _signIn;
    private string? _verificationUrl;

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

        // Enter in a field of the sign-in signs in (the browser way from address and device name, else with the password).
        ServerUrlBox.KeyDown += OnBrowserFieldKeyDown;
        DeviceNameBox.KeyDown += OnBrowserFieldKeyDown;
        UserNameBox.KeyDown += OnPasswordFieldKeyDown;
        PasswordBox.KeyDown += OnPasswordFieldKeyDown;
        AttachedToVisualTree += (_, _) =>
        {
            SyncService.StatusChanged += OnSyncStatusChanged;
            Load();
        };
        DetachedFromVisualTree += (_, _) => SyncService.StatusChanged -= OnSyncStatusChanged;
        Load();
    }

    /// <summary>Opens a tab (null: leave it where it is).</summary>
    public void ShowTab(string? tab)
    {
        Tabs.SelectedItem = tab switch
        {
            CloudTab => CloudTabItem,
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
            PolicyNote.IsVisible = locked || AccessPolicy.IsCloudDisabled || AccessPolicy.AreUpdatesDisabled;
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
            LoadCloud();
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

    // ---- Cloud ----

    /// <summary>The snapshots show the code as it waits for the browser (no cloud, no browser).</summary>
    internal void ShowLinkCodeForSnapshot(string? code, string url = "")
    {
        if (code is null)
        {
            HideLinkCode();
        }
        else
        {
            ShowLinkCode(code, url);
        }

        LoadCloud();
    }

    /// <summary>
    /// The Cloud tab in one of three states: the sign-in (browser first, user name and password as the second way), the code
    /// that waits for the browser, or the connection with its state.
    /// </summary>
    private void LoadCloud()
    {
        var cloud = SettingsService.Get().Cloud;
        var status = SyncService.Status;
        var waiting = CloudCode.IsVisible;
        CloudSignIn.IsVisible = !cloud.IsConnected && !waiting;
        CloudConnected.IsVisible = cloud.IsConnected && !waiting;
        CloudSignIn.IsEnabled = !AccessPolicy.IsCloudDisabled;
        if (AccessPolicy.IsCloudDisabled)
        {
            CloudMessage.Show("Von deiner Organisation festgelegt: die Cloud-Synchronisierung ist ausgeschaltet (Richtlinie DisableCloud).", InfoSeverity.Warning);
        }
        else if (status.State == CloudState.SignedOut)
        {
            CloudMessage.Show(status.Message, InfoSeverity.Warning);
        }

        if (!cloud.IsConnected)
        {
            if (string.IsNullOrEmpty(ServerUrlBox.Text))
            {
                ServerUrlBox.Text = cloud.ServerUrl;
            }

            if (string.IsNullOrEmpty(UserNameBox.Text))
            {
                UserNameBox.Text = cloud.UserName;
            }

            if (string.IsNullOrEmpty(DeviceNameBox.Text))
            {
                DeviceNameBox.Text = cloud.DeviceName.Length > 0 ? cloud.DeviceName : AppInfo.MachineName;
            }

            return;
        }

        CloudAccountText.Text = cloud.DisplayName.Length > 0 && cloud.DisplayName != cloud.UserName ? $"{cloud.DisplayName} ({cloud.UserName})" : cloud.UserName;
        CloudServerText.Text = cloud.ServerUrl;
        CloudDeviceText.Text = cloud.DeviceName;
        CloudStateText.Text = status.Message;
        CloudLastSyncText.Text = status.LastSync is { } last ? Ui.FormatDateTime(last) : "–";
    }

    private void OnSyncStatusChanged(object? sender, SyncStatus status) => Ui.Post(LoadCloud);

    private void ShowLinkCode(string code, string url)
    {
        _verificationUrl = url;
        LinkCodeText.Text = code;
        LinkUrlText.Text = url;
        CloudCode.IsVisible = true;
        CloudSignIn.IsVisible = CloudConnected.IsVisible = false;
    }

    private void HideLinkCode()
    {
        _verificationUrl = null;
        CloudCode.IsVisible = false;
    }

    private void OnBrowserFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = SignInInBrowserAsync();
        }
    }

    private void OnPasswordFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = SignInWithPasswordAsync();
        }
    }

    private async void OnBrowserSignInClick(object? sender, RoutedEventArgs e) => await SignInInBrowserAsync();

    private async void OnPasswordSignInClick(object? sender, RoutedEventArgs e) => await SignInWithPasswordAsync();

    /// <summary>
    /// "Im Browser anmelden": the cloud gives a code, the browser opens its page, the person signs in there and confirms the
    /// code; meanwhile the code is shown big here and ThixNote waits (Abbrechen ends the wait).
    /// </summary>
    private async Task SignInInBrowserAsync()
    {
        if (_signIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        BrowserSignInButton.IsEnabled = PasswordSignInButton.IsEnabled = false;
        using var cancel = new CancellationTokenSource();
        _signIn = cancel;
        try
        {
            var signIn = await SyncService.StartBrowserSignInAsync(ServerUrlBox.Text ?? string.Empty, DeviceNameBox.Text ?? string.Empty, cancel.Token);
            ShowLinkCode(signIn.Code, signIn.VerificationUrl);
            Ui.OpenExternal(signIn.VerificationUrl);
            CancelSignInButton.Focus();
            await SyncService.FinishBrowserSignInAsync(signIn, cancel.Token);
            CloudMessage.Show("Verbunden. Deine Notizen werden jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            CloudMessage.Show("Die Anmeldung wurde abgebrochen.", InfoSeverity.Info);
        }
        catch (BusinessException exception)
        {
            ShowCloudError(exception);
        }
        finally
        {
            _signIn = null;
            HideLinkCode();
            BrowserSignInButton.IsEnabled = PasswordSignInButton.IsEnabled = true;
            LoadCloud();
        }
    }

    /// <summary>The second way: user name and password of a local account of the cloud (the password is not kept).</summary>
    private async Task SignInWithPasswordAsync()
    {
        if (_signIn is not null)
        {
            return;
        }

        CloudMessage.Hide();
        FormErrors.ShowError(this, null, "");
        BrowserSignInButton.IsEnabled = PasswordSignInButton.IsEnabled = false;
        try
        {
            await SyncService.ConnectAsync(ServerUrlBox.Text ?? string.Empty, UserNameBox.Text ?? string.Empty, PasswordBox.Text ?? string.Empty, DeviceNameBox.Text ?? string.Empty);
            PasswordBox.Text = string.Empty;
            CloudMessage.Show("Verbunden. Deine Notizen werden jetzt mit der Cloud abgeglichen.", InfoSeverity.Success);
        }
        catch (BusinessException exception)
        {
            ShowCloudError(exception);
        }
        finally
        {
            BrowserSignInButton.IsEnabled = PasswordSignInButton.IsEnabled = true;
            LoadCloud();
        }
    }

    private void ShowCloudError(BusinessException exception)
    {
        if (exception.Field is "UserName" or "Password")
        {
            PasswordSignIn.IsVisible = true;
        }

        if (!FormErrors.ShowError(this, exception.Field, exception.Message))
        {
            CloudMessage.Show(exception.Message, InfoSeverity.Error);
        }
    }

    private void OnShowPasswordSignInClick(object? sender, RoutedEventArgs e)
    {
        PasswordSignIn.IsVisible = !PasswordSignIn.IsVisible;
        if (PasswordSignIn.IsVisible)
        {
            (string.IsNullOrEmpty(UserNameBox.Text) ? UserNameBox : PasswordBox).Focus();
        }
    }

    private void OnOpenLinkAgainClick(object? sender, RoutedEventArgs e)
    {
        if (_verificationUrl is { } url)
        {
            Ui.OpenExternal(url);
        }
    }

    private void OnCancelSignInClick(object? sender, RoutedEventArgs e) => _signIn?.Cancel();

    private void OnSyncNowClick(object? sender, RoutedEventArgs e)
    {
        SyncService.Kick();
        CloudStateText.Text = "Synchronisiere …";
    }

    private void OnOpenCloudClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal(SettingsService.Get().Cloud.ServerUrl);

    private async void OnDisconnectClick(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDialog.AskAsync(Ui.WindowOf(this), "Von der Cloud abmelden?",
                "Dieser Computer wird aus deinem Cloud-Konto entfernt und gleicht nicht mehr ab. Die Notizen auf diesem Computer bleiben, wie sie sind.",
                "Abmelden", isDestructive: true))
        {
            return;
        }

        await SyncService.DisconnectAsync();
        CloudMessage.Show("Abgemeldet. Die Notizen auf diesem Computer bleiben, wie sie sind.", InfoSeverity.Info);
        LoadCloud();
    }

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
