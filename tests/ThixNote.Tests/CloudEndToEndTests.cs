using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;

namespace ThixNote.Tests;

/// <summary>
/// Two computers against a REAL MatCloud (e.g. the local stack of ../MatCloud): runs only when <c>THIXNOTE_CLOUD_URL</c>, <c>_USER</c> and
/// <c>_PASSWORD</c> name a cloud and an account with a password. Everything happens in a folder of its own, so notes the account already has do not matter;
/// both computers sign out at the end.
/// </summary>
public sealed class CloudEndToEndTests : IDisposable
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("THIXNOTE_CLOUD_URL");
    private static readonly string? User = Environment.GetEnvironmentVariable("THIXNOTE_CLOUD_USER");
    private static readonly string? Password = Environment.GetEnvironmentVariable("THIXNOTE_CLOUD_PASSWORD");

    private readonly TestData _data = new();

    public void Dispose()
    {
        SyncService.HandlerForTests = null;
        _data.Dispose();
    }

    [Fact]
    public async Task Two_computers_keep_their_notes_the_same_through_a_real_MatCloud()
    {
        if (string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(User) || string.IsNullOrEmpty(Password))
        {
            return; // no cloud configured: nothing to check here
        }

        SyncService.HandlerForTests = null;
        var run = "E2E-" + Guid.NewGuid().ToString("N")[..8];
        var desktop = new Computer(Path.Combine(_data.Folder, "desktop"), run + "-Desktop");
        var laptop = new Computer(Path.Combine(_data.Folder, "laptop"), run + "-Laptop");
        try
        {
            desktop.Use();
            Directory.CreateDirectory(Path.Combine(desktop.Notes, run));
            var plan = NoteLibrary.CreateNote(Path.Combine(desktop.Notes, run), "Plan", "# Plan\n\n- Meilensteine");
            await desktop.ConnectAsync();
            await desktop.SyncAsync();

            await laptop.ConnectAsync();
            await laptop.SyncAsync();
            Assert.Equal("# Plan\n\n- Meilensteine", laptop.Read(run + "/Plan.md"));

            // changed on the laptop
            laptop.Use();
            NoteContent.Write(laptop.PathOf(run + "/Plan.md"), "# Plan\n\n- Meilensteine\n- Termine");
            await laptop.SyncAsync();
            await desktop.SyncAsync();
            Assert.Equal("# Plan\n\n- Meilensteine\n- Termine", desktop.Read(run + "/Plan.md"));

            // renamed on the desktop
            desktop.Use();
            NoteLibrary.Rename(plan, "Projektplan");
            await desktop.SyncAsync();
            await laptop.SyncAsync();
            Assert.False(File.Exists(laptop.PathOf(run + "/Plan.md")));
            Assert.Equal("# Plan\n\n- Meilensteine\n- Termine", laptop.Read(run + "/Projektplan.md"));

            // deleted on the laptop: gone on the desktop too
            laptop.Use();
            NoteLibrary.MoveToTrash(laptop.PathOf(run + "/Projektplan.md"));
            await laptop.SyncAsync();
            await desktop.SyncAsync();
            Assert.False(File.Exists(desktop.PathOf(run + "/Projektplan.md")));
        }
        finally
        {
            foreach (var computer in new[] { desktop, laptop })
            {
                computer.Use();
                if (SyncService.IsConnected)
                {
                    await SyncService.DisconnectAsync();
                }
            }
        }
    }

    /// <summary>One computer: its own data folder (settings with its token, the sync index) and notes folder.</summary>
    private sealed class Computer(string folder, string name)
    {
        public string Notes => Path.Combine(folder, "Notizen");

        public void Use()
        {
            AppPaths.UseDirectoryForTests(folder);
            SettingsStore.Reset();
            NoteSync.Reset();
            Directory.CreateDirectory(Notes);
        }

        public async Task ConnectAsync()
        {
            Use();
            await SyncService.ConnectAsync(Url!, User!, Password!, name);
        }

        public async Task SyncAsync()
        {
            Use();
            await SyncService.SyncOnceAsync();
        }

        public string PathOf(string relative) => Path.GetFullPath(Path.Combine(Notes, relative.Replace('/', Path.DirectorySeparatorChar)));

        public string Read(string relative) => File.ReadAllText(PathOf(relative));
    }
}
