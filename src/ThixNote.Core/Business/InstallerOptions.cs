using System.Text.Json;
using ThixNote.Core.Infrastructure;
using ThixNote.Core.Models;

namespace ThixNote.Core.Business;

/// <summary>
/// The install options of ThixNote (SETUP-08 of MatDesign) – one contract for every Thix app:
/// <list type="bullet">
/// <item><see cref="Config"/>: what the setup offers, from <c>installer.json</c> (embedded; CI puts the same file into the update
/// manifest as <c>"installer"</c>, where ThixLauncher and ThixInstall read it): desktop shortcut, automatic updates and the
/// file types .md, .txt and .txts. ThixNote has no tray, so it offers no autostart.</item>
/// <item><see cref="Parse"/>: the command line of SETUP-05 – <c>--quiet</c> (install), <c>--update</c>, <c>--configure</c>
/// (only apply), <c>--uninstall</c>, <c>--start</c>, and the options <c>--desktop-shortcut=yes|no</c>,
/// <c>--automatic-updates=yes|no</c>, <c>--file-types=.md,.txt|none|default</c>. A wrong value or an option ThixNote does not
/// offer (<c>--autostart=…</c>) fails at once (exit code 2).</item>
/// <item><see cref="Current"/> and <see cref="Apply"/>: what the installed app has, and making it so (shortcut, automatic
/// updates, file types – Windows per user in HKCU\Software\Classes, macOS with Launch Services).</item>
/// </list>
/// </summary>
public static class InstallerOptions
{
    public const string QuietArgument = "--quiet";
    public const string UpdateArgument = UpdateService.WindowsUpdateArgument;
    public const string ConfigureArgument = "--configure";
    public const string UninstallArgument = "--uninstall";
    public const string StartArgument = "--start";
    public const string DesktopShortcutOption = "--desktop-shortcut";
    public const string AutostartOption = "--autostart";
    public const string AutomaticUpdatesOption = "--automatic-updates";
    public const string FileTypesOption = "--file-types";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static InstallerConfig? _config;

    /// <summary>What the setup of ThixNote offers (installer.json).</summary>
    public static InstallerConfig Config => _config ??= Load();

    /// <summary>The choices of a first install that is told nothing: the defaults of <see cref="Config"/>.</summary>
    public static InstallChoices Defaults => new(
        Config.DesktopShortcut?.Default ?? false,
        Config.Autostart?.Default ?? false,
        Config.AutomaticUpdates?.Default ?? false,
        Config.FileTypes.Where(type => type.Default).Select(type => type.Extension).ToList());

    /// <summary>Another configuration (tests); null = installer.json again.</summary>
    internal static void UseConfigForTests(InstallerConfig? config) => _config = config;

    /// <summary>
    /// Reads the setup part of a command line. Throws <see cref="BusinessException"/> for a value it does not know – a quiet
    /// install must not guess (SETUP-05). Arguments without "=" (a note to open, "--restart") are none of its business.
    /// </summary>
    public static SetupRequest Parse(IReadOnlyList<string> args)
    {
        var action = args.Contains(UninstallArgument) ? SetupAction.Uninstall
            : args.Contains(ConfigureArgument) ? SetupAction.Configure
            : args.Contains(UpdateArgument) ? SetupAction.Update
            : args.Contains(QuietArgument) ? SetupAction.Install
            : SetupAction.None;
        var choices = new InstallChoices();
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            if (!arg.StartsWith("--", StringComparison.Ordinal) || separator < 0)
            {
                continue;
            }

            var (name, value) = (arg[..separator], arg[(separator + 1)..].Trim());
            choices = name switch
            {
                DesktopShortcutOption => choices with { DesktopShortcut = Switch(name, value, Config.DesktopShortcut) },
                AutostartOption => choices with { Autostart = Switch(name, value, Config.Autostart) },
                AutomaticUpdatesOption => choices with { AutomaticUpdates = Switch(name, value, Config.AutomaticUpdates) },
                FileTypesOption => choices with { FileTypes = FileTypes(value) },
                _ => throw new BusinessException($"Unbekannte Option „{name}“. Möglich: {string.Join(", ", OfferedOptions())}."),
            };
        }

        if (action is SetupAction.None or SetupAction.Uninstall && choices != new InstallChoices())
        {
            throw new BusinessException($"Optionen gelten nur mit {QuietArgument}, {UpdateArgument} oder {ConfigureArgument}.");
        }

