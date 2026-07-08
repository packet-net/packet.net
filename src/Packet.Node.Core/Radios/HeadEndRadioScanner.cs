using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The production <see cref="IHeadEndRadioScanner"/>. Merges the configured head-end list with an
/// mDNS browse, resolves each instance's address (config wins; a duplicate-id discovery with no
/// config address is a conflict, not a guess), fetches its inventory, and reaches through each
/// <b>free</b> device's raw pipe to classify it — NinoTNC via GETVER, Tait via MODEL with a CCDI
/// baud sweep when the current clock is wrong. Devices already bound to a configured port are skipped
/// (the head-end is single-client-per-pipe — probing a bound device would fight the running port).
/// </summary>
/// <remarks>
/// USB VID hints pick the likely probe first (NinoTNC ≈ Microchip <c>04d8</c>; Tait CP2102 ≈ SiLabs
/// <c>10c4</c>), then the other confirms — so a mislabelled or hint-less device still identifies, just
/// a beat slower. Everything is bounded (per-probe timeouts) and total: any device/instance failure
/// degrades that row, never the scan.
/// </remarks>
public sealed partial class HeadEndRadioScanner : IHeadEndRadioScanner
{
    /// <summary>The standard CCDI line rates the Tait sweep tries, in likelihood order (the factory
    /// default first). Capped deliberately — a handful of rates clock-and-identify in one pass.</summary>
    public static readonly IReadOnlyList<int> SweepBaudRates = [28800, 19200, 9600, 38400, 57600];

    /// <summary>A NinoTNC's KISS serial rate is a fixed <b>57600</b> — it never changes and there is
    /// nothing to sweep (#567). The head-end line is clocked to this before the GETVER reach-through
    /// so the raw pipe always speaks at the rate the NinoTNC expects.</summary>
    public const int NinoTncKissBaud = 57600;

    private const string MicrochipVid = "04d8"; // NinoTNC (PIC USB-CDC)
    private const string SiLabsVid = "10c4";    // Tait CP2102 CCDI dongle

    private readonly IHeadEndDiscovery discovery;
    private readonly Func<Uri, HeadEndClient> clientFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan discoveryTimeout;
    private readonly TimeSpan identifyTimeout;
    private readonly TimeSpan connectTimeout;
    private readonly ILogger<HeadEndRadioScanner> logger;

    /// <summary>Build the scanner. <paramref name="clientFactory"/> builds a
    /// <see cref="HeadEndClient"/> for a resolved base address (default: the real client); tests point
    /// it at a stub inventory server. Timeouts default to short, sane values.</summary>
    public HeadEndRadioScanner(
        IHeadEndDiscovery discovery,
        Func<Uri, HeadEndClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? discoveryTimeout = null,
        TimeSpan? identifyTimeout = null,
        TimeSpan? connectTimeout = null)
    {
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.clientFactory = clientFactory ?? (uri => new HeadEndClient(uri));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.discoveryTimeout = discoveryTimeout is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(2);
        this.identifyTimeout = identifyTimeout is { } i && i > TimeSpan.Zero ? i : TimeSpan.FromSeconds(2);
        this.connectTimeout = connectTimeout is { } c && c > TimeSpan.Zero ? c : TimeSpan.FromSeconds(2);
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HeadEndRadioScanner>();
    }

    /// <inheritdoc/>
    /// <remarks>Single-flight with keyup pairing (#581): a scan's Tait baud sweep re-clocks lines
    /// under a pairing run's PTT watchers (and the head-end queues, not rejects, a second pipe
    /// client), so the two probe actions serialise on the shared <see cref="HeadEndProbeGate"/> —
    /// a concurrent caller waits (bounded) rather than corrupting the in-flight run's results.</remarks>
    public async Task<HeadEndScan> ScanAsync(NodeConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await HeadEndProbeGate.EnterAsync("head-end scan", cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await discovery.DiscoverAsync(discoveryTimeout, cancellationToken).ConfigureAwait(false);
            var bound = BoundDevices(config);

            var (instancesToScan, conflicts) = ResolveInstances(config.HeadEnds, discovered);

            var instances = new List<HeadEndInstanceScan>(instancesToScan.Count);
            foreach (var instance in instancesToScan)
            {
                instances.Add(await ScanInstanceAsync(instance, bound, cancellationToken).ConfigureAwait(false));
            }

            return new HeadEndScan(instances, conflicts);
        }
        finally
        {
            HeadEndProbeGate.Exit();
        }
    }

