using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using MatDesktop;
using ThixNote.Core.Infrastructure;

namespace ThixNote;

/// <summary>
/// Small helpers for code-built UI: the app icon, logo and word mark, the UI thread, opening folders and addresses, texts
/// of times. Colours, sizes and named looks are MatDesktop's (<c>control.Use(property, "Mat.TextMuted")</c>,
/// <c>control.Themed("MatButton.Primary")</c>), icons are <c>MatIcon</c> with the name of the sprite.
/// </summary>
internal static class Ui
{
    private static WindowIcon? _appIcon;
    private static Bitmap? _logo;

    /// <summary>The icon of every window of ThixNote (the task bar, Alt+Tab): MatThemeOptions.Icon gives it to every MatWindow.</summary>
    public static WindowIcon AppIcon => _appIcon ??= new WindowIcon(AssetLoader.Open(new Uri("avares://ThixNote/Assets/thixnote.ico")));

    /// <summary>The logo (the mark of the app), for the title bar, About and the header of the setup.</summary>
    public static Bitmap Logo => _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://ThixNote/Assets/logo.png")));

    /// <summary>The word mark: "Thix" and "Note" in the accent text (title bar, setup, About).</summary>
    public static TextBlock WordMark(double? fontSize = null)
    {
        var mark = new TextBlock { FontSize = fontSize ?? MatTokens.Type.FsXl, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var note = new Run("Note");
        note.Bind(TextElement.ForegroundProperty, mark.GetResourceObservable("Mat.AccentText"));
        mark.Inlines = [new Run("Thix"), note];
        return mark;
    }

    /// <summary>A named look of MatDesktop (a control theme such as "MatButton.Primary") for an object initializer.</summary>
    public static ControlTheme Theme(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is ControlTheme theme
            ? theme
            : throw new InvalidOperationException($"The control theme {key} is missing (MatDesktop/Themes/Controls.axaml).");

    /// <summary>Runs the action on the UI thread (events of the business layer come from any thread).</summary>
    public static void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <summary>The window that hosts a control (null while it is not shown).</summary>
    public static Window? WindowOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    /// <summary>"Strg" on Windows, "⌘" on macOS: the modifier of the app's own shortcuts, as the tooltips name it.</summary>
    public static string CommandKey => OperatingSystem.IsMacOS() ? "⌘" : "Strg";

    /// <summary>A shortcut as the tooltips name it: "Strg+N" / "⌘N".</summary>
    public static string Shortcut(string key, bool shift = false) => OperatingSystem.IsMacOS()
        ? $"⌘{(shift ? "⇧" : "")}{key}"
        : $"Strg+{(shift ? "Umschalt+" : "")}{key}";

    /// <summary>Puts text into the clipboard of the window's platform.</summary>
    public static void CopyToClipboard(Visual visual, string text) => _ = TopLevel.GetTopLevel(visual)?.Clipboard?.SetTextAsync(text);

    /// <summary>Opens a web address or a folder with the program the system uses for it (browser, Explorer, Finder).</summary>
    public static void OpenExternal(string pathOrUrl)
    {
        try
        {
            var startInfo = OperatingSystem.IsMacOS()
                ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                : OperatingSystem.IsLinux()
                    ? new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false }
                    : new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true };
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {pathOrUrl}: {exception.Message}");
        }
    }

    /// <summary>Shows a file in Explorer (selected) or Finder.</summary>
    public static void RevealFile(string path)
    {
        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : OperatingSystem.IsMacOS()
                    ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                    : new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } };
            startInfo.UseShellExecute = false;
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not show {path}: {exception.Message}");
        }
    }

    /// <summary>"14:28" / "Gestern, 17:56" / "12.11., 09:14" / "12.11.2025" (the list of notes).</summary>
    public static string FormatTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = AppClock.Today;
        if (local.Date == today)
        {
            return local.ToString("HH:mm");
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Gestern, {local:HH:mm}";
        }

        return local.Year == today.Year ? local.ToString("dd.MM., HH:mm") : local.ToString("dd.MM.yyyy");
    }

    /// <summary>"Heute, 13:47" or "12.11.2026, 13:47".</summary>
    public static string FormatDateTime(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        if (local.Date == AppClock.Today)
        {
            return $"Heute, {local:HH:mm}";
        }

        return local.Date == AppClock.Today.AddDays(-1) ? $"Gestern, {local:HH:mm}" : local.ToString("dd.MM.yyyy, HH:mm");
    }
}
