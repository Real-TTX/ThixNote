using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ThixNote.Core.Infrastructure;

namespace ThixNote.Core.Business;

/// <summary>The choices of the setup assistant; an update (null) keeps what the user has.</summary>
public sealed record InstallOptions(bool DesktopShortcut, bool AssociateSecureNotes = true, bool InstallUpdatesAutomatically = true);

/// <summary>
/// Windows only (macOS: the app bundle, <see cref="MacAppBundle"/>). ThixNote installs itself – like ThixClip – without a
/// separate installer tool. The published ThixNote.exe is the setup: started from anywhere else it copies itself to
/// %LocalAppData%\Programs\ThixNote (per user, no administrator rights), creates the Start menu entry, the entry in
/// "Installierte Apps", the file types (.txts opens with ThixNote, .md and .txt offer it under "Öffnen mit") and optionally a
/// desktop shortcut. "--uninstall" removes every one of them. The notes are never touched; the data folder
/// (%LocalAppData%\ThixNote: settings, logs) only when the user asks for it while uninstalling.
/// A former QuickNote installation (the old name of ThixNote) is removed on the way; its notes stay.
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallService
{
    public const string UpdateArgument = UpdateService.WindowsUpdateArgument;
    public const string RestartArgument = UpdateService.RestartArgument;
    public const string UninstallArgument = "--uninstall";
    public const string QuietArgument = "--quiet";
    public const string ExecutableName = "ThixNote.exe";
    public const string DocumentIconName = "ThixNote-Document.ico";

    /// <summary>A running ThixNote listens for this signal, saves its notes and ends itself.</summary>
    public const string ExitSignalName = "ThixNote.Exit";

    public const string SecureNoteProgId = "ThixNote.SecureNote";
    public const string NoteProgId = "ThixNote.Note";

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

    /// <summary>.txts opens with ThixNote (chosen in the setup).</summary>
    public static bool OpensSecureNotes
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\.txts");
            return key?.GetValue(string.Empty) as string == SecureNoteProgId;
        }
    }

    public static bool IsInstalledExecutable(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase);

    /// <summary>Installs or updates. <paramref name="options"/> null = update: shortcuts and file types stay as they are.</summary>
    public static void Install(string sourceExecutable, InstallOptions? options, Action<string> log)
    {
        var isUpdate = options is null;
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

        if (options?.DesktopShortcut ?? HasDesktopShortcut)
        {
            CreateShortcut(DesktopShortcut);
            log("Desktop-Verknüpfung angelegt");
        }

        var associateSecureNotes = options?.AssociateSecureNotes ?? OpensSecureNotes;
        RegisterFileTypes(associateSecureNotes);
        log(associateSecureNotes ? "Verschlüsselte Notizen (.txts) öffnen mit ThixNote" : "ThixNote unter „Öffnen mit“ eingetragen");

        if (options is not null)
        {
            ApplyUpdateChoice(options.InstallUpdatesAutomatically, log);
        }

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
        UnregisterFileTypes(SecureNoteProgId, NoteProgId, ExecutableName);
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
            return;
        }

        var root = @"Software\ThixNoteTests\" + Path.GetFileName(programFolder);
        _testLocations = new TestLocations(programFolder, Path.Combine(programFolder, "StartMenu"), Path.Combine(programFolder, "Desktop"),
            root + @"\Uninstall", root + @"\Classes");
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

    /// <summary>.txts opens with ThixNote (own document icon) if chosen; .md and .txt get ThixNote in "Öffnen mit" without taking them over.</summary>
    private static void RegisterFileTypes(bool associateSecureNotes)
    {
        var openCommand = $"\"{InstalledExecutable}\" \"%1\"";
        var documentIcon = Path.Combine(ProgramFolder, DocumentIconName);
        RegisterProgId(SecureNoteProgId, "ThixNote verschlüsselte Notiz", File.Exists(documentIcon) ? $"\"{documentIcon}\"" : $"\"{InstalledExecutable}\",0", openCommand);
        RegisterProgId(NoteProgId, "ThixNote Notiz", $"\"{InstalledExecutable}\",0", openCommand);

        using (var extensionKey = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\.txts"))
        {
            if (associateSecureNotes)
            {
                extensionKey.SetValue(string.Empty, SecureNoteProgId);
                extensionKey.SetValue("Content Type", "application/x-thixnote-secure");
            }
            else if (extensionKey.GetValue(string.Empty) as string == SecureNoteProgId)
            {
                extensionKey.DeleteValue(string.Empty, throwOnMissingValue: false);
            }
        }

        AddOpenWithProgId(".txts", SecureNoteProgId);
        AddOpenWithProgId(".md", NoteProgId);
        AddOpenWithProgId(".txt", NoteProgId);

        using (var applicationKey = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\Applications\{ExecutableName}"))
        {
            applicationKey.SetValue("FriendlyAppName", AppInfo.ProductName);
            using (var supportedTypes = applicationKey.CreateSubKey("SupportedTypes"))
            {
                foreach (var extension in new[] { ".md", ".txt", ".txts" })
                {
                    supportedTypes.SetValue(extension, string.Empty);
                }
            }

            using var commandKey = applicationKey.CreateSubKey(@"shell\open\command");
            commandKey.SetValue(string.Empty, openCommand);
        }

        NotifyShellAboutAssociations();
    }

    private static void UnregisterFileTypes(string secureNoteProgId, string noteProgId, string executableName)
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
        Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesPath}\Applications\{executableName}", throwOnMissingSubKey: false);
        NotifyShellAboutAssociations();
    }

    private static void RegisterProgId(string progId, string description, string icon, string openCommand)
    {
        using var typeKey = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{progId}");
        typeKey.SetValue(string.Empty, description);
        using (var iconKey = typeKey.CreateSubKey("DefaultIcon"))
        {
            iconKey.SetValue(string.Empty, icon);
        }

        using var commandKey = typeKey.CreateSubKey(@"shell\open\command");
        commandKey.SetValue(string.Empty, openCommand);
    }

    private static void AddOpenWithProgId(string extension, string progId)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\{extension}\OpenWithProgids");
        key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private static void RemoveOpenWithProgId(string extension, string progId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesPath}\{extension}\OpenWithProgids", writable: true);
        key?.DeleteValue(progId, throwOnMissingValue: false);
    }

    /// <summary>Tells Explorer to reload file icons and associations at once (not for the test locations).</summary>
    private static void NotifyShellAboutAssociations()
    {
        if (_testLocations is null)
        {
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

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

    /// <summary>"Updates automatisch installieren" of the setup; a locked configuration (policy) keeps its value.</summary>
    private static void ApplyUpdateChoice(bool installAutomatically, Action<string> log)
    {
        try
        {
            SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = installAutomatically });
            log(installAutomatically ? "Updates werden automatisch installiert" : "Updates werden nur angeboten");
        }
        catch (Models.BusinessException exception)
        {
            log(exception.Message);
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
