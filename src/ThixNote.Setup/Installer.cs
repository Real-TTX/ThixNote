using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace ThixNote.Setup;

public sealed record InstallOptions(string InstallPath, bool CreateDesktopShortcut, bool AssociateSecureNotes);

/// <summary>Per-user Windows installation (no admin rights required).</summary>
public static class Installer
{
    private const string AppName = "ThixNote";
    private const string ExecutableName = "ThixNote.exe";
    private const string UninstallerName = "Uninstall.exe";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ThixNote";
    private const string DocumentIconName = "ThixNote-Document.ico";
    private const string ClassesPath = @"Software\Classes";
    private const string SecureNoteProgId = "ThixNote.SecureNote";
    private const string NoteProgId = "ThixNote.Note";

    /// <summary>ThixNote was called QuickNote before; its installation is replaced (the notes stay, the app takes them over).</summary>
    private const string FormerAppName = "QuickNote";
    private const string FormerExecutableName = "QuickNote.exe";
    private const string FormerUninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuickNote";

    public static string Version { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";

    public static string DefaultInstallPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains("payload.zip");

    public static string ReadInstalledPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("InstallLocation") as string ?? DefaultInstallPath;
    }

    public static async Task InstallAsync(InstallOptions options, IProgress<(double Percent, string Message)> progress)
    {
        await Task.Run(() =>
        {
            progress.Report((0.05, "Vorbereiten …"));
            StopRunningInstances(options.InstallPath);
            RemoveFormerInstallation();

            progress.Report((0.15, "Dateien kopieren …"));
            ExtractPayload(options.InstallPath, progress);

            progress.Report((0.80, "Deinstallation einrichten …"));
            var uninstallerPath = Path.Combine(options.InstallPath, UninstallerName);
            File.Copy(Environment.ProcessPath!, uninstallerPath, overwrite: true);
            RegisterUninstaller(options.InstallPath, uninstallerPath);

            progress.Report((0.88, "Verknüpfungen erstellen …"));
            var executablePath = Path.Combine(options.InstallPath, ExecutableName);
            CreateShortcut(StartMenuShortcutPath(AppName), executablePath);
            if (options.CreateDesktopShortcut)
            {
                CreateShortcut(DesktopShortcutPath(AppName), executablePath);
            }

            progress.Report((0.95, "Dateitypen registrieren …"));
            RegisterFileTypes(options.InstallPath, options.AssociateSecureNotes);

            progress.Report((1.0, "Fertig"));
        });
    }

    public static async Task UninstallAsync(string installPath)
    {
        await Task.Run(() =>
        {
            StopRunningInstances(installPath);
            File.Delete(StartMenuShortcutPath(AppName));
            File.Delete(DesktopShortcutPath(AppName));
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
            UnregisterFileTypes(SecureNoteProgId, NoteProgId, ExecutableName);
            ScheduleFolderRemoval(installPath);
        });
    }

    public static void StartApplication(string installPath)
        => Process.Start(new ProcessStartInfo(Path.Combine(installPath, ExecutableName)) { UseShellExecute = true });

