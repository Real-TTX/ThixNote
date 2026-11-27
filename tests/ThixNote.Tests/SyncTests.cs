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
    private const string SecretText = "Alter Text";
    private const string Password = "pw-1234";

    /// <summary>Where Anna's computer keeps its protected file when a plain version without the marker takes the note.</summary>
    private const string ProtectedCopy = "Titel (Konflikt ANNA-PC).txts";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();
    private readonly Device _anna;
    private readonly Device _laptop;
    private long _revisionBeforeProtection;

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
    public async Task Removing_the_protection_on_purpose_goes_up_with_the_marker_once()
    {
        var key = await ProtectedOnBothAsync(laptopHasIt: true);
        _laptop.Use();
        var secret = _laptop.PathOf("Titel.txts");
        NoteContent.Unlock(secret, Password);
        NoteContent.Unprotect(secret);
        await _laptop.SyncAsync();

        var marked = Assert.Single(_cloud.Pushed, item => item.Key == key && item.Data?.Contains("\"unprotected\":true", StringComparison.Ordinal) == true);
        using (var data = JsonDocument.Parse(marked.Data!))
        {
            Assert.Equal("Titel.md", data.RootElement.GetProperty("path").GetString());
            Assert.Equal(SecretText, data.RootElement.GetProperty("content").GetString());
            Assert.False(data.RootElement.TryGetProperty("bytes", out _));
        }

        var item = Assert.Single(_cloud.Notes);
        Assert.Contains("\"path\":\"Titel.md\"", item.Data, StringComparison.Ordinal);

        // Anna's computer takes it: a removal on purpose is a change like any other.
        await _anna.SyncAsync();
        Assert.Equal(SecretText, _anna.Read("Titel.md"));
        Assert.False(File.Exists(_anna.PathOf("Titel.txts")));

        // The marker went with that one push only.
        _laptop.Use();
        NoteContent.Write(_laptop.PathOf("Titel.md"), "nicht mehr geheim");
        await _laptop.SyncAsync();
        Assert.Single(_cloud.Pushed, pushed => pushed.Data?.Contains("unprotected", StringComparison.Ordinal) == true);
        await _anna.SyncAsync();
        Assert.Equal("nicht mehr geheim", _anna.Read("Titel.md"));
    }

    [Fact]
    public async Task A_note_protected_and_unprotected_before_the_cloud_had_it_protected_needs_no_marker()
    {
        _anna.Use();
        var plain = NoteLibrary.CreateNote(_anna.Notes, "Titel", SecretText);
        await _anna.ConnectAsync();
        await _anna.SyncAsync();

        _anna.Use();
        var secret = NoteContent.Protect(plain, SecretText, Password);
        NoteContent.Unprotect(secret);
        await _anna.SyncAsync();

        Assert.DoesNotContain(_cloud.Pushed, item => item.Data?.Contains("unprotected", StringComparison.Ordinal) == true);
        Assert.Contains("\"path\":\"Titel.md\"", Assert.Single(_cloud.Notes).Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_edit_of_the_plain_version_is_refused_by_the_cloud_and_kept_as_a_conflict_copy()
    {
        var key = await ProtectedOnBothAsync(laptopHasIt: false);
        var encrypted = File.ReadAllBytes(_anna.PathOf("Titel.txts"));

        await StaleEditOnLaptopAsync("Alter Text, auf dem Laptop ergänzt");

        // The cloud kept the protected note; the laptop took it and kept its edit next to it.
        AssertProtected(_cloud.Items[("notes", key)], encrypted);
        Assert.Equal(encrypted, File.ReadAllBytes(_laptop.PathOf("Titel.txts")));
        Assert.False(File.Exists(_laptop.PathOf("Titel.md")));
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _laptop.Read("Titel (Konflikt LAPTOP).md"));

        await _anna.SyncAsync();
        Assert.Equal(encrypted, File.ReadAllBytes(_anna.PathOf("Titel.txts")));
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _anna.Read("Titel (Konflikt LAPTOP).md"));
        Assert.Equal(2, _cloud.Notes.Count);
        await AssertSettledAsync();
    }

    [Fact]
    public async Task A_removal_of_the_protection_the_cloud_refuses_ends_as_a_conflict_copy_and_is_not_pushed_again_and_again()
    {
        // The protection removed without the marker (as an older ThixNote does it): the cloud refuses the plain version and answers
        // with the protected note – the very state this computer synced last.
        var key = await ProtectedOnBothAsync(laptopHasIt: true);
        var encrypted = File.ReadAllBytes(_laptop.PathOf("Titel.txts"));
        _laptop.Use();
        var secret = _laptop.PathOf("Titel.txts");
        var plain = _laptop.PathOf("Titel.md");
        File.WriteAllText(plain, SecretText);
        File.Delete(secret);
        SyncService.NoteMoved(secret, plain);
        await _laptop.SyncAsync();

        // The protected note is back, the plain text is kept next to it – nothing waits to be refused again.
        AssertProtected(_cloud.Items[("notes", key)], encrypted);
        Assert.Equal(encrypted, File.ReadAllBytes(_laptop.PathOf("Titel.txts")));
        Assert.False(File.Exists(plain));
        Assert.Equal(SecretText, _laptop.Read("Titel (Konflikt LAPTOP).md"));

        var pushes = _cloud.Pushed.Count(item => item.Key == key);
        await _laptop.SyncAsync();
        await _laptop.SyncAsync();
        Assert.Equal(pushes, _cloud.Pushed.Count(item => item.Key == key));
        await AssertSettledAsync();
        Assert.Equal(2, _cloud.Notes.Count);
    }

    [Fact]
    public async Task A_plain_version_without_the_marker_never_turns_a_protected_file_into_plain_text()
    {
        // An older cloud took the laptop's stale edit of the plain version over the protected note (the last writer wins) …
        _cloud.GuardsProtectedNotes = false;
        var key = await ProtectedOnBothAsync(laptopHasIt: false);
        var encrypted = File.ReadAllBytes(_anna.PathOf("Titel.txts"));
        await StaleEditOnLaptopAsync("Alter Text, auf dem Laptop ergänzt");
        Assert.Contains("\"content\"", _cloud.Items[("notes", key)].Data, StringComparison.Ordinal);

        // … Anna's computer keeps the protected file protected as a note of its own and does not push it back over the plain
        // version (it cannot tell it from a removal of the protection on purpose); the plain version takes the note.
        await _anna.SyncAsync();
        Assert.Equal(encrypted, File.ReadAllBytes(_anna.PathOf(ProtectedCopy)));
        Assert.False(File.Exists(_anna.PathOf("Titel.txts")));
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _anna.Read("Titel.md"));
        Assert.Contains("\"content\"", _cloud.Items[("notes", key)].Data, StringComparison.Ordinal);
        AssertProtected(Assert.Single(_cloud.Notes, item => item.Key != key), encrypted, ProtectedCopy);

        // The laptop gets the protected note as it was; nothing is lost and nothing protected was written as plain text.
        await _laptop.SyncAsync();
        Assert.Equal(encrypted, File.ReadAllBytes(_laptop.PathOf(ProtectedCopy)));
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _laptop.Read("Titel.md"));
        _laptop.Use();
        Assert.Equal(SecretText, NoteContent.Unlock(_laptop.PathOf(ProtectedCopy), Password));
        NoteContent.Lock(_laptop.PathOf(ProtectedCopy));

        Assert.Equal(2, _cloud.Notes.Count);
        await AssertSettledAsync();
    }

    [Fact]
    public async Task A_protected_note_changed_here_too_stays_protected_with_its_change()
    {
        _cloud.GuardsProtectedNotes = false;
        var key = await ProtectedOnBothAsync(laptopHasIt: false);

        // Anna changes the protected note and has not synced yet when the laptop's stale plain edit wins on the older cloud.
        _anna.Use();
        var secret = _anna.PathOf("Titel.txts");
        NoteContent.Unlock(secret, Password);
        NoteContent.Write(secret, "Neuer geheimer Text");
        NoteContent.Lock(secret);
        await StaleEditOnLaptopAsync("Alter Text, auf dem Laptop ergänzt");

        await _anna.SyncAsync();
        _anna.Use();
        var copy = _anna.PathOf(ProtectedCopy);
        Assert.False(File.Exists(secret));
        Assert.Equal("Neuer geheimer Text", NoteContent.Unlock(copy, Password));
        NoteContent.Lock(copy);
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _anna.Read("Titel.md"));
        AssertProtected(Assert.Single(_cloud.Notes, item => item.Key != key), File.ReadAllBytes(copy), ProtectedCopy);
        Assert.DoesNotContain(_cloud.Pushed, item => item.Data?.Contains("Neuer geheimer", StringComparison.Ordinal) == true);

        await _laptop.SyncAsync();
        Assert.Equal(File.ReadAllBytes(copy), File.ReadAllBytes(_laptop.PathOf(ProtectedCopy)));
        Assert.Equal("Alter Text, auf dem Laptop ergänzt", _laptop.Read("Titel.md"));
        await AssertSettledAsync();
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("rename")]
    [InlineData("browser")]
    public async Task A_removal_of_the_protection_that_a_computer_missed_is_not_undone(string after)
    {
        var key = await ProtectedOnBothAsync(laptopHasIt: true);
        var encrypted = File.ReadAllBytes(_anna.PathOf("Titel.txts"));
        _laptop.Use();
        var secret = _laptop.PathOf("Titel.txts");
        NoteContent.Unlock(secret, Password);
        NoteContent.Unprotect(secret);
        await _laptop.SyncAsync();

        // Anna's computer is away while the note changes once more: the cloud keeps only that latest state, without the marker.
        var (path, text) = after switch
        {
            "edit" => ("Titel.md", "nicht mehr geheim"),
            "rename" => ("Notiz.md", SecretText),
            _ => ("Titel.md", "im Browser geändert"),
        };
        _laptop.Use();
        switch (after)
        {
            case "edit":
                NoteContent.Write(_laptop.PathOf("Titel.md"), text);
                break;
            case "rename":
                NoteLibrary.Rename(_laptop.PathOf("Titel.md"), "Notiz");
                break;
            default:
                _cloud.SaveInBrowser(key, path, text);
                break;
        }

        await _laptop.SyncAsync();
        Assert.DoesNotContain("unprotected", _cloud.Items[("notes", key)].Data, StringComparison.Ordinal);

        // Anna's computer takes the plain note; its protected file is not pushed back, it stays protected as a note of its own.
        await _anna.SyncAsync();
        Assert.Equal(text, _anna.Read(path));
        Assert.False(File.Exists(_anna.PathOf("Titel.txts")));
        Assert.Equal(encrypted, File.ReadAllBytes(_anna.PathOf(ProtectedCopy)));
        using (var data = JsonDocument.Parse(_cloud.Items[("notes", key)].Data!))
        {
            Assert.Equal(path, data.RootElement.GetProperty("path").GetString());
            Assert.Equal(text, data.RootElement.GetProperty("content").GetString());
        }

        // The laptop keeps the plain note as the person left it.
        await _laptop.SyncAsync();
        Assert.Equal(text, _laptop.Read(path));
        Assert.False(File.Exists(_laptop.PathOf("Titel.txts")));
        Assert.Equal(encrypted, File.ReadAllBytes(_laptop.PathOf(ProtectedCopy)));
        Assert.Equal(2, _cloud.Notes.Count);
        await AssertSettledAsync();
    }

    [Fact]
    public async Task Every_computer_that_held_the_protected_file_keeps_the_same_one_note_of_it()
    {
        _cloud.GuardsProtectedNotes = false;
        var office = new Device(Path.Combine(_data.Folder, "buero"), "BUERO");
        await ProtectedOnBothAsync(laptopHasIt: false);
        var encrypted = File.ReadAllBytes(_anna.PathOf("Titel.txts"));
        await office.ConnectAsync();
        await office.SyncAsync();
        Assert.Equal(encrypted, File.ReadAllBytes(office.PathOf("Titel.txts")));

        await StaleEditOnLaptopAsync("Alter Text, auf dem Laptop ergänzt");
        var stale = _cloud.Revision;

        // Anna's computer and the office take the plain version at the same time: the office pushes before it sees Anna's copy.
        await _anna.SyncAsync();
        _cloud.HideChangesAbove = stale;
        try
        {
            await office.SyncAsync();
        }
        finally
        {
            _cloud.HideChangesAbove = null;
        }

        var devices = new[] { _anna, _laptop, office };
        for (var round = 0; round < 2; round++)
        {
            foreach (var device in devices)
            {
                await device.SyncAsync();
            }
        }

        var revision = _cloud.Revision;
        foreach (var device in devices)
        {
            await device.SyncAsync();
        }

        Assert.Equal(revision, _cloud.Revision);
        Assert.Equal(2, _cloud.Notes.Count);
        foreach (var device in devices)
        {
            Assert.Equal("Alter Text, auf dem Laptop ergänzt", device.Read("Titel.md"));
            Assert.Equal(encrypted, File.ReadAllBytes(Assert.Single(Directory.GetFiles(device.Notes, "*.txts"))));
            Assert.Equal(2, Directory.GetFiles(device.Notes).Length);
        }
    }

    /// <summary>
    /// "Titel.md" on both computers, then protected on Anna's computer and synced. <paramref name="laptopHasIt"/>: the laptop
    /// synced since (it has the .txts); otherwise it still has the plain "Titel.md". Returns the key of the note.
    /// </summary>
    private async Task<string> ProtectedOnBothAsync(bool laptopHasIt)
    {
        _anna.Use();
        var plain = NoteLibrary.CreateNote(_anna.Notes, "Titel", SecretText);
        await _anna.ConnectAsync();
        await _anna.SyncAsync();
        await _laptop.ConnectAsync();
        await _laptop.SyncAsync();
        var key = Assert.Single(_cloud.Notes).Key;

        _anna.Use();
        _revisionBeforeProtection = _cloud.Revision;
        var secret = NoteContent.Protect(plain, SecretText, Password);
        NoteContent.Lock(secret);
        await _anna.SyncAsync();
        AssertProtected(_cloud.Items[("notes", key)], File.ReadAllBytes(secret));

        if (laptopHasIt)
        {
            await _laptop.SyncAsync();
            Assert.Equal(File.ReadAllBytes(secret), File.ReadAllBytes(_laptop.PathOf("Titel.txts")));
        }

        return key;
    }

    /// <summary>
    /// The laptop edits its old plain "Titel.md" (its clock a minute ahead) and pushes it without having seen the protection:
    /// the protection reached the cloud between its pull and its push.
    /// </summary>
    private async Task StaleEditOnLaptopAsync(string text)
    {
        _laptop.Use();
        var path = _laptop.PathOf("Titel.md");
        NoteContent.Write(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        _cloud.HideChangesAbove = _revisionBeforeProtection;
        try
        {
            await _laptop.SyncAsync();
        }
        finally
        {
            _cloud.HideChangesAbove = null;
        }
    }

    private static void AssertProtected(SyncItemDto item, byte[] encrypted, string path = "Titel.txts")
    {
        Assert.False(item.IsDeleted);
        using var data = JsonDocument.Parse(item.Data!);
        Assert.Equal(path, data.RootElement.GetProperty("path").GetString());
        Assert.Equal(encrypted, Convert.FromBase64String(data.RootElement.GetProperty("bytes").GetString()!));
        Assert.False(data.RootElement.TryGetProperty("content", out _));
    }

    /// <summary>Nothing goes up again by itself: no computer keeps pushing the note back and forth.</summary>
    private async Task AssertSettledAsync()
    {
        await _anna.SyncAsync();
        await _laptop.SyncAsync();
        var revision = _cloud.Revision;
        await _anna.SyncAsync();
        await _laptop.SyncAsync();
        Assert.Equal(revision, _cloud.Revision);
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
