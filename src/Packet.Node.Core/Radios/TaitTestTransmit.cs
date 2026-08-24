using System.Globalization;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Hosting;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>Why a test transmission was refused.</summary>
public enum TaitTestTxError
{
    /// <summary>The port id is unknown or the port is not running (-> HTTP 404).</summary>
    NotFound,

    /// <summary>The port has no Tait CCDI radio, or the request is malformed (-> HTTP 400).</summary>
    BadRequest,

    /// <summary>Something else already holds the port - a tuning session or a programming run
    /// (-> HTTP 409).</summary>
    Conflict,

    /// <summary>The radio was reached but the test could not be completed (-> HTTP 502).</summary>
    RadioFault,
}

/// <summary>A test transmission was refused or failed; <see cref="Error"/> classifies it.</summary>
public sealed class TaitTestTxException : Exception
{
    /// <summary>Create with a classification and an operator-facing reason.</summary>
    public TaitTestTxException(TaitTestTxError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public TaitTestTxException()
    {
    }

    /// <summary>Message-only form (defaults to <see cref="TaitTestTxError.BadRequest"/>).</summary>
    public TaitTestTxException(string message)
        : base(message)
    {
    }

    /// <summary>Message + inner form (framework convention).</summary>
    public TaitTestTxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The refusal classification.</summary>
    public TaitTestTxError Error { get; }
}

/// <summary>
/// The service manual's own forward/reverse go-no-go figures for one band split, in detector
/// millivolts read back by CCTM 318 / 319.
/// </summary>
/// <remarks>
/// From MMA-00005-05 Table 11.3 (bodies over 25 W, p.278) and Table 12.3 (25 W bodies, p.350).
/// Both tables are specified <b>at High power into a good 50 ohm load</b> and nowhere else: there
/// is no per-power-step table, and the manual publishes no millivolts-to-watts transfer function,
/// which is why nothing here converts a reading into watts.
/// <para>
/// The two tables disagree, because a 25 W body couples less forward voltage than a 50 W one, and
/// the product code does not say which body a radio has. So both are carried and both are shown;
/// the ceiling used for a verdict is the more forgiving of the two, which is the direction that
/// does not cry wolf.
/// </para>
/// <para>
/// <b>The manual contradicts itself on the range</b> and this follows the tables. Table 4.5 (p.127)
/// describes CCTM 318's answer as "a value from 0 to 1100 mV", which cannot be reconciled with
/// Tables 11.3 and 12.3 expecting 1100-4000 mV at High power. Our own bench capture on a TM8110
/// (docs/research/tait-ccdi-spike.md: 388 mV forward at Very Low) scales toward the tables, not the
/// 1100 mV ceiling, so the tables are treated as authoritative. A reading from a real radio at High
/// power into a dummy load would settle it outright, and is worth taking when one is to hand.
/// </para>
/// </remarks>
/// <param name="Code">The band designator, e.g. <c>B1</c>.</param>
/// <param name="HighPowerForwardMinMillivolts">Bottom of the expected forward reading at High power.</param>
/// <param name="HighPowerForwardMaxMillivolts">Top of the expected forward reading at High power.</param>
/// <param name="ReverseCeilingMillivolts">The reverse reading the manual expects to stay under at
/// High power into a good load - the more forgiving of the two tables where both list the band.</param>
public sealed record TaitPowerDetectorReference(
    string Code,
    int HighPowerForwardMinMillivolts,
    int HighPowerForwardMaxMillivolts,
    int ReverseCeilingMillivolts);

/// <summary>The service manual's detector figures, by band split.</summary>
public static class TaitPowerDetectorReferences
{
    // Forward range spans both tables (min of the mins, max of the maxes) because the body class is
    // not knowable from the product code; the reverse ceiling is the more forgiving figure.
    private static readonly Dictionary<string, TaitPowerDetectorReference> ByCode =
        new(StringComparer.Ordinal)
        {
            ["A4"] = new("A4", 2700, 3900, 700),    // 12.3 only
            ["B1"] = new("B1", 1100, 3400, 500),    // 11.3: 2600-3400 <500; 12.3: 1100-2000 <500
            ["C0"] = new("C0", 1100, 2000, 500),    // 12.3 only
            ["D1"] = new("D1", 1600, 2500, 700),    // 12.3 only
            ["G2"] = new("G2", 3100, 3800, 600),    // 11.3 only
            ["H5"] = new("H5", 2500, 3900, 1000),   // 11.3: 3200-3900 <700; 12.3: 2500-3500 <1000
            ["H6"] = new("H6", 2800, 3900, 1000),   // 12.3 only
            ["H7"] = new("H7", 3300, 4000, 900),    // 11.3 only
            ["K5"] = new("K5", 2000, 2800, 500),    // 11.3 only
        };

