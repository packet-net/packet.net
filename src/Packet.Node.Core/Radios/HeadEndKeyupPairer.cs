using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Discovers the <b>physical</b> modem↔radio map on one head-end by keying RF: for each free NinoTNC
/// it opens the raw pipe and <b>briefly transmits through it</b> (a CQBEEP request frame keys the
/// NinoTNC's cabled radio) while watching every free Tait's CCDI PROGRESS stream for a
/// <see cref="TaitCcdiRadio.TransmitterStateChanged"/> (PTT-activated) edge — the Tait whose PTT line
/// the NinoTNC asserted is its physical pair. This is ground truth: it replaces the scan's free-device
/// co-location <em>guess</em> for the ambiguous case and verifies the unambiguous one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This transmits.</b> It is an explicitly operator-initiated action
/// (<c>POST /api/v1/radios/headends/{instanceId}/pair-by-keyup</c>, operate-scope) with an RF caveat on
/// the response — never part of the passive <c>GET /radios/headends</c> scan. One NinoTNC is keyed at a
/// time, briefly, and each Tait watcher is reset between keyups so an edge is attributed to the right
/// modem.
/// </para>
/// <para>
/// Every open is bounded and total: a Tait that fails to open is excluded (and stays "unpaired"); a
/// NinoTNC that fails to open/key is reported unpaired; a keyup that fires no Tait is unpaired; one
/// that fires more than one is flagged ambiguous rather than guessed. The RF-keying loop is behind an
/// injectable seam (<see cref="OpenKeyupModem"/> / <see cref="OpenKeyupWatch"/>) so it is unit-tested
/// entirely with in-memory fakes — no real serial port or socket is opened in tests.
/// </para>
/// </remarks>
public sealed partial class HeadEndKeyupPairer : IHeadEndKeyupPairer
{
    private readonly IHeadEndRadioScanner scanner;
    private readonly Func<Uri, HeadEndClient> clientFactory;
    private readonly TimeProvider timeProvider;
    private readonly HeadEndKeyupOptions options;
    private readonly ILogger<HeadEndKeyupPairer> logger;
    private readonly OpenKeyupModem? modemOverride;
    private readonly OpenKeyupWatch? watchOverride;

    /// <summary>Build the pairer. It reuses the fleet <paramref name="scanner"/> to find each
    /// instance's free NinoTNCs + Taits, then <paramref name="clientFactory"/> (default: the real
    /// client) to read the inventory for their raw-pipe TCP ports. Timing lives in
    /// <paramref name="options"/>.</summary>
    public HeadEndKeyupPairer(
        IHeadEndRadioScanner scanner,
        Func<Uri, HeadEndClient>? clientFactory = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        HeadEndKeyupOptions? options = null)
        : this(scanner, clientFactory, timeProvider, loggerFactory, options, modemOverride: null, watchOverride: null)
    {
    }

    // Test seam (InternalsVisibleTo Packet.Node.Tests): the modem/watch open overrides let the full
    // PairByKeyupAsync path run against fake devices with no real socket.
    internal HeadEndKeyupPairer(
        IHeadEndRadioScanner scanner,
        Func<Uri, HeadEndClient>? clientFactory,
        TimeProvider? timeProvider,
        ILoggerFactory? loggerFactory,
        HeadEndKeyupOptions? options,
        OpenKeyupModem? modemOverride,
        OpenKeyupWatch? watchOverride)
    {
        this.scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        this.clientFactory = clientFactory ?? (uri => new HeadEndClient(uri));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.options = options ?? new HeadEndKeyupOptions();
        this.modemOverride = modemOverride;
        this.watchOverride = watchOverride;
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HeadEndKeyupPairer>();
    }

    /// <inheritdoc/>
    public async Task<HeadEndKeyupResult> PairByKeyupAsync(
        NodeConfig config, string instanceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);

        var scan = await scanner.ScanAsync(config, cancellationToken).ConfigureAwait(false);
        var instance = scan.Instances.FirstOrDefault(
            i => string.Equals(i.InstanceId, instanceId, StringComparison.Ordinal));

        if (instance is null)
        {
            bool conflicted = scan.Conflicts.Any(c => string.Equals(c.InstanceId, instanceId, StringComparison.Ordinal));
            return Unreachable(instanceId, conflicted
                ? "instance id is a duplicate-address conflict — resolve the clash (pin a config address) first"
                : "no such head-end instance in the current scan (config ∪ discovery)");
        }
        if (!instance.Reachable)
        {
            return Unreachable(instanceId, instance.Error ?? "head-end unreachable");
        }

