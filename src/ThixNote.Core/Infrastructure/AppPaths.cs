namespace ThixNote.Core.Infrastructure;

/// <summary>
/// Where ThixNote keeps its files. Program files, its own data and the notes are separate: updates and uninstalling never
/// touch the notes, and the data folder only when the user decides so.
///   Data (settings, updates, logs): Windows %LocalAppData%\ThixNote, macOS ~/Library/Application Support/ThixNote
///   Notes (the default):            Dokumente/ThixNote (a setting can point elsewhere)
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Developers point ThixNote to a folder of its own with this variable (sample notes, screenshots): settings, logs and,
    /// unless the settings say otherwise, the notes ("Notizen" inside it). It is then an instance of its own.
    /// </summary>
    public const string DataDirectoryVariable = "THIXNOTE_DATA_DIR";

    private static string? _dataDirectoryOverride = NullIfEmpty(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    public static string DataDirectory => _dataDirectoryOverride ?? Path.Combine(UserFolder(), AppInfo.ProductName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string UpdateDirectory => Path.Combine(DataDirectory, "updates");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The notes folder unless the settings name another one.</summary>
    public static string DefaultNotesFolder => _dataDirectoryOverride is { } own
        ? Path.Combine(own, "Notizen")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppInfo.ProductName);

    /// <summary>True when a developer or a test pointed ThixNote to its own folder: it is then its own instance.</summary>
    public static bool HasOverride => _dataDirectoryOverride is not null;

    /// <summary>Tests use a temporary folder.</summary>
    internal static void UseDirectoryForTests(string? directory) => _dataDirectoryOverride = directory;

    private static string UserFolder() => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
