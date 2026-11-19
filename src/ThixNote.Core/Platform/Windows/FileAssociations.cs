using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ThixNote.Core.Models;

namespace ThixNote.Core.Platform.Windows;

/// <summary>
/// File types of an app for the current user, without administrator rights (SETUP-08): under HKCU\Software\Classes a ProgID
/// "&lt;Product&gt;.&lt;ext&gt;" (name, icon, "open" = the program with the file) and the app in "Öffnen mit" of the extension
/// (OpenWithProgids, Applications\&lt;exe&gt;\SupportedTypes) for EVERY type the app opens; the ProgID becomes the handler of the
/// extension only for the chosen ones (associated = opens with the app on a double click). Windows protects the choice a user
/// made himself in "Öffnen mit" (UserChoice): it stays, the app is then offered there.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class FileAssociations
{
    private const string DefaultClassesRoot = @"Software\Classes";
    private static string _classesRoot = DefaultClassesRoot;

    /// <summary>Another key instead of HKCU\Software\Classes (tests); null = the real one.</summary>
    internal static void UseClassesRootForTests(string? root) => _classesRoot = root ?? DefaultClassesRoot;

    public static string ProgId(string product, string extension) => $"{product}.{extension.TrimStart('.').ToLowerInvariant()}";

    /// <summary>
    /// Offers <paramref name="executable"/> for all <paramref name="offered"/> types and makes it the handler of the
    /// <paramref name="associate"/>d ones; of the others it gives up the handler (if it has it). <paramref name="iconOf"/> may
    /// give a type an icon of its own (DefaultIcon value), null = the program's icon.
    /// </summary>
    public static void Register(string product, string executable, IReadOnlyList<InstallerFileType> offered, IReadOnlyList<InstallerFileType> associate,
        Func<InstallerFileType, string?>? iconOf = null)
    {
        var exeName = Path.GetFileName(executable);
        var openCommand = $"\"{executable}\" \"%1\"";
        using (var application = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\Applications\{exeName}"))
        {
            application.SetValue("FriendlyAppName", product);
            using var command = application.CreateSubKey(@"shell\open\command");
            command.SetValue("", openCommand);
        }

        foreach (var type in offered)
        {
            var progId = ProgId(product, type.Extension);
            using (var key = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{progId}"))
            {
                key.SetValue("", type.Name);
                key.SetValue("FriendlyTypeName", type.Name);
            }

            using (var icon = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{progId}\DefaultIcon"))
            {
                icon.SetValue("", iconOf?.Invoke(type) ?? $"\"{executable}\",0");
            }

            using (var command = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{progId}\shell\open\command"))
            {
                command.SetValue("", openCommand);
            }

            using (var openWith = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{type.Extension}\OpenWithProgids"))
            {
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using (var supported = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\Applications\{exeName}\SupportedTypes"))
            {
                supported.SetValue(type.Extension, "");
            }

            using (var extension = Registry.CurrentUser.CreateSubKey($@"{_classesRoot}\{type.Extension}"))
            {
                if (associate.Contains(type))
                {
                    extension.SetValue("", progId);
                }
                else if (extension.GetValue("") is string handler && handler.Equals(progId, StringComparison.OrdinalIgnoreCase))
                {
                    extension.DeleteValue("", throwOnMissingValue: false);
                }
            }
        }

        NotifyShell();
    }

    /// <summary>Removes the app <paramref name="executableName"/> from these file types (what other apps registered stays).</summary>
    public static void Unregister(string product, string executableName, IEnumerable<string> extensions)
    {
        foreach (var extension in extensions)
        {
            var progId = ProgId(product, extension);
            Registry.CurrentUser.DeleteSubKeyTree($@"{_classesRoot}\{progId}", throwOnMissingSubKey: false);
            using (var openWith = Registry.CurrentUser.OpenSubKey($@"{_classesRoot}\{extension}\OpenWithProgids", writable: true))
            {
                openWith?.DeleteValue(progId, throwOnMissingValue: false);
            }

            using var key = Registry.CurrentUser.OpenSubKey($@"{_classesRoot}\{extension}", writable: true);
            if (key?.GetValue("") is string handler && handler.Equals(progId, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue("", throwOnMissingValue: false);
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree($@"{_classesRoot}\Applications\{executableName}", throwOnMissingSubKey: false);
        NotifyShell();
    }

    /// <summary>The extensions (of <paramref name="candidates"/>) that open with the app now (their handler is its ProgID).</summary>
    public static IReadOnlyList<string> Registered(string product, IEnumerable<string> candidates) =>
        candidates.Where(extension => HandlerOf(extension) is { } handler && handler.Equals(ProgId(product, extension), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The ProgID that handles the extension in the user's classes, or null.</summary>
    public static string? HandlerOf(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_classesRoot}\{extension}");
        return key?.GetValue("") as string;
    }

    /// <summary>Explorer reads the associations again (SHCNE_ASSOCCHANGED); not for the test key.</summary>
    internal static void NotifyShell()
    {
        if (_classesRoot == DefaultClassesRoot)
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
