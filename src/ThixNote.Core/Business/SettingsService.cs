using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// Reading and changing the settings of this computer. Every change of a setting is checked here (values, policy) before
/// it is saved; the UI only offers. What the app remembers by itself (window, tabs, favourites, zoom) goes through
/// <see cref="Remember"/> and is never locked. <see cref="Changed"/> tells the app what is new.
/// </summary>
public static class SettingsService
{
    public const int MinZoom = 50;
    public const int MaxZoom = 300;

    /// <summary>Raised after every saved change (any thread).</summary>
    public static event EventHandler<AppSettings>? Changed;

    public static AppSettings Get() => SettingsStore.Read();

    /// <summary>The notes folder in use: the setting, or the default.</summary>
    public static string NotesFolder => Get().NotesFolder is { Length: > 0 } folder ? folder : AppPaths.DefaultNotesFolder;

    /// <summary>Changes settings the user chooses (appearance, updates, notes folder). Locked by the policy LockConfiguration.</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        AccessPolicy.EnsureConfigurationEditable();
        var next = change(SettingsStore.Read());
        Validate(next);

        // The cloud section belongs to SyncService (its background loop may have written it meanwhile).
        return Save(current => next with { Cloud = current.Cloud });
    }

    /// <summary>The connection to MatCloud: only SyncService changes it (sign-in, sign-out), in one step under the store's lock.</summary>
    internal static AppSettings UpdateCloud(Func<CloudSettings, CloudSettings> change) =>
        Save(current => current with { Cloud = change(current.Cloud) });

    /// <summary>Chooses another notes folder (it is created if needed); the notes stay where they are.</summary>
    public static AppSettings SetNotesFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
        {
            throw new BusinessException("Bitte einen vollständigen Ordnerpfad wählen.", nameof(AppSettings.NotesFolder));
        }

        AccessPolicy.EnsureConfigurationEditable();
        var full = Path.GetFullPath(folder);
        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new BusinessException($"Der Ordner kann nicht verwendet werden: {exception.Message}", nameof(AppSettings.NotesFolder));
        }

        return Update(settings => settings with { NotesFolder = NoteLibrary.IsSamePath(full, AppPaths.DefaultNotesFolder) ? string.Empty : full });
    }

    /// <summary>Things the app remembers by itself (window size, tabs, favourites, folded menu, compact view, zoom): no configuration lock.</summary>
    public static AppSettings Remember(Func<AppSettings, AppSettings> change)
    {
        var current = SettingsStore.Read();
        var next = change(current);
        return Save(_ => current with
        {
            WindowWidth = Math.Clamp(next.WindowWidth, 420, 4000),
            WindowHeight = Math.Clamp(next.WindowHeight, 320, 3000),
            IsSidebarCollapsed = next.IsSidebarCollapsed,
            IsListHidden = next.IsListHidden,
            IsCompactMode = next.IsCompactMode,
            Favorites = next.Favorites,
            OpenFiles = next.OpenFiles,
            ActiveFile = next.ActiveFile,
            EditorZoom = Math.Clamp(next.EditorZoom, MinZoom, MaxZoom),
            WordWrap = next.WordWrap,
        });
    }

    private static AppSettings Save(Func<AppSettings, AppSettings> change)
    {
        var saved = SettingsStore.Update(change);
        Changed?.Invoke(null, saved);
        return saved;
    }

    private static void Validate(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
        {
            throw new BusinessException("Dieses Erscheinungsbild gibt es nicht.", nameof(AppSettings.Theme));
        }

        if (settings.EditorZoom is < MinZoom or > MaxZoom)
        {
            throw new BusinessException($"Die Textgröße muss zwischen {MinZoom} und {MaxZoom} % liegen.", nameof(AppSettings.EditorZoom));
        }
    }
}
