namespace ThixNote.Core.Models;

/// <summary>User settings, stored as JSON in the user's application data folder.</summary>
public sealed class AppSettings
{
    public string NotesFolder { get; set; } = string.Empty;
    public List<string> Favorites { get; set; } = [];
    public List<string> OpenFiles { get; set; } = [];
    public string? ActiveFile { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 760;
    public bool IsLibraryCollapsed { get; set; }
}
