using ThixNote.Core.Business;
using ThixNote.Core.Models;

namespace ThixNote.Tests;

public sealed class NoteLibraryTests : IDisposable
{
    private readonly string notesFolder = Path.Combine(Path.GetTempPath(), "thixnote-tests-" + Guid.NewGuid());

    public NoteLibraryTests() => Directory.CreateDirectory(notesFolder);

    public void Dispose() => Directory.Delete(notesFolder, recursive: true);

    [Fact]
    public void Encryption_RoundTrip_ReturnsOriginalText()
    {
        var encrypted = NoteEncryption.Encrypt("Geheim äöü", "kennwort");
        Assert.Equal("Geheim äöü", NoteEncryption.Decrypt(encrypted, "kennwort").Content);
    }

    [Fact]
    public void Encryption_WrongPassword_Throws()
    {
        var encrypted = NoteEncryption.Encrypt("Geheim", "richtig");
        Assert.Throws<InvalidPasswordException>(() => NoteEncryption.Decrypt(encrypted, "falsch"));
    }

    [Fact]
    public void CreateNote_UsesTitleAsFileName_AndStaysUnique()
    {
        var first = NoteLibrary.CreateNote(notesFolder, "Einkauf");
        var second = NoteLibrary.CreateNote(notesFolder, "Einkauf");

        Assert.Equal("Einkauf.md", Path.GetFileName(first));
        Assert.Equal("Einkauf (2).md", Path.GetFileName(second));
    }

    [Fact]
    public void Rename_ChangesFileName_KeepsExtension()
    {
        var path = NoteLibrary.CreateNote(notesFolder, "Alt");
        var renamed = NoteLibrary.Rename(path, "Neu: Titel?");

        Assert.Equal("Neu Titel.md", Path.GetFileName(renamed));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Protect_WritesTxts_WhichNeedsPasswordAfterLock()
    {
        var path = NoteLibrary.CreateNote(notesFolder, "Geheim");
        var protectedPath = NoteContent.Protect(path, "streng geheim", "pw");

        Assert.EndsWith(".txts", protectedPath);
        Assert.False(File.Exists(path));
        Assert.Equal("streng geheim", NoteContent.Read(protectedPath));

        NoteContent.Lock(protectedPath);
        Assert.Throws<UnauthorizedAccessException>(() => NoteContent.Read(protectedPath));
        Assert.Throws<UnauthorizedAccessException>(() => NoteContent.Write(protectedPath, "überschrieben"));
        Assert.Throws<InvalidPasswordException>(() => NoteContent.Unlock(protectedPath, "falsch"));

        Assert.Equal("streng geheim", NoteContent.Unlock(protectedPath, "pw"));
        NoteContent.Write(protectedPath, "neu");
        NoteContent.Lock(protectedPath);
        Assert.Equal("neu", NoteContent.Unlock(protectedPath, "pw"));
    }

    [Fact]
    public void Unprotect_RequiresUnlockedNote()
    {
        var path = NoteContent.Protect(NoteLibrary.CreateNote(notesFolder, "X"), "inhalt", "pw");
        NoteContent.Lock(path);
        Assert.Throws<UnauthorizedAccessException>(() => NoteContent.Unprotect(path));

        NoteContent.Unlock(path, "pw");
        var plainPath = NoteContent.Unprotect(path);
        Assert.Equal("inhalt", File.ReadAllText(plainPath));
    }

    [Fact]
    public void Guard_RejectsForeignFileTypes()
    {
        var foreign = Path.Combine(notesFolder, "programm.exe");
        File.WriteAllText(foreign, "x");
        Assert.Throws<UnauthorizedAccessException>(() => NoteLibrary.MoveToTrash(foreign));
        Assert.Throws<UnauthorizedAccessException>(() => NoteContent.Write(foreign, "y"));
    }

    [Fact]
    public void Export_LockedNote_IsRejected()
    {
        var path = NoteContent.Protect(NoteLibrary.CreateNote(notesFolder, "Y"), "inhalt", "pw");
        NoteContent.Lock(path);
        Assert.Throws<UnauthorizedAccessException>(() =>
            NoteExporter.ExportToFile(path, "Y", "inhalt", ExportFormat.Markdown, Path.Combine(notesFolder, "out.md")));
    }

    [Fact]
    public void Legacy_FrontMatterNotes_AreRenamedAndCleaned()
    {
        var legacyPath = Path.Combine(notesFolder, Guid.NewGuid() + ".md");
        File.WriteAllText(legacyPath, "---\nid: 1\ntitle: Raum für Ideen\nfavorite: false\n---\n\n# Hallo");

        LegacyNotes.MigrateFolder(notesFolder);

        var migrated = Path.Combine(notesFolder, "Raum für Ideen.md");
        Assert.True(File.Exists(migrated));
        Assert.Equal("# Hallo", File.ReadAllText(migrated));
    }
}
