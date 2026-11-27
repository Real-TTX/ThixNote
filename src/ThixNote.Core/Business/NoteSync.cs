using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MatCloud.Contract;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// A note the sync changed on this computer (full paths): written anew (<see cref="ContentChanged"/>), moved or renamed
/// (<see cref="OldPath"/> to <see cref="NewPath"/>), new (no old path) or gone into the trash (no new path). A folder that
/// came or went is a change without content. Open tabs follow it (<see cref="SyncService.NotesChanged"/>).
/// </summary>
public sealed record NoteChange(string? OldPath, string? NewPath, bool ContentChanged);

/// <summary>
/// The notes folder and MatCloud's general sync (<see cref="SyncContract"/>): what goes up, and what comes down.
///
/// Every note file is one item of the collection "notes" whose key is a GUID kept in the sync index (<see cref="SyncIndex"/>);
/// its data is JSON: <c>{"path":"Ordner/Titel.md","content":"…"}</c> for .md and .txt, and for .txts (password-protected)
/// <c>{"path":"…","bytes":"&lt;base64&gt;"}</c> – the encrypted file as it is, so the cloud never sees the text of a protected note.
/// The content hash is the SHA-256 (hex) of the UTF-8 text, or of the encrypted bytes. Every folder is an item of "folders"
/// (key = its path), so empty folders travel too.
///
/// Up (<see cref="PendingChanges"/>): the folder is compared with the index – a new file gets a new key, a changed one goes up
/// with the time of its last write, a renamed or moved one keeps its key (ThixNote tells it with <see cref="Moved"/>, a move in
/// Explorer / Finder is found by its content), a missing one goes up as a tombstone.
/// Down (<see cref="ApplyRemote"/>): a change of the cloud is written into the folder; a tombstone moves the file into the
/// trash. When a note changed here since the last sync AND in the cloud, both stay: the cloud's version takes the name, this
/// computer's is saved next to it as "Titel (Konflikt Gerätename).md" and goes up as a new note. A note that is open with
/// unsaved changes is never touched: its change waits in the index until the editor has saved.
///
/// A protected note never turns into plain text by the way: only "Schutz aufheben" here (<see cref="Moved"/> with
/// unprotected) sends its plain version with <c>"unprotected":true</c>, once (MatCloud refuses a plain version of a protected
/// note without it and answers with the protected one, and keeps the marker on the later plain states of the note until it is
/// protected again or deleted – so a computer that was away still sees that the removal was on purpose and applies it like any
/// change). A plain version without the marker over a note that is protected here is either a stale version of a device that
/// missed the protection (on an older cloud) or, on a cloud that keeps the marker with the one push only, a later state of a
/// note whose protection was removed on purpose on another computer while this one was away. The protected file is then never
/// written as plain text and never pushed back over the plain version: it keeps its encrypted bytes as
/// "Titel (Konflikt Gerät).txts", a note of its own (the same key on every computer), and the plain version takes the note's
/// key and name.
/// Everything runs under <see cref="Gate"/>; the network calls are SyncService's. Nothing here logs a title or a text.
/// </summary>
internal static class NoteSync
{
    public const string NotesCollection = "notes";
    public const string FoldersCollection = "folders";

    /// <summary>The characters of data one push carries at most (the cloud takes 32 MB per push; JSON escapes some characters).</summary>
    private const long MaxBatchChars = 16_000_000;

    public static readonly object Gate = new();

    private static readonly HashSet<string> WarnedTooLarge = [];
    private static SyncIndex? _index;
    private static string? _indexFile;

    /// <summary>Relative paths compare like the file system (case-insensitive on Windows and macOS).</summary>
    private static StringComparer PathComparer => EncryptionSession.PathComparer;

    /// <summary>The highest revision of the cloud applied here.</summary>
    public static long LastRevision(string account)
    {
        lock (Gate)
        {
            return Index(account).LastRevision;
        }
    }

