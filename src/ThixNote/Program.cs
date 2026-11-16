using Avalonia;
using Avalonia.Controls;
using ThixNote.Core.Infrastructure;

namespace ThixNote;

internal static class Program
{
    /// <summary>
    /// One program for everything (App decides): the application and - on Windows - its setup. ThixNote has no tray: closing
    /// the main window ends it (App.ExitApplication saves the notes first).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: no error ends ThixNote without a report (and on Windows a message box).
        var isSnapshot = args.Contains(Snapshot.Argument);
        ProblemReports.Install(showMessageBox: !args.Contains(SelfTest.Argument) && !isSnapshot);

        // The self test and the snapshots work in a fresh folder of their own (before anything reads the data folder).
        if (args.Contains(SelfTest.Argument) || isSnapshot)
        {
            Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable,
                Path.Combine(Path.GetTempPath(), isSnapshot ? "thixnote-snapshot" : "thixnote-self-test", Guid.NewGuid().ToString("N")));
        }

        // The snapshots draw the windows headless: no screen, no single-instance check, no update check.
        if (isSnapshot)
        {
            return Snapshot.Run(args);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { OverlayPopups = true })
            .LogToTrace();
}