    private static string StartMenuShortcutPath(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), appName + ".lnk");

    private static string DesktopShortcutPath(string appName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), appName + ".lnk");

    /// <summary>Removes a QuickNote installation (program files, shortcuts, registry). Notes and settings are left alone.</summary>
    private static void RemoveFormerInstallation()
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

        StopRunningInstances(formerPath, FormerExecutableName);
        File.Delete(StartMenuShortcutPath(FormerAppName));
        File.Delete(DesktopShortcutPath(FormerAppName));
        UnregisterFileTypes("QuickNote.SecureNote", "QuickNote.Note", FormerExecutableName);
        Registry.CurrentUser.DeleteSubKeyTree(FormerUninstallKeyPath, throwOnMissingSubKey: false);

        // Only delete the folder if it really is a QuickNote installation.
        if (File.Exists(Path.Combine(formerPath, FormerExecutableName)))
        {
            try
            {
                Directory.Delete(formerPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Leftover files do no harm; shortcuts and registry entries are already gone.
            }
        }
    }

    private static void ExtractPayload(string installPath, IProgress<(double Percent, string Message)> progress)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("Dieses Setup enthält keine Programmdateien.");
        using var archive = new ZipArchive(payload);

        // Only ever replace an existing ThixNote installation or an empty folder, never foreign data.
        if (Directory.Exists(installPath) && Directory.EnumerateFileSystemEntries(installPath).Any())
        {
            if (!File.Exists(Path.Combine(installPath, ExecutableName)))
            {
                throw new InvalidOperationException("Der gewählte Ordner ist nicht leer und enthält keine ThixNote-Installation. Bitte einen leeren Ordner wählen.");
            }

            Directory.Delete(installPath, recursive: true);
        }

        Directory.CreateDirectory(installPath);
        var fullInstallPath = Path.GetFullPath(installPath) + Path.DirectorySeparatorChar;
        var fileCount = archive.Entries.Count;
        for (var index = 0; index < fileCount; index++)
        {
            var entry = archive.Entries[index];
            var targetPath = Path.GetFullPath(Path.Combine(installPath, entry.FullName));
            if (!targetPath.StartsWith(fullInstallPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Ungültiger Pfad im Setup-Archiv.");
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
            progress.Report((0.15 + 0.6 * (index + 1) / fileCount, $"Kopiere {entry.Name} …"));
        }
    }

    private static void RegisterUninstaller(string installPath, string uninstallerPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Matthias");
        key.SetValue("InstallLocation", installPath);
        key.SetValue("DisplayIcon", Path.Combine(installPath, ExecutableName));
        key.SetValue("UninstallString", $"\"{uninstallerPath}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>.txts opens with ThixNote (own document icon); .md and .txt get ThixNote in "Open with" without taking them over.</summary>
    private static void RegisterFileTypes(string installPath, bool associateSecureNotes)
    {
        var executablePath = Path.Combine(installPath, ExecutableName);
        var openCommand = $"\"{executablePath}\" \"%1\"";

        RegisterProgId(SecureNoteProgId, "ThixNote verschlüsselte Notiz", $"\"{Path.Combine(installPath, DocumentIconName)}\"", openCommand);
        RegisterProgId(NoteProgId, "ThixNote Notiz", $"\"{executablePath}\",0", openCommand);

        if (associateSecureNotes)
        {
            using var extensionKey = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\.txts");
            extensionKey.SetValue(string.Empty, SecureNoteProgId);
            extensionKey.SetValue("Content Type", "application/x-thixnote-secure");
        }

        AddOpenWithProgId(".txts", SecureNoteProgId);
        AddOpenWithProgId(".md", NoteProgId);
        AddOpenWithProgId(".txt", NoteProgId);

        using (var applicationKey = Registry.CurrentUser.CreateSubKey($@"{ClassesPath}\Applications\{ExecutableName}"))
        {
            applicationKey.SetValue("FriendlyAppName", AppName);
            using var supportedTypes = applicationKey.CreateSubKey("SupportedTypes");
            foreach (var extension in new[] { ".md", ".txt", ".txts" })
            {
                supportedTypes.SetValue(extension, string.Empty);
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

    /// <summary>Tells Explorer to reload file icons and associations immediately.</summary>
    private static void NotifyShellAboutAssociations() => SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Escape(shortcutPath)}');" +
                     $"$s.TargetPath='{Escape(targetPath)}';$s.WorkingDirectory='{Escape(Path.GetDirectoryName(targetPath)!)}';" +
                     $"$s.IconLocation='{Escape(targetPath)},0';$s.Description='ThixNote – schnell notieren';$s.Save()";
        RunHidden("powershell", $"-NoProfile -NonInteractive -Command \"{script}\"");
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static void StopRunningInstances(string installPath, string executableName = ExecutableName)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executableName)))
        {
            try
            {
                if (process.MainModule?.FileName.StartsWith(installPath, StringComparison.OrdinalIgnoreCase) == true)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process already exited or is inaccessible; nothing to stop.
            }
        }
    }

    /// <summary>The running uninstaller lives inside the folder, so the folder is removed after this process exits.</summary>
    private static void ScheduleFolderRemoval(string installPath)
        => RunHidden("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{installPath}\"", wait: false);

    private static void RunHidden(string fileName, string arguments, bool wait = true)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments) { CreateNoWindow = true, UseShellExecute = false };
        using var process = Process.Start(startInfo);
        if (wait)
        {
            process?.WaitForExit();
        }
    }
}
