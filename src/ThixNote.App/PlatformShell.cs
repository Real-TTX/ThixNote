using System.Diagnostics;

namespace ThixNote.App;

/// <summary>Opens folders and files in Explorer (Windows) or Finder (macOS).</summary>
internal static class PlatformShell
{
    public static void OpenFolder(string folder)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
            : new ProcessStartInfo("open") { ArgumentList = { folder } };
        Start(startInfo);
    }

    public static void RevealFile(string path)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            : new ProcessStartInfo("open") { ArgumentList = { "-R", path } };
        Start(startInfo);
    }

    private static void Start(ProcessStartInfo startInfo)
    {
        startInfo.UseShellExecute = false;
        using var process = Process.Start(startInfo);
    }
}