        var freeTncs = instance.Devices
            .Where(d => d.Free && d.Kind == HeadEndDeviceKind.NinoTnc).Select(d => d.DeviceId).ToList();
        var freeRadios = instance.Devices
            .Where(d => d.Free && d.Kind == HeadEndDeviceKind.TaitCcdi).ToList();

        // Nothing to key ⇒ no RF, no opens: every free radio is trivially unpaired.
        if (freeTncs.Count == 0)
        {
            return new HeadEndKeyupResult(
                instanceId, Reachable: true, Error: null, Pairs: [], UnpairedTncs: [],
                UnpairedRadios: freeRadios.Select(d => d.DeviceId).ToList(), Ambiguous: [], HeadEndKeyupCaveat.Text);
        }

        // Map deviceId → raw-pipe TCP port via the inventory (the scan model doesn't carry it).
        HeadEndInventory inventory;
        HeadEndClient client;
        try
        {
            client = clientFactory(new Uri($"http://{instance.Host}:{instance.HttpPort}/", UriKind.Absolute));
            inventory = await client.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unreachable(instanceId, $"inventory fetch failed: {ex.Message}");
        }

        // Build deviceId → port defensively: an indexer assignment never throws on a duplicate id, so a
        // malformed inventory degrades instead of crashing — this method is total.
        var byId = new Dictionary<string, HeadEndPortInfo>(StringComparer.Ordinal);
        foreach (var p in inventory.Ports)
        {
            byId[p.Id] = p;
        }

        KeyupTarget? Target(string deviceId, int baud) =>
            byId.TryGetValue(deviceId, out var p) ? new KeyupTarget(instance.Host, p.TcpPort, baud, deviceId) : null;

        var tncTargets = freeTncs.Select(id => Target(id, HeadEndRadioScanner.NinoTncKissBaud))
            .OfType<KeyupTarget>().ToList();
        var radioTargets = freeRadios.Select(d => Target(d.DeviceId, d.Baud > 0 ? d.Baud : TaitCcdiRadio.DefaultBaudRate))
            .OfType<KeyupTarget>().ToList();

        // Free devices the scan saw but this inventory fetch doesn't list (a scan↔inventory race) can't
        // be dialled — surface them as unpaired rather than dropping them silently.
        var missingTncs = freeTncs.Where(id => !byId.ContainsKey(id)).ToList();
        var missingRadios = freeRadios.Where(d => !byId.ContainsKey(d.DeviceId)).Select(d => d.DeviceId).ToList();

        // The keying frame's source callsign is this node's identity. An unparseable identity is a
        // misconfiguration we surface rather than transmit with a bogus source.
        if (!Callsign.TryParse(config.Identity?.Callsign, out var source))
        {
            return Unreachable(instanceId, $"node identity callsign '{config.Identity?.Callsign}' is not a valid callsign to key with");
        }

        var openModem = modemOverride ?? ProductionModemOpener(client, source);
        var openWatch = watchOverride ?? ProductionWatchOpener(client);

        var result = await RunKeyupAsync(instanceId, tncTargets, radioTargets, openModem, openWatch, options, cancellationToken)
            .ConfigureAwait(false);

