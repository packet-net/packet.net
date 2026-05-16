using System.Reflection;

namespace Packet.Term;

/// <summary>
/// Build / version banner constants. Read from the assembly's
/// informational version so the welcome message stays in sync with the
/// version the build is stamped with.
/// </summary>
internal static class AppInfo
{
    /// <summary>Human-readable version string, e.g. <c>"0.1.0"</c>.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AppInfo).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
}
