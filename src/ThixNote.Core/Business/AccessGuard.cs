using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>Permission checks. Always executed in the business layer, never only in the UI.</summary>
public static class AccessGuard
{
    /// <summary>ThixNote only reads, writes, renames and deletes its own note types.</summary>
    public static void EnsureIsNoteFile(string path)
    {
        if (!NoteLibrary.IsSupportedFile(path))
        {
            throw new UnauthorizedAccessException($"ThixNote bearbeitet nur .md, .txt und .txts: {Path.GetFileName(path)}");
        }
    }

    public static void EnsureCanRead(string path)
    {
        EnsureIsNoteFile(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Die Datei wurde nicht gefunden: {path}", path);
        }
    }

    public static void EnsureCanWrite(string path)
    {
        EnsureIsNoteFile(path);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly))
        {
            throw new UnauthorizedAccessException($"Die Datei ist schreibgeschützt: {Path.GetFileName(path)}");
        }
    }

    /// <summary>Encrypted notes may only be read, written, exported or decrypted while they are unlocked.</summary>
    public static EncryptionKey EnsureUnlocked(string path)
    {
        return EncryptionSession.TryGetKey(path)
            ?? throw new UnauthorizedAccessException("Die Notiz ist verschlüsselt und nicht entsperrt.");
    }

    public static void EnsureCanExport(string path)
    {
        if (NoteLibrary.GetKind(path) == NoteFileKind.Encrypted)
        {
            EnsureUnlocked(path);
        }
    }
}
