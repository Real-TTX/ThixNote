using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MatDesktop;
using ThixNote.Core.Business;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote;

/// <summary>
/// Start-up and end of ThixNote.
///  - Windows: the published ThixNote.exe is also its setup (RunSetupIfRequested).
///  - The look is MatDesktop's theme with ThixNote's accent (DESK-02).
///  - One instance per user and data folder (MatSingleInstance, DESK-05): a second start (a double click on a note) hands
///    its files to the first one, which opens them as tabs.
///  - No tray: closing the main window ends ThixNote. The notes are saved first, and a downloaded update is installed then.
/// </summary>
public partial class App : Application
{
    /// <summary>ThixNote's accent (IDENT-01, the red of its mark): MatDesktop derives every accent token of light and dark from it.</summary>
    public const string Accent = "#DB4C3F";

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MatSingleInstance? _instance;
    private MainWindow? _mainWindow;
    private bool _isExiting;

    /// <summary>The program file; shortcuts and file types point to it.</summary>
    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ThixNote");

    public static App Instance => (App)Current!;

    /// <summary>The main window (null during setup).</summary>
    public MainWindow? MainWindow => _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // ThixNote speaks German, the texts of MatDesktop (title bar, menu, setup) too.
        MatText.Language = "de";
        MatTheme.Install(this, new MatThemeOptions(Accent) { Icon = Ui.AppIcon });

