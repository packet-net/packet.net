using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A serial port the host believes may host a NinoTNC. <see cref="PortName"/>
/// is what you pass to <see cref="NinoTncSerialPort.Open(string,int,byte)"/>.
/// </summary>
/// <param name="PortName">
/// Cross-platform open string: a stable <c>/dev/serial/by-id/...</c> path on
/// Linux when available, otherwise <c>/dev/ttyACM*</c>, otherwise the
/// platform's COM name.
/// </param>
/// <param name="ResolvedDevicePath">
/// The kernel device path the friendly name resolves to (e.g. <c>/dev/ttyACM0</c>),
/// or <c>null</c> on platforms where the friendly name *is* the device.
/// </param>
public readonly record struct NinoTncCandidatePort(string PortName, string? ResolvedDevicePath);

/// <summary>
/// Cross-platform NinoTNC USB-CDC port discovery.
/// </summary>
/// <remarks>
/// <para>
/// On Linux we prefer <c>/dev/serial/by-id/</c> entries — those are stable
/// across reboots and across re-plug ordering. We then fall back to raw
/// <c>/dev/ttyACM*</c>. On Windows / macOS we return <see cref="SerialPort.GetPortNames"/>
/// unchanged; further USB VID/PID filtering is left to a future revision
/// when we know enough firmware variants to match safely without false
/// negatives (the NinoTNC's USB descriptor identifies a generic Microchip
/// USB-CDC ACM, shared with many other hardware projects).
/// </para>
/// <para>
/// This is a discovery helper, not a probe — it does not open the port or
/// attempt protocol negotiation. Callers wanting "is this *really* a NinoTNC"
/// should open the candidate, optionally <c>SETHW</c> a known mode, and
/// invite the operator to press the TX-Test button to elicit a TX-Test
/// diagnostic frame (parsed via <see cref="NinoTncTxTestFrame"/>).
/// </para>
/// </remarks>
public static class NinoTncPortDiscovery
{
    /// <summary>
    /// Environment variable consulted before platform enumeration. Set it to
    /// a comma-separated list of port names (e.g. <c>"COM6,COM8"</c> on
    /// Windows or <c>"/dev/ttyACM0,/dev/ttyACM1"</c> on Linux) to bypass
    /// auto-discovery — useful on dev boxes where unrelated USB-CDC devices
    /// (modem cards, virtual COM ports) look indistinguishable from a
    /// NinoTNC at the VID/PID-less enumeration layer.
    /// </summary>
    public const string PortsEnvVar = "PACKETNET_NINOTNC_PORTS";

    /// <summary>
    /// USB VID/PID pairs the NinoTNC has been observed to present as. The
    /// stock firmware uses Microchip's USB-CDC reference (04D8:00DD) which
    /// is shared with many small Microchip-based projects — match is
    /// best-effort, not exclusive. The TX-Test diagnostic frame is the
    /// authoritative "this is definitely a NinoTNC" probe.
    /// </summary>
    public static readonly IReadOnlyCollection<(ushort Vid, ushort Pid)> KnownVidPids =
        new[] { ((ushort)0x04D8, (ushort)0x00DD) };

    /// <summary>
    /// Return every serial port the host believes could be a NinoTNC. If the
    /// <see cref="PortsEnvVar"/> environment variable is set, its comma-
    /// separated entries win — auto-discovery is skipped.
    /// </summary>
    public static IReadOnlyList<NinoTncCandidatePort> EnumerateCandidates()
    {
        var explicitPorts = Environment.GetEnvironmentVariable(PortsEnvVar);
        if (!string.IsNullOrWhiteSpace(explicitPorts))
        {
            return explicitPorts
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new NinoTncCandidatePort(name, null))
                .ToList();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return EnumerateLinux();
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var matched = EnumerateWindowsByVidPid();
            return matched.Count > 0 ? matched : EnumerateGeneric();
        }
        return EnumerateGeneric();
    }

    private static List<NinoTncCandidatePort> EnumerateLinux()
    {
        var results = new List<NinoTncCandidatePort>();
        const string byId = "/dev/serial/by-id";
        if (Directory.Exists(byId))
        {
            foreach (var link in Directory.EnumerateFileSystemEntries(byId).OrderBy(p => p, StringComparer.Ordinal))
            {
                string? resolved = null;
                try
                {
                    resolved = File.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName;
                }
                catch
                {
                    // Unreadable symlink → still surface the by-id path.
                }
                results.Add(new NinoTncCandidatePort(link, resolved));
            }
            if (results.Count > 0)
            {
                return results;
            }
        }

        // Fall back to raw /dev/ttyACM* (some minimal udev configs skip by-id).
        if (Directory.Exists("/dev"))
        {
            foreach (var dev in Directory.EnumerateFiles("/dev", "ttyACM*").OrderBy(p => p, StringComparer.Ordinal))
            {
                results.Add(new NinoTncCandidatePort(dev, dev));
            }
        }
        return results;
    }

    private static List<NinoTncCandidatePort> EnumerateGeneric()
    {
        return SerialPort.GetPortNames()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new NinoTncCandidatePort(p, null))
            .ToList();
    }

    /// <summary>
    /// Walk the Windows registry's USB device tree under
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB\</c>, find subkeys whose
    /// name matches a <see cref="KnownVidPids"/> pair, and for each
    /// matching device instance read its <c>Device Parameters\PortName</c>
    /// value. Returns the corresponding COM port names.
    /// </summary>
    /// <remarks>
    /// No <c>System.Management</c> / WMI dependency — uses
    /// <see cref="Microsoft.Win32.Registry"/> from the BCL. Failures
    /// (locked-down hosts, missing branches) fall back to the generic
    /// enumeration via the caller.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static List<NinoTncCandidatePort> EnumerateWindowsByVidPid()
    {
        var results = new List<NinoTncCandidatePort>();
        try
        {
            using var usbRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbRoot is null)
            {
                return results;
            }

            var matchPrefixes = KnownVidPids
                .Select(vp => $"VID_{vp.Vid:X4}&PID_{vp.Pid:X4}")
                .ToArray();

            foreach (var deviceKeyName in usbRoot.GetSubKeyNames())
            {
                if (!matchPrefixes.Any(p => deviceKeyName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                using var deviceKey = usbRoot.OpenSubKey(deviceKeyName);
                if (deviceKey is null) continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var instance = deviceKey.OpenSubKey(instanceName);
                    using var deviceParams = instance?.OpenSubKey("Device Parameters");
                    var portName = deviceParams?.GetValue("PortName") as string;
                    if (!string.IsNullOrWhiteSpace(portName))
                    {
                        results.Add(new NinoTncCandidatePort(portName, null));
                    }
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            // Locked-down host. Fall through; caller will retry with generic.
        }
        catch (UnauthorizedAccessException)
        {
            // Same — registry node visible but read-denied.
        }

        return results
            .OrderBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
