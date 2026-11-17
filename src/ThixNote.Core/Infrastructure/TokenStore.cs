using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace ThixNote.Core.Infrastructure;

/// <summary>
/// Keeps the device token of MatCloud where only this user can read it: Windows DPAPI (the protected value lives in
/// settings.json), macOS Keychain (generic password "info.schmoldt.thixnote.cloud"). The token never goes into a log.
/// Ported from ThixClip.
/// </summary>
internal static class TokenStore
{
    private const string KeychainService = "info.schmoldt.thixnote.cloud";
    private const string TestPrefix = "test:";
    private static readonly byte[] Entropy = "ThixNote.MatCloud.DeviceToken"u8.ToArray();

    private static bool _useTestStore;

    /// <summary>Stores the token; returns the value to keep in the settings (Windows) or an empty string (Keychain).</summary>
    public static string Save(string account, string token)
    {
        if (_useTestStore)
        {
            return TestPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
        }

        if (OperatingSystem.IsWindows())
        {
            return Convert.ToBase64String(Protect(token));
        }

        if (OperatingSystem.IsMacOS())
        {
            RunSecurity("add-generic-password", "-U", "-a", account, "-s", KeychainService, "-w", token);
            return string.Empty;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(token));
    }

    public static string? Read(string account, string protectedValue)
    {
        try
        {
            if (_useTestStore)
            {
                return protectedValue.StartsWith(TestPrefix, StringComparison.Ordinal)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[TestPrefix.Length..]))
                    : null;
            }

            if (OperatingSystem.IsWindows())
            {
                return protectedValue.Length == 0 ? null : Unprotect(Convert.FromBase64String(protectedValue));
            }

            if (OperatingSystem.IsMacOS())
            {
                var token = RunSecurity("find-generic-password", "-a", account, "-s", KeychainService, "-w").Trim();
                return token.Length == 0 ? null : token;
            }

            return protectedValue.Length == 0 ? null : Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidOperationException or IOException
                                              or System.ComponentModel.Win32Exception)
        {
            Log.Warning($"The device token could not be read: {exception.GetType().Name}");
            return null;
        }
    }

    public static void Delete(string account)
    {
        if (_useTestStore || !OperatingSystem.IsMacOS())
        {
            return; // Windows: the protected value goes with the settings
        }

        try
        {
            RunSecurity("delete-generic-password", "-a", account, "-s", KeychainService);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // not there
        }
    }

    /// <summary>
    /// Tests keep the token in the settings of each test device (base64, never the Keychain or DPAPI), so that two devices in
    /// one process have their own tokens.
    /// </summary>
    internal static void UseMemoryForTests(bool enabled) => _useTestStore = enabled;

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(string token) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Unprotect(byte[] data) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));

    private static string RunSecurity(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("security could not be started");
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"security {arguments[0]} failed ({process.ExitCode})");
        }

        return output;
    }
}
