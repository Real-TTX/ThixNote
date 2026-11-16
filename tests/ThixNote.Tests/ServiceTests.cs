using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

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
            var log = new List<string>();
            InstallService.Install(source, new InstallOptions(DesktopShortcut: true, AssociateSecureNotes: true, InstallUpdatesAutomatically: false), log.Add);
            Assert.True(InstallService.IsInstalled);
            Assert.True(File.Exists(InstallService.StartMenuShortcut));
            Assert.True(InstallService.HasDesktopShortcut);
            Assert.True(InstallService.OpensSecureNotes);
            Assert.False(SettingsService.Get().InstallUpdatesAutomatically);
            Assert.Contains(log, line => line.Contains("Installation abgeschlossen", StringComparison.Ordinal));

            // An update keeps what the user chose.
            InstallService.Install(source, options: null, log.Add);
            Assert.True(InstallService.HasDesktopShortcut);
            Assert.True(InstallService.OpensSecureNotes);

            // The notes are never part of the user data that an uninstall may delete.
            Directory.CreateDirectory(data.NotesFolder);
            File.WriteAllText(Path.Combine(data.NotesFolder, "Bleibt.md"), "x");
            InstallService.Uninstall(deleteUserData: true, log.Add);
            Assert.False(InstallService.IsInstalled);
            Assert.False(File.Exists(InstallService.StartMenuShortcut));
            Assert.False(InstallService.OpensSecureNotes);
            Assert.False(Directory.Exists(program));
            Assert.True(File.Exists(Path.Combine(data.NotesFolder, "Bleibt.md")));
        }
        finally
        {
            InstallService.UseLocationsForTests(null);
            InstallService.RemoveTestRegistry();
        }
    }
}
