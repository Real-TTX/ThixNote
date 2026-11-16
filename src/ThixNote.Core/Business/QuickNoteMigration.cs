using System.Text.Json;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// One-time takeover from QuickNote, the former name of ThixNote: settings from AppData/QuickNote and the
/// notes folder Documents/QuickNote, which is moved to Documents/ThixNote (stored note paths follow it).
/// </summary>
public static class QuickNoteMigration
{
    private const string FormerName = "QuickNote";

    private static readonly string FormerSettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FormerName, "settings.json");

    private static readonly string FormerNotesFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FormerName);

    /// <summary>Returns the taken over settings, or null if QuickNote left no settings behind.</summary>
    public static AppSettings? TryImport() => TryImport(FormerSettingsPath, FormerNotesFolder, NoteLibrary.DefaultNotesFolder);

    public static AppSettings? TryImport(string formerSettingsPath, string formerNotesFolder, string notesFolder)
    {
        var settings = TryRead(formerSettingsPath);
        var usesFormerFolder = settings is null
            || string.IsNullOrWhiteSpace(settings.NotesFolder)
            || NoteLibrary.IsSamePath(settings.NotesFolder, formerNotesFolder);
        if (!usesFormerFolder)
        {
            return settings;
        }

        if (TryMoveFolder(formerNotesFolder, notesFolder))
        {
            settings?.ReplaceFolder(formerNotesFolder, notesFolder);
        }
        else if (settings is not null && Directory.Exists(formerNotesFolder))
        {
            // Moving failed (e.g. a file is in use): keep working on the old folder rather than hiding the notes.
            settings.NotesFolder = formerNotesFolder;
        }

        return settings;
    }

    private static bool TryMoveFolder(string source, string target)
    {
        if (!Directory.Exists(source) || Directory.Exists(target))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ReplaceFolder(this AppSettings settings, string formerFolder, string folder)
    {
        string Map(string path) => path.StartsWith(formerFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? folder + path[formerFolder.Length..]
            : path;

        settings.NotesFolder = folder;
        settings.Favorites = settings.Favorites.Select(Map).ToList();
        settings.OpenFiles = settings.OpenFiles.Select(Map).ToList();
        settings.ActiveFile = settings.ActiveFile is null ? null : Map(settings.ActiveFile);
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