        if (missingTncs.Count == 0 && missingRadios.Count == 0)
        {
            return result;
        }
        return result with
        {
            UnpairedTncs = [.. result.UnpairedTncs, .. missingTncs],
            UnpairedRadios = [.. result.UnpairedRadios, .. missingRadios],
        };
    }

    // The RF-keying loop, isolated behind the open seams so it is unit-tested with fakes. Opens every
    // Tait watcher, then keys each NinoTNC in turn (resetting watchers between) and attributes the PTT
    // edge(s) that fire within the observation window.
    internal async Task<HeadEndKeyupResult> RunKeyupAsync(
        string instanceId,
        IReadOnlyList<KeyupTarget> tncs,
        IReadOnlyList<KeyupTarget> radios,
        OpenKeyupModem openModem,
        OpenKeyupWatch openWatch,
        HeadEndKeyupOptions options,
        CancellationToken cancellationToken)
    {
        var watches = new List<(KeyupTarget Target, IKeyupWatch Watch)>();
        foreach (var radio in radios)
        {
            try
            {
                watches.Add((radio, await openWatch(radio, cancellationToken).ConfigureAwait(false)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeWatchesAsync(watches).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                LogWatchOpenFailed(ex, radio.DeviceId);
            }
        }

        var pairs = new List<HeadEndKeyupPair>();
        var ambiguous = new List<HeadEndKeyupAmbiguity>();
        var unpairedTncs = new List<string>();
        var pairedRadios = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var tnc in tncs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (_, watch) in watches)
                {
                    watch.Reset();
                }

                bool keyed = false;
                try
                {
                    await using var modem = await openModem(tnc, cancellationToken).ConfigureAwait(false);
                    await modem.KeyAsync(cancellationToken).ConfigureAwait(false);
                    keyed = true;
                    // Watch for the co-located Tait's PTT edge to land (TX + CCDI PROGRESS round-trip).
                    await Task.Delay(options.ObservationWindow, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogKeyupFailed(ex, tnc.DeviceId);
                }

                if (!keyed)
                {
                    unpairedTncs.Add(tnc.DeviceId);
                    continue;
                }

                // A Tait pairs with at most one modem (one radio, one cable): ignore any already-paired
                // radio that re-fires (a previous transmission still unkeying) so it can't be double-claimed.
                var fired = watches
                    .Where(w => w.Watch.ObservedKeyup && !pairedRadios.Contains(w.Target.DeviceId))
                    .Select(w => w.Target.DeviceId).ToList();
                switch (fired.Count)
                {
                    case 0:
                        unpairedTncs.Add(tnc.DeviceId);
                        break;
                    case 1:
                        pairs.Add(new HeadEndKeyupPair(tnc.DeviceId, fired[0]));
                        pairedRadios.Add(fired[0]);
                        break;
                    default:
                        ambiguous.Add(new HeadEndKeyupAmbiguity(tnc.DeviceId, fired));
                        foreach (var r in fired)
                        {
                            pairedRadios.Add(r);
                        }
                        break;
                }

                if (options.SettleBetween > TimeSpan.Zero)
                {
                    await Task.Delay(options.SettleBetween, timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DisposeWatchesAsync(watches).ConfigureAwait(false);
        }

        var unpairedRadios = radios.Select(r => r.DeviceId)
            .Where(id => !pairedRadios.Contains(id)).ToList();

        return new HeadEndKeyupResult(
            instanceId, Reachable: true, Error: null, pairs, unpairedTncs, unpairedRadios, ambiguous,
            HeadEndKeyupCaveat.Text);
    }

    private static async Task DisposeWatchesAsync(List<(KeyupTarget Target, IKeyupWatch Watch)> watches)
    {
        foreach (var (_, watch) in watches)
        {
            try
            {
                await watch.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort teardown — a watch that already faulted must not mask the result.
            }
        }
    }

    // Production modem opener: clock the head-end line to the fixed NinoTNC KISS baud (#567), open the
    // raw pipe, and key by transmitting a short CQBEEP request through it.
    private OpenKeyupModem ProductionModemOpener(HeadEndClient client, Callsign source) => async (target, ct) =>
    {
        await client.SetLineAsync(target.DeviceId, HeadEndRadioScanner.NinoTncKissBaud, cancellationToken: ct).ConfigureAwait(false);
        var nino = await NinoTncSerialPort.OpenTcp(target.Host, target.TcpPort, timeProvider, ct).ConfigureAwait(false);
        return new NinoKeyupModem(nino, source, options.KeySeconds);
    };

    // Production watch opener: open the Tait over the raw pipe (clocking the head-end line to its CCDI
    // rate) and turn on unsolicited PROGRESS so the PTT edge fires.
    private OpenKeyupWatch ProductionWatchOpener(HeadEndClient client) => async (target, ct) =>
    {
        Func<int, CancellationToken, Task> setBaud =
            (baud, c) => client.SetLineAsync(target.DeviceId, baud, cancellationToken: c);
        var radio = await TaitCcdiRadio.OpenTcp(
            target.Host, target.TcpPort, target.Baud, setBaud, options: null, timeProvider, ct).ConfigureAwait(false);
        try
        {
            await radio.SetProgressMessagesAsync(true, ct).ConfigureAwait(false);
        }
        catch
        {
            await radio.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new TaitKeyupWatch(radio);
    };

    private static HeadEndKeyupResult Unreachable(string instanceId, string error) =>
        new(instanceId, Reachable: false, Error: error, Pairs: [], UnpairedTncs: [], UnpairedRadios: [],
            Ambiguous: [], HeadEndKeyupCaveat.Text);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keyup-pairing: Tait watcher for device '{DeviceId}' failed to open — excluded from pairing.")]
    private partial void LogWatchOpenFailed(Exception ex, string deviceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keyup-pairing: NinoTNC '{DeviceId}' failed to open/key — reported unpaired.")]
    private partial void LogKeyupFailed(Exception ex, string deviceId);

    // A production Tait watcher: latches the PTT-activated edge (TransmitterStateChanged) so the loop
    // can read, per keyup, whether this radio's transmitter was keyed.
    private sealed class TaitKeyupWatch : IKeyupWatch
    {
        private readonly TaitCcdiRadio radio;
        private int observed;

        public TaitKeyupWatch(TaitCcdiRadio radio)
        {
            this.radio = radio;
            radio.TransmitterStateChanged += OnTransmitterStateChanged;
        }

        public bool ObservedKeyup => Volatile.Read(ref observed) != 0;

        public void Reset() => Volatile.Write(ref observed, 0);

        private void OnTransmitterStateChanged(object? sender, TransmitterStateChange e)
        {
            if (e.Transmitting)
            {
                Volatile.Write(ref observed, 1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            radio.TransmitterStateChanged -= OnTransmitterStateChanged;
            await radio.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A production NinoTNC keyer: transmits a short CQBEEP request through the TNC, keying its cabled
    // radio for the frame duration (enough for the co-located Tait to raise its PTT edge).
    private sealed class NinoKeyupModem : IKeyupModem
    {
        private readonly NinoTncSerialPort nino;
        private readonly Callsign source;
        private readonly int seconds;

        public NinoKeyupModem(NinoTncSerialPort nino, Callsign source, int seconds)
        {
            this.nino = nino;
            this.source = source;
            this.seconds = seconds;
        }

        public Task KeyAsync(CancellationToken cancellationToken) =>
            nino.SendCqBeepRequestAsync(source, seconds, cancellationToken);

        public ValueTask DisposeAsync() => nino.DisposeAsync();
    }
}

/// <summary>The keyup-pairing seam behind <c>POST /api/v1/radios/headends/{instanceId}/pair-by-keyup</c>:
/// discover one head-end's physical modem↔radio map by keying RF. A test double returns scripted
/// results; the production <see cref="HeadEndKeyupPairer"/> keys the real radios over the socket.</summary>
public interface IHeadEndKeyupPairer
{
    /// <summary>Key each free NinoTNC on <paramref name="instanceId"/> in turn and resolve the Tait it
    /// is physically cabled to (by the PTT it asserts). Bounded and total: an unknown/unreachable/
    /// conflicting instance returns <c>Reachable: false</c> with a reason, never a throw.</summary>
    Task<HeadEndKeyupResult> PairByKeyupAsync(NodeConfig config, string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>Timing knobs for a keyup-pairing run. Defaults are conservative for real RF round-trips;
/// tests pass tiny values.</summary>
public sealed record HeadEndKeyupOptions
{
    /// <summary>Seconds of tone the CQBEEP request asks for (1–15). The local radio only keys for the
    /// frame transmission, but the value must be a valid request. Default 2.</summary>
    public int KeySeconds { get; init; } = 2;

    /// <summary>How long to watch each Tait for a PTT edge after a keyup (TX + CCDI PROGRESS
    /// round-trip). Default 2 s.</summary>
    public TimeSpan ObservationWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A quiet gap between keying successive NinoTNCs, so one radio has unkeyed before the
    /// next is keyed. Default 300 ms.</summary>
    public TimeSpan SettleBetween { get; init; } = TimeSpan.FromMilliseconds(300);
}

/// <summary>One device to open in a keyup-pairing run: the raw-pipe host + TCP port and the CCDI baud
/// (for a Tait watcher; a NinoTNC keyer uses the fixed KISS baud). Internal — the open seam's currency.</summary>
internal sealed record KeyupTarget(string Host, int TcpPort, int Baud, string DeviceId);

/// <summary>Opens a NinoTNC ready to key (transmit through). Internal seam — production wraps
/// <see cref="NinoTncSerialPort"/>; tests return an in-memory fake.</summary>
internal delegate Task<IKeyupModem> OpenKeyupModem(KeyupTarget target, CancellationToken cancellationToken);

/// <summary>Opens a Tait watching for its transmitter's PTT edge. Internal seam — production wraps
/// <see cref="TaitCcdiRadio"/>; tests return an in-memory fake.</summary>
internal delegate Task<IKeyupWatch> OpenKeyupWatch(KeyupTarget target, CancellationToken cancellationToken);

/// <summary>A NinoTNC opened for keying: <see cref="KeyAsync"/> briefly transmits through it (keying
/// its cabled radio).</summary>
internal interface IKeyupModem : IAsyncDisposable
{
    /// <summary>Briefly key the cabled radio (transmit a short frame through the NinoTNC).</summary>
    Task KeyAsync(CancellationToken cancellationToken);
}

/// <summary>A Tait watched for its transmitter's PTT edge. <see cref="ObservedKeyup"/> latches true when
/// the transmitter keyed since the last <see cref="Reset"/>.</summary>
internal interface IKeyupWatch : IAsyncDisposable
{
    /// <summary>True when a PTT-activated edge was seen since the last <see cref="Reset"/>.</summary>
    bool ObservedKeyup { get; }

    /// <summary>Clear the latch before keying the next NinoTNC.</summary>
    void Reset();
}
