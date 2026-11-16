using System.Text.Json;
using ThixNote.Core.Business;
using ThixNote.Core.Models;

namespace ThixNote.Core.Tests;

public sealed class QuickNoteMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "thixnote-tests-" + Guid.NewGuid());

    private string FormerSettingsPath => Path.Combine(root, "AppData", "QuickNote", "settings.json");

    private string FormerNotesFolder => Path.Combine(root, "Documents", "QuickNote");

    private string NotesFolder => Path.Combine(root, "Documents", "ThixNote");

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void TryImport_MovesNotesFolder_AndRewritesNotePaths()
    {
        var note = Path.Combine(FormerNotesFolder, "Einkauf.md");
        Directory.CreateDirectory(FormerNotesFolder);
        File.WriteAllText(note, "Milch");
        WriteFormerSettings(new AppSettings
        {
            NotesFolder = FormerNotesFolder,
            Favorites = [note],
            OpenFiles = [note, @"D:\Woanders\Fremd.md"],
            ActiveFile = note,
            WindowWidth = 640,
        });

        var settings = QuickNoteMigration.TryImport(FormerSettingsPath, FormerNotesFolder, NotesFolder);

        var movedNote = Path.Combine(NotesFolder, "Einkauf.md");
        Assert.NotNull(settings);
        Assert.False(Directory.Exists(FormerNotesFolder));
        Assert.Equal("Milch", File.ReadAllText(movedNote));
        Assert.Equal(NotesFolder, settings.NotesFolder);
        Assert.Equal([movedNote], settings.Favorites);
        Assert.Equal([movedNote, @"D:\Woanders\Fremd.md"], settings.OpenFiles);
        Assert.Equal(movedNote, settings.ActiveFile);
        Assert.Equal(640, settings.WindowWidth);
    }

    [Fact]
    public void TryImport_WithoutSettings_StillMovesNotesFolder()
    {
        Directory.CreateDirectory(FormerNotesFolder);
        File.WriteAllText(Path.Combine(FormerNotesFolder, "Idee.md"), "Text");

        Assert.Null(QuickNoteMigration.TryImport(FormerSettingsPath, FormerNotesFolder, NotesFolder));
        Assert.True(File.Exists(Path.Combine(NotesFolder, "Idee.md")));
    }

    [Fact]
    public void TryImport_KeepsCustomNotesFolder()
    {
        var customFolder = Path.Combine(root, "Eigene Notizen");
        Directory.CreateDirectory(FormerNotesFolder);
        WriteFormerSettings(new AppSettings { NotesFolder = customFolder });

        var settings = QuickNoteMigration.TryImport(FormerSettingsPath, FormerNotesFolder, NotesFolder);

        Assert.Equal(customFolder, settings!.NotesFolder);
        Assert.True(Directory.Exists(FormerNotesFolder));
        Assert.False(Directory.Exists(NotesFolder));
    }

    [Fact]
    public void TryImport_TargetExists_KeepsUsingFormerFolder()
    {
        Directory.CreateDirectory(FormerNotesFolder);
        Directory.CreateDirectory(NotesFolder);
        WriteFormerSettings(new AppSettings { NotesFolder = FormerNotesFolder });

        var settings = QuickNoteMigration.TryImport(FormerSettingsPath, FormerNotesFolder, NotesFolder);

        Assert.Equal(FormerNotesFolder, settings!.NotesFolder);
        Assert.True(Directory.Exists(FormerNotesFolder));
    }

    private void WriteFormerSettings(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FormerSettingsPath)!);
        File.WriteAllText(FormerSettingsPath, JsonSerializer.Serialize(settings));
    }
}
