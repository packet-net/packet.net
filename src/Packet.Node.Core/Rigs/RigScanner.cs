using System.IO.Ports;
using System.Runtime.InteropServices;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Radios;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// The production <see cref="IRigScanner"/>: enumerates the machine's candidate CAT serial
/// devices, resolves each one's <c>/dev/serial/by-id</c> symlink (<see cref="SerialByIdResolver"/>),
/// marks devices the current config already claims (<see cref="ClaimedSerialDevices"/>), and
/// suggests a hamlib model where the descriptor is model-distinctive
/// (<see cref="RigDescriptorSuggestions"/> + <see cref="RigModelCatalogue.ResolveNumber"/>).
/// Passive — no serial port is ever opened — but kept bounded and single-flight like
/// <see cref="TaitRadioScanner"/> anyway: the work is filesystem reads plus (first scan only) the
/// catalogue's one <c>rigctl -l</c> run, all of which should be instant, and the cap just bounds
/// a pathological hang.
/// </summary>
public sealed class RigScanner : IRigScanner, IDisposable
{
    /// <summary>Colon/semicolon/comma-separated list of devices to consider INSTEAD of
    /// enumerating <c>/dev</c> (e.g. <c>"/dev/ttyUSB0,/dev/ttyACM0"</c>) — mirrors
    /// <c>PACKETNET_TAIT_PORTS</c>.</summary>
    public const string PortsOverrideEnvVar = "PACKETNET_RIG_PORTS";

    /// <summary>Default hard ceiling on a scan — passive filesystem work, so mostly instant; the
    /// cap bounds a pathological hang (a wedged rigctl is already bounded by the runner).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly RigModelCatalogue catalogue;
    private readonly SerialByIdResolver byId;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim single = new(1, 1);

    /// <summary>Build the scanner. <paramref name="byId"/> defaults to the standard udev
    /// resolver; <paramref name="timeout"/> defaults to <see cref="DefaultTimeout"/>.</summary>
    public RigScanner(RigModelCatalogue catalogue, SerialByIdResolver? byId = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        this.catalogue = catalogue;
        this.byId = byId ?? new SerialByIdResolver();
        this.timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <inheritdoc/>
    public async Task<RigScan> ScanAsync(NodeConfig current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Single-flight for symmetry with the radio scanner (and so two concurrent wizard tabs
        // don't both pay the first-scan rigctl run).
        await single.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var claimed = ClaimedSerialDevices.Collect(current);
            var devices = new List<RigScanDevice>();
            try
            {
                foreach (var device in EnumerateCandidateDevices())
                {
                    cts.Token.ThrowIfCancellationRequested();
                    devices.Add(Inspect(device, claimed));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own timeout tripped — return whatever we found rather than hanging or throwing.
            }
            return new RigScan(devices, catalogue.Available);
        }
        finally
        {
            single.Release();
        }
    }

    /// <summary>
    /// Candidate CAT devices, best-first: the env-var override verbatim if set; otherwise on
    /// Linux <c>/dev/ttyUSB*</c> (CAT bridge chips) plus <c>/dev/ttyACM*</c> (native-USB rigs —
    /// newer Icoms/Kenwoods enumerate as CDC-ACM); otherwise every port the OS reports.
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
            if (Directory.Exists("/dev"))
            {
                return Directory.GetFiles("/dev", "ttyUSB*")
                    .Concat(Directory.GetFiles("/dev", "ttyACM*"))
                    .Order()
                    .ToArray();
            }
            return [];
        }

        return SerialPort.GetPortNames();
    }

    private RigScanDevice Inspect(string device, IReadOnlyDictionary<string, string> claimed)
    {
        var byIdPath = byId.Resolve(device);
        var descriptor = byIdPath is null ? null : Path.GetFileName(byIdPath);

        claimed.TryGetValue(ClaimedSerialDevices.Canonicalise(device), out var claimedBy);

        RigSuggestion? suggestion = null;
        if (descriptor is not null && RigDescriptorSuggestions.Suggest(descriptor) is { } match)
        {
            // Resolve the number against the installed hamlib by name — never hardcoded, so the
            // suggestion survives hamlib version skew. Null when rigctl is absent (the response's
            // catalogueAvailable says so) or the installed catalogue doesn't know the model.
            suggestion = new RigSuggestion(
                match.Manufacturer, match.ModelName,
                catalogue.ResolveNumber(match.Manufacturer, match.ModelName),
                Source: "by-id");
        }

        return new RigScanDevice(device, byIdPath, descriptor, claimedBy, suggestion);
    }

    /// <inheritdoc/>
    public void Dispose() => single.Dispose();
}
