using System.Text;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>Reads and writes the content of a single note file, including password protection.</summary>
public static class NoteContent
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    /// <summary>Reads a plain note, or an encrypted note that is already unlocked.</summary>
    public static string Read(string path)
    {
        AccessGuard.EnsureCanRead(path);
        if (NoteLibrary.GetKind(path) != NoteFileKind.Encrypted)
        {
            return File.ReadAllText(path);
        }

        var key = AccessGuard.EnsureUnlocked(path);
        return NoteEncryption.Decrypt(File.ReadAllBytes(path), key);
    }

    /// <summary>Unlocks an encrypted note. Throws <see cref="InvalidPasswordException"/> for a wrong password.</summary>
    public static string Unlock(string path, string password)
    {
        AccessGuard.EnsureCanRead(path);
        var (content, key) = NoteEncryption.Decrypt(File.ReadAllBytes(path), password);
        EncryptionSession.Register(path, key);
        return content;
    }

    public static void Lock(string path) => EncryptionSession.Forget(path);

    public static void Write(string path, string content)
    {
        AccessGuard.EnsureCanWrite(path);
        if (NoteLibrary.GetKind(path) != NoteFileKind.Encrypted)
        {
            File.WriteAllText(path, content, Utf8WithoutBom);
            SyncService.NotifyLocalChange();
            return;
        }

        var key = AccessGuard.EnsureUnlocked(path);
        WriteAtomically(path, NoteEncryption.Encrypt(content, key));
        SyncService.NotifyLocalChange();
    }

    /// <summary>Protects a plain note with a password. Returns the new path (.txts); the plain file is removed.</summary>
    public static string Protect(string path, string content, string password)
    {
        AccessGuard.EnsureCanWrite(path);
        if (NoteLibrary.GetKind(path) == NoteFileKind.Encrypted)
        {
            throw new InvalidOperationException("Die Notiz ist bereits verschlüsselt.");
        }

        var key = NoteEncryption.CreateKey(password);
        var targetPath = NoteLibrary.MakeUniquePath(Path.ChangeExtension(path, NoteLibrary.EncryptedExtension), path);
        WriteAtomically(targetPath, NoteEncryption.Encrypt(content, key));
        EncryptionSession.Register(targetPath, key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        SyncService.NoteMoved(path, targetPath);
        return targetPath;
    }

    /// <summary>Removes the password protection. Requires the note to be unlocked. Returns the new path (.md).</summary>
    public static string Unprotect(string path)
    {
        AccessGuard.EnsureCanWrite(path);
        var key = AccessGuard.EnsureUnlocked(path);
        var content = NoteEncryption.Decrypt(File.ReadAllBytes(path), key);
        var targetPath = NoteLibrary.MakeUniquePath(Path.ChangeExtension(path, NoteLibrary.MarkdownExtension), path);
        File.WriteAllText(targetPath, content, Utf8WithoutBom);
        File.Delete(path);
        EncryptionSession.Forget(path);
        SyncService.NoteMoved(path, targetPath, unprotected: true);
        return targetPath;
    }

    /// <summary>Writes to a temporary file first so a crash never leaves a half written encrypted note.</summary>
    private static void WriteAtomically(string path, byte[] data)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, data);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