    /// <summary>The figures for a band split, or null when the manual tabulates none.</summary>
    /// <param name="code">The two-character band designator.</param>
    public static TaitPowerDetectorReference? For(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var reference) ? reference : null;
}

/// <summary>What a test transmission found.</summary>
/// <param name="PortId">The port whose radio was keyed.</param>
/// <param name="At">When the test ran.</param>
/// <param name="KeyedMilliseconds">How long the transmitter was actually held on.</param>
/// <param name="RadioModel">The radio's product code, when it answered with one.</param>
/// <param name="RadioSerial">The radio's CCDI serial number.</param>
/// <param name="Band">The band split parsed from that product code, e.g. <c>B1</c>.</param>
/// <param name="Keyed">Whether the radio reported its transmitter on (PROGRESS PTT edge).</param>
/// <param name="Inhibited">Whether the radio refused the transmission (PROGRESS 02 Tx Inhibited).</param>
/// <param name="IdleForwardMillivolts">Forward detector reading with the transmitter off - the
/// detector's zero-power offset, subtracted from the keyed reading.</param>
/// <param name="IdleReverseMillivolts">Reverse detector reading with the transmitter off.</param>
/// <param name="ForwardMillivolts">Median forward detector reading while keyed (raw, CCTM 318).</param>
/// <param name="ReverseMillivolts">Median reverse detector reading while keyed (raw, CCTM 319).</param>
/// <param name="ForwardOverIdleMillivolts">Forward reading minus the idle offset.</param>
/// <param name="ReverseOverIdleMillivolts">Reverse reading minus the idle offset.</param>
/// <param name="ReflectionCoefficient">Estimated voltage reflection coefficient - see
/// <see cref="TaitTestTransmitService"/> remarks for exactly what this is and is not.</param>
/// <param name="Vswr">Estimated VSWR from that coefficient, or null when the forward reading was
/// too small for the estimate to mean anything.</param>
/// <param name="Foldback">Whether the forward reading collapsed during the key, which the manual
/// attributes to the antenna VSWR threshold being exceeded and the PA shutting back.</param>
/// <param name="Verdict"><c>ok</c> / <c>elevated</c> / <c>high-reverse</c> / <c>foldback</c> /
/// <c>inhibited</c> / <c>no-transmit</c> / <c>unknown</c>.</param>
/// <param name="Reference">The service manual's figures for this band, when it tabulates any.</param>
/// <param name="Notes">Operator-facing findings, in the order they matter.</param>
/// <param name="Samples">How many detector reads landed while the transmitter was keyed.</param>
public sealed record TaitTestTxResult(
    string PortId,
    DateTimeOffset At,
    int KeyedMilliseconds,
    string? RadioModel,
    string? RadioSerial,
    string? Band,
    bool Keyed,
    bool Inhibited,
    int? IdleForwardMillivolts,
    int? IdleReverseMillivolts,
    int? ForwardMillivolts,
    int? ReverseMillivolts,
    int? ForwardOverIdleMillivolts,
    int? ReverseOverIdleMillivolts,
    double? ReflectionCoefficient,
    double? Vswr,
    bool Foldback,
    string Verdict,
    TaitPowerDetectorReference? Reference,
    IReadOnlyList<string> Notes,
    int Samples);

/// <summary>
/// Keys the port's attached Tait for about a second with no modulation and reads its forward and
/// reverse power detectors while it is up: the "is this antenna actually connected" check, from the
/// panel, with no test set.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the numbers are.</b> CCTM 318 and 319 return the DC millivolts on the forward and
/// reverse detector diodes of the directional coupler (MMA-00005-05 Table 4.5, p.127). They are
/// uncalibrated, the manual publishes no millivolts-to-watts transfer function, and Tait's own
/// service tooling never computes VSWR from them anywhere - the reverse detector exists to drive
/// mismatch <i>protection</i>, not measurement. So this reports the readings, and an estimate
/// clearly labelled as one. It does not report watts.
/// </para>
/// <para>
/// <b>Where the estimate comes from.</b> Detector voltage goes as the square root of power (the
/// calibration database stores "Power Level Sqrt" constants), so the offset-corrected voltage ratio
/// reverse/forward is an estimate of the voltage reflection coefficient directly, and
/// (1+r)/(1-r) an estimate of VSWR. That holds only if the two detectors share a transfer constant
/// and the coupler's directivity floor is well below the reflected signal - neither of which Tait
/// specifies. Treat a figure from this as an indication, and a change in it between runs on one
/// station as the real signal.
/// </para>
/// <para>
/// <b>The failure the radio does not tell you about.</b> On high reverse power the radio reduces
/// power and sounds two warbles at whoever is standing next to it (MMA-00030-14, UI Preferences,
/// "High Reverse Power Warning"); there is no CCDI message for it, so software can only infer it.
/// Two inferences are made here: the forward reading collapsing mid-key, which the manual's own
/// fault-finding calls out as "the antenna VSWR threshold has been exceeded and the PA has shut
/// down to very low power" (Task 4, p.280), and a reverse reading over the band's tabulated
/// ceiling. If the codeplug has <c>Override VSWR Foldback Power</c> set the radio refuses to
/// transmit at all instead, and <i>that</i> does reach software, as PROGRESS 02 Tx Inhibited.
/// </para>
/// </remarks>
public sealed partial class TaitTestTransmitService
{
    /// <summary>Shortest test key.</summary>
    public const int MinimumMilliseconds = 300;

