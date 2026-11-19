using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using MatDesktop;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;
using ThixNote.Dialogs;
using ThixNote.ViewModels;

namespace ThixNote;

/// <summary>
/// "ThixNote --snapshot &lt;folder&gt;": draws the windows of ThixNote into PNG files without a screen (Avalonia headless with
/// Skia, the real windows, themes and fonts of the machine), in light and dark, at 1x and their natural size – the pictures
/// of the README (docs/images). It works in a temporary data folder (Program sets it) filled with the sample notes
/// (SampleNotes), with a pinned clock, version 1.1.0 on the release channel and a fixed computer name: no real note,
/// setting, registry value, network or update check is touched, and no path of this machine shows.
/// </summary>
internal static class Snapshot
{
    public const string Argument = "--snapshot";

    /// <summary>What every picture shows as version, channel and computer.</summary>
    public const string Version = "1.1.0";

    public const string Channel = "release";

    public const string MachineName = "ANNA-PC";

    /// <summary>"Now" in every picture (local time): Monday, 16 November 2026, 14:32.</summary>
    public static readonly DateTimeOffset Moment = new(new DateTime(2026, 11, 16, 14, 32, 0, DateTimeKind.Local));

    private const string ProgramFolder = @"C:\Users\Anna\AppData\Local\Programs\ThixNote";
    private const string DataFolder = @"C:\Users\Anna\AppData\Local\ThixNote";
    private const string NotesFolder = @"C:\Users\Anna\Documents\ThixNote";
    private const string CloudUrl = "https://cloud.example.com";

    private static readonly string[] Themes = ["light", "dark"];

    private static readonly string[] ThemedPictures =
    [
        "setup-welcome", "setup-options", "setup-progress", "setup-finish", "setup-update", "setup-uninstall",
        "main-notes", "main-preview", "main-locked", "main-compact", "main-folded", "main-settings",
        "main-settings-cloud-signin", "main-settings-cloud-code", "main-settings-cloud", "main-about",
        "dialog-password", "dialog-rename", "toast",
    ];

    /// <summary>Every file a snapshot writes.</summary>
    public static IReadOnlyList<string> FileNames { get; } = [.. ThemedPictures.SelectMany(name => Themes.Select(theme => $"{name}-{theme}.png"))];

