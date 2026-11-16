using System.Reflection;

namespace ThixNote.Core.Business;

public static class VersionInfo
{
    /// <summary>The full version string, e.g. "1.0.42-20261114", "nightly-42-20261114" or "local-20261114".</summary>
    public static string Current { get; } = ReadInformationalVersion();

    private static string ReadInformationalVersion()
    {
        var attribute = typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attribute?.InformationalVersion.Split('+')[0] ?? "local";
    }
}