    /// <summary>Longest test key. A test transmission is a carrier with nothing on it, so it is
    /// kept short: this is an antenna check, not a soak test.</summary>
    public const int MaximumMilliseconds = 5_000;

    /// <summary>The default key length - Tom's "keys the radio for a second".</summary>
    public const int DefaultMilliseconds = 1_000;

    /// <summary>
    /// Below this corrected forward reading the estimate is not reported: at low detector voltages
    /// the reverse reading is dominated by the diode knee and the coupler's directivity floor, and
    /// a ratio computed there is noise wearing a decimal point.
    /// </summary>
    public const int MinimumForwardForEstimateMillivolts = 200;

    /// <summary>The caveat every response carries: this puts a carrier on the air.</summary>
    public const string Caveat =
        "This TRANSMITS: the radio is keyed on its current channel for about a second with no " +
        "modulation, which is a carrier on air on that frequency. Make sure an antenna or a dummy " +
        "load is connected - keying into an open socket is what this test is meant to find, but it " +
        "is still a transmission into a mismatch. The port stays in service throughout, so the " +
        "node may briefly key over the top of it.";

    private readonly NodeHostedService host;
    private readonly Func<string, bool> portBusy;
    private readonly ILogger<TaitTestTransmitService> logger;
    private readonly TimeProvider clock;

