using System.Runtime.Versioning;
using MatDesktop;
using ThixNote.Controls;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Setup;

/// <summary>
/// The Windows setup of ThixNote in MatDesktop's setup window (SETUP-01, SETUP-02): one window for install, update and
/// uninstall, 600 × 560, the footer with "Version x · channel" (SETUP-07). Install: Welcome (per user, no administrator
/// rights, where to: SETUP-03) → Options → Progress → Finish. Update: Welcome with the installed and the new version →
/// Progress → Finish. Uninstall: what goes and "Auch meine Einstellungen löschen" (off; the notes are never deleted) →
/// Progress → Finish (SETUP-04). The work (InstallService) runs on a background task and reports its steps.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ThixNoteSetup
{
    /// <summary>Shows the assistant; <paramref name="finished"/> gets true when the program folder has to be removed after this process ended.</summary>
    public static void Run(bool isUninstall, Action<bool> finished)
    {
        var removeProgramFolder = false;
        var isUpdate = !isUninstall && InstallService.IsInstalled;
        var source = App.ExecutablePath;
        MatSetupWindow? window = null;
        window = Create(isUninstall, isUpdate, InstallService.InstalledVersion, InstallService.ProgramFolder, async run =>
        {
            void Report(string step) => run.Progress.Report(new MatSetupReport(step, MatSetupStepState.Done));
            try
            {
                if (isUninstall)
                {
                    removeProgramFolder = await Task.Run(() => InstallService.Uninstall(run.DeleteData, Report));
                    window!.Steps[^1].Text = UninstalledText(run.DeleteData);
                }
                else
                {
                    var choices = isUpdate ? null : Choices(run.Options);
                    await Task.Run(() => InstallService.Install(source, choices, Report));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                Log.Error(isUninstall ? "Uninstall failed" : "Setup failed", exception);
                throw new InvalidOperationException($"{exception.Message}\n\nDetails stehen im Protokoll ({AppPaths.LogDirectory}).", exception);
            }
        });

        window.Finished += (_, _) =>
        {
            if (window.StartNow)
            {
                InstallService.StartInstalled();
            }

            finished(removeProgramFolder);
        };
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// The window for install (or update) or uninstall, with what it shows decided here once: the mode, the installed
    /// version and the program folder. <paramref name="work"/> is the work of the Install, Update or Uninstall button.
    /// The self test and the snapshots pass work that touches nothing.
    /// </summary>
    public static MatSetupWindow Create(bool isUninstall, bool isUpdate, string? installedVersion, string programFolder, Func<MatSetupRun, Task> work)
    {
        var mode = isUninstall ? MatSetupMode.Uninstall : isUpdate ? MatSetupMode.Update : MatSetupMode.Setup;
        IReadOnlyList<MatSetupStep> steps = mode switch
        {
            MatSetupMode.Uninstall =>
            [
                MatSetupStep.Uninstall("Programm, Startmenü-Eintrag, Desktop-Verknüpfung und Dateitypen werden entfernt. Deine Notizen bleiben in jedem Fall erhalten."),
            ],
            MatSetupMode.Update =>
            [
                MatSetupStep.Welcome(string.Empty, Note("Ein laufendes ThixNote speichert seine Notizen und wird beendet. Deine Notizen und Einstellungen bleiben erhalten.")),
            ],
            _ =>
            [
                // SETUP-03: for the user, without administrator rights, and where to. The folder on a line of its own: wrapped
                // inside a sentence the path breaks after "C:".
                MatSetupStep.Welcome(
                    "ThixNote ist eine einfache Notiz-App: Jede Notiz ist eine normale Datei – Markdown, Text oder mit Kennwort verschlüsselt – mit Tabs wie in einem Editor.",
                    Note($"Wird für dein Benutzerkonto installiert – ohne Administratorrechte – nach\n{programFolder}")),
                MatSetupStep.WithOptions(null, [.. Options()]),
            ],
        };

        var finish = isUninstall
            ? MatSetupStep.Finish(UninstalledText(deleteData: false), offersStart: false)
            : MatSetupStep.Finish($"ThixNote {AppInfo.Version} ist installiert. Du findest es im Startmenü; ein Doppelklick auf eine Notiz öffnet sie in ThixNote.");
        var version = isUninstall ? installedVersion ?? AppInfo.Version : AppInfo.Version;
        return new MatSetupWindow(new MatSetup(AppInfo.ProductName, version, AppInfo.Channel, mode, steps, work)
        {
            Logo = Ui.Logo,
            WordMark = Ui.WordMark(MatTokens.Type.Fs2xl),
            InstalledVersion = installedVersion ?? "eine ältere Version",
            Finish = finish,
            DeleteDataText = "Auch meine Einstellungen löschen",
            DeleteDataDescription = "Einstellungen und Protokolle auf diesem Computer. Deine Notizen bleiben in jedem Fall erhalten.",
        });
    }

    /// <summary>
    /// The switches of the Options step from the install options (installer.json, SETUP-08), each on as its default: the same
    /// questions ThixLauncher asks before it installs quietly. File types: key "type:.md".
    /// </summary>
    internal static IEnumerable<MatSetupOption> Options()
    {
        var config = InstallerOptions.Config;
        var first = true;
        foreach (var type in config.FileTypes)
        {
            var description = first ? "Sonst steht ThixNote für diese Dateien unter „Öffnen mit“" : null;
            yield return new MatSetupOption(FileTypeKey + type.Extension, $"{type.Name} ({type.Extension}) mit ThixNote öffnen", description, type.Default);
            first = false;
        }

        if (config.DesktopShortcut is { } shortcut)
        {
            yield return MatSetupOption.DesktopShortcut("Ein Startmenü-Eintrag wird immer angelegt") with { IsOn = shortcut.Default };
        }

        if (config.AutomaticUpdates is { } updates)
        {
            yield return MatSetupOption.AutomaticUpdates("Neue Versionen werden installiert, wenn du ThixNote schließt") with { IsOn = updates.Default };
        }
    }

    /// <summary>The answers of the Options step as install choices.</summary>
    internal static InstallChoices Choices(IReadOnlyDictionary<string, bool> options) => new(
        options.TryGetValue("shortcut", out var shortcut) ? shortcut : null,
        null,
        options.TryGetValue("updates", out var updates) ? updates : null,
        InstallerOptions.Config.FileTypes.Where(type => options.GetValueOrDefault(FileTypeKey + type.Extension)).Select(type => type.Extension).ToList());

    private const string FileTypeKey = "type:";

    /// <summary>A note under the text of a step, in the tint of information.</summary>
    private static InfoBar Note(string text)
    {
        var note = new InfoBar { Margin = new Avalonia.Thickness(0) };
        note.Show(text, InfoSeverity.Info);
        return note;
    }

    private static string UninstalledText(bool deleteData) =>
        deleteData ? "Programm und Einstellungen wurden entfernt. Deine Notizen sind noch da." : "Das Programm wurde entfernt. Deine Notizen und Einstellungen bleiben erhalten.";
}
