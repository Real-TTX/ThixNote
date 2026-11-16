using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MatDesktop;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;

namespace ThixNote.Pages;

/// <summary>Version and channel ("Version 1.1.0 · release", SETUP-07), build, where the notes and the data live.</summary>
public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Refresh();
        UpdateCoordinator.Changed += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    private void Refresh()
    {
        VersionText.Text = MatVersion.Format(AppInfo.Version, AppInfo.Channel);
        BuildText.Text = AppInfo.BuildNumber > 0 ? $"#{AppInfo.BuildNumber}" : "–";
        SystemText.Text = $"{RuntimeInformation.OSDescription} ({UpdateService.PlatformKey}), .NET {Environment.Version}";
        NotesText.Text = SettingsService.NotesFolder;
        DataText.Text = AppPaths.DataDirectory;

        var result = UpdateService.LastResult;
        var available = result is { IsUpdateAvailable: true, Manifest: not null };
        var waiting = available && UpdateService.Downloaded?.Manifest.BuildNumber == result!.Manifest!.BuildNumber;
        UpdateTitle.Text = available ? $"Version {result!.Manifest!.Version} ist verfügbar" : AppInfo.IsLocalBuild ? "Lokaler Build" : "Updates";
        UpdateText.Text = waiting
            ? "Sie ist geladen und wird installiert, wenn du ThixNote schließt."
            : available
                ? "Installieren dauert nur einen Moment, deine Notizen bleiben, wie sie sind."
                : AppInfo.IsLocalBuild
                    ? MatVersion.LocalNote(AppInfo.Channel)
                    : result is null
                        ? "Noch nicht geprüft."
                        : $"{result.Message} Zuletzt geprüft: {(UpdateService.LastCheck is { } last ? Ui.FormatDateTime(last) : "–")}.";
        UpdateLabel.Text = available ? "Update installieren …" : "Nach Updates suchen …";
        if (available)
        {
            UpdateButton.Themed("MatButton.Primary");
        }
        else
        {
            UpdateButton.ClearValue(ThemeProperty);
        }

        UpdateIcon.Use(MatIcon.ForegroundProperty, available ? "Mat.AccentText" : "Mat.TextMuted");
    }

    private void OnUpdateClick(object? sender, RoutedEventArgs e) => (Ui.WindowOf(this) as MainWindow)?.ShowSettings(SettingsPage.UpdatesTab);

    private void OnGitHubClick(object? sender, RoutedEventArgs e) => Ui.OpenExternal($"https://github.com/{AppInfo.Repository}");

    private void OnBackClick(object? sender, RoutedEventArgs e) => (Ui.WindowOf(this) as MainWindow)?.ShowNotes();
}