    /// <summary>Create the service.</summary>
    /// <param name="host">The node host (supervisor access to the running port's radio).</param>
    /// <param name="logger">Logger for test runs and radio faults.</param>
    /// <param name="portBusy">Optional: whether some other operator-initiated session holds a port
    /// (a tuning session or a programming run). A busy port is refused.</param>
    /// <param name="clock">Time source; null = system.</param>
    public TaitTestTransmitService(
        NodeHostedService host,
        ILogger<TaitTestTransmitService> logger,
        Func<string, bool>? portBusy = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(logger);
        this.host = host;
        this.logger = logger;
        this.portBusy = portBusy ?? (_ => false);
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Key the port's radio for <paramref name="milliseconds"/> and report what the detectors said.
    /// The transmitter is always unkeyed, on every exit path.
    /// </summary>
    /// <param name="portId">The running port whose radio to key.</param>
    /// <param name="milliseconds">How long to hold the key, clamped to
    /// <see cref="MinimumMilliseconds"/>..<see cref="MaximumMilliseconds"/>.</param>
    /// <param name="cancellationToken">Cuts the test short (the transmitter still comes down).</param>
    /// <exception cref="TaitTestTxException">The test was refused or the radio faulted.</exception>
    public async Task<TaitTestTxResult> RunAsync(
        string portId, int? milliseconds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(portId);
        int keyMs = Math.Clamp(milliseconds ?? DefaultMilliseconds, MinimumMilliseconds, MaximumMilliseconds);

        var running = host.Supervisor?.GetPort(portId)
            ?? throw new TaitTestTxException(TaitTestTxError.NotFound, $"port '{portId}' is not running");

        // Resolve the LIVE driver each time: a head-end-bound radio sits behind the reconnect
        // facade, so a cached handle would go stale across a reconnect.
        if (RadioControls.LiveTait(running.Radio) is not { } tait)
        {
            throw new TaitTestTxException(
                TaitTestTxError.BadRequest,
                $"port '{portId}' has no Tait CCDI radio attached - a test transmission keys the radio over its " +
                "CCDI control channel and reads its power detectors, neither of which anything else offers." +
                RadioControls.WhyNoRadio(host.Supervisor, portId));
        }

        if (portBusy(portId))
        {
            throw new TaitTestTxException(
                TaitTestTxError.Conflict,
                $"port '{portId}' is busy with a tuning session or a programming run - stop it first");
        }

        LogTestStarted(portId, keyMs);
        try
        {
            return await MeasureAsync(portId, tait, keyMs, cancellationToken).ConfigureAwait(false);
        }
        catch (TaitCcdiException ex)
        {
            LogTestFailed(logger, portId, ex);
            throw new TaitTestTxException(
                TaitTestTxError.RadioFault, $"the radio did not complete the test: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            LogTestFailed(logger, portId, ex);
            throw new TaitTestTxException(
                TaitTestTxError.RadioFault,
                $"the radio stopped answering during the test: {ex.Message}. The transmitter was unkeyed.");
        }
    }

    private async Task<TaitTestTxResult> MeasureAsync(
        string portId, TaitCcdiRadio tait, int keyMs, CancellationToken cancellationToken)
    {
        var start = clock.GetUtcNow();
        bool inhibited = false;
        bool keyed = false;

        void OnInhibited(object? sender, TaitTransmitInhibited e) => Volatile.Write(ref inhibited, true);
        void OnTransmitter(object? sender, TransmitterStateChange e)
        {
            if (e.Transmitting)
            {
                Volatile.Write(ref keyed, true);
            }
        }

        // PROGRESS has to be on for either edge to arrive at all. A radio without pdn-basic applied
        // has it off, which is why the absence of a PTT edge is never on its own read as a refusal.
        await tait.SetProgressMessagesAsync(true, cancellationToken).ConfigureAwait(false);
        TaitRadioIdentity identity = await tait.QueryIdentityAsync(cancellationToken).ConfigureAwait(false);

        tait.TransmitInhibited += OnInhibited;
        tait.TransmitterStateChanged += OnTransmitter;
        try
        {
            var idleForward = new List<int>();
            var idleReverse = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                await SampleAsync(tait, idleForward, idleReverse, cancellationToken).ConfigureAwait(false);
            }

            var forward = new List<int>();
            var reverse = new List<int>();
            var forwardOrder = new List<int>();
            int heldMs;

            await tait.SetTransmitterAsync(true, cancellationToken).ConfigureAwait(false);
            long keyedAt = Environment.TickCount64;
            try
            {
                while (Environment.TickCount64 - keyedAt < keyMs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int before = forward.Count;
                    await SampleAsync(tait, forward, reverse, cancellationToken).ConfigureAwait(false);
                    if (forward.Count > before)
                    {
                        forwardOrder.Add(forward[^1]);
                    }
                }
            }
            finally
            {
                heldMs = (int)(Environment.TickCount64 - keyedAt);
                // Never cancellable: a cancelled test must still put the transmitter down.
                await tait.SetTransmitterAsync(false, CancellationToken.None).ConfigureAwait(false);
            }

            var result = Summarise(
                portId, start, heldMs, identity, Volatile.Read(ref keyed), Volatile.Read(ref inhibited),
                idleForward, idleReverse, forward, reverse, forwardOrder);
            LogTestResult(
                portId, result.Verdict, result.ForwardOverIdleMillivolts ?? -1,
                result.ReverseOverIdleMillivolts ?? -1);
            return result;
        }
        finally
        {
            tait.TransmitInhibited -= OnInhibited;
            tait.TransmitterStateChanged -= OnTransmitter;
        }
    }

    private static async Task SampleAsync(
        TaitCcdiRadio tait, List<int> forward, List<int> reverse, CancellationToken cancellationToken)
    {
        if (await tait.ReadForwardPowerAsync(cancellationToken).ConfigureAwait(false) is { } f)
        {
            forward.Add(f);
        }

        if (await tait.ReadReversePowerAsync(cancellationToken).ConfigureAwait(false) is { } r)
        {
            reverse.Add(r);
        }
    }

    /// <summary>
    /// Turn a run's raw detector samples into the result the operator sees. Pure, and internal so
    /// the verdict logic - which is the part with the judgement in it - is unit-testable without a
    /// node host, a port or a radio.
    /// </summary>
    /// <param name="portId">The port the samples came from.</param>
    /// <param name="at">When the test ran.</param>
    /// <param name="heldMs">How long the transmitter was held on.</param>
    /// <param name="identity">The radio's identity, for the model and the band split.</param>
    /// <param name="keyed">Whether a transmitter-on PROGRESS edge arrived.</param>
    /// <param name="inhibited">Whether a Tx Inhibited PROGRESS message arrived.</param>
    /// <param name="idleForward">Forward detector reads with the transmitter off.</param>
    /// <param name="idleReverse">Reverse detector reads with the transmitter off.</param>
    /// <param name="forward">Forward detector reads while keyed.</param>
    /// <param name="reverse">Reverse detector reads while keyed.</param>
    /// <param name="forwardOrder">The keyed forward reads in the order they arrived, for the
    /// collapse test - <paramref name="forward"/> is only ever used as a bag.</param>
    internal static TaitTestTxResult Summarise(
        string portId, DateTimeOffset at, int heldMs, TaitRadioIdentity identity, bool keyed, bool inhibited,
        List<int> idleForward, List<int> idleReverse, List<int> forward, List<int> reverse,
        List<int> forwardOrder)
    {
        // The product code, which is what the band split is parsed from and what an operator
        // recognises the radio by - the MODEL query's RUTYPE triple is far vaguer.
        string? model = identity.Versions.GetValueOrDefault(TaitBandCatalog.ProductCodeRecord);
        TaitBand? band = identity.Band;
        var reference = TaitPowerDetectorReferences.For(band?.Code);

        int? idleF = Median(idleForward);
        int? idleR = Median(idleReverse);
        int? fwd = Median(forward);
        int? rev = Median(reverse);
        int? fwdOver = fwd is { } f && idleF is { } fi ? Math.Max(0, f - fi) : fwd;
        int? revOver = rev is { } r && idleR is { } ri ? Math.Max(0, r - ri) : rev;

        // Foldback: the manual's own signature for the antenna VSWR threshold being exceeded is the
        // forward reading collapsing part-way through a key while the PA shuts back to very low
        // power. Compare the run's peak against where it ended up.
        bool foldback = false;
        if (forwardOrder.Count >= 3)
        {
            int peak = forwardOrder.Max();
            int last = forwardOrder[^1];
            foldback = peak >= MinimumForwardForEstimateMillivolts && last < peak / 2;
        }

        double? rho = null;
        double? vswr = null;
        if (fwdOver is { } fo && revOver is { } ro && fo >= MinimumForwardForEstimateMillivolts)
        {
            rho = Math.Clamp((double)ro / fo, 0, 0.98);
            vswr = (1 + rho.Value) / (1 - rho.Value);
        }

        var notes = new List<string>();
        string verdict;
        if (inhibited)
        {
            verdict = "inhibited";
            notes.Add(
                "The radio REFUSED to transmit (CCDI progress 02, Tx Inhibited). The radio does not say why. " +
                "The manual's causes are: high reverse power with 'Override VSWR Foldback Power' set in the " +
                "codeplug, over-temperature, the synthesiser out of lock, the channel's power set to Off, " +
                "channel activity with Tx Inhibit programmed, or a Tx lockout timer.");
        }
        else if (fwdOver is null or 0 && !keyed)
        {
            verdict = "no-transmit";
            notes.Add(
                "Nothing came out: the radio reported no transmitter edge and the forward detector never moved. " +
                "Check that the channel's power is not set to Off and that the radio is not muted or inhibited.");
        }
        else if (foldback)
        {
            verdict = "foldback";
            notes.Add(
                "The forward power COLLAPSED during the key. The service manual's fault-finding calls this out " +
                "directly: the antenna VSWR threshold has been exceeded and the PA has shut back to very low " +
                "power. On the radio itself this is the two-warble High Reverse Power warning. Check the " +
                "antenna, the feeder and the connectors before transmitting again.");
        }
        else if (reference is { } re && revOver is { } rv && rv > re.ReverseCeilingMillivolts)
        {
            verdict = "high-reverse";
            notes.Add(
                $"Reverse power is {rv} mV, over the {re.ReverseCeilingMillivolts} mV the service manual expects " +
                $"a {re.Code} radio to stay under at High power into a good load. Suspect the antenna, the " +
                "feeder or a connector.");
        }
        else if (vswr >= 3.0)
        {
            verdict = "high-reverse";
            notes.Add("The estimated VSWR is high enough to be worth investigating before transmitting in anger.");
        }
        else if (vswr >= 2.0)
        {
            verdict = "elevated";
            notes.Add("The estimated VSWR is higher than a well-matched antenna would give, but not alarming.");
        }
        else if (vswr is not null)
        {
            verdict = "ok";
        }
        else
        {
            verdict = "unknown";
            notes.Add(
                $"The forward detector read {fwdOver?.ToString(CultureInfo.InvariantCulture) ?? "nothing"} mV over its idle offset, which is " +
                $"below the {MinimumForwardForEstimateMillivolts} mV this needs before a reverse/forward ratio " +
                "means anything. Try again at a higher power step.");
        }

        if (vswr is not null)
        {
            notes.Add(
                "The VSWR figure is an ESTIMATE from uncalibrated detectors: CCTM 318/319 are raw detector " +
                "millivolts, Tait publishes no millivolts-to-watts curve, and its own service tooling never " +
                "computes VSWR from them. Trust a change between runs on this station over the absolute number.");
        }

        if (reference is { } r2)
        {
            notes.Add(
                $"For reference, the service manual expects a {r2.Code} radio at High power into a good load to " +
                $"read {r2.HighPowerForwardMinMillivolts}-{r2.HighPowerForwardMaxMillivolts} mV forward and under " +
                $"{r2.ReverseCeilingMillivolts} mV reverse. Those figures are High power only, and span both the " +
                "25 W and the larger bodies, which the product code does not tell them apart.");
        }
        else if (band is not null)
        {
            notes.Add($"The service manual tabulates no detector figures for the {band.Code} band split.");
        }

        if (!keyed)
        {
            notes.Add(
                "The radio sent no transmitter-on progress message. That is expected if PROGRESS output is off " +
                "in its codeplug (apply pdn-basic), and only meaningful alongside the detector readings.");
        }

        return new TaitTestTxResult(
            portId, at, heldMs, model, identity.SerialNumber, band?.Code, keyed, inhibited,
            idleF, idleR, fwd, rev, fwdOver, revOver, rho, vswr, foldback, verdict, reference, notes,
            forward.Count);
    }

    private static int? Median(List<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = new List<int>(values);
        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    [LoggerMessage(EventId = 7800, Level = LogLevel.Information,
        Message = "port {PortId}: test transmission, keying for {KeyMilliseconds} ms")]
    private partial void LogTestStarted(string portId, int keyMilliseconds);

    [LoggerMessage(EventId = 7801, Level = LogLevel.Information,
        Message = "port {PortId}: test transmission {Verdict} (fwd {ForwardMillivolts} mV, rev {ReverseMillivolts} mV over idle)")]
    private partial void LogTestResult(string portId, string verdict, int forwardMillivolts, int reverseMillivolts);

    [LoggerMessage(EventId = 7802, Level = LogLevel.Warning,
        Message = "port {PortId}: the test transmission failed")]
    private static partial void LogTestFailed(ILogger logger, string portId, Exception exception);
}