    // Merge config head-ends with discovery into the set to scan, and separate out duplicate-id
    // conflicts (same instance id at 2+ discovered addresses with no config address to disambiguate).
    private static (List<ResolvedInstance> ToScan, List<HeadEndConflict> Conflicts) ResolveInstances(
        IReadOnlyList<HeadEndConfig> headEnds, IReadOnlyList<DiscoveredHeadEnd> discovered)
    {
        var ids = new List<string>();
        void AddId(string id)
        {
            if (!ids.Any(existing => string.Equals(existing, id, StringComparison.Ordinal)))
            {
                ids.Add(id);
            }
        }
        foreach (var h in headEnds.Where(h => !string.IsNullOrWhiteSpace(h.Id)))
        {
            AddId(h.Id);
        }
        foreach (var d in discovered.Where(d => !string.IsNullOrWhiteSpace(d.InstanceId)))
        {
            AddId(d.InstanceId);
        }

        var toScan = new List<ResolvedInstance>();
        var conflicts = new List<HeadEndConflict>();

        foreach (var id in ids)
        {
            var config = headEnds.FirstOrDefault(h => string.Equals(h.Id, id, StringComparison.Ordinal));
            var hits = discovered.Where(d => string.Equals(d.InstanceId, id, StringComparison.Ordinal)).ToList();

            // Config address wins — an operator pinned it, so no discovery clash can override it.
            if (config is not null && !string.IsNullOrWhiteSpace(config.Address)
                && HeadEndAddress.TryParse(config.Address, out var host, out var port))
            {
                toScan.Add(new ResolvedInstance(id, host, port, "config"));
                continue;
            }

            var distinct = hits
                .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (distinct.Count == 0)
            {
                // Declared but unreachable: blank config address and nothing discovered.
                toScan.Add(new ResolvedInstance(id, "", 0, "config"));
            }
            else if (distinct.Count == 1)
            {
                toScan.Add(new ResolvedInstance(id, distinct[0].Host, distinct[0].HttpPort, "mdns"));
            }
            else
            {
                conflicts.Add(new HeadEndConflict(id, distinct.Select(d => d.Address).ToList()));
            }
        }

        return (toScan, conflicts);
    }

