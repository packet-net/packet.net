using System.IO.Ports;
using System.Runtime.InteropServices;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;
using Packet.Node.Core.Rigs;

namespace Packet.Node.Core.Modems;

/// <summary>
/// The production <see cref="IModemScanner"/>: enumerates the machine's candidate serial devices,
/// resolves each one's <c>/dev/serial/by-id</c> symlink (<see cref="SerialByIdResolver"/>), marks
/// the ones the current config already claims (<see cref="ClaimedSerialDevices"/>), and - for the
/// free ones that could plausibly be a NinoTNC - opens the port and asks the modem who it is
/// (GETVER). A device that answers is a NinoTNC beyond doubt, with its firmware version; one that
/// does not is still offered, just unidentified (a generic KISS TNC has no identify handshake).
/// </summary>
/// <remarks>
/// <para><b>What gets probed, and why not everything.</b> Opening a serial port and writing to it
/// is not free of consequence: a rig's CAT port or a GPS on <c>/dev/ttyUSB0</c> should not be
/// poked by a device picker. So the probe is limited to devices that could actually be a NinoTNC:
/// the USB VID/PID matches Microchip's CDC reference (which the NinoTNC firmware presents), or -
/// where the USB ids cannot be read - the device is CDC-ACM class (<c>/dev/ttyACM*</c>), which is
/// how a NinoTNC always enumerates. Claimed devices are never probed: something is using them.</para>
/// <para><b>Bounded and single-flight</b>, like the rig and radio scanners: two wizard tabs share
/// one pass, each probe has its own short ceiling, and the whole scan has an outer one - a wedged
/// device returns a partial list rather than hanging the request.</para>
/// </remarks>
public sealed class ModemScanner : IModemScanner, IDisposable
{
    /// <summary>Comma/semicolon/colon-separated list of devices to consider INSTEAD of enumerating
    /// <c>/dev</c> - mirrors <c>PACKETNET_RIG_PORTS</c> / <c>PACKETNET_TAIT_PORTS</c>.</summary>
    public const string PortsOverrideEnvVar = "PACKETNET_MODEM_PORTS";

    /// <summary>Kind string for a device that answered the NinoTNC identify.</summary>
    public const string NinoTncKind = "nino-tnc";

    /// <summary>Kind string for a serial port that was not identified. It may still be a KISS TNC -
    /// generic KISS has nothing to answer with.</summary>
    public const string SerialKind = "serial";

    /// <summary>Per-device ceiling on the identify exchange (open + GETVER + reply).</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Outer ceiling on a whole scan. Generous enough for a handful of devices at
    /// <see cref="DefaultProbeTimeout"/> each, tight enough that the wizard never appears to hang.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly SerialByIdResolver byId;
    private readonly Func<string, CancellationToken, Task<string>> identify;
    private readonly Func<IReadOnlyList<string>> enumerate;
    private readonly Func<string, bool> probeCandidate;
    private readonly TimeSpan probeTimeout;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim single = new(1, 1);

    /// <summary>
    /// Build the scanner. Every collaborator is injectable so the whole thing is testable off real
    /// hardware; null takes the production behaviour described on the type.
    /// </summary>
    /// <param name="byId">udev by-id resolver; null uses the standard <c>/dev/serial/by-id</c>.</param>
    /// <param name="identify">Opens the device and returns the NinoTNC firmware version, or throws.
    /// Null uses the real serial GETVER exchange.</param>
    /// <param name="enumerate">Candidate device paths; null uses <see cref="EnumerateCandidateDevices"/>.</param>
    /// <param name="probeCandidate">Whether a device is worth a NinoTNC identify; null uses
    /// <see cref="LooksProbeWorthy"/>.</param>
    /// <param name="probeTimeout">Per-device identify ceiling; null uses <see cref="DefaultProbeTimeout"/>.</param>
    /// <param name="timeout">Whole-scan ceiling; null uses <see cref="DefaultTimeout"/>.</param>
    public ModemScanner(
        SerialByIdResolver? byId = null,
        Func<string, CancellationToken, Task<string>>? identify = null,
        Func<IReadOnlyList<string>>? enumerate = null,
        Func<string, bool>? probeCandidate = null,
        TimeSpan? probeTimeout = null,
        TimeSpan? timeout = null)
    {
        this.byId = byId ?? new SerialByIdResolver();
        this.identify = identify ?? IdentifyNinoTncAsync;
        this.enumerate = enumerate ?? EnumerateCandidateDevices;
        this.probeCandidate = probeCandidate ?? LooksProbeWorthy;
        this.probeTimeout = probeTimeout is { } p && p > TimeSpan.Zero ? p : DefaultProbeTimeout;
        this.timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <inheritdoc/>
    public async Task<ModemScan> ScanAsync(NodeConfig current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        await single.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var claimed = ClaimedSerialDevices.Collect(current);
            var devices = new List<ModemScanDevice>();
            bool permissionDenied = false;

            try
            {
                foreach (var kernelPath in enumerate())
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var (device, denied) = await InspectAsync(kernelPath, claimed, cts.Token).ConfigureAwait(false);
                    permissionDenied |= denied;
                    devices.Add(device);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own ceiling tripped: return what we found rather than failing the request.
            }

            // Best-first, so the picker's default selection is the most useful one: identified
            // NinoTNCs, then free unidentified serial ports, then whatever is already claimed.
            var ordered = devices
                .OrderBy(d => d.ClaimedBy is null ? 0 : 1)
                .ThenBy(d => d.Kind == NinoTncKind ? 0 : 1)
                .ThenBy(d => d.DevicePath, StringComparer.Ordinal)
                .ToList();

            return new ModemScan(ordered, permissionDenied);
        }
        finally
        {
            single.Release();
        }
    }

