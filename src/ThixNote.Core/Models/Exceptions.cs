namespace ThixNote.Core.Models;

public sealed class InvalidPasswordException : Exception
{
    public InvalidPasswordException() : base("Das Kennwort ist falsch oder die Datei ist beschädigt.") { }
}

public sealed class InvalidNoteFileException(string message) : Exception(message);

public enum ExportFormat
{
    Markdown,
    Html,
    PlainText,
}

/// <summary>Key material of an unlocked encrypted note. Only the derived key is kept, never the password.</summary>
public sealed record EncryptionKey(byte[] Salt, byte[] Key, int Iterations);