    /// <summary>The command: sets Avalonia up headless, draws every picture into the folder, ends with 0 (all written) or 1.</summary>
    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"Usage: ThixNote {Argument} <folder>");
            return 2;
        }

        var folder = Path.GetFullPath(args[index + 1]);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        AvaloniaSynchronizationContext.InstallIfNeeded();

        int exitCode;
        try
        {
            var report = Render(folder);
            foreach (var file in report.Written)
            {
                Console.WriteLine($"written  {Path.Combine(folder, file)}");
            }

            foreach (var (file, reason) in report.Skipped)
            {
                Console.WriteLine($"skipped  {file}: {reason}");
            }

            exitCode = report.Written.Count + report.Skipped.Count == FileNames.Count ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Snapshot failed: {exception}");
            exitCode = 1;
        }

        // The temporary data folder goes again.
        try
        {
            Directory.Delete(AppPaths.DataDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return exitCode;
    }

    /// <summary>
    /// Draws every picture into <paramref name="folder"/> (Avalonia must run headless, the data folder must be a fresh one:
    /// the command and the UI tests take care of both). Existing files of the same names are replaced.
    /// </summary>
    public static SnapshotReport Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new SnapshotReport();
        var previousTheme = Application.Current!.RequestedThemeVariant;
        using (Isolate())
        {
            var notes = SampleNotes.Create(AppPaths.DefaultNotesFolder);
            SettingsService.Remember(settings => settings with
            {
                OpenFiles = [notes[SampleNotes.Plan], notes[SampleNotes.Shopping], notes[SampleNotes.Secret]],
                ActiveFile = notes[SampleNotes.Plan],
                Favorites = [notes[SampleNotes.Plan], notes[SampleNotes.Shopping]],
                WindowWidth = 1180,
                WindowHeight = 760,
                IsSidebarCollapsed = false,
                IsListHidden = false,
                EditorZoom = 100,
                WordWrap = true,
            });

            foreach (var theme in Themes)
            {
                Application.Current.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                RenderSetup(folder, theme, report);
                RenderMainWindow(folder, theme, report);
                RenderToast(folder, theme, report);
            }
        }

        Application.Current.RequestedThemeVariant = previousTheme;
        return report;
    }

    private static void RenderSetup(string folder, string theme, SnapshotReport report)
    {
        foreach (var page in Enum.GetValues<SetupSnapshotPage>())
        {
            var name = $"setup-{page.ToString().ToLowerInvariant()}";
            if (!OperatingSystem.IsWindows())
            {
                report.Skipped.Add(($"{name}-{theme}.png", "the setup assistant exists on Windows only (macOS: the app bundle and the dmg)"));
                continue;
            }

            RenderSetupPage(folder, $"{name}-{theme}.png", page, report);
        }
    }

    /// <summary>
    /// One page of the setup with fixed values (MatSetupWindow, ThixNoteSetup.Create): nothing is installed, removed or asked
    /// from the system. Progress shows an installation two thirds along, Finish the end of one that worked.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RenderSetupPage(string folder, string file, SetupSnapshotPage page, SnapshotReport report)
    {
        var installed = page == SetupSnapshotPage.Update ? "1.0.5-20261114" : Version;
        var hold = new TaskCompletionSource();
        var window = Setup.ThixNoteSetup.Create(page == SetupSnapshotPage.Uninstall, page == SetupSnapshotPage.Update, installed, ProgramFolder, async run =>
        {
            if (page != SetupSnapshotPage.Progress)
            {
                return;
            }

            run.Progress.Report(new MatSetupReport($"Installiere ThixNote nach {ProgramFolder}", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Programmdatei kopiert", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Startmenü-Eintrag angelegt", MatSetupStepState.Done));
            run.Progress.Report(new MatSetupReport("Öffnen mit ThixNote: .md, .txts", MatSetupStepState.Running, 65));
            await hold.Task;
        });
        window.Show();
        Settle(window);
        if (page == SetupSnapshotPage.Options)
        {
            window.GoTo(MatSetupStepKind.Options);
        }
        else if (page is SetupSnapshotPage.Progress or SetupSnapshotPage.Finish)
        {
            _ = window.RunAsync();
        }

        Settle(window);
        Save(window, folder, file, report);
        hold.TrySetResult();
        Settle(window);
        window.Close();
    }

    private static void RenderMainWindow(string folder, string theme, SnapshotReport report)
    {
        var window = new MainWindow { Width = 1180, Height = 760 };
        window.Show();
        Settle(window, waitLonger: true);
        var notes = window.Notes.ViewModel;
        Save(window, folder, $"main-notes-{theme}.png", report);

        notes.ActiveDocument!.TogglePreviewCommand.Execute(null);
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-preview-{theme}.png", report);
        notes.ActiveDocument!.TogglePreviewCommand.Execute(null);

        notes.ActiveDocument = notes.Documents.Single(document => document.IsEncrypted);
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-locked-{theme}.png", report);
        notes.ActiveDocument = notes.Documents[0];

        window.ShowNotes();
        window.ShowSettings();
        var settings = window.PageHost.Content as Pages.SettingsPage ?? throw new InvalidOperationException("no settings page");
        settings.NotesFolderRow.Description = NotesFolder;
        Settle(window);
        Save(window, folder, $"main-settings-{theme}.png", report);
        RenderCloudTab(window, settings, folder, theme, report);

        // The about page tells this machine's system and folders: the pictures show a typical installation instead.
        window.ShowAbout();
        var about = window.PageHost.Content as Pages.AboutPage ?? throw new InvalidOperationException("no about page");
        about.SystemText.Text = $"Microsoft Windows 10.0.26100 ({UpdateService.PlatformKey}), .NET 10.0.0";
        about.NotesText.Text = NotesFolder;
        about.DataText.Text = DataFolder;
        Settle(window);
        Save(window, folder, $"main-about-{theme}.png", report);

        window.ShowNotes();
        window.ToggleSidebar();
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-folded-{theme}.png", report);
        window.ToggleSidebar();
        Settle(window);

        _ = PasswordDialogSnapshot(window, folder, theme, report);
        _ = TextInputDialog.AskAsync(window, "Notiz umbenennen", "Titel", notes.ActiveDocument!.Title);
        Settle(window);
        var rename = window.OwnedWindows.OfType<TextInputDialog>().Single();
        Settle(rename);
        Save(rename, folder, $"dialog-rename-{theme}.png", report);
        rename.Close();
        Settle(window);

        // The compact view: Notepad – the logo and the tabs in the title bar, the formatting bar, the text, the status bar.
        notes.IsCompactMode = true;
        window.Width = 960;
        window.Height = 640;
        Settle(window, waitLonger: true);
        Save(window, folder, $"main-compact-{theme}.png", report);
        notes.IsCompactMode = false;

        window.IsExiting = true;
        window.Close();
        Settle(window);
    }

    private static Task PasswordDialogSnapshot(Window owner, string folder, string theme, SnapshotReport report)
    {
        var dialog = PasswordDialog.ForSnapshot();
        var shown = dialog.ShowDialog(owner);
        Settle(dialog);
        Save(dialog, folder, $"dialog-password-{theme}.png", report);
        dialog.Close();
        Settle(owner);
        return shown;
    }

    private static void RenderToast(string folder, string theme, SnapshotReport report)
    {
        var toast = MatNotification.Show("Update verfügbar", "Version 1.1.2 wird installiert, wenn du ThixNote schließt. Deine Notizen bleiben, wie sie sind.");
        Settle(toast);
        Save(toast, folder, $"toast-{theme}.png", report);
        toast.Close();
        Settle(toast);
    }

    /// <summary>Lets the dispatcher, the layout and the render timer run; longer for the Markdown preview and the images.</summary>
    internal static void Settle(TopLevel topLevel, bool waitLonger = false)
    {
        for (var round = 0; round < (waitLonger ? 40 : 8); round++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(waitLonger ? 40 : 15);
        }

        Dispatcher.UIThread.RunJobs();
        topLevel.UpdateLayout();
    }

    private static void Save(TopLevel topLevel, string folder, string name, SnapshotReport report)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = topLevel.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{name}: no frame was rendered");
        frame.Save(Path.Combine(folder, name));
        report.Written.Add(name);
    }

    /// <summary>
    /// Einstellungen › Cloud in its three states: the sign-in, the code that waits for the browser, and the connection. No
    /// cloud and no browser: the code, the account and the state are fixed values; the settings forget them again.
    /// </summary>
    private static void RenderCloudTab(MainWindow window, Pages.SettingsPage settings, string folder, string theme, SnapshotReport report)
    {
        settings.ShowTab(Pages.SettingsPage.CloudTab);
        settings.ServerUrlBox.Text = CloudUrl;
        settings.DeviceNameBox.Text = MachineName;
        Settle(window);
        Save(window, folder, $"main-settings-cloud-signin-{theme}.png", report);

        settings.ShowLinkCodeForSnapshot("K7QF-M2XD", $"{CloudUrl}/Connect?code=K7QF-M2XD");
        Settle(window);
        Save(window, folder, $"main-settings-cloud-code-{theme}.png", report);
        settings.ShowLinkCodeForSnapshot(null);

        SettingsService.UpdateCloud(_ => new CloudSettings { ServerUrl = CloudUrl, UserName = "anna", DisplayName = "Anna Berger", DeviceName = MachineName, DeviceId = 7 });
        SyncService.ShowStatusForSnapshot(new SyncStatus(CloudState.Online, "Synchronisiert", Moment.AddMinutes(-1)));
        Settle(window);
        Save(window, folder, $"main-settings-cloud-{theme}.png", report);

        ForgetCloud();
        settings.ShowLinkCodeForSnapshot(null);
        settings.ShowTab(Pages.SettingsPage.GeneralTab);
        Settle(window);
    }

    private static void ForgetCloud()
    {
        SyncService.ShowStatusForSnapshot(null);
        SettingsService.UpdateCloud(_ => new CloudSettings());
    }

    /// <summary>Pins clock, version and computer name and keeps the policies out.</summary>
    private static IDisposable Isolate()
    {
        AppClock.Pin(Moment);
        AppInfo.Pin(Version, Channel, buildNumber: 0, MachineName);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        return new Restore(() =>
        {
            ForgetCloud();
            AccessPolicy.UsePolicyReaderForTests(null);
            AppInfo.Pin(null);
            AppClock.Pin(null);
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>What a snapshot wrote (file names) and what it left out, with the reason.</summary>
internal sealed class SnapshotReport
{
    public List<string> Written { get; } = [];

    public List<(string File, string Reason)> Skipped { get; } = [];
}

/// <summary>The pages of the setup that "ThixNote --snapshot" draws (setup-welcome ... setup-uninstall).</summary>
internal enum SetupSnapshotPage
{
    Welcome,
    Options,
    Progress,
    Finish,
    Update,
    Uninstall,
}