    /// <summary>
    /// Candidate devices: the env-var override verbatim if set; otherwise on Linux
    /// <c>/dev/ttyACM*</c> (USB-CDC - how a NinoTNC enumerates) plus <c>/dev/ttyUSB*</c> (bridge
    /// chips - how most other KISS TNCs and cables do); otherwise every port the OS reports.
    /// </summary>
    public static IReadOnlyList<string> EnumerateCandidateDevices()
    {
        if (Environment.GetEnvironmentVariable(PortsOverrideEnvVar) is { Length: > 0 } overrideDevices)
        {
            return overrideDevices.Split(
                [',', ';', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (!Directory.Exists("/dev"))
            {
                return [];
            }
            return Directory.GetFiles("/dev", "ttyACM*")
                .Concat(Directory.GetFiles("/dev", "ttyUSB*"))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        return SerialPort.GetPortNames();
    }

    /// <summary>
    /// Whether <paramref name="kernelPath"/> is worth a NinoTNC identify: its USB VID/PID matches
    /// one the NinoTNC is known to present, or the USB ids are unreadable and the device is
    /// CDC-ACM (the class a NinoTNC always enumerates as). Everything else - a bridge-chip
    /// <c>/dev/ttyUSB*</c> that is far more likely to be a CAT cable or a GPS - is listed but left
    /// alone.
    /// </summary>
    public static bool LooksProbeWorthy(string kernelPath)
    {
        ArgumentNullException.ThrowIfNull(kernelPath);

        if (UsbSerialIds.Read(kernelPath) is { } ids)
        {
            return NinoTncPortDiscovery.KnownVidPids.Contains(ids);
        }

        // No sysfs (a container, a non-udev host, off Linux): fall back to the device class.
        return Path.GetFileName(kernelPath).StartsWith("ttyACM", StringComparison.Ordinal)
            || !RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    private async Task<(ModemScanDevice Device, bool PermissionDenied)> InspectAsync(
        string kernelPath, IReadOnlyDictionary<string, string> claimed, CancellationToken cancellationToken)
    {
        var byIdPath = byId.Resolve(kernelPath);
        var descriptor = byIdPath is null ? null : Path.GetFileName(byIdPath);
        // Bind to the stable name when udev gave us one: a port pinned to /dev/ttyACM0 moves to a
        // different modem the moment two are plugged in in the other order.
        var devicePath = byIdPath ?? kernelPath;

        claimed.TryGetValue(ClaimedSerialDevices.Canonicalise(kernelPath), out var claimedBy);

        if (claimedBy is not null || !probeCandidate(kernelPath))
        {
            return (new ModemScanDevice(devicePath, kernelPath, descriptor, SerialKind,
                FirmwareVersion: null, claimedBy, ProbeError: null), false);
        }

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(probeTimeout);
            var version = await identify(devicePath, probeCts.Token).ConfigureAwait(false);
            return (new ModemScanDevice(devicePath, kernelPath, descriptor, NinoTncKind,
                version, ClaimedBy: null, ProbeError: null), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not a NinoTNC, or not openable. Either way the device is still offered - it may be a
            // generic KISS TNC, which has nothing to answer with - but say why we could not tell.
            return (new ModemScanDevice(devicePath, kernelPath, descriptor, SerialKind,
                FirmwareVersion: null, ClaimedBy: null, ProbeReason(ex)),
                ex is UnauthorizedAccessException);
        }
    }

    /// <summary>One operator-readable phrase per identify failure. The permission case is the one
    /// that matters: it is a fixable install problem, not "no modem here".</summary>
    private static string ProbeReason(Exception ex) => ex switch
    {
        // System.IO.Ports reports an in-use device as "busy" inside an otherwise generic
        // exception, so the message is the only thing that separates it from a permissions
        // problem. Check it before the type arms.
        _ when ex.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) => "device is busy",
        UnauthorizedAccessException => "permission denied",
        FileNotFoundException => "device disappeared",
        _ => "no reply",
    };

    // The real identify: open the port at the NinoTNC's fixed USB-serial rate and ask for the
    // firmware version. A NinoTNC answers in milliseconds; anything else times out.
    private static async Task<string> IdentifyNinoTncAsync(string devicePath, CancellationToken cancellationToken)
    {
        await using var tnc = NinoTncSerialPort.Open(devicePath, NinoTncSerialPort.DefaultBaudRate, TimeProvider.System);
        return await tnc.GetVersionAsync(DefaultProbeTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public void Dispose() => single.Dispose();
}
