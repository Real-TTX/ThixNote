using System.Text.Json;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>Loads and saves <see cref="AppSettings"/> (AppData/ThixNote/settings.json).</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Optional isolated profile (settings + notes in one folder), e.g. for tests: THIXNOTE_PROFILE=C:\Temp\tn.</summary>
    private static readonly string? ProfileFolder = Environment.GetEnvironmentVariable("THIXNOTE_PROFILE") is { Length: > 0 } profile
        ? Path.GetFullPath(profile)
        : null;

    public static string SettingsPath { get; } = ProfileFolder is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ThixNote", "settings.json")
        : Path.Combine(ProfileFolder, "settings.json");

    public static AppSettings Load()
    {
        var settings = TryRead()
            ?? (ProfileFolder is null && !File.Exists(SettingsPath) ? QuickNoteMigration.TryImport() : null)
            ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(settings.NotesFolder))
        {
            settings.NotesFolder = ProfileFolder is null ? NoteLibrary.DefaultNotesFolder : Path.Combine(ProfileFolder, "Notizen");
        }

        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience only; failing to store them must never block closing the app.
        }
    }

    private static AppSettings? TryRead()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