    private async Task<HeadEndInstanceScan> ScanInstanceAsync(
        ResolvedInstance instance, Dictionary<(string, string), string> bound, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instance.Host) || instance.HttpPort <= 0)
        {
            return Unreachable(instance, "address not resolved (blank config address, not discovered)");
        }

        HeadEndInventory inventory;
        try
        {
            var client = clientFactory(new Uri($"http://{instance.Host}:{instance.HttpPort}/", UriKind.Absolute));
            inventory = await client.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogInstanceUnreachable(ex, instance.InstanceId, instance.Host, instance.HttpPort);
            return Unreachable(instance, $"inventory fetch failed: {ex.Message}");
        }

        var devices = new List<HeadEndDeviceScan>(inventory.Ports.Count);
        foreach (var port in inventory.Ports)
        {
            string? boundKind = null;
            if (!bound.TryGetValue((instance.InstanceId, port.Id), out boundKind)
                && !string.IsNullOrWhiteSpace(port.ById))
            {
                // Legacy-binding fallback (#578): a config adopted against head-end ≤0.1.2 binds
                // the by-id basename; 0.1.3's ids are by-path. Without this, the scan would treat
                // a bound device as free and probe it — fighting the running port for the pipe.
                var legacyId = HeadEndDeviceResolver.ByIdBasename(port.ById);
                if (bound.TryGetValue((instance.InstanceId, legacyId), out boundKind))
                {
                    LogLegacyByIdBound(legacyId, port.Id, instance.InstanceId);
                }
            }
            if (boundKind is not null)
            {
                // Already bound to a running/configured port — don't probe (single-client-per-pipe).
                // Its role is known from the binding, so still surface it (marked not-free).
                devices.Add(new HeadEndDeviceScan(port.Id, boundKind, Model: null, Version: null,
                    Serial: null, Baud: port.Baud, Free: false));
                continue;
            }

            // Build a per-device head-end client for the line-control (baud sweep) verb.
            var lineClient = clientFactory(new Uri($"http://{instance.Host}:{instance.HttpPort}/", UriKind.Absolute));
            devices.Add(await IdentifyDeviceAsync(instance.Host, port, lineClient, cancellationToken).ConfigureAwait(false));
        }

        var (pairs, ambiguous) = ProposePairs(devices);
        return new HeadEndInstanceScan(
            instance.InstanceId, instance.Host, instance.HttpPort, instance.Source,
            Reachable: true, Error: null, Devices: devices, ProposedPairs: pairs, PairingAmbiguous: ambiguous);
    }

    // Reach through the raw pipe to classify one free device. USB VID hint picks the likely probe
    // first; the other confirms. Total — a probe failure just means "not that kind".
    private async Task<HeadEndDeviceScan> IdentifyDeviceAsync(
        string host, HeadEndPortInfo port, HeadEndClient lineClient, CancellationToken cancellationToken)
    {
        var vid = port.UsbVid?.Trim().ToLowerInvariant() ?? "";
        bool taitFirst = string.Equals(vid, SiLabsVid, StringComparison.Ordinal);

        Func<int, CancellationToken, Task> setBaud =
            (baud, ct) => lineClient.SetLineAsync(port.Id, baud, cancellationToken: ct);

        if (taitFirst)
        {
            return await ProbeTaitAsync(host, port, setBaud, cancellationToken).ConfigureAwait(false)
                ?? await ProbeNinoAsync(host, port, setBaud, cancellationToken).ConfigureAwait(false)
                ?? Unidentified(port);
        }

        return await ProbeNinoAsync(host, port, setBaud, cancellationToken).ConfigureAwait(false)
            ?? await ProbeTaitAsync(host, port, setBaud, cancellationToken).ConfigureAwait(false)
            ?? Unidentified(port);
    }

    private async Task<HeadEndDeviceScan?> ProbeNinoAsync(
        string host, HeadEndPortInfo port, Func<int, CancellationToken, Task> setBaud, CancellationToken cancellationToken)
    {
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(connectTimeout);
            // #567: a NinoTNC's KISS baud is a fixed 57600 (never swept). Clock the head-end line to it
            // via the line verb before GETVER so the raw pipe speaks at the rate the NinoTNC expects.
            await setBaud(NinoTncKissBaud, connectCts.Token).ConfigureAwait(false);
            await using var nino = await NinoTncSerialPort
                .OpenTcp(host, port.TcpPort, timeProvider, options: null, connectCts.Token).ConfigureAwait(false);

            var version = await nino.GetVersionAsync(identifyTimeout, cancellationToken).ConfigureAwait(false);
            // NinoTNC baud is fictional over USB-CDC — report the inventory baud, no sweep.
            return new HeadEndDeviceScan(port.Id, HeadEndDeviceKind.NinoTnc, Model: null,
                Version: version, Serial: null, Baud: port.Baud, Free: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // not a NinoTNC (or unreachable) — let the other probe try
        }
    }

    private async Task<HeadEndDeviceScan?> ProbeTaitAsync(
        string host, HeadEndPortInfo port, Func<int, CancellationToken, Task> setBaud, CancellationToken cancellationToken)
    {
        TaitCcdiRadio? radio = null;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(connectTimeout);
            int startBaud = port.Baud > 0 ? port.Baud : TaitCcdiRadio.DefaultBaudRate;
            radio = await TaitCcdiRadio.OpenTcp(
                host, port.TcpPort, startBaud, setBaud, options: null, timeProvider, connectCts.Token).ConfigureAwait(false);

            // Try MODEL at the current (inventory) clock first, then sweep the standard CCDI rates —
            // clocking (via the head-end line verb) AND identifying in one step.
            var identity = await TryQueryIdentityAsync(radio, cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                return TaitResult(port, identity, startBaud);
            }

            foreach (var rate in SweepBaudRates.Where(r => r != startBaud))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await setBaud(rate, cancellationToken).ConfigureAwait(false);
                identity = await TryQueryIdentityAsync(radio, cancellationToken).ConfigureAwait(false);
                if (identity is not null)
                {
                    LogBaudSwept(port.Id, rate);
                    return TaitResult(port, identity, rate);
                }
            }

            return null; // no CCDI reply at any swept rate — not a Tait (or powered off)
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (radio is not null)
            {
                await radio.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // One bounded MODEL/serial/versions round-trip. A timeout (wrong clock, not a Tait) is a null,
    // not a throw — the sweep tries the next rate.
    private async Task<TaitRadioIdentity?> TryQueryIdentityAsync(TaitCcdiRadio radio, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(identifyTimeout);
        try
        {
            return await radio.QueryIdentityAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static HeadEndDeviceScan TaitResult(HeadEndPortInfo port, TaitRadioIdentity identity, int baud) =>
        new(port.Id, HeadEndDeviceKind.TaitCcdi, Model: identity.ProductName,
            Version: identity.CcdiVersion, Serial: identity.SerialNumber, Baud: baud, Free: true,
            // The band split (hence the amateur band) is CCDI-readable off the product code even though
            // the tuned frequency is not — adopt uses it to label the port by band.
            BandCode: identity.Band?.Code, AmateurBand: identity.Band?.AmateurBand);

    private static HeadEndDeviceScan Unidentified(HeadEndPortInfo port) =>
        new(port.Id, HeadEndDeviceKind.Unknown, Model: null, Version: null, Serial: null, Baud: port.Baud, Free: true);

    // Within one instance, pair the free TNC(s) with the free radio(s). Exactly one of each is an
    // unambiguous auto-suggestion; more than one of either is a manual choice (every combination is
    // listed, none auto). Only-TNCs or only-radios yields nothing to pair.
    private static (List<HeadEndPairProposal> Pairs, bool Ambiguous) ProposePairs(IReadOnlyList<HeadEndDeviceScan> devices)
    {
        var tncs = devices.Where(d => d.Free && d.Kind == HeadEndDeviceKind.NinoTnc).Select(d => d.DeviceId).ToList();
        var radios = devices.Where(d => d.Free && d.Kind == HeadEndDeviceKind.TaitCcdi).Select(d => d.DeviceId).ToList();

        if (tncs.Count == 0 || radios.Count == 0)
        {
            return ([], false);
        }

        if (tncs.Count == 1 && radios.Count == 1)
        {
            return ([new HeadEndPairProposal(tncs[0], radios[0], Auto: true)], false);
        }

        var combos = (from tnc in tncs from radio in radios select new HeadEndPairProposal(tnc, radio, Auto: false)).ToList();
        return (combos, true);
    }

    // Every (instanceId, deviceId) a configured port already binds — a nino-tnc-tcp transport (TNC)
    // or a head-end-bound radio (radio) — with the role the binding implies.
    private static Dictionary<(string, string), string> BoundDevices(NodeConfig config)
    {
        var bound = new Dictionary<(string, string), string>();
        foreach (var port in config.Ports)
        {
            if (port.Transport is NinoTncTcpTransport t && !string.IsNullOrWhiteSpace(t.HeadEndId)
                && !string.IsNullOrWhiteSpace(t.DeviceId))
            {
                bound[(t.HeadEndId, t.DeviceId)] = HeadEndDeviceKind.NinoTnc;
            }
            if (port.Radio is { IsHeadEndBound: true } radio)
            {
                bound[(radio.HeadEndId, radio.DeviceId)] = HeadEndDeviceKind.TaitCcdi;
            }
        }
        return bound;
    }

    private static HeadEndInstanceScan Unreachable(ResolvedInstance instance, string error) =>
        new(instance.InstanceId, instance.Host, instance.HttpPort, instance.Source,
            Reachable: false, Error: error, Devices: [], ProposedPairs: [], PairingAmbiguous: false);

    private sealed record ResolvedInstance(string InstanceId, string Host, int HttpPort, string Source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Head-end device '{DeviceId}' identified as a Tait CCDI radio after sweeping to {Baud} baud.")]
    private partial void LogBaudSwept(string deviceId, int baud);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Head-end '{InstanceId}': a port binding uses the legacy by-id device id '{LegacyId}' (inventory device '{CurrentId}') — re-adopt the pairing to migrate the config to the stable by-path id.")]
    private partial void LogLegacyByIdBound(string legacyId, string currentId, string instanceId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Head-end '{InstanceId}' at {Host}:{Port} inventory fetch failed during scan.")]
    private partial void LogInstanceUnreachable(Exception ex, string instanceId, string host, int port);
}
