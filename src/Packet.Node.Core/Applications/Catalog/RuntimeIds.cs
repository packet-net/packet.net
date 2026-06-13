using System.Runtime.InteropServices;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The runtime ids the catalog pins artifacts for: <c>linux-x64</c>, <c>linux-arm64</c>,
/// <c>linux-arm</c>. pdn ships Linux-only, so the RID is just the CPU architecture mapped to
/// the .NET RID spelling.
/// </summary>
public static class RuntimeIds
{
    /// <summary><c>linux-x64</c> — Intel/AMD 64-bit.</summary>
    public const string LinuxX64 = "linux-x64";

    /// <summary><c>linux-arm64</c> — ARM 64-bit (Pi 4/5 in 64-bit mode, etc.).</summary>
    public const string LinuxArm64 = "linux-arm64";

    /// <summary><c>linux-arm</c> — ARM 32-bit (armhf; older Pi / 32-bit OS).</summary>
    public const string LinuxArm = "linux-arm";

    /// <summary>
    /// The runtime id this process is running on, derived from
    /// <see cref="RuntimeInformation.OSArchitecture"/>. Returns one of <see cref="LinuxX64"/>,
    /// <see cref="LinuxArm64"/>, <see cref="LinuxArm"/>; for an architecture outside that set
    /// it falls back to <see cref="LinuxX64"/> (the catalog only pins these three).
    /// </summary>
    public static string Current() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => LinuxX64,
        Architecture.Arm64 => LinuxArm64,
        Architecture.Arm => LinuxArm,
        _ => LinuxX64,
    };
}
