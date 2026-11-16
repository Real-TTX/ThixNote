using Avalonia;

namespace ThixNote.Setup;

internal static class Program
{
    public static bool IsUninstallMode { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        IsUninstallMode = args.Contains("/uninstall", StringComparer.OrdinalIgnoreCase);
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
