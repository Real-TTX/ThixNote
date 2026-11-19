using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Avalonia.Threading;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote;

/// <summary>
/// "ThixNote.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or single-file
/// packaging could break silently: JSON of the settings, the update manifest, the MatCloud contract and the sync index, the
/// protection of the device token (DPAPI), encryption, the Markdown export, the embedded document icon, Windows shortcuts (COM) and the windows of the app with their bindings. It runs in a fresh
/// temporary data folder (Program sets it) with the sample notes, never touches the user's notes, writes a report and ends
/// with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixNote {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { Theme = ThemeChoice.Dark, InstallUpdatesAutomatically = false });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { Theme: ThemeChoice.Dark, InstallUpdatesAutomatically: false }, "settings did not survive a save");
            SettingsService.Update(settings => settings with { Theme = ThemeChoice.System, InstallUpdatesAutomatically = true });
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", async () =>
        {
            const string json = """{"version":"1.1.9-20261116","channel":"release","buildNumber":9,"setup":"ThixNoteSetup.exe","sha256":"AB","publishedAt":"2026-11-16T12:00:00Z","packages":{"win-x64":{"file":"ThixNoteSetup.exe","sha256":"AB"}}}""";
            var manifest = await new StringContent(json, Encoding.UTF8, "application/json").ReadFromJsonAsync<UpdateManifest>();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixNoteSetup.exe", "manifest not read");
            return "";
        });

        var samples = SampleNotes.Create(AppPaths.DefaultNotesFolder);
        await Check("notes, encryption and export", () =>
        {
            Ensure(NoteLibrary.ListNotes(AppPaths.DefaultNotesFolder).Count == samples.Count, "sample notes not listed");
            var secret = samples[SampleNotes.Secret];
            Ensure(NoteContent.Unlock(secret, SampleNotes.Password).Contains("WLAN", StringComparison.Ordinal), "protected note not decrypted");
            NoteContent.Lock(secret);
            var html = NoteExporter.Render("Plan", File.ReadAllText(samples[SampleNotes.Plan]), ExportFormat.Html);
            Ensure(html.Contains("<table>", StringComparison.Ordinal), "Markdown not rendered to HTML");
            return Task.FromResult($"{samples.Count} notes");
        });

        await Check("install options (installer.json, arguments)", () =>
        {
            var config = InstallerOptions.Config;
            Ensure(config is { DesktopShortcut: not null, AutomaticUpdates: not null, Autostart: null } && config.FileTypes.Count == 3, "installer.json not read");
            var request = InstallerOptions.Parse([InstallerOptions.QuietArgument, "--desktop-shortcut=yes", "--file-types=md,txts"]);
            Ensure(request is { Action: SetupAction.Install, Choices.DesktopShortcut: true } && request.Choices.FileTypes is [".md", ".txts"], "arguments not read");
            return Task.FromResult(string.Join(", ", config.FileTypes.Select(type => type.Extension)));
        });

        await Check("cloud (contract JSON, sync index, token)", () =>
        {
            var item = new MatCloud.Contract.SyncItemDto { Collection = "notes", Key = "k1", Data = """{"path":"a.md","content":"x"}""", UpdatedAt = DateTimeOffset.UtcNow };
            var push = System.Text.Json.JsonSerializer.Serialize(new MatCloud.Contract.SyncPushRequest([item]), MatCloud.Contract.CloudContract.Json);
            Ensure(push.Contains("\"collection\":\"notes\"", StringComparison.Ordinal), "push not written");
            const string json = """{"items":[{"collection":"notes","key":"k1","data":"{}","updatedAt":"2026-11-16T12:00:00Z","revision":3}],"latest":3,"hasMore":false}""";
            var changes = System.Text.Json.JsonSerializer.Deserialize<MatCloud.Contract.SyncChangeSet>(json, MatCloud.Contract.CloudContract.Json);
            Ensure(changes is { Latest: 3, Items.Count: 1 } && changes.Items[0].Revision == 3, "change set not read");

            var indexFile = Path.Combine(AppPaths.DataDirectory, "self-test-index.json");
            new SyncIndex { Account = "a@b", LastRevision = 5, Notes = { ["k1"] = new SyncedNote { Path = "a.md", Hash = "h" } }, Deferred = { item } }.Save(indexFile);
            var index = SyncIndex.Load(indexFile);
            Ensure(index is { LastRevision: 5, Deferred.Count: 1 } && index.Notes["k1"].Path == "a.md", "sync index did not survive a save");

            if (OperatingSystem.IsWindows())
            {
                Ensure(TokenStore.Read("a@b", TokenStore.Save("a@b", "token-123")) == "token-123", "the token was not protected and read back (DPAPI)");
            }

            return Task.FromResult(SyncService.UserAgent);
        });

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM) and document icon", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixNote.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                using var icon = typeof(App).Assembly.GetManifestResourceStream("thixnote-document.ico");
                Ensure(icon is { Length: > 0 }, "the document icon of .txts is not embedded");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            SettingsService.Remember(settings => settings with { OpenFiles = [samples[SampleNotes.Plan], samples[SampleNotes.Secret]], ActiveFile = samples[SampleNotes.Plan] });
            var window = new MainWindow();
            window.Show();
            await Settle();
            var notes = window.Notes.ViewModel;
            Ensure(notes.Documents.Count == 2 && notes.ActiveDocument?.Text.Contains("Meilensteine", StringComparison.Ordinal) == true, "tabs not restored");
            Ensure(window.Notes.EditorBox.Text == notes.ActiveDocument!.Text, "editor not bound");
            Ensure(notes.LibraryItems.Count == samples.Count, "note list not filled");
            notes.ActiveDocument.TogglePreviewCommand.Execute(null);
            await Settle();
            notes.ActiveDocument.TogglePreviewCommand.Execute(null);
            window.ShowSettings();
            await Settle();
            window.ShowAbout();
            await Settle();
            window.Width = 640;
            await Settle();
            Ensure(window.IsCompact, "the narrow window is not the small editor");

            if (OperatingSystem.IsWindows())
            {
                var setup = Setup.ThixNoteSetup.Create(isUninstall: false, InstallService.IsInstalled, InstallService.InstalledVersion, InstallService.ProgramFolder, _ => Task.CompletedTask);
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Lets the dispatcher and the layout run (the real platform, a real window).</summary>
    private static async Task Settle()
    {
        for (var round = 0; round < 5; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(40);
        }
    }
}
