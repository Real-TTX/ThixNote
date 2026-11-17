using System.Text.Json;
using MatCloud.Contract;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Tests;

/// <summary>
/// The sync with MatCloud's general sync against <see cref="FakeCloud"/>: two computers (two data folders, each with its
/// notes folder) that keep their notes the same – new, changed, renamed, moved, deleted, in conflict, protected – and the
/// sign-in with a password and in the browser.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private const string Server = "https://cloud.example.test";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();
    private readonly Device _anna;
    private readonly Device _laptop;

    public SyncTests()
    {
        SyncService.HandlerForTests = () => _cloud;
        _anna = new Device(Path.Combine(_data.Folder, "anna-pc"), "ANNA-PC");
        _laptop = new Device(Path.Combine(_data.Folder, "laptop"), "LAPTOP");
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Two_computers_keep_new_changed_renamed_moved_and_deleted_notes_the_same()
    {
        _anna.Use();
        var plan = NoteLibrary.CreateNote(_anna.Notes, "Plan", "# Plan\n\n- Meilensteine");
        NoteLibrary.CreateFolder(_anna.Notes, "Leer");
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();

        Assert.Equal("# Plan\n\n- Meilensteine", _laptop.Read("Plan.md"));
        Assert.True(Directory.Exists(Path.Combine(_laptop.Notes, "Leer")), "the empty folder did not travel");
        Assert.Single(_cloud.Notes);

        // Changed on the laptop
        _laptop.Use();
        NoteContent.Write(_laptop.PathOf("Plan.md"), "# Plan\n\n- Meilensteine\n- Termine");
        await _laptop.SyncAsync();
        await _anna.SyncAsync();
        Assert.Equal("# Plan\n\n- Meilensteine\n- Termine", _anna.Read("Plan.md"));

        // Renamed in ThixNote on Anna's computer, then moved into a folder outside ThixNote (Explorer): the key stays.
        _anna.Use();
        var key = Assert.Single(_cloud.Notes).Key;
        var renamed = NoteLibrary.Rename(plan, "Projektplan");
        Directory.CreateDirectory(Path.Combine(_anna.Notes, "Arbeit"));
        File.Move(renamed, Path.Combine(_anna.Notes, "Arbeit", "Projektplan.md"));
        await _anna.SyncAsync();
        await _laptop.SyncAsync();
        Assert.False(File.Exists(_laptop.PathOf("Plan.md")));
        Assert.Equal("# Plan\n\n- Meilensteine\n- Termine", _laptop.Read("Arbeit/Projektplan.md"));
        var note = Assert.Single(_cloud.Notes);
        Assert.Equal(key, note.Key);
        Assert.Contains("\"path\":\"Arbeit/Projektplan.md\"", note.Data, StringComparison.Ordinal);

        // Deleted on the laptop: on Anna's computer it goes into the trash, not away.
        _laptop.Use();
        NoteLibrary.MoveToTrash(_laptop.PathOf("Arbeit/Projektplan.md"));
        await _laptop.SyncAsync();
        await _anna.SyncAsync();
        Assert.False(File.Exists(_anna.PathOf("Arbeit/Projektplan.md")));
        Assert.True(File.Exists(Path.Combine(_data.TrashFolder, "Projektplan.md")), "the note is not in the trash");
        Assert.Empty(_cloud.Notes);
        Assert.True(_cloud.Items[("notes", key)].IsDeleted);

        // And nothing goes up again by itself.
        var revision = _cloud.Revision;
        await _anna.SyncAsync();
        await _laptop.SyncAsync();
        Assert.Equal(revision, _cloud.Revision);
    }

    [Fact]
    public async Task A_note_changed_on_both_computers_keeps_both_versions()
    {
        _anna.Use();
        NoteLibrary.CreateNote(_anna.Notes, "Einkauf", "Milch");
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();

        _anna.Use();
        NoteContent.Write(_anna.PathOf("Einkauf.md"), "Milch\nBrot");
        _laptop.Use();
        NoteContent.Write(_laptop.PathOf("Einkauf.md"), "Milch\nEier");

        await _anna.SyncAsync();   // Anna's version reaches the cloud first …
        await _laptop.SyncAsync(); // … the laptop finds it there and keeps its own next to it
        await _anna.SyncAsync();

        foreach (var device in new[] { _anna, _laptop })
        {
            Assert.Equal("Milch\nBrot", device.Read("Einkauf.md"));
            Assert.Equal("Milch\nEier", device.Read("Einkauf (Konflikt LAPTOP).md"));
        }

        Assert.Equal(2, _cloud.Notes.Count);
    }

    [Fact]
    public async Task The_first_sync_merges_notes_both_computers_had_already()
    {
        _anna.Use();
        NoteLibrary.CreateNote(_anna.Notes, "Gleich", "derselbe Text");
        NoteLibrary.CreateNote(_anna.Notes, "Ideen", "von Anna");
        _laptop.Use();
        NoteLibrary.CreateNote(_laptop.Notes, "Gleich", "derselbe Text");
        NoteLibrary.CreateNote(_laptop.Notes, "Ideen", "vom Laptop");

        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        await _anna.SyncAsync();

        Assert.Equal(3, _cloud.Notes.Count); // "Gleich" once, "Ideen" and the laptop's copy of it
        foreach (var device in new[] { _anna, _laptop })
        {
            Assert.Equal("derselbe Text", device.Read("Gleich.md"));
            Assert.Equal("von Anna", device.Read("Ideen.md"));
            Assert.Equal("vom Laptop", device.Read("Ideen (Konflikt LAPTOP).md"));
            Assert.Equal(3, Directory.GetFiles(device.Notes).Length);
        }
    }

    [Fact]
    public async Task A_protected_note_travels_only_as_its_encrypted_bytes()
    {
        _anna.Use();
        var plain = NoteLibrary.CreateNote(_anna.Notes, "WLAN", "Kennwort: streng-geheim-42");
        var secret = NoteContent.Protect(plain, "Kennwort: streng-geheim-42", "s3cret!");
        NoteContent.Lock(secret);
        await _anna.ConnectAsync();
        await _anna.SyncAsync();

        var item = Assert.Single(_cloud.Notes);
        Assert.DoesNotContain("streng-geheim", item.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content\"", item.Data, StringComparison.Ordinal);
        using (var data = JsonDocument.Parse(item.Data!))
        {
            Assert.Equal("WLAN.txts", data.RootElement.GetProperty("path").GetString());
            Assert.Equal(File.ReadAllBytes(secret), Convert.FromBase64String(data.RootElement.GetProperty("bytes").GetString()!));
        }

        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        var copy = _laptop.PathOf("WLAN.txts");
        Assert.Equal(File.ReadAllBytes(secret), File.ReadAllBytes(copy));
        Assert.Equal("Kennwort: streng-geheim-42", NoteContent.Unlock(copy, "s3cret!"));
        NoteContent.Lock(copy);
    }

    [Fact]
    public async Task Protecting_a_note_keeps_its_key_and_the_text_leaves_the_cloud()
    {
        _anna.Use();
        var plain = NoteLibrary.CreateNote(_anna.Notes, "Konto", "IBAN DE00 1234");
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        var key = Assert.Single(_cloud.Notes).Key;

        _anna.Use();
        var secret = NoteContent.Protect(plain, "IBAN DE00 1234", "pw");
        NoteContent.Lock(secret);
        await _anna.SyncAsync();

        var item = Assert.Single(_cloud.Notes);
        Assert.Equal(key, item.Key);
        Assert.DoesNotContain("IBAN", item.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_note_open_with_unsaved_changes_is_not_touched_until_it_is_saved()
    {
        _anna.Use();
        NoteLibrary.CreateNote(_anna.Notes, "Brief", "Hallo");
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();

        _laptop.Use();
        NoteContent.Write(_laptop.PathOf("Brief.md"), "Hallo vom Laptop");
        await _laptop.SyncAsync();

        // Anna types in the open tab; nothing saved yet.
        var open = _anna.PathOf("Brief.md");
        SyncService.SetUnsavedChanges(open, true);
        await _anna.SyncAsync();
        Assert.Equal("Hallo", _anna.Read("Brief.md"));

        // Saved: both versions stay.
        _anna.Use();
        NoteContent.Write(open, "Hallo, liebe Grüße");
        SyncService.SetUnsavedChanges(open, false);
        await _anna.SyncAsync();
        Assert.Equal("Hallo vom Laptop", _anna.Read("Brief.md"));
        Assert.Equal("Hallo, liebe Grüße", _anna.Read("Brief (Konflikt ANNA-PC).md"));
    }

    [Fact]
    public async Task Changes_of_the_cloud_are_reported_for_the_open_tabs()
    {
        _anna.Use();
        NoteLibrary.CreateNote(_anna.Notes, "Liste", "eins");
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        _laptop.Use();
        NoteLibrary.Rename(_laptop.PathOf("Liste.md"), "Aufgaben");
        await _laptop.SyncAsync();

        var reported = new List<NoteChange>();
        void OnChanged(object? sender, IReadOnlyList<NoteChange> changes) => reported.AddRange(changes);
        SyncService.NotesChanged += OnChanged;
        try
        {
            await _anna.SyncAsync();
        }
        finally
        {
            SyncService.NotesChanged -= OnChanged;
        }

        var change = Assert.Single(reported);
        Assert.Equal(_anna.PathOf("Liste.md"), change.OldPath);
        Assert.Equal(_anna.PathOf("Aufgaben.md"), change.NewPath);
        Assert.False(change.ContentChanged);
    }

    [Fact]
    public async Task A_device_removed_in_the_cloud_is_signed_out()
    {
        await _anna.ConnectAsync();
        _cloud.RemoveAllDevices();
        await _anna.SyncAsync();

        Assert.False(SettingsService.Get().Cloud.IsConnected);
        Assert.Equal(CloudState.SignedOut, SyncService.Status.State);
        Assert.Equal(Server, SettingsService.Get().Cloud.ServerUrl); // the form shows the address again
        Assert.Equal(string.Empty, SettingsService.Get().Cloud.ProtectedToken);
    }

    [Fact]
    public async Task An_app_switched_off_in_the_cloud_waits_and_stays_signed_in()
    {
        await _anna.ConnectAsync();
        _cloud.IsAppSwitchedOff = true;
        await _anna.SyncAsync();

        Assert.True(SettingsService.Get().Cloud.IsConnected);
        Assert.Equal(CloudState.Offline, SyncService.Status.State);
        Assert.Contains("ausgeschaltet", SyncService.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signing_in_with_the_password_stores_only_a_protected_token()
    {
        _anna.Use();
        var error = await Assert.ThrowsAsync<BusinessException>(() => SyncService.ConnectAsync(Server, FakeCloud.UserName, "falsch", "ANNA-PC"));
        Assert.Equal("Password", error.Field);
        Assert.False(SettingsService.Get().Cloud.IsConnected);

        await SyncService.ConnectAsync("cloud.example.test/", FakeCloud.UserName, FakeCloud.Password, "ANNA-PC");
        var cloud = SettingsService.Get().Cloud;
        Assert.True(cloud.IsConnected);
        Assert.Equal(Server, cloud.ServerUrl);
        Assert.Equal(FakeCloud.DisplayName, cloud.DisplayName);
        Assert.DoesNotContain(FakeCloud.Password, File.ReadAllText(AppPaths.SettingsFile), StringComparison.Ordinal);
        Assert.All(_cloud.UserAgents, agent => Assert.StartsWith("ThixNote/", agent, StringComparison.Ordinal));
        Assert.Contains($"({AppInfo.PlatformName})", _cloud.UserAgents[0], StringComparison.Ordinal);

        await SyncService.DisconnectAsync();
        Assert.False(SettingsService.Get().Cloud.IsConnected);
        Assert.Equal(CloudState.Off, SyncService.Status.State);
    }

    [Fact]
    public async Task Signing_in_in_the_browser_waits_for_the_code_to_be_confirmed()
    {
        _anna.Use();
        var signIn = await SyncService.StartBrowserSignInAsync(Server, "ANNA-PC");
        Assert.Equal(_cloud.LastCode, signIn.Code);
        Assert.Equal($"{Server}/Connect?code={signIn.Code}", signIn.VerificationUrl);

        var waiting = SyncService.FinishBrowserSignInAsync(signIn);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);
        _cloud.Approve(signIn.Code);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        var cloud = SettingsService.Get().Cloud;
        Assert.True(cloud.IsConnected);
        Assert.Equal(FakeCloud.UserName, cloud.UserName);
        Assert.Equal("ANNA-PC", cloud.DeviceName);
    }

    [Fact]
    public async Task A_code_refused_in_the_browser_or_cancelled_signs_nothing_in()
    {
        _anna.Use();
        var refused = await SyncService.StartBrowserSignInAsync(Server, "ANNA-PC");
        _cloud.Deny(refused.Code);
        var error = await Assert.ThrowsAsync<BusinessException>(() => SyncService.FinishBrowserSignInAsync(refused).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("abgelehnt", error.Message, StringComparison.Ordinal);

        var cancelled = await SyncService.StartBrowserSignInAsync(Server, "ANNA-PC");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SyncService.FinishBrowserSignInAsync(cancelled, cancel.Token));
        Assert.False(SettingsService.Get().Cloud.IsConnected);
    }

    [Fact]
    public async Task The_address_is_checked_before_anything_is_sent()
    {
        _anna.Use();
        var empty = await Assert.ThrowsAsync<BusinessException>(() => SyncService.StartBrowserSignInAsync(" ", "ANNA-PC"));
        Assert.Equal("ServerUrl", empty.Field);
        var wrong = await Assert.ThrowsAsync<BusinessException>(() => SyncService.StartBrowserSignInAsync("ftp://cloud.example.test", "ANNA-PC"));
        Assert.Equal("ServerUrl", wrong.Field);
        Assert.Empty(_cloud.UserAgents);
    }

    [Fact]
    public async Task Installed_by_ThixLauncher_ThixNote_connects_with_its_sign_in_once()
    {
        _anna.Use();
        Assert.Null(await SyncService.ConnectFromLauncherAsync());
        var handoff = new DeviceHandoff(Server, _cloud.LeaveHandoff("ANNA-PC"), FakeCloud.UserName, DateTimeOffset.UtcNow.AddDays(1));
        CloudHandoff.Write(CloudApps.Note, handoff);

        Assert.Equal(FakeCloud.DisplayName, await SyncService.ConnectFromLauncherAsync());
        var cloud = SettingsService.Get().Cloud;
        Assert.True(cloud.IsConnected);
        Assert.Equal((Server, "ANNA-PC"), (cloud.ServerUrl, cloud.DeviceName));
        Assert.False(CloudHandoff.Exists(CloudApps.Note));

        // connected already: a later hand-over is only removed
        CloudHandoff.Write(CloudApps.Note, handoff with { PollToken = _cloud.LeaveHandoff("ANNA-PC") });
        Assert.Null(await SyncService.ConnectFromLauncherAsync());
        Assert.False(CloudHandoff.Exists(CloudApps.Note));
    }

    [Fact]
    public async Task A_sign_in_of_ThixLauncher_waits_while_the_cloud_is_away_and_goes_when_it_was_used()
    {
        _anna.Use();
        var handoff = new DeviceHandoff(Server, _cloud.LeaveHandoff("ANNA-PC"), FakeCloud.UserName, DateTimeOffset.UtcNow.AddDays(1));
        CloudHandoff.Write(CloudApps.Note, handoff);

        SyncService.HandlerForTests = () => new UnreachableHandler();
        Assert.Null(await SyncService.ConnectFromLauncherAsync());
        Assert.True(CloudHandoff.Exists(CloudApps.Note));

        // already used (here: by hand), or expired: removed, not connected, no error
        SyncService.HandlerForTests = () => _cloud;
        using (var client = new MatCloudClient(Server, null, "test", _cloud))
        {
            await client.RedeemHandoffAsync(handoff, CancellationToken.None);
        }

        Assert.Null(await SyncService.ConnectFromLauncherAsync());
        Assert.False(CloudHandoff.Exists(CloudApps.Note));
        Assert.False(SettingsService.Get().Cloud.IsConnected);

        // a hand-over that expired on this computer is never sent
        CloudHandoff.Write(CloudApps.Note, handoff with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        var asked = _cloud.UserAgents.Count;
        Assert.Null(await SyncService.ConnectFromLauncherAsync());
        Assert.Equal(asked, _cloud.UserAgents.Count);
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No such host is known.");
    }

    [Fact]
    public async Task The_policy_DisableCloud_keeps_every_note_on_the_computer()
    {
        _anna.Use();
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloud");
        await Assert.ThrowsAsync<BusinessException>(() => SyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, "ANNA-PC"));
        await Assert.ThrowsAsync<BusinessException>(() => SyncService.StartBrowserSignInAsync(Server, "ANNA-PC"));
        Assert.Empty(_cloud.UserAgents);
    }

    [Fact]
    public void The_copy_of_the_MatCloud_contract_is_unchanged()
    {
        // The contract is copied from MatCloud (node tools/contract-copy.js src/ThixNote.Core there) and never edited here.
        var folder = Path.Combine(Repository.Root, "src", "ThixNote.Core", "MatCloud");
        var lines = File.ReadAllLines(Path.Combine(folder, "matcloud.version"));
        Assert.StartsWith("MatCloud.Contract ", lines[0], StringComparison.Ordinal);
        var listed = new List<string>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            listed.Add(file);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"MatCloud/{file} was changed: copy the contract again from MatCloud instead of editing it");
        }

        var present = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'));
        Assert.Equal(listed.Order(StringComparer.Ordinal), present.Order(StringComparer.Ordinal));
    }

    /// <summary>One computer: its own data folder (settings with its token, the sync index) and notes folder.</summary>
    private sealed class Device(string folder, string name)
    {
        public string Notes => Path.Combine(folder, "Notizen");

        /// <summary>Makes this computer the one ThixNote runs on.</summary>
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
            await SyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, name);
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
