using Microsoft.Win32;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;
using ThixNote.Core.Platform.Windows;

namespace ThixNote.Tests;

/// <summary>Settings and policies, the update rules and the Windows installation (in test locations only).</summary>
public sealed class ServiceTests
{
    [Fact]
    public void Settings_survive_a_restart_and_the_lock_policy_keeps_them()
    {
        using var data = new TestData();
        SettingsService.Update(settings => settings with { Theme = ThemeChoice.Dark, InstallUpdatesAutomatically = false });
        SettingsStore.Reset();
        Assert.Equal(ThemeChoice.Dark, SettingsService.Get().Theme);
        Assert.False(SettingsService.Get().InstallUpdatesAutomatically);

        AccessPolicy.UsePolicyReaderForTests(policy => policy == "LockConfiguration");
        var locked = Assert.Throws<BusinessException>(() => SettingsService.Update(settings => settings with { Theme = ThemeChoice.Light }));
        Assert.Contains("LockConfiguration", locked.Message);

        // What ThixNote remembers by itself (tabs, window) is no setting: the lock does not stop it.
        SettingsService.Remember(settings => settings with { WindowWidth = 900, EditorZoom = 130 });
        Assert.Equal(900, SettingsService.Get().WindowWidth);
        Assert.Equal(130, SettingsService.Get().EditorZoom);
        Assert.Equal(ThemeChoice.Dark, SettingsService.Get().Theme);
    }

    [Fact]
    public void Another_notes_folder_is_checked_and_the_default_stays_empty()
    {
        using var data = new TestData();
        Assert.Throws<BusinessException>(() => SettingsService.SetNotesFolder("relativ/ordner"));

        var other = Path.Combine(data.Folder, "Andere Notizen");
        SettingsService.SetNotesFolder(other);
        Assert.True(Directory.Exists(other));
        Assert.Equal(other, SettingsService.NotesFolder);

        SettingsService.SetNotesFolder(AppPaths.DefaultNotesFolder);
        Assert.Equal(string.Empty, SettingsService.Get().NotesFolder);
        Assert.Equal(AppPaths.DefaultNotesFolder, SettingsService.NotesFolder);
    }

    [Fact]
    public void An_update_is_newer_only_in_its_own_channel_and_only_safe_files_are_taken()
    {
        var manifest = new UpdateManifest("1.1.9-20261116", "release", 9, "ThixNoteSetup.exe", "AB", DateTimeOffset.UtcNow,
            new Dictionary<string, UpdatePackage> { ["osx-arm64"] = new("ThixNote-macOS-arm64.zip", "CD") });

        Assert.True(UpdateService.IsNewer(manifest, "release", 8));
        Assert.False(UpdateService.IsNewer(manifest, "release", 9));
        Assert.False(UpdateService.IsNewer(manifest, "nightly", 1));
        Assert.Equal("ThixNoteSetup.exe", UpdateService.PackageFor(manifest, "win-x64")?.File);
        Assert.Equal("ThixNote-macOS-arm64.zip", UpdateService.PackageFor(manifest, "osx-arm64")?.File);
        Assert.Null(UpdateService.PackageFor(manifest, "osx-x64"));

        Assert.True(UpdateService.IsSafeFileName("ThixNoteSetup.exe"));
        Assert.False(UpdateService.IsSafeFileName("../ThixNoteSetup.exe"));
        Assert.False(UpdateService.IsSafeFileName("ThixNote.sh"));
        Assert.EndsWith("/releases/download/nightly-channel/thixnote-update.json", UpdateService.ManifestUrl("nightly"));
    }

    [Fact]
    public void The_mac_update_reopens_ThixNote_only_when_asked()
    {
        var reopen = MacAppBundle.ReplacementScript(42, "/Applications/ThixNote.app", "/tmp/new/ThixNote.app", "/tmp/update.log", reopen: true);
        var stay = MacAppBundle.ReplacementScript(42, "/Applications/ThixNote.app", "/tmp/new/ThixNote.app", "/tmp/update.log", reopen: false);
        Assert.Contains("open \"$BUNDLE\"", reopen);
        Assert.DoesNotContain("open \"$BUNDLE\"", stay);
        Assert.Contains("while kill -0 42", stay);
    }

    [Fact]
    public async Task The_policy_DisableUpdates_stops_every_update()
    {
        using var data = new TestData();
        AccessPolicy.UsePolicyReaderForTests(policy => policy == "DisableUpdates");
        Assert.False(UpdateService.CanUpdate);
        await Assert.ThrowsAsync<BusinessException>(() => UpdateService.CheckAsync());
    }

