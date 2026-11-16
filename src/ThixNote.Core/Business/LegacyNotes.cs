namespace ThixNote.Core.Business;

/// <summary>
/// Converts notes of the first ThixNote build (GUID file names, front matter block) into plain files named after their title.
/// </summary>
public static class LegacyNotes
{
    private const string Fence = "---";

    /// <summary>Converts all unencrypted legacy notes in the notes folder. Encrypted ones are converted when they are unlocked.</summary>
    public static void MigrateFolder(string notesFolder)
    {
        if (!Directory.Exists(notesFolder))
        {
            return;
        }

        var legacyFiles = Directory.EnumerateFiles(notesFolder, "*" + NoteLibrary.MarkdownExtension, SearchOption.AllDirectories)
            .Where(path => Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _));

        foreach (var path in legacyFiles)
        {
            if (!TrySplit(File.ReadAllText(path), out var title, out var body))
            {
                continue;
            }

            File.WriteAllText(path, body);
            NoteLibrary.Rename(path, title.Length > 0 ? title : NoteLibrary.DefaultTitle);
        }
    }

    /// <summary>Splits legacy content into title and Markdown body. Returns false for regular content.</summary>
    public static bool TrySplit(string content, out string title, out string body)
    {
        title = string.Empty;
        body = content;
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != Fence || !lines[1].StartsWith("id:", StringComparison.Ordinal))
        {
            return false;
        }

        var closingIndex = Array.FindIndex(lines, 1, line => line.Trim() == Fence);
        if (closingIndex < 0)
        {
            return false;
        }

        var titleLine = lines[1..closingIndex].FirstOrDefault(line => line.StartsWith("title:", StringComparison.Ordinal));
        title = titleLine?["title:".Length..].Trim() ?? string.Empty;
        body = string.Join('\n', lines[(closingIndex + 1)..]).TrimStart('\n');
        return true;
    }
}
