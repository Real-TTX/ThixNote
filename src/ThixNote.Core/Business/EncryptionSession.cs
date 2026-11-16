using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// Remembers which encrypted notes are unlocked in this process (path -> derived key).
/// Writing an encrypted note is only allowed while it is registered here.
/// </summary>
public static class EncryptionSession
{
    private static readonly Dictionary<string, EncryptionKey> UnlockedNotes = new(PathComparer);
    private static readonly object Gate = new();

    public static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static bool IsUnlocked(string path)
    {
        lock (Gate)
        {
            return UnlockedNotes.ContainsKey(Path.GetFullPath(path));
        }
    }

    internal static EncryptionKey? TryGetKey(string path)
    {
        lock (Gate)
        {
            return UnlockedNotes.GetValueOrDefault(Path.GetFullPath(path));
        }
    }

    internal static void Register(string path, EncryptionKey key)
    {
        lock (Gate)
        {
            UnlockedNotes[Path.GetFullPath(path)] = key;
        }
    }

    internal static void Move(string oldPath, string newPath)
    {
        lock (Gate)
        {
            if (UnlockedNotes.Remove(Path.GetFullPath(oldPath), out var key))
            {
                UnlockedNotes[Path.GetFullPath(newPath)] = key;
            }
        }
    }

    public static void Forget(string path)
    {
        lock (Gate)
        {
            UnlockedNotes.Remove(Path.GetFullPath(path));
        }
    }
}
