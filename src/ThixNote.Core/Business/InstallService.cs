using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;
using ThixNote.Core.Platform.Windows;

namespace ThixNote.Core.Business;

/// <summary>
/// Windows only (macOS: the app bundle, <see cref="MacAppBundle"/>). ThixNote installs itself – like ThixClip – without a
/// separate installer tool. The published ThixNote.exe is the setup: started from anywhere else it copies itself to
/// %LocalAppData%\Programs\ThixNote (per user, no administrator rights), creates the Start menu entry, the entry in
/// "Installierte Apps" and – as the install options say (InstallerOptions, SETUP-08) – a desktop shortcut, automatic updates
/// and the file types (ThixNote is under "Öffnen mit" for .md, .txt and .txts; the chosen ones open with it on a double
/// click). "--uninstall" removes every one of them. The notes are never touched; the data folder
/// (%LocalAppData%\ThixNote: settings, logs) only when the user asks for it while uninstalling.
/// A former QuickNote installation (the old name of ThixNote) is removed on the way; its notes stay.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallService
{
    public const string UpdateArgument = InstallerOptions.UpdateArgument;
    public const string RestartArgument = UpdateService.RestartArgument;
    public const string UninstallArgument = InstallerOptions.UninstallArgument;
    public const string QuietArgument = InstallerOptions.QuietArgument;
    public const string ExecutableName = "ThixNote.exe";
    public const string DocumentIconName = "ThixNote-Document.ico";

    /// <summary>A running ThixNote listens for this signal, saves its notes and ends itself.</summary>
    public const string ExitSignalName = "ThixNote.Exit";

    /// <summary>The ProgIDs of ThixNote 1.1 and older (one for .txts, one for .md and .txt); an update replaces them.</summary>
    internal const string FormerSecureNoteProgId = "ThixNote.SecureNote";
    internal const string FormerNoteProgId = "ThixNote.Note";

    private const string DefaultUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ThixNote";
    private const string DefaultClassesPath = @"Software\Classes";
    private const string FormerUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuickNote";

    private static TestLocations? _testLocations;

    public static string ProgramFolder => _testLocations?.ProgramFolder ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.ProductName);

    public static string InstalledExecutable => Path.Combine(ProgramFolder, ExecutableName);

    public static string StartMenuShortcut => ShortcutIn(_testLocations?.ShortcutFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.ProductName);

    public static string DesktopShortcut => ShortcutIn(_testLocations?.DesktopFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.ProductName);

    private static string UninstallKeyPath => _testLocations?.UninstallKeyPath ?? DefaultUninstallKeyPath;

    private static string ClassesPath => _testLocations?.ClassesPath ?? DefaultClassesPath;

    /// <summary>Installed means: the entry in "Installierte Apps" and the program file exist.</summary>
    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key is not null && File.Exists(InstalledExecutable);
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
    }

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <summary>
    /// The file types that open with ThixNote now (install options): its own ProgIDs, and .txts when an older ThixNote made
    /// it open with its former ProgID – an update keeps that.
    /// </summary>
    public static IReadOnlyList<string> AssociatedFileTypes()
    {
        var types = FileAssociations.Registered(AppInfo.ProductName, InstallerOptions.Config.FileTypes.Select(type => type.Extension)).ToList();
        if (FileAssociations.HandlerOf(NoteLibrary.EncryptedExtension) == FormerSecureNoteProgId && !types.Contains(NoteLibrary.EncryptedExtension))
        {
            types.Add(NoteLibrary.EncryptedExtension);
        }

        return types;
    }

    public static bool IsInstalledExecutable(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installs or updates with the install options (SETUP-08): what <paramref name="choices"/> leaves open (null, or a value of
    /// null) an installed ThixNote keeps as it is and a first install takes from installer.json.
    /// </summary>
    public static void Install(string sourceExecutable, InstallChoices? choices, Action<string> log)
    {
        var isUpdate = IsInstalled;
        var wanted = (choices ?? new InstallChoices()).Over(isUpdate ? InstallerOptions.Current() : InstallerOptions.Defaults);
        log(isUpdate ? $"Aktualisiere ThixNote in {ProgramFolder}" : $"Installiere ThixNote nach {ProgramFolder}");
        StopRunningInstances(log);
        if (_testLocations is null)
        {
            RemoveFormerInstallation(log);
        }

        Directory.CreateDirectory(ProgramFolder);
        if (!IsInstalledExecutable(sourceExecutable))
        {
            CopyWithRetry(sourceExecutable, InstalledExecutable);
            log("Programmdatei kopiert");
        }

        WriteDocumentIcon();
        RemoveLeftoversOfTheOldSetup();

        CreateShortcut(StartMenuShortcut);
        log("Startmenü-Eintrag angelegt");

        // The file types of ThixNote 1.1 go; also an update writes them again: they point at the installed file.
        UnregisterFileTypes(FormerSecureNoteProgId, FormerNoteProgId, executableName: null);
        InstallerOptions.Apply(wanted, InstalledExecutable, log);

        WriteUninstallEntry();
        log("Eintrag unter „Installierte Apps“ geschrieben");
        log(isUpdate ? "Update abgeschlossen" : "Installation abgeschlossen");
    }

    /// <summary>
    /// Removes program, shortcuts, file types and the entry in "Installierte Apps"; the data folder only on request, the
    /// notes never. Returns true when the program folder could not be deleted yet because this process runs from it –
    /// then call <see cref="ScheduleProgramFolderRemoval"/> right before the process ends.
    /// </summary>
    public static bool Uninstall(bool deleteUserData, Action<string> log)
    {
        log("Entferne ThixNote");
        StopRunningInstances(log);

        DeleteFile(StartMenuShortcut);
        DeleteFile(DesktopShortcut);
        log("Verknüpfungen entfernt");
        InstallerOptions.RemoveFileTypes();
        UnregisterFileTypes(FormerSecureNoteProgId, FormerNoteProgId, ExecutableName);
        log("Dateitypen abgemeldet");
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        log("Eintrag unter „Installierte Apps“ entfernt");

        if (deleteUserData)
        {
            // The notes are never part of it, not even when the notes folder was put inside the data folder.
            var notesInside = Path.GetFullPath(SettingsService.NotesFolder).StartsWith(Path.GetFullPath(AppPaths.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (notesInside)
            {
                DeleteFile(AppPaths.SettingsFile);
                DeleteFolder(AppPaths.LogDirectory);
                DeleteFolder(AppPaths.UpdateDirectory);
            }
            else
            {
                DeleteFolder(AppPaths.DataDirectory);
            }

            log("Einstellungen und Protokolle gelöscht");
        }

        var runsFromProgramFolder = (Environment.ProcessPath ?? "").StartsWith(ProgramFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (runsFromProgramFolder)
        {
            log("Programmordner wird nach dem Schließen gelöscht");
            return true;
        }

        DeleteFolder(ProgramFolder);
        log("Programmordner gelöscht");
        return false;
    }

    /// <summary>The program folder cannot go while this exe runs from it: a short-lived cmd removes it afterwards.</summary>
    public static void ScheduleProgramFolderRemoval() =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{ProgramFolder}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

    /// <summary>Starts the installed ThixNote (after setup or update).</summary>
    public static void StartInstalled(string arguments = "") =>
        Process.Start(new ProcessStartInfo(InstalledExecutable, arguments) { UseShellExecute = false, WorkingDirectory = ProgramFolder });

    /// <summary>Redirects program folder, shortcuts, the uninstall entry and the file types for tests; null = the real places.</summary>
    internal static void UseLocationsForTests(string? programFolder)
    {
        if (programFolder is null)
        {
            _testLocations = null;
            FileAssociations.UseClassesRootForTests(null);
            return;
        }

        var root = @"Software\ThixNoteTests\" + Path.GetFileName(programFolder);
        _testLocations = new TestLocations(programFolder, Path.Combine(programFolder, "StartMenu"), Path.Combine(programFolder, "Desktop"),
            root + @"\Uninstall", root + @"\Classes");
        FileAssociations.UseClassesRootForTests(_testLocations.ClassesPath);
    }

    /// <summary>Removes what the tests wrote into the registry.</summary>
    internal static void RemoveTestRegistry() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\ThixNoteTests", throwOnMissingSubKey: false);

    /// <summary>
    /// QuickNote, the former name of ThixNote: its program files, shortcuts, uninstall entry and file types go. Its notes and
    /// settings stay; ThixNote takes them over at its first start (QuickNoteMigration).
    /// </summary>
    private static void RemoveFormerInstallation(Action<string> log)
    {
        string? formerPath;
        using (var key = Registry.CurrentUser.OpenSubKey(FormerUninstallKeyPath))
        {
            formerPath = key?.GetValue("InstallLocation") as string;
        }

        if (formerPath is null)
        {
            return;
        }

        log("Entferne QuickNote (den alten Namen von ThixNote)");
        StopProcesses("QuickNote.exe", formerPath, log);
        DeleteFile(ShortcutIn(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "QuickNote"));
        DeleteFile(ShortcutIn(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "QuickNote"));
        UnregisterFileTypes("QuickNote.SecureNote", "QuickNote.Note", "QuickNote.exe");
        Registry.CurrentUser.DeleteSubKeyTree(FormerUninstallKeyPath, throwOnMissingSubKey: false);
        if (File.Exists(Path.Combine(formerPath, "QuickNote.exe")))
        {
            DeleteFolder(formerPath);
        }

        log("QuickNote entfernt, die Notizen bleiben");
    }

    /// <summary>The first ThixNote setup (a separate program) left an Uninstall.exe and the document icon in the program folder.</summary>
    private static void RemoveLeftoversOfTheOldSetup() => DeleteFile(Path.Combine(ProgramFolder, "Uninstall.exe"));

    /// <summary>.txts needs an icon file of its own (the single-file program carries only the app's icon).</summary>
    private static void WriteDocumentIcon()
    {
        using var icon = Assembly.GetEntryAssembly()?.GetManifestResourceStream("thixnote-document.ico");
        if (icon is null)
        {
            return;
        }

        using var file = File.Create(Path.Combine(ProgramFolder, DocumentIconName));
        icon.CopyTo(file);
    }

    /// <summary>The file types of ThixNote 1.1 and older (and of QuickNote); <paramref name="executableName"/> null keeps the "Öffnen mit" entry of the program.</summary>
    internal static void UnregisterFileTypes(string secureNoteProgId, string noteProgId, string? executableName)
    {
        using (var extensionKey = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\.txts", writable: true))
        {
            if (extensionKey?.GetValue(string.Empty) as string == secureNoteProgId)
            {
                extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }

        RemoveOpenWithProgId(".txts", secureNoteProgId);
        RemoveOpenWithProgId(".md", noteProgId);
        RemoveOpenWithProgId(".txt", noteProgId);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{secureNoteProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\{noteProgId}", throwOnMissingSubKey: false);
        if (executableName is not null)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\Applications\{executableName}", throwOnMissingSubKey: false);
        }

        FileAssociations.NotifyShell();
    }

    private static void RemoveOpenWithProgId(string extension, string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{extension}\OpenWithProgids", writable: true);
        key?.DeleteValue(progId, throwOnMissingValue: false);
    }

    /// <summary>Asks a running ThixNote from the program folder to end (it saves its notes), and ends it when it does not react in time.</summary>
    private static void StopRunningInstances(Action<string> log)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName))
            .Where(process => process.Id != Environment.ProcessId && RunsFrom(process, InstalledExecutable))
            .ToList();
        if (running.Count == 0)
        {
            return;
        }

        log("Beende das laufende ThixNote (die Notizen werden gespeichert)");
        if (_testLocations is null && EventWaitHandle.TryOpenExisting(ExitSignalName, out var exitSignal))
        {
            exitSignal.Set();
            exitSignal.Dispose();
        }

        WaitOrKill(running, TimeSpan.FromSeconds(20));
    }

    private static void StopProcesses(string executableName, string folder, Action<string> log)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName))
            .Where(process => RunsFrom(process, Path.Combine(folder, executableName)))
            .ToList();
        if (running.Count > 0)
        {
            log($"Beende {Path.GetFileNameWithoutExtension(executableName)}");
            foreach (var process in running)
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                }
            }

            WaitOrKill(running, TimeSpan.FromSeconds(10));
        }
    }

    private static void WaitOrKill(IEnumerable<Process> processes, TimeSpan patience)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.WaitForExit(patience))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool RunsFrom(Process process, string executable)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>The old program may need a moment to release its file after it ended.</summary>
    private static void CopyWithRetry(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>The desktop shortcut on or off (install options).</summary>
    internal static void SetDesktopShortcut(bool wanted)
    {
        if (wanted)
        {
            CreateShortcut(DesktopShortcut);
        }
        else
        {
            DeleteFile(DesktopShortcut);
        }
    }

    private static string ShortcutIn(string folder, string name) => Path.Combine(folder, $"{name}.lnk");

    private static void CreateShortcut(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Platform.Windows.ShellLink.Write(path, InstalledExecutable, ProgramFolder, "Notizen als Dateien", InstalledExecutable);
    }

    private static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppInfo.ProductName);
        key.SetValue("DisplayVersion", AppInfo.Version);
        key.SetValue("Publisher", "Matthias Schmoldt");
        key.SetValue("DisplayIcon", InstalledExecutable + ",0");
        key.SetValue("InstallLocation", ProgramFolder);
        key.SetValue("UninstallString", $"\"{InstalledExecutable}\" {UninstallArgument}");
        key.SetValue("QuietUninstallString", $"\"{InstalledExecutable}\" {UninstallArgument} {QuietArgument}");
        key.SetValue("URLInfoAbout", $"https://github.com/{AppInfo.Repository}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExecutable).Length / 1024), RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {exception.Message}");
        }
    }

    private static void DeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not delete {path}: {exception.Message}");
        }
    }

    private sealed record TestLocations(string ProgramFolder, string ShortcutFolder, string DesktopFolder, string UninstallKeyPath, string ClassesPath);
}
