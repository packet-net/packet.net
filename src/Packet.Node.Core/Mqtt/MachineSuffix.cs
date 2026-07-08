using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace Packet.Node.Core.Mqtt;

/// <summary>
/// A stable 8-hex-char per-machine token, ported 1:1 from the Go head-end's <c>machineSuffix</c>
/// (<c>headend/config.go</c>) so both daemons derive machine identity the same way: hash the first
/// readable, non-empty machine-id file (<c>/etc/machine-id</c>, then the D-Bus copy); failing that,
/// the first non-loopback NIC MAC (sorted by interface name so the pick is deterministic across
/// reboots); failing that, warn and return a fixed literal. The value source is domain-tagged before
/// hashing (<c>machine-id:</c> / <c>mac:</c>) so a machine-id can never collide with a MAC.
/// Deterministic across reboots yet distinct across image-cloned machines — two Pis flashed from one
/// image (both hostname <c>raspberrypi</c>) carry different machine-ids, so they don't collide.
/// </summary>
public static class MachineSuffix
{
    /// <summary>The machine-id files read (in order): systemd's per-install id, then the D-Bus copy
    /// on hosts without the systemd one. Mirrors the head-end's <c>machineIDFiles</c>.</summary>
    internal static readonly string[] MachineIdFiles = ["/etc/machine-id", "/var/lib/dbus/machine-id"];

    /// <summary>The last-resort token when neither a machine-id file nor a NIC MAC is available.
    /// Deliberately NOT unique — reaching it is a (warned) signal that the operator should pin an
    /// explicit identity. Mirrors the head-end's <c>fallbackMachineToken</c>.</summary>
    internal const string FallbackToken = "nomachineid";

    private static readonly Lazy<string> Cached = new(
        () => Compute(MachineIdFiles, ReadFileOrNull, NicMacs, warn: null),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The real host's suffix, computed once per process.</summary>
    public static string Value => Cached.Value;

    /// <summary>
    /// Pure core, with the file reader / MAC source / warn sink injected so tests never read the real
    /// host. <paramref name="readFile"/> returns the file's text or null when unreadable;
    /// <paramref name="warn"/> (optional) fires only on the last-resort literal fallback.
    /// </summary>
    internal static string Compute(
        IReadOnlyList<string> paths,
        Func<string, string?> readFile,
        Func<IReadOnlyList<string>> macs,
        Action<string>? warn)
    {
        foreach (var path in paths)
        {
            if (readFile(path)?.Trim() is { Length: > 0 } id)
            {
                return ShortHash("machine-id:" + id);
            }
        }
        foreach (var mac in Safe(macs))
        {
            if (mac?.Trim() is { Length: > 0 } m)
            {
                return ShortHash("mac:" + m);
            }
        }
        warn?.Invoke(
            "could not derive a stable machine id (no machine-id file, no NIC MAC); " +
            "using a fixed fallback suffix — two same-named nodes on one broker will collide.");
        return FallbackToken;
    }

    /// <summary>The first 8 hex chars (the leading 4 bytes) of SHA-256(<paramref name="s"/>) —
    /// byte-for-byte the head-end's <c>shortHash</c>.</summary>
    internal static string ShortHash(string s)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8];

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Hardware addresses of non-loopback interfaces that have one, sorted by interface
    /// name so the "first" pick is deterministic across reboots (the head-end's <c>nicMACs</c>).</summary>
    private static IReadOnlyList<string> NicMacs()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => i.GetPhysicalAddress().ToString())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToArray();

    private static IReadOnlyList<string> Safe(Func<IReadOnlyList<string>> macs)
    {
        try
        {
            return macs();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