        // What only ThixNote has (tabs, editor, note list), after the theme so that it wins where it says something.
        Styles.Add(new StyleInclude(new Uri("avares://ThixNote/App.axaml")) { Source = new Uri("avares://ThixNote/Themes/ThixNoteControls.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _desktop = desktop;
        var args = desktop.Args ?? [];

        var selfTest = Array.IndexOf(args, SelfTest.Argument);
        if (selfTest >= 0)
        {
            var report = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppPaths.DataDirectory, "self-test.txt");
            Dispatcher.UIThread.Post(async () => EndProcess(await SelfTest.RunAsync(report)));
            return;
        }

        if (OperatingSystem.IsWindows() && RunSetupIfRequested(args))
        {
            return;
        }

        var files = NoteFilesIn(args);
        if (!TakeSingleInstance(files))
        {
            EndProcess(0);
            return;
        }

        Start(files);
    }

    /// <summary>
    /// Ends the process through the main loop. Shutting down straight from OnFrameworkInitializationCompleted (update,
    /// quiet uninstall, second instance) throws "Dispatcher shut down" because the loop has not started yet.
    /// </summary>
    private void EndProcess(int exitCode) => Dispatcher.UIThread.Post(() => _desktop?.Shutdown(exitCode));

    private void Start(IReadOnlyList<string> files)
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        UiWatchdog.Start();
        Log.Info($"ThixNote {AppInfo.Version} starting on {UpdateService.PlatformKey}");
        UpdateService.RemoveOldPackages();

        ApplyTheme(SettingsService.Get().Theme);
        SettingsService.Changed += (_, settings) => Ui.Post(() => ApplyTheme(settings.Theme));

        _mainWindow = new MainWindow();
        _mainWindow.Closed += (_, _) => ExitApplication();
        _desktop!.MainWindow = _mainWindow;
        _mainWindow.Show();
        _mainWindow.Notes.ViewModel.OpenFiles(files);

        // Crashed or hung last time: say so, with the report.
        Dispatcher.UIThread.Post(() => Dialogs.ProblemReportDialog.ShowUnseen(), DispatcherPriority.Background);

        // macOS: Finder hands opened documents over as an activation; opening the app again shows the window.
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, activation) =>
            {
                if (activation is FileActivatedEventArgs opened)
                {
                    var paths = opened.Files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
                    Ui.Post(() => ShowMainWindow(paths));
                }
                else if (activation.Kind == ActivationKind.Reopen)
                {
                    Ui.Post(() => ShowMainWindow([]));
                }
            };
        }

        UpdateCoordinator.Start();
        SyncService.Start(); // MatCloud: only when this computer is signed in
    }

    /// <summary>Brings the window to the front and opens the files as tabs (a second start, Finder).</summary>
    public void ShowMainWindow(IReadOnlyList<string> files)
    {
        if (_mainWindow is null || _isExiting)
        {
            return;
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Notes.ViewModel.OpenFiles(files);
    }

    /// <summary>The setting "Erscheinungsbild": Wie das System, Hell, Dunkel (DESK-02).</summary>
    public static void ApplyTheme(ThemeChoice theme) => MatTheme.SetAppearance(theme switch
    {
        ThemeChoice.Light => MatAppearance.Light,
        ThemeChoice.Dark => MatAppearance.Dark,
        _ => MatAppearance.System,
    });

    /// <summary>
    /// Ends ThixNote: the notes and the session are saved (the window does that while it closes), then a downloaded update
    /// is installed when automatic updates are on – ThixNote is closed anyway, nobody is interrupted.
    /// </summary>
    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        UiWatchdog.Stop();
        SyncService.Stop();
        if (_mainWindow is { IsVisible: true })
        {
            _mainWindow.IsExiting = true;
            _mainWindow.Close();
        }

        var update = UpdateService.Downloaded;
        if (update is not null && SettingsService.Get().InstallUpdatesAutomatically && UpdateService.CanUpdate)
        {
            try
            {
                // Off the UI thread: its continuations must not wait for the thread that waits here.
                Task.Run(() => UpdateService.StartInstallerAsync(update, restart: false)).GetAwaiter().GetResult();
            }
            catch (Exception exception) when (exception is IOException or BusinessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warning($"The waiting update could not be started: {exception.Message}");
            }
        }

        _instance?.Dispose();
        Log.Info("ThixNote ended");
        _desktop?.Shutdown(0);
    }

    /// <summary>"Jetzt installieren": saves, starts the update and ends; the update opens ThixNote again with its tabs.</summary>
    public async Task InstallUpdateNowAsync(DownloadedUpdate update)
    {
        _mainWindow?.Notes.ViewModel.SaveSession();
        await UpdateService.StartInstallerAsync(update, restart: true);
        _isExiting = true;
        UiWatchdog.Stop();
        SyncService.Stop();
        if (_mainWindow is not null)
        {
            _mainWindow.IsExiting = true;
            _mainWindow.Close();
        }

        _instance?.Dispose();
        _desktop?.Shutdown(0);
    }

    /// <summary>
    /// Windows: the published ThixNote.exe is its own setup (InstallService):
    ///  - "--uninstall" (from "Installierte Apps"): the uninstall assistant, or silently with "--quiet".
    ///  - "--update" (the update, ThixLauncher): installs itself silently; with "--restart" it starts ThixNote again.
    ///  - "--quiet": installs itself silently with the default choices.
    ///  - started anywhere else than the program folder (e.g. from Downloads): the setup assistant.
    /// A development build (not single-file) always runs as the application.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool RunSetupIfRequested(string[] args)
    {
        if (args.Contains(InstallService.UninstallArgument))
        {
            if (args.Contains(InstallService.QuietArgument))
            {
                if (InstallService.Uninstall(deleteUserData: false, Log.Info))
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
                return true;
            }

            Setup.ThixNoteSetup.Run(isUninstall: true, removeProgramFolder =>
            {
                if (removeProgramFolder)
                {
                    InstallService.ScheduleProgramFolderRemoval();
                }

                EndProcess(0);
            });
            return true;
        }

        var isUpdate = args.Contains(InstallService.UpdateArgument);
        if (isUpdate || (args.Contains(InstallService.QuietArgument) && !InstallService.IsInstalledExecutable(ExecutablePath)))
        {
            var exitCode = 0;
            try
            {
                InstallService.Install(ExecutablePath, isUpdate && InstallService.IsInstalled ? null : new InstallOptions(DesktopShortcut: false), Log.Info);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
            {
                Log.Error("Silent installation failed", exception);
                Console.Error.WriteLine(exception.Message);
                exitCode = 1;
            }

            if (args.Contains(InstallService.RestartArgument) && File.Exists(InstallService.InstalledExecutable))
            {
                InstallService.StartInstalled();
            }

            EndProcess(exitCode);
            return true;
        }

#if SINGLE_FILE
        const bool isPublishedBuild = true;
#else
        const bool isPublishedBuild = false;
#endif
        if (isPublishedBuild && !InstallService.IsInstalledExecutable(ExecutablePath) && NoteFilesIn(args).Count == 0)
        {
            Setup.ThixNoteSetup.Run(isUninstall: false, _ => EndProcess(0));
            return true;
        }

        return false;
    }

    /// <summary>The notes named on the command line (a double click on a .txts file, "Öffnen mit"), as full paths.</summary>
    private static IReadOnlyList<string> NoteFilesIn(string[] args) =>
        args.Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
            .Select(argument =>
            {
                try
                {
                    return Path.GetFullPath(argument);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return null;
                }
            })
            .OfType<string>()
            .Where(File.Exists)
            .ToList();

    /// <summary>
    /// One instance per user and data folder (MatSingleInstance, DESK-05: a named mutex and a pipe, on macOS a lock file
    /// and a socket): a second start hands its files to the first one, which opens them and comes to the front, and ends.
    /// A development run with its own THIXNOTE_DATA_DIR does not meet the installed one. On Windows the setup may also ask
    /// the running instance to end (ListenForExitSignal).
    /// </summary>
    private bool TakeSingleInstance(IReadOnlyList<string> files)
    {
        _instance = new MatSingleInstance(AppInfo.ProductName, AppPaths.HasOverride ? AppPaths.DataDirectory : null);
        _instance.SecondStart += (_, args) => Ui.Post(() => ShowMainWindow(NoteFilesIn(args)));
        if (!_instance.TryClaim([.. files]))
        {
            Log.Info("ThixNote runs already: the files were handed to it");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            ListenForExitSignal();
        }

        return true;
    }

    /// <summary>The setup asks a running ThixNote to end (update, uninstall): it saves its notes and ends.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void ListenForExitSignal()
    {
        if (AppPaths.HasOverride)
        {
            return; // a development run with its own data folder is none of the setup's business
        }

        var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstallService.ExitSignalName);
        var listener = new Thread(() =>
        {
            exitSignal.WaitOne();
            Ui.Post(() =>
            {
                Log.Info("Setup asked ThixNote to end");
                ExitApplication();
            });
        })
        {
            IsBackground = true,
            Name = "ThixNote exit signal listener",
        };
        listener.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error", e.Exception);
        e.Handled = true;
        var text = e.Exception is BusinessException ? e.Exception.Message : $"{e.Exception.Message} – Details im Protokoll ({AppPaths.LogDirectory}).";
        MatNotification.Show(e.Exception is BusinessException ? "ThixNote" : "Unerwarteter Fehler", text, isWarning: true);
    }
}