        return new SetupRequest(action, choices, args.Contains(QuietArgument), args.Contains(StartArgument));
    }

    /// <summary>The arguments of SETUP-05 for these choices (what ThixLauncher passes; tests and documentation).</summary>
    public static IReadOnlyList<string> ToArguments(InstallChoices choices)
    {
        var arguments = new List<string>();
        static string YesNo(bool value) => value ? "yes" : "no";
        if (choices.DesktopShortcut is { } shortcut)
        {
            arguments.Add($"{DesktopShortcutOption}={YesNo(shortcut)}");
        }

        if (choices.Autostart is { } autostart)
        {
            arguments.Add($"{AutostartOption}={YesNo(autostart)}");
        }

        if (choices.AutomaticUpdates is { } updates)
        {
            arguments.Add($"{AutomaticUpdatesOption}={YesNo(updates)}");
        }

        if (choices.FileTypes is { } types)
        {
            arguments.Add($"{FileTypesOption}={(types.Count == 0 ? "none" : string.Join(',', types))}");
        }

        return arguments;
    }

    /// <summary>What the installed ThixNote has now (an update keeps it). File types: Windows only (macOS cannot tell).</summary>
    public static InstallChoices Current() => new(
        OperatingSystem.IsWindows() && InstallService.HasDesktopShortcut,
        null,
        SettingsService.Get().InstallUpdatesAutomatically,
        OperatingSystem.IsWindows() ? InstallService.AssociatedFileTypes() : null);

    /// <summary>
    /// Makes the choices so for the program <paramref name="executable"/> (the installed one). Only offered options are
    /// touched; a value of null stays as it is. Each step is reported to <paramref name="log"/>.
    /// </summary>
    public static void Apply(InstallChoices choices, string executable, Action<string> log)
    {
        if (Config.DesktopShortcut is not null && choices.DesktopShortcut is { } shortcut && OperatingSystem.IsWindows())
        {
            InstallService.SetDesktopShortcut(shortcut);
            log(shortcut ? "Desktop-Verknüpfung angelegt" : "Keine Desktop-Verknüpfung");
        }

        // ThixNote offers no autostart (no tray): Parse refuses "--autostart=…", installer.json has none.

        if (Config.AutomaticUpdates is not null && choices.AutomaticUpdates is { } updates)
        {
            try
            {
                if (SettingsService.Get().InstallUpdatesAutomatically != updates)
                {
                    SettingsService.Update(settings => settings with { InstallUpdatesAutomatically = updates });
                }

                log(updates ? "Updates werden automatisch installiert" : "Updates werden nur angeboten");
            }
            catch (BusinessException exception)
            {
                // a configuration locked by policy keeps its value
                log(exception.Message);
            }
        }

        if (Config.FileTypes.Count > 0 && choices.FileTypes is { } wanted)
        {
            var associate = Config.FileTypes.Where(type => wanted.Contains(type.Extension, StringComparer.OrdinalIgnoreCase)).ToList();
            if (OperatingSystem.IsWindows())
            {
                // every type ThixNote opens stays under "Öffnen mit"; the chosen ones open with it on a double click
                var documentIcon = Path.Combine(Path.GetDirectoryName(executable) ?? "", InstallService.DocumentIconName);
                Platform.Windows.FileAssociations.Register(AppInfo.ProductName, executable, Config.FileTypes, associate,
                    type => type.Extension == NoteLibrary.EncryptedExtension && File.Exists(documentIcon) ? $"\"{documentIcon}\"" : null);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Platform.Mac.MacFileAssociations.MakeDefault(MacAppBundle.BundleId, associate.Select(type => type.Extension));
            }

            log(associate.Count == 0 ? "Keine Dateitypen verknüpft" : $"Öffnen mit ThixNote: {string.Join(", ", associate.Select(type => type.Extension))}");
        }
    }

    /// <summary>Removes the file types of ThixNote (uninstall).</summary>
    public static void RemoveFileTypes()
    {
        if (OperatingSystem.IsWindows() && Config.FileTypes.Count > 0)
        {
            Platform.Windows.FileAssociations.Unregister(AppInfo.ProductName, InstallService.ExecutableName, Config.FileTypes.Select(type => type.Extension));
        }
    }

    private static IEnumerable<string> OfferedOptions()
    {
        if (Config.DesktopShortcut is not null)
        {
            yield return DesktopShortcutOption;
        }

        if (Config.Autostart is not null)
        {
            yield return AutostartOption;
        }

        if (Config.AutomaticUpdates is not null)
        {
            yield return AutomaticUpdatesOption;
        }

        if (Config.FileTypes.Count > 0)
        {
            yield return FileTypesOption;
        }
    }

    private static bool Switch(string name, string value, InstallerSwitch? offered)
    {
        if (offered is null)
        {
            throw new BusinessException($"{AppInfo.ProductName} bietet „{name}“ nicht an.");
        }

        return value.ToLowerInvariant() switch
        {
            "yes" or "true" or "1" or "ja" => true,
            "no" or "false" or "0" or "nein" => false,
            _ => throw new BusinessException($"{name} erwartet yes oder no, nicht „{value}“."),
        };
    }

    private static IReadOnlyList<string> FileTypes(string value)
    {
        if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return Defaults.FileTypes!;
        }

        if (value.Length == 0 || value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var types = new List<string>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var extension = part.StartsWith('.') ? part : "." + part;
            var offered = Config.FileTypes.FirstOrDefault(type => type.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase))
                          ?? throw new BusinessException(Config.FileTypes.Count == 0
                              ? $"{AppInfo.ProductName} verknüpft keine Dateitypen."
                              : $"{AppInfo.ProductName} verknüpft „{extension}“ nicht. Möglich: {string.Join(", ", Config.FileTypes.Select(type => type.Extension))}.");
            if (!types.Contains(offered.Extension))
            {
                types.Add(offered.Extension);
            }
        }

        return types;
    }

    private static InstallerConfig Load()
    {
        using var stream = typeof(InstallerOptions).Assembly.GetManifestResourceStream("installer.json")
                           ?? throw new InvalidOperationException("installer.json is not embedded");
        return JsonSerializer.Deserialize<InstallerConfig>(stream, JsonOptions) ?? new InstallerConfig();
    }
}
