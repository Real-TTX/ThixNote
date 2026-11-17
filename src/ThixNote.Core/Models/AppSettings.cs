namespace ThixNote.Core.Models;

/// <summary>The setting "Erscheinungsbild" (DESK-02).</summary>
public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>
/// The settings of this computer, stored as settings.json in the data folder (AppPaths). A record: a change is a new
/// object (SettingsService.Update / Remember). The notes themselves are files in <see cref="NotesFolder"/>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Where the notes live; empty = the default (Dokumente/ThixNote, or "Notizen" in a data folder of its own).</summary>
    public string NotesFolder { get; init; } = string.Empty;

    public List<string> Favorites { get; init; } = [];

    /// <summary>The tabs of the last session, restored at start (encrypted ones locked again).</summary>
    public List<string> OpenFiles { get; init; } = [];

    public string? ActiveFile { get; init; }

    public double WindowWidth { get; init; } = 1180;

    public double WindowHeight { get; init; } = 760;

    /// <summary>The menu at the left folded to its icons (Ctrl/Cmd+B).</summary>
    public bool IsSidebarCollapsed { get; init; }

    /// <summary>The note list next to the editor hidden (Ctrl/Cmd+Shift+L): more room for writing.</summary>
    public bool IsListHidden { get; init; }

    /// <summary>
    /// The compact view (Ctrl/Cmd+Shift+K): ThixNote is a small editor like Notepad at any width – the tabs in the title
    /// bar, the formatting bar, the text, the status bar; no menu, the list over the editor. Below 900 px it is always so.
    /// </summary>
    public bool IsCompactMode { get; init; }

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>The text size of the editor in percent (zoom: Ctrl/Cmd + plus / minus / 0).</summary>
    public int EditorZoom { get; init; } = 100;

    /// <summary>Long lines wrap at the edge of the editor (like "Zeilenumbruch" in Notepad).</summary>
    public bool WordWrap { get; init; } = true;

    public bool CheckForUpdates { get; init; } = true;

    /// <summary>A new version is downloaded by itself and installed when ThixNote is closed.</summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    /// <summary>The connection to MatCloud (only SyncService changes it).</summary>
    public CloudSettings Cloud { get; init; } = new();
}

/// <summary>
/// The connection to MatCloud, the cloud of the Thix apps (ThixNote keeps its notes in its general sync). The device token is
/// stored protected (DPAPI on Windows, the Keychain on macOS), never in plain text; the password is never stored.
/// </summary>
public sealed record CloudSettings
{
    /// <summary>The address of the cloud, e.g. https://cloud.example.com. Kept after signing out (the form shows it again).</summary>
    public string ServerUrl { get; init; } = string.Empty;

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>The name of this computer in the cloud (and in the name of a conflict copy).</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>The id of this device in the cloud; 0 = not signed in.</summary>
    public long DeviceId { get; init; }

    /// <summary>The device token, protected for this user (Windows DPAPI); on macOS it lives in the Keychain.</summary>
    public string ProtectedToken { get; init; } = string.Empty;

    public bool IsConnected => ServerUrl.Length > 0 && DeviceId > 0;
}
