namespace ThixNote.Core.Models;

public enum NoteFileKind
{
    Markdown,
    PlainText,
    Encrypted,
}

/// <summary>A note as it exists on disk. The file name (without extension) is the title. Plain data only.</summary>
public sealed class NoteFile
{
    public required string FullPath { get; init; }
    public required string Title { get; init; }

    /// <summary>Folder relative to the notes folder, empty for notes in the root.</summary>
    public required string FolderName { get; init; }

    public required NoteFileKind Kind { get; init; }
    public required DateTime ModifiedUtc { get; init; }

    /// <summary>File content for unencrypted notes (used for preview and search), empty for encrypted notes.</summary>
    public string Content { get; init; } = string.Empty;
}
