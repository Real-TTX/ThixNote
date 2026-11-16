using Avalonia;

namespace ThixNote.App;

internal static class Program
{
    public static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        StartupFiles = args.Where(File.Exists).Select(Path.GetFullPath).ToList();
        if (SingleInstance.TryForwardToRunningInstance(StartupFiles))
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