    /// <summary>Drops what is kept in memory (tests switch between data folders; another notes folder).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _index = null;
            _indexFile = null;
        }
    }

    // ---- up ----

    /// <summary>What is new, changed, moved or gone here since the last sync: at most <paramref name="maxItems"/> items (notes before folders).</summary>
    public static List<PendingItem> PendingChanges(string account, int maxItems)
    {
        lock (Gate)
        {
            var index = Index(account);
            var root = NotesRoot;
            var result = new List<PendingItem>();
            if (!Directory.Exists(root))
            {
                return result; // a missing notes folder (an unplugged drive) must not delete every note in the cloud
            }

            var files = ListFiles(root);
            var held = index.Deferred.Where(item => item.Collection == NotesCollection).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            var claimed = new HashSet<string>(PathComparer);
            var missing = new List<(string Key, SyncedNote Note)>();
            var changed = false;
            long chars = 0;

            // Every file is looked at (so that each one is claimed by its note), but only as much as one push carries is read.
            bool IsFull() => result.Count >= maxItems || chars >= MaxBatchChars;

            void Add(PendingItem? item)
            {
                var length = item?.Item.Data?.Length ?? 0;
                if (item is null || IsFull() || (result.Count > 0 && chars + length > MaxBatchChars))
                {
                    return;
                }

                result.Add(item);
                chars += length;
            }

            foreach (var (key, note) in index.Notes)
            {
                if (!files.TryGetValue(note.Here, out var file))
                {
                    missing.Add((key, note));
                    continue;
                }

                claimed.Add(file.Relative);
                if (held.Contains(key) || IsFull() || !TrySeenHash(note, file, out var hash, ref changed))
                {
                    continue; // a change of the cloud waits for this note, the push is full, or the file is being written
                }

                if (string.Equals(file.Relative, note.Path, StringComparison.Ordinal) && hash == note.Hash)
                {
                    continue;
                }

                var contentChanged = hash != note.Hash;
                Add(Upsert(key, file, contentChanged ? Later(file.WriteTimeUtc, note.UpdatedAt) : Later(AppClock.UtcNow, note.UpdatedAt), Unprotects(note, file.Relative)));
            }

            var fresh = files.Values.Where(file => !claimed.Contains(file.Relative)).ToList();
            var freshHashes = new Dictionary<string, string?>(PathComparer);
            foreach (var (key, note) in missing)
            {
                if (held.Contains(key))
                {
                    continue;
                }

                // Moved or renamed outside ThixNote (Explorer, Finder): a new file of the same kind with the same content keeps the key.
                var match = fresh.FirstOrDefault(file => IsEncrypted(file.Relative) == IsEncrypted(note.Here) && HashOrNull(file, freshHashes) is { } hash
                                                         && hash == (note.Path.Length == 0 ? note.SeenHash : note.Hash));
                if (match is not null)
                {
                    fresh.Remove(match);
                    note.LocalPath = string.Equals(match.Relative, note.Path, StringComparison.Ordinal) ? null : match.Relative;
                    note.SeenLength = -1;
                    changed = true;
                    if (!IsFull())
                    {
                        Add(Upsert(key, match, Later(AppClock.UtcNow, note.UpdatedAt)));
                    }
                }
                else if (note.Path.Length == 0)
                {
                    index.Notes.Remove(key); // new here and gone again before it reached the cloud
                    changed = true;
                }
                else
                {
                    Add(new PendingItem(new SyncItemDto { Collection = NotesCollection, Key = key, IsDeleted = true, UpdatedAt = Later(AppClock.UtcNow, note.UpdatedAt) }));
                }
            }

            // A new note gets its key at once and keeps it until the cloud has confirmed it: a push whose answer got lost
            // is sent again under the same key and never makes a second note.
            foreach (var file in fresh)
            {
                var key = Guid.NewGuid().ToString("N");
                index.Notes[key] = new SyncedNote { LocalPath = file.Relative };
                changed = true;
                if (!IsFull())
                {
                    Add(Upsert(key, file, new DateTimeOffset(file.WriteTimeUtc, TimeSpan.Zero)));
                }
            }

            // The folders, after the notes: a folder that went is empty by then on the other computers.
            var folders = ListFolders(root);
            var synced = new HashSet<string>(index.Folders, PathComparer);
            foreach (var folder in folders.Where(folder => !synced.Contains(folder)))
            {
                Add(new PendingItem(new SyncItemDto { Collection = FoldersCollection, Key = folder, Data = "{}", UpdatedAt = AppClock.UtcNow }));
            }

            foreach (var folder in index.Folders.Where(folder => !folders.Contains(folder)))
            {
                Add(new PendingItem(new SyncItemDto { Collection = FoldersCollection, Key = folder, IsDeleted = true, UpdatedAt = AppClock.UtcNow }));
            }

            if (changed)
            {
                Save(index);
            }

            return result;
        }
    }

    /// <summary>Takes the answers of a push: what the cloud applied is the synced state now; where it had something newer, that comes down.</summary>
    public static List<NoteChange> ConfirmPushed(string account, IReadOnlyList<PendingItem> sent, SyncPushResponse response)
    {
        lock (Gate)
        {
            var index = Index(account);
            var changes = new List<NoteChange>();
            foreach (var result in response.Results)
            {
                var pushed = sent.FirstOrDefault(item => item.Item.Collection == result.Collection && item.Item.Key == result.Key);
                if (pushed is null)
                {
                    continue;
                }

                if (!result.Applied)
                {
                    if (result.Current is { } current)
                    {
                        // Not older than the cloud's state and still refused: the cloud keeps it as it is (a protected note stays protected).
                        // Even when that state is the one this computer synced last, the change here must not wait to be pushed again
                        // forever: it goes next to the note as a conflict copy.
                        Apply(index, current, changes, kept: current.UpdatedAt <= pushed.Item.UpdatedAt);
                    }

                    continue;
                }

                Confirm(index, pushed);
            }

            Save(index);
            return changes;
        }
    }

    /// <summary>The cloud refused an item (too large, not valid): it stays on this computer only, until it changes again.</summary>
    public static void KeepLocalOnly(string account, PendingItem item)
    {
        lock (Gate)
        {
            var index = Index(account);
            Confirm(index, item);
            Save(index);
        }
    }

    // ---- down ----

    /// <summary>Applies the changes of the cloud (in revision order) and remembers <paramref name="latest"/> as seen.</summary>
    public static List<NoteChange> ApplyRemote(string account, IReadOnlyList<SyncItemDto> items, long latest)
    {
        lock (Gate)
        {
            var index = Index(account);
            var changes = new List<NoteChange>();
            foreach (var item in items)
            {
                index.Deferred.RemoveAll(waiting => waiting.Collection == item.Collection && waiting.Key == item.Key);
                Apply(index, item, changes);
            }

            index.LastRevision = Math.Max(index.LastRevision, latest);
            Save(index);
            return changes;
        }
    }

    /// <summary>The changes that waited for an editor: applied now where the note has been saved (or closed).</summary>
    public static List<NoteChange> RetryDeferred(string account)
    {
        lock (Gate)
        {
            var index = Index(account);
            var changes = new List<NoteChange>();
            if (index.Deferred.Count == 0)
            {
                return changes;
            }

            var waiting = index.Deferred.ToList();
            index.Deferred.Clear();
            foreach (var item in waiting)
            {
                Apply(index, item, changes);
            }

            Save(index);
            return changes;
        }
    }

    // ---- what ThixNote tells ----

    /// <summary>
    /// ThixNote moved a note (rename, protect, unprotect): it keeps its key in the cloud. <paramref name="unprotected"/>: the
    /// person removed the protection ("Schutz aufheben") – when the cloud has the note protected, its next push says so.
    /// </summary>
    public static void Moved(string account, string oldPath, string newPath, bool unprotected = false)
    {
        lock (Gate)
        {
            var index = Index(account);
            var root = NotesRoot;
            if (RelativeInside(root, oldPath) is not { } oldRelative || RelativeInside(root, newPath) is not { } newRelative)
            {
                return;
            }

            var note = index.Notes.Values.FirstOrDefault(entry => PathComparer.Equals(entry.Here, oldRelative));
            if (note is null)
            {
                return;
            }

            note.LocalPath = string.Equals(newRelative, note.Path, StringComparison.Ordinal) ? null : newRelative;
            note.SeenLength = -1;
            if (IsEncrypted(newRelative))
            {
                note.Unprotected = false; // protected again before the removal went up
            }
            else if (unprotected && IsEncrypted(note.Path))
            {
                note.Unprotected = true;
            }

            Save(index);
        }
    }

    // ---- the parts ----

    /// <summary>The index of this account and notes folder (a new account or another notes folder starts afresh).</summary>
    private static SyncIndex Index(string account)
    {
        var file = AppPaths.SyncIndexFile;
        if (_index is null || _indexFile != file)
        {
            _index = SyncIndex.Load(file);
            _indexFile = file;
        }

        var root = NotesRoot;
        if (_index.Account != account || _index.NotesFolder.Length == 0 || !NoteLibrary.IsSamePath(_index.NotesFolder, root))
        {
            if (_index.Account.Length > 0)
            {
                Log.Info("Sync: another account or notes folder – the notes are compared with the cloud afresh");
            }

            _index = new SyncIndex { Account = account, NotesFolder = root };
        }

        return _index;
    }

    private static string NotesRoot => Path.GetFullPath(SettingsService.NotesFolder);

    private static void Save(SyncIndex index)
    {
        try
        {
            index.Save(AppPaths.SyncIndexFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Sync: the index could not be saved ({exception.GetType().Name})");
        }
    }

    private static void Confirm(SyncIndex index, PendingItem pushed)
    {
        var item = pushed.Item;
        if (item.Collection == FoldersCollection)
        {
            index.Folders.RemoveAll(folder => PathComparer.Equals(folder, item.Key));
            if (!item.IsDeleted)
            {
                index.Folders.Add(item.Key);
            }

            return;
        }

        if (item.IsDeleted)
        {
            index.Notes.Remove(item.Key);
            return;
        }

        if (!index.Notes.TryGetValue(item.Key, out var note))
        {
            note = new SyncedNote();
            index.Notes[item.Key] = note;
        }

        if (pushed.Unprotected)
        {
            note.Unprotected = false; // the cloud has the plain note now: the marker goes with this one push only
        }

        var here = note.LocalPath;
        note.Path = pushed.Here!;
        note.LocalPath = here is null || string.Equals(here, pushed.Here, StringComparison.Ordinal) ? null : here; // moved again meanwhile: that goes up next
        note.Hash = pushed.Hash!;
        note.UpdatedAt = item.UpdatedAt;
        if (note.LocalPath is null)
        {
            note.SeenLength = pushed.Length;
            note.SeenWriteTimeUtc = pushed.WriteTimeUtc;
            note.SeenHash = pushed.Hash!;
        }
    }

    /// <summary>
    /// One change of the cloud. A failure leaves it waiting in the index (tried again every round). <paramref name="kept"/>: the
    /// cloud refused this computer's push and kept the item as it is (see <see cref="ApplyNote"/>).
    /// </summary>
    private static void Apply(SyncIndex index, SyncItemDto item, List<NoteChange> changes, bool kept = false)
    {
        try
        {
            var applied = item.Collection switch
            {
                NotesCollection => ApplyNote(index, item, changes, kept),
                FoldersCollection => ApplyFolder(index, item, changes),
                _ => true, // another kind of item of ThixNote (a later version): not this version's business
            };
            if (!applied)
            {
                index.Deferred.Add(item);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Sync: item {item.Key} could not be applied yet ({exception.GetType().Name})");
            index.Deferred.Add(item);
        }
    }

    /// <summary>
    /// Applies one note; false when it has to wait (open with unsaved changes). <paramref name="kept"/>: the cloud refused this
    /// computer's push of the note and kept its state – even when that is the state synced here last, a change here is then a
    /// conflict (it would otherwise go up again and be refused again every round).
    /// </summary>
    private static bool ApplyNote(SyncIndex index, SyncItemDto item, List<NoteChange> changes, bool kept = false)
    {
        var root = NotesRoot;
        index.Notes.TryGetValue(item.Key, out var note);
        NoteData? data = null;
        string? target = null;
        string? remoteHash = null;
        if (!item.IsDeleted)
        {
            data = ParseData(item);
            target = data is null ? null : CleanNotePath(data.Path);
            if (data is null || target is null)
            {
                Log.Warning($"Sync: item {item.Key} is no note this version understands; skipped");
                return true;
            }

            remoteHash = HashOf(data);
            if (!kept && note is not null && string.Equals(note.Path, target, StringComparison.Ordinal) && note.Hash == remoteHash)
            {
                note.UpdatedAt = Max(note.UpdatedAt, item.UpdatedAt); // what this computer pushed itself, or the same again
                return true;
            }
        }

        if (note is null)
        {
            return item.IsDeleted || ApplyNewNote(index, item, data!, target!, remoteHash!, changes);
        }

        var here = note.Here;
        var hereFull = Full(root, here);
        var exists = File.Exists(hereFull);
        if (exists && SyncService.HasUnsavedChanges(hereFull))
        {
            return false;
        }

        var localHash = exists ? HashFile(hereFull) : null;
        var changedHere = exists && localHash != note.Hash;

        if (item.IsDeleted)
        {
            // Changed here, deleted there: it stays and goes up again as a new note. Otherwise it goes into the trash.
            if (exists && !changedHere)
            {
                NoteLibrary.MoveToTrash(hereFull);
                changes.Add(new NoteChange(hereFull, null, false));
            }

            index.Notes.Remove(item.Key);
            return true;
        }

        if (exists && IsUnmarkedUnprotect(here, data!))
        {
            KeepProtectedApart(index, item, data!, target!, remoteHash!, note, here, localHash!, changes);
            return true;
        }

        if (!exists)
        {
            // Deleted here, changed there: the change is newer than the deletion as far as anyone knows – the note comes back.
            var restored = FreePath(root, target!, null);
            Write(root, restored, data!, item.UpdatedAt, note, remoteHash!);
            SetSynced(note, target!, restored, remoteHash!, item.UpdatedAt);
            changes.Add(new NoteChange(null, Full(root, restored), true));
            return true;
        }

        if (changedHere && localHash != remoteHash)
        {
            // Changed here and there: the cloud's version takes the name, this one goes next to it as a new note.
            var copy = ConflictCopy(root, here);
            MoveFile(root, here, copy);
            changes.Add(new NoteChange(hereFull, Full(root, copy), false));
            var place = FreePath(root, target!, null);
            Write(root, place, data!, item.UpdatedAt, note, remoteHash!);
            SetSynced(note, target!, place, remoteHash!, item.UpdatedAt);
            changes.Add(new NoteChange(null, Full(root, place), true));
            return true;
        }

        // Not changed here (or changed to the same): the cloud's content; a move here that the cloud does not know yet stays.
        var movedHere = !string.Equals(here, note.Path, StringComparison.Ordinal);
        var wanted = movedHere && string.Equals(target, note.Path, StringComparison.Ordinal) ? here : target!;
        var destination = here;
        if (!string.Equals(wanted, here, StringComparison.Ordinal))
        {
            destination = FreePath(root, wanted, here);
            MoveFile(root, here, destination);
        }

        var contentChanged = localHash != remoteHash;
        if (contentChanged)
        {
            Write(root, destination, data!, item.UpdatedAt, note, remoteHash!);
        }

        SetSynced(note, target!, destination, remoteHash!, item.UpdatedAt);
        if (contentChanged || !string.Equals(destination, here, StringComparison.Ordinal))
        {
            changes.Add(new NoteChange(hereFull, Full(root, destination), contentChanged));
        }

        return true;
    }

    /// <summary>A note this computer does not know yet. The same note already here (first sync) is taken as it is.</summary>
    private static bool ApplyNewNote(SyncIndex index, SyncItemDto item, NoteData data, string target, string remoteHash, List<NoteChange> changes)
    {
        var root = NotesRoot;
        var targetFull = Full(root, target);
        var holder = index.Notes.FirstOrDefault(pair => PathComparer.Equals(pair.Value.Here, target));
        if (File.Exists(targetFull) && (holder.Value is null || holder.Value.Path.Length == 0))
        {
            // A note the cloud does not know yet has this name (both computers had it before their first sync).
            if (SyncService.HasUnsavedChanges(targetFull))
            {
                return false;
            }

            var actual = RelativeInside(root, ActualPath(targetFull)) ?? target;
            if (HashFile(targetFull) == remoteHash)
            {
                // The same note: it takes the cloud's key instead of going up a second time.
                if (holder.Key is not null)
                {
                    index.Notes.Remove(holder.Key);
                }

                var adopted = new SyncedNote();
                SetSynced(adopted, target, actual, remoteHash, item.UpdatedAt);
                index.Notes[item.Key] = adopted;
                return true;
            }

            var copy = ConflictCopy(root, actual);
            MoveFile(root, actual, copy);
            if (holder.Value is not null)
            {
                holder.Value.LocalPath = copy;
            }

            changes.Add(new NoteChange(Full(root, actual), Full(root, copy), false));
        }

        var place = FreePath(root, target, null);
        var note = new SyncedNote();
        Write(root, place, data, item.UpdatedAt, note, remoteHash);
        SetSynced(note, target, place, remoteHash, item.UpdatedAt);
        index.Notes[item.Key] = note;
        changes.Add(new NoteChange(null, Full(root, place), true));
        return true;
    }

    /// <summary>
    /// A plain version of a note that is protected here (a .txts) and does not say it was unprotected on purpose. This computer
    /// cannot tell which of two it is: a stale plain version of a device that missed the protection (an older cloud took it), or
    /// the person's own "Schutz aufheben" on another computer followed by a later edit, rename or save in the browser on a cloud
    /// that keeps the marker with the one push only (MatCloud keeps it on the later plain states: then this is not the case).
    /// </summary>
    private static bool IsUnmarkedUnprotect(string here, NoteData data) =>
        data.Bytes is null && data.Unprotected != true && IsEncrypted(here);

    /// <summary>
    /// The protected file is never turned into plain text and never pushed back over the plain version (that would undo a removal
    /// of the protection on purpose): it keeps its encrypted bytes and goes next to the note as "Titel (Konflikt Gerät).txts", a
    /// note of its own. The plain version takes the key and the name like any change of the cloud. The copy's key follows from
    /// the note's key and the encrypted bytes, so every computer that held the same protected file makes the same one note of it.
    /// </summary>
    private static void KeepProtectedApart(SyncIndex index, SyncItemDto item, NoteData data, string target, string remoteHash, SyncedNote note, string here, string localHash, List<NoteChange> changes)
    {
        var root = NotesRoot;
        var copy = ConflictCopy(root, here);
        MoveFile(root, here, copy);
        var copyKey = CopyKey(item.Key, localHash);
        index.Notes[index.Notes.ContainsKey(copyKey) ? Guid.NewGuid().ToString("N") : copyKey] = new SyncedNote { LocalPath = copy };
        changes.Add(new NoteChange(Full(root, here), Full(root, copy), false));

        var place = FreePath(root, target, null);
        Write(root, place, data, item.UpdatedAt, note, remoteHash);
        SetSynced(note, target, place, remoteHash, item.UpdatedAt);
        changes.Add(new NoteChange(null, Full(root, place), true));
        Log.Info($"Sync: item {item.Key} came as plain text without the marker; the protected file is kept protected as a note of its own");
    }

    /// <summary>The key of the protected copy of a note: the same on every computer for the same note and the same encrypted bytes.</summary>
    private static string CopyKey(string key, string hash) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"protected-copy|{key}|{hash}")).AsSpan(0, 16)).ToString("N");

    private static bool ApplyFolder(SyncIndex index, SyncItemDto item, List<NoteChange> changes)
    {
        var root = NotesRoot;
        var folder = CleanFolderPath(item.Key);
        if (folder is null)
        {
            return true;
        }

        var full = Full(root, folder);
        if (item.IsDeleted)
        {
            // Gone elsewhere: it goes here too when nothing but what the system leaves (.DS_Store, Thumbs.db) is in it. A
            // folder that still holds something stays here, and as known, so it does not go up again as a new one.
            if (!Directory.Exists(full))
            {
                index.Folders.RemoveAll(known => PathComparer.Equals(known, folder));
            }
            else if (IsEmptyFolder(full))
            {
                Directory.Delete(full, recursive: true);
                index.Folders.RemoveAll(known => PathComparer.Equals(known, folder));
                changes.Add(new NoteChange(full, null, false));
            }

            return true;
        }

        if (!Directory.Exists(full))
        {
            Directory.CreateDirectory(full);
            changes.Add(new NoteChange(null, full, false));
        }

        if (!index.Folders.Contains(folder, PathComparer))
        {
            index.Folders.Add(folder);
        }

        return true;
    }

    /// <summary>No note, no folder, nothing of the person's in it: only files the system leaves behind.</summary>
    private static bool IsEmptyFolder(string full) =>
        !Directory.EnumerateFileSystemEntries(full).Any(entry =>
            Directory.Exists(entry) || !(Path.GetFileName(entry).StartsWith('.')
                                         || Path.GetFileName(entry).Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                                         || Path.GetFileName(entry).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)));

    private static void SetSynced(SyncedNote note, string path, string here, string hash, DateTimeOffset updatedAt)
    {
        note.Path = path;
        note.LocalPath = string.Equals(here, path, StringComparison.Ordinal) ? null : here;
        note.Hash = hash;
        note.UpdatedAt = updatedAt;
        note.Unprotected = false; // the cloud's state is the note's state now: a removal of the protection here is no more
    }

    /// <summary>Writes a note of the cloud (atomically) with the time of its change, and remembers the file as hashed.</summary>
    private static void Write(string root, string relative, NoteData data, DateTimeOffset updatedAt, SyncedNote note, string hash)
    {
        var full = Full(root, relative);
        AccessGuard.EnsureCanWrite(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".sync.tmp";
        if (data.Bytes is { } bytes)
        {
            File.WriteAllBytes(temporary, Convert.FromBase64String(bytes));
        }
        else
        {
            File.WriteAllText(temporary, data.Content ?? string.Empty, new UTF8Encoding(false));
        }

        File.Move(temporary, full, overwrite: true);
        File.SetLastWriteTimeUtc(full, updatedAt.UtcDateTime);
        var info = new FileInfo(full);
        note.SeenLength = info.Length;
        note.SeenWriteTimeUtc = info.LastWriteTimeUtc;
        note.SeenHash = hash;
    }

    private static void MoveFile(string root, string from, string to)
    {
        var fromFull = Full(root, from);
        var toFull = Full(root, to);
        AccessGuard.EnsureCanWrite(fromFull);
        Directory.CreateDirectory(Path.GetDirectoryName(toFull)!);
        File.Move(fromFull, toFull);
        EncryptionSession.Move(fromFull, toFull);
    }

    /// <summary>"Ordner/Titel (Konflikt ANNA-PC).md" next to the note (made unique).</summary>
    private static string ConflictCopy(string root, string relative)
    {
        var device = NoteLibrary.SanitizeTitle(SettingsService.Get().Cloud.DeviceName is { Length: > 0 } name ? name : AppInfo.MachineName);
        var full = Full(root, relative);
        var title = Path.GetFileNameWithoutExtension(full);
        var copy = Path.Combine(Path.GetDirectoryName(full)!, $"{title} (Konflikt {device}){Path.GetExtension(full)}");
        return RelativeInside(root, NoteLibrary.MakeUniquePath(copy))!;
    }

    /// <summary>The path itself when it is free (or the note's own), otherwise "Titel (2).md".</summary>
    private static string FreePath(string root, string relative, string? own) =>
        RelativeInside(root, NoteLibrary.MakeUniquePath(Full(root, relative), own is null ? null : Full(root, own)))!;

    /// <summary>The spelling of a path as the file system has it (Windows and macOS ignore the case when they look it up).</summary>
    private static string ActualPath(string full)
    {
        var folder = Path.GetDirectoryName(full)!;
        var name = Path.GetFileName(full);
        return Directory.EnumerateFiles(folder).FirstOrDefault(file => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)) ?? full;
    }

    /// <summary>The push of this file says that the protection was removed on purpose (once, see <see cref="SyncedNote.Unprotected"/>).</summary>
    private static bool Unprotects(SyncedNote note, string here) => note.Unprotected && IsEncrypted(note.Path) && !IsEncrypted(here);

    private static PendingItem? Upsert(string key, LocalFile file, DateTimeOffset updatedAt, bool unprotected = false)
    {
        string data;
        string hash;
        try
        {
            if (IsEncrypted(file.Relative))
            {
                var bytes = File.ReadAllBytes(file.FullPath);
                data = JsonSerializer.Serialize(new NoteData(file.Relative, null, Convert.ToBase64String(bytes)), CloudContract.Json);
                hash = Sha256(bytes);
            }
            else
            {
                var text = File.ReadAllText(file.FullPath);
                data = JsonSerializer.Serialize(new NoteData(file.Relative, text, Unprotected: unprotected ? true : null), CloudContract.Json);
                hash = Sha256(Encoding.UTF8.GetBytes(text));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null; // being written right now: next round
        }

        if (data.Length > SyncContract.MaxDataLength)
        {
            if (WarnedTooLarge.Add($"{key}|{hash}"))
            {
                Log.Warning($"Sync: a note is too large for the cloud ({data.Length} characters, at most {SyncContract.MaxDataLength}); it stays on this computer");
            }

            return null;
        }

        return new PendingItem(
            new SyncItemDto { Collection = NotesCollection, Key = key, Data = data, ContentHash = hash, UpdatedAt = updatedAt },
            file.Relative, hash, file.Length, file.WriteTimeUtc, unprotected && !IsEncrypted(file.Relative));
    }

    /// <summary>The hash of a note's file, read again only when its size or time changed.</summary>
    private static bool TrySeenHash(SyncedNote note, LocalFile file, out string hash, ref bool changed)
    {
        if (note.SeenLength == file.Length && note.SeenWriteTimeUtc == file.WriteTimeUtc && note.SeenHash.Length > 0)
        {
            hash = note.SeenHash;
            return true;
        }

        try
        {
            hash = HashFile(file.FullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            hash = string.Empty;
            return false;
        }

        note.SeenLength = file.Length;
        note.SeenWriteTimeUtc = file.WriteTimeUtc;
        note.SeenHash = hash;
        changed = true;
        return true;
    }

    private static string? HashOrNull(LocalFile file, Dictionary<string, string?> cache)
    {
        if (!cache.TryGetValue(file.Relative, out var hash))
        {
            try
            {
                hash = HashFile(file.FullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                hash = null;
            }

            cache[file.Relative] = hash;
        }

        return hash;
    }

    /// <summary>The content hash of a file: SHA-256 of its UTF-8 text, or of the bytes of a protected note.</summary>
    private static string HashFile(string full) => NoteLibrary.GetKind(full) == NoteFileKind.Encrypted
        ? Sha256(File.ReadAllBytes(full))
        : Sha256(Encoding.UTF8.GetBytes(File.ReadAllText(full)));

    private static string HashOf(NoteData data) => data.Bytes is { } bytes
        ? Sha256(Convert.FromBase64String(bytes))
        : Sha256(Encoding.UTF8.GetBytes(data.Content ?? string.Empty));

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static NoteData? ParseData(SyncItemDto item)
    {
        try
        {
            var data = item.Data is null ? null : JsonSerializer.Deserialize<NoteData>(item.Data, CloudContract.Json);
            if (data is null || (data.Bytes is null) == (data.Content is null))
            {
                return null;
            }

            // A protected note travels only as its encrypted bytes, a plain one only as text.
            var encrypted = IsEncrypted(data.Path ?? string.Empty);
            if (encrypted != (data.Bytes is not null))
            {
                return null;
            }

            if (data.Bytes is not null)
            {
                _ = Convert.FromBase64String(data.Bytes);
            }

            return data;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A path from the cloud made safe for this computer: relative, below the notes folder, every part a valid file name
    /// (cleaned like a title), a note's extension. Null when nothing usable is left.
    /// </summary>
    private static string? CleanNotePath(string? path)
    {
        var clean = CleanFolderPath(path);
        return clean is not null && NoteLibrary.IsSupportedFile(clean) && Path.GetFileNameWithoutExtension(clean).Length > 0 ? clean : null;
    }

    private static string? CleanFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000)
        {
            return null;
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>();
        foreach (var part in parts)
        {
            if (part is "." or "..")
            {
                return null;
            }

            var name = NoteLibrary.SanitizeTitle(part);
            if (name.Length == 0 || name.StartsWith('.'))
            {
                return null;
            }

            clean.Add(name);
        }

        return clean.Count == 0 ? null : string.Join('/', clean);
    }

    private static bool IsEncrypted(string path) => NoteLibrary.GetKind(path) == NoteFileKind.Encrypted;

    private static DateTimeOffset Later(DateTime utc, DateTimeOffset than) => Later(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), than);

    /// <summary>A change here is newer than what this computer last had from the cloud, whatever the clocks say.</summary>
    private static DateTimeOffset Later(DateTimeOffset value, DateTimeOffset than) => value > than ? value : than.AddMilliseconds(1);

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second) => first > second ? first : second;

    private static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The path relative to the notes folder with forward slashes, or null when it is outside.</summary>
    private static string? RelativeInside(string root, string full)
    {
        var relative = Path.GetRelativePath(root, Path.GetFullPath(full));
        return relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? null
            : relative.Replace('\\', '/');
    }

    /// <summary>The notes below the folder by their relative path (hidden files and folders, and folders starting with a dot, left out).</summary>
    private static Dictionary<string, LocalFile> ListFiles(string root)
    {
        var files = new Dictionary<string, LocalFile>(PathComparer);
        foreach (var full in Directory.EnumerateFiles(root, "*", Enumeration))
        {
            if (!NoteLibrary.IsSupportedFile(full) || RelativeInside(root, full) is not { } relative || relative.Split('/').Any(part => part.StartsWith('.')))
            {
                continue;
            }

            var info = new FileInfo(full);
            files[relative] = new LocalFile(relative, full, info.Length, info.LastWriteTimeUtc);
        }

        return files;
    }

    private static HashSet<string> ListFolders(string root) =>
        Directory.EnumerateDirectories(root, "*", Enumeration)
            .Select(full => RelativeInside(root, full))
            .OfType<string>()
            .Where(relative => !relative.Split('/').Any(part => part.StartsWith('.')))
            .ToHashSet(PathComparer);

    private static EnumerationOptions Enumeration => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private sealed record LocalFile(string Relative, string FullPath, long Length, DateTime WriteTimeUtc);
}

/// <summary>One item to push, with what it was read from (path, hash, size, time) to confirm it afterwards; <see cref="Unprotected"/>: it carries the marker.</summary>
internal sealed record PendingItem(SyncItemDto Item, string? Here = null, string? Hash = null, long Length = -1, DateTime WriteTimeUtc = default, bool Unprotected = false);

/// <summary>
/// The data of a note in the cloud: its path and its text, or the encrypted bytes of a protected note (base64).
/// <see cref="Unprotected"/> (<c>"unprotected":true</c>, left out otherwise) goes only with the one push of a plain note whose
/// protection the person removed ("Schutz aufheben") while the cloud had it protected: MatCloud's Note module refuses a plain
/// version of a protected note without it (and keeps it on the note's later plain states), and ThixNote never writes a
/// protected file as plain text for a version without it.
/// </summary>
internal sealed record NoteData(string Path, string? Content = null, string? Bytes = null, bool? Unprotected = null);
