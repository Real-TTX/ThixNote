using System.Text.Json;
using System.Text.Json.Serialization;
using MatCloud.Contract;

namespace ThixNote.Core.Infrastructure;

/// <summary>
/// What the notes folder was when it was last synchronised with MatCloud (sync-index.json in the data folder): for every note
/// its key in the cloud with the path, hash and time the cloud has, the folders, the last revision seen and the changes of the
/// cloud that wait for an editor to save. It belongs to one account and one notes folder; another one starts afresh. Only
/// <see cref="Business.NoteSync"/> uses it. It holds paths (= titles) and hashes, never the text of a note.
/// </summary>
internal sealed class SyncIndex
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>user@server of the account the index belongs to.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>The notes folder the paths are relative to.</summary>
    public string NotesFolder { get; set; } = string.Empty;

    /// <summary>The highest revision of the cloud that is applied here.</summary>
    public long LastRevision { get; set; }

    /// <summary>The notes by their key (a GUID).</summary>
    public Dictionary<string, SyncedNote> Notes { get; set; } = [];

    /// <summary>The folders the cloud has (relative, forward slashes), also the empty ones.</summary>
    public List<string> Folders { get; set; } = [];

    /// <summary>Changes of the cloud for notes that are open with unsaved changes: applied once the editor has saved.</summary>
    public List<SyncItemDto> Deferred { get; set; } = [];

    public static SyncIndex Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SyncIndex();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<SyncIndex>(stream, SerializerOptions) ?? new SyncIndex();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Starting afresh is safe: every note is then compared with the cloud by its content again.
            Log.Warning($"sync-index.json could not be read ({exception.GetType().Name}); the notes are compared with the cloud again");
            return new SyncIndex();
        }
    }

    /// <summary>Writes to a temporary file first and replaces the old one, so a crash never leaves half an index.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}

/// <summary>One note as the cloud has it, and where it is here.</summary>
internal sealed class SyncedNote
{
    /// <summary>The path in the cloud, relative to the notes folder with forward slashes ("Ordner/Titel.md").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Where the note is here when it was moved (renamed) and the cloud does not know yet; null = at <see cref="Path"/>.</summary>
    public string? LocalPath { get; set; }

    /// <summary>The SHA-256 of the content the cloud has (<see cref="Business.NoteSync"/> says how it is computed).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>When the cloud's version was written (last writer wins).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The file as it was last hashed: size, time of the last write and the hash, so an unchanged file is not read again.</summary>
    public long SeenLength { get; set; } = -1;

    public DateTime SeenWriteTimeUtc { get; set; }

    public string SeenHash { get; set; } = string.Empty;

    /// <summary>Where the note is here (<see cref="LocalPath"/> or <see cref="Path"/>).</summary>
    [JsonIgnore]
    public string Here => LocalPath ?? Path;
}