    [Fact]
    public void Install_update_and_uninstall_in_test_locations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the setup is Windows only (macOS: the app bundle)
        }

        using var data = new TestData();
        var program = Path.Combine(data.Folder, "Programs");
        var source = Path.Combine(data.Folder, "Download", "ThixNote.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "not a real program");
        InstallService.UseLocationsForTests(program);
        try
        {
            // A first install takes the defaults of installer.json for what it is not told: .md and .txts open with ThixNote.
            var log = new List<string>();
            InstallService.Install(source, new InstallChoices(DesktopShortcut: true, AutomaticUpdates: false), log.Add);
            Assert.True(InstallService.IsInstalled);
            Assert.True(File.Exists(InstallService.StartMenuShortcut));
            Assert.True(InstallService.HasDesktopShortcut);
            Assert.Equal([".md", ".txts"], InstallService.AssociatedFileTypes());
            Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
            Assert.Contains(log, line => line.Contains("Installation abgeschlossen", StringComparison.Ordinal));

            // Every type ThixNote opens is under "Öffnen mit", also .txt that does not open with it.
            Assert.Equal("ThixNote.txts", FileAssociations.HandlerOf(".txts"));
            Assert.Null(FileAssociations.HandlerOf(".txt"));
            Assert.Contains("ThixNote.txt", OpenWithProgIds(program, ".txt"));
            Assert.Equal($"\"{InstallService.InstalledExecutable}\" \"%1\"", RegistryValue(program, @"ThixNote.txt\shell\open\command"));

            // An update keeps what the user chose; a given option changes it.
            InstallService.Install(source, null, log.Add);
            Assert.True(InstallService.HasDesktopShortcut);
            Assert.Equal([".md", ".txts"], InstallService.AssociatedFileTypes());
            Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
            InstallService.Install(source, new InstallChoices(DesktopShortcut: false, FileTypes: [".txt"]), log.Add);
            Assert.False(InstallService.HasDesktopShortcut);
            Assert.Equal([".txt"], InstallService.AssociatedFileTypes());
            Assert.Null(FileAssociations.HandlerOf(".md"));
            Assert.Contains("ThixNote.md", OpenWithProgIds(program, ".md"));

            // The notes are never part of the user data that an uninstall may delete.
            Directory.CreateDirectory(data.NotesFolder);
            File.WriteAllText(Path.Combine(data.NotesFolder, "Bleibt.md"), "x");
            InstallService.Uninstall(deleteUserData: true, log.Add);
            Assert.False(InstallService.IsInstalled);
            Assert.False(File.Exists(InstallService.StartMenuShortcut));
            Assert.Empty(InstallService.AssociatedFileTypes());
            Assert.DoesNotContain(OpenWithProgIds(program, ".md"), name => name.StartsWith("ThixNote", StringComparison.Ordinal));
            Assert.Null(RegistryValue(program, @"Applications\ThixNote.exe\shell\open\command"));
            Assert.False(Directory.Exists(program));
            Assert.True(File.Exists(Path.Combine(data.NotesFolder, "Bleibt.md")));
        }
        finally
        {
            InstallService.UseLocationsForTests(null);
            InstallService.RemoveTestRegistry();
        }
    }

    [Fact]
    public void An_update_of_ThixNote_1_1_keeps_txts_with_ThixNote_and_replaces_its_old_file_types()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var data = new TestData();
        var program = Path.Combine(data.Folder, "Programs");
        var source = Path.Combine(data.Folder, "Download", "ThixNote.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "not a real program");
        InstallService.UseLocationsForTests(program);
        try
        {
            InstallService.Install(source, new InstallChoices(FileTypes: []), _ => { });
            var classes = ClassesKey(program);

            // What ThixNote 1.1 wrote: .txts opens with ThixNote.SecureNote, .md and .txt offer ThixNote.Note.
            using (var key = Registry.CurrentUser.CreateSubKey($@"{classes}\.txts"))
            {
                key.SetValue("", "ThixNote.SecureNote");
            }

            using (var key = Registry.CurrentUser.CreateSubKey($@"{classes}\ThixNote.SecureNote\shell\open\command"))
            {
                key.SetValue("", "old");
            }

            using (var key = Registry.CurrentUser.CreateSubKey($@"{classes}\.md\OpenWithProgids"))
            {
                key.SetValue("ThixNote.Note", Array.Empty<byte>(), RegistryValueKind.None);
            }

            Assert.Equal([".txts"], InstallService.AssociatedFileTypes());
            InstallService.Install(source, null, _ => { });

            Assert.Equal("ThixNote.txts", FileAssociations.HandlerOf(".txts"));
            Assert.Equal([".txts"], InstallService.AssociatedFileTypes());
            Assert.Null(RegistryValue(program, @"ThixNote.SecureNote\shell\open\command"));
            Assert.DoesNotContain("ThixNote.Note", OpenWithProgIds(program, ".md"));
            Assert.Contains("ThixNote.md", OpenWithProgIds(program, ".md"));
        }
        finally
        {
            InstallService.UseLocationsForTests(null);
            InstallService.RemoveTestRegistry();
        }
    }

    [Fact]
    public void The_command_line_carries_the_install_options_and_refuses_what_it_does_not_know()
    {
        var request = InstallerOptions.Parse(["--quiet", "--desktop-shortcut=yes", "--automatic-updates=no", "--file-types=md,.TXTS", "--start"]);
        Assert.Equal(SetupAction.Install, request.Action);
        Assert.True(request.Start);
        Assert.Equal((true, false), (request.Choices.DesktopShortcut, request.Choices.AutomaticUpdates));
        Assert.Equal([".md", ".txts"], request.Choices.FileTypes);
        Assert.Equal(["--desktop-shortcut=yes", "--automatic-updates=no", "--file-types=.md,.txts"], InstallerOptions.ToArguments(request.Choices));
        Assert.Equal([".md", ".txts"], InstallerOptions.Parse(["--update", "--file-types=default"]).Choices.FileTypes);
        Assert.Empty(InstallerOptions.Parse(["--configure", "--file-types=none"]).Choices.FileTypes!);
        Assert.Equal(new InstallChoices(), InstallerOptions.Parse(["--update", "--restart"]).Choices);
        Assert.Equal(SetupAction.None, InstallerOptions.Parse([@"C:\Notizen\a=b.md"]).Action);

        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--desktop-shortcut=vielleicht"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--file-types=.docx"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--autostart=yes"])); // ThixNote has no autostart
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--quiet", "--color=blue"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--desktop-shortcut=yes"]));
        Assert.Throws<BusinessException>(() => InstallerOptions.Parse(["--uninstall", "--file-types=none"]));
    }

    [Fact]
    public void The_embedded_installer_json_offers_what_ThixNote_opens_and_the_mac_bundle_declares_it()
    {
        var config = InstallerOptions.Config;
        Assert.NotNull(config.DesktopShortcut);
        Assert.NotNull(config.AutomaticUpdates);
        Assert.Null(config.Autostart);
        Assert.Equal([".md", ".txt", ".txts"], config.FileTypes.Select(type => type.Extension));
        Assert.All(config.FileTypes, type => Assert.True(NoteLibrary.IsSupportedFile("x" + type.Extension)));
        Assert.Equal([".md", ".txts"], InstallerOptions.Defaults.FileTypes);
        Assert.Equal((false, true), (InstallerOptions.Defaults.DesktopShortcut, InstallerOptions.Defaults.AutomaticUpdates));

        // What CI publishes as "installer" is this file; the Info.plist of the bundle names every extension.
        var workflow = File.ReadAllText(Path.Combine(Repository.Root, ".github", "workflows", "desktop.yml"));
        Assert.Contains("\"installer\": $(cat ../source/src/ThixNote.Core/installer.json)", workflow, StringComparison.Ordinal);
        var package = File.ReadAllText(Path.Combine(Repository.Root, "scripts", "package-macos.sh"));
        Assert.All(config.FileTypes, type => Assert.Contains($"<string>{type.Extension.TrimStart('.')}</string>", package, StringComparison.Ordinal));
        Assert.Contains($"<string>{MacAppBundle.BundleId}</string>", package, StringComparison.Ordinal);
    }

    private static string ClassesKey(string program) => $@"Software\ThixNoteTests\{Path.GetFileName(program)}\Classes";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string[] OpenWithProgIds(string program, string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey(program)}\{extension}\OpenWithProgids");
        return key?.GetValueNames() ?? [];
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? RegistryValue(string program, string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesKey(program)}\{path}");
        return key?.GetValue("") as string;
    }
}
