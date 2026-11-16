using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace ThixNote.Setup;

public sealed partial class SetupWindow : Window
{
    private readonly bool isUninstall = Program.IsUninstallMode;
    private string installedPath = Installer.DefaultInstallPath;

    public SetupWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {Installer.Version}";
        PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        };

        if (isUninstall)
        {
            PrepareUninstallPage();
            return;
        }

        PathBox.Text = Installer.DefaultInstallPath;
        if (!Installer.HasPayload)
        {
            OptionsSubtitle.Text = "Dieses Setup enthält keine Programmdateien (Entwicklungsbuild).";
            ActionButton.IsEnabled = false;
        }
    }

    private void PrepareUninstallPage()
    {
        installedPath = Installer.ReadInstalledPath();
        OptionsTitle.Text = "ThixNote entfernen";
        OptionsSubtitle.Text = $"ThixNote wird aus „{installedPath}“ entfernt. Deine Notizen im Ordner „Dokumente\\ThixNote“ bleiben erhalten.";
        OptionsBody.IsVisible = false;
        ActionButton.Content = "Entfernen";
    }

    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Installationsordner wählen" });
        var selected = folders.FirstOrDefault()?.TryGetLocalPath();
        if (selected is not null)
        {
            PathBox.Text = System.IO.Path.Combine(selected, "ThixNote");
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        if (!isUninstall && LaunchBox.IsChecked == true)
        {
            Installer.StartApplication(installedPath);
        }

        Close();
    }

    private async void OnActionClick(object? sender, RoutedEventArgs e)
    {
        OptionsPage.IsVisible = false;
        ProgressPage.IsVisible = true;
        try
        {
            if (isUninstall)
            {
                ProgressTitle.Text = "Wird entfernt …";
                ProgressBar.IsIndeterminate = true;
                await Installer.UninstallAsync(installedPath);
                ShowFinished("Entfernt", "ThixNote wurde von diesem Computer entfernt.", offerLaunch: false);
                return;
            }

            installedPath = PathBox.Text?.Trim() ?? Installer.DefaultInstallPath;
            var options = new InstallOptions(installedPath, DesktopShortcutBox.IsChecked == true, AssociationBox.IsChecked == true);
            var progress = new Progress<(double Percent, string Message)>(report =>
            {
                ProgressBar.Value = report.Percent;
                ProgressText.Text = report.Message;
            });
            await Installer.InstallAsync(options, progress);
            ShowFinished("Fertig!", "ThixNote wurde installiert. Du findest es im Startmenü.", offerLaunch: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            ShowFinished("Das hat nicht geklappt", exception.Message, offerLaunch: false);
        }
    }

    private void ShowFinished(string title, string text, bool offerLaunch)
    {
        ProgressPage.IsVisible = false;
        FinishedPage.IsVisible = true;
        FinishedTitle.Text = title;
        FinishedText.Text = text;
        LaunchBox.IsVisible = offerLaunch;
    }
}
