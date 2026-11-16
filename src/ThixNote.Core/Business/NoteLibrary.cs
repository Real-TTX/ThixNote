using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// The notes folder (default: Documents/ThixNote). Every note is a regular file named after its title;
/// sub folders group notes. There is no database and no hidden index.
/// </summary>
public static class NoteLibrary
{
    public const string MarkdownExtension = ".md";
    public const string PlainTextExtension = ".txt";
    public const string EncryptedExtension = ".txts";
    public const string DefaultTitle = "Neue Notiz";

    private const int PreviewLength = 4_000;

    public static string DefaultNotesFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ThixNote");

    public static bool IsSupportedFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(MarkdownExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(PlainTextExtension, StringComparison.OrdinalIgnoreCase)
            || extension.Equals(EncryptedExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static NoteFileKind GetKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(EncryptedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return NoteFileKind.Encrypted;
        }

        return extension.Equals(PlainTextExtension, StringComparison.OrdinalIgnoreCase) ? NoteFileKind.PlainText : NoteFileKind.Markdown;
    }

    /// <summary>Creates the notes folder if needed. Returns true when it was created (first start).</summary>
    public static bool EnsureNotesFolder(string notesFolder)
    {
        if (Directory.Exists(notesFolder))
        {
            return false;
        }

        Directory.CreateDirectory(notesFolder);
        return true;
    }

    public static IReadOnlyList<string> ListFolders(string notesFolder)
    {
        if (!Directory.Exists(notesFolder))
        {
            return [];
        }

        return Directory.GetDirectories(notesFolder)
            .Where(folder => !IsHidden(folder))
            .Select(folder => Path.GetFileName(folder))
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<NoteFile> ListNotes(string notesFolder)
    {
        if (!Directory.Exists(notesFolder))
        {
            return [];
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        return Directory.EnumerateFiles(notesFolder, "*", options)
            .Where(IsSupportedFile)
            .Select(path => ReadNoteFile(notesFolder, path))
            .ToList();
    }

    public static NoteFile ReadNoteFile(string notesFolder, string path)
    {
        var kind = GetKind(path);
        var folder = Path.GetRelativePath(notesFolder, Path.GetDirectoryName(path)!);
        return new NoteFile
        {
            FullPath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            FolderName = folder == "." || folder.StartsWith("..", StringComparison.Ordinal) ? string.Empty : folder,
            Kind = kind,
            ModifiedUtc = File.GetLastWriteTimeUtc(path),
            Content = kind == NoteFileKind.Encrypted ? string.Empty : ReadPreview(path),
        };
    }

    /// <summary>Creates an empty Markdown note with a unique name in the given folder.</summary>
    public static string CreateNote(string folder, string title = DefaultTitle, string content = "")
    {
        Directory.CreateDirectory(folder);
        var path = MakeUniquePath(Path.Combine(folder, SanitizeTitle(title) + MarkdownExtension));
        AccessGuard.EnsureCanWrite(path);
        File.WriteAllText(path, content);
        return path;
    }

    public static string CreateFolder(string notesFolder, string name)
    {
        var safeName = SanitizeTitle(name);
        if (safeName.Length == 0)
        {
            throw new ArgumentException("Bitte einen Ordnernamen eingeben.", nameof(name));
        }

        var path = Path.Combine(notesFolder, safeName);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Renames a note file to a new title (keeps folder and extension). Returns the new path.</summary>
    public static string Rename(string path, string newTitle)
    {
        AccessGuard.EnsureCanWrite(path);
        var safeTitle = SanitizeTitle(newTitle);
        if (safeTitle.Length == 0 || safeTitle == Path.GetFileNameWithoutExtension(path))
        {
            return path;
        }

        var targetPath = MakeUniquePath(Path.Combine(Path.GetDirectoryName(path)!, safeTitle + Path.GetExtension(path)), path);
        if (string.Equals(targetPath, path, StringComparison.Ordinal))
        {
            return path;
        }

        File.Move(path, targetPath);
        EncryptionSession.Move(path, targetPath);
        return targetPath;
    }

    /// <summary>Moves a note to the recycle bin (Windows) or trash (macOS) instead of deleting it permanently.</summary>
    public static void MoveToTrash(string path)
    {
        AccessGuard.EnsureCanWrite(path);
        EncryptionSession.Forget(path);
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }

        var trashFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        if (!Directory.Exists(trashFolder))
        {
            File.Delete(path);
            return;
        }

        File.Move(path, MakeUniquePath(Path.Combine(trashFolder, Path.GetFileName(path))));
    }

    /// <summary>Removes a note only if it is still completely empty (used for new notes that were never filled).</summary>
    public static void DeleteIfEmpty(string path)
    {
        AccessGuard.EnsureCanWrite(path);
        if (File.Exists(path) && new FileInfo(path).Length == 0)
        {
            File.Delete(path);
        }
    }

    /// <summary>Returns the path itself if it is free (or equals <paramref name="ownPath"/>), otherwise "Name (2).ext" etc.</summary>
    public static string MakeUniquePath(string desiredPath, string? ownPath = null)
    {
        var folder = Path.GetDirectoryName(desiredPath)!;
        var title = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        var candidate = desiredPath;
        for (var counter = 2; File.Exists(candidate) && !IsSamePath(candidate, ownPath); counter++)
        {
            candidate = Path.Combine(folder, $"{title} ({counter}){extension}");
        }

        return candidate;
    }

    public static string SanitizeTitle(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var cleaned = new string(title.ReplaceLineEndings(" ").Where(character => !invalid.Contains(character)).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 120 ? cleaned[..120].Trim() : cleaned;
    }

    public static bool IsSamePath(string? first, string? second)
        => first is not null && second is not null
           && EncryptionSession.PathComparer.Equals(Path.GetFullPath(first), Path.GetFullPath(second));

    private static string ReadPreview(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[PreviewLength];
            var length = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, length);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static bool IsHidden(string folder)
    {
        var name = Path.GetFileName(folder);
        return name.StartsWith('.') || new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.Hidden);
    }
}
