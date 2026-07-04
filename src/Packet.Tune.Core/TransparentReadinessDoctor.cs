using System.Globalization;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>
/// Options for the <see cref="TransparentReadinessDoctor"/> probes.
/// </summary>
public sealed record TransparentReadinessOptions
{
    /// <summary>The Transparent-mode escape character (§1.7.2) — the byte sent ×3 to leave
    /// Transparent. Default <c>'+'</c> (the <c>+++</c> sequence).</summary>
    public char EscapeChar { get; init; } = '+';

    /// <summary>How many times the escape-recovers probe retries the <c>+++</c> escape (each with
    /// a full guard) before declaring the radio wedged. Default 2.</summary>
    public int EscapeAttempts { get; init; } = 2;

    /// <summary>The §1.7.2 idle guard either side of the escape burst. Default 2.1 s (the protocol
    /// minimum). Tests pass a short value.</summary>
    public TimeSpan EscapeGuard { get; init; } = TimeSpan.FromMilliseconds(2100);

    /// <summary>How long to wait for the confirming MODEL reply after each escape attempt. Default
    /// 3 s.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the baud-clean probe waits for the loopback frame to arrive at the peer.
    /// Default 8 s.</summary>
    public TimeSpan LoopbackTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Source callsign for the baud-clean loopback frame. Default <c>N0CALL</c>.</summary>
    public string Callsign { get; init; } = "N0CALL";
}

/// <summary>
/// The outcome of <see cref="TransparentReadinessDoctor.RunEnableAndEscapeProbesAsync"/>: the
/// probe rows plus the two facts a caller must act on — whether the radio is now wedged in
/// Transparent (needs a power cycle) and whether it entered and cleanly recovered (so a
/// baud-clean loopback may safely follow).
/// </summary>
/// <param name="Probes">The <c>transparent-mode-enabled</c> and <c>escape-recovers</c> probe rows.</param>
/// <param name="RadioWedged">When <c>true</c> the escape was ignored and the radio is STUCK in
/// Transparent mode — a power cycle is the only recovery. The caller must surface this loudly and
/// must not attempt further CCDI transactions.</param>
/// <param name="EnteredAndRecovered">When <c>true</c> the radio entered Transparent and the escape
/// returned it to Command mode — it is safe to continue (e.g. run the baud-clean probe).</param>
public sealed record TransparentReadinessResult(
    IReadOnlyList<DoctorProbe> Probes,
    bool RadioWedged,
    bool EnteredAndRecovered);

/// <summary>
/// The Transparent-mode readiness doctor: the "esoteric radio configuration" checks for a Tait
/// FFSK <b>Transparent</b> transport (the TNC-less <c>tait-transparent</c> port), folded into the
/// same <see cref="DoctorProbe"/> pass/fail/unknown shape the <see cref="TuningDoctor"/> uses.
/// Every check is <b>behavioral</b> — the codeplug settings that matter (Transparent Mode enabled,
/// "Ignore Escape Sequence", the two baud fields, FFSK Baud Rate) are not CCDI-readable, so each
/// probe exercises the behaviour and maps the result to a remedy naming the exact Data-form field.
/// <list type="number">
///   <item><b>transparent-mode-enabled</b> — attempt to enter Transparent mode; error 0/06 means
///     the feature is disabled in the radio's programming.</item>
///   <item><b>escape-recovers</b> — from Transparent, verify the <c>+++</c> escape returns the
///     radio to Command mode. THE RISKY ONE: a radio programmed "Ignore Escape Sequence" ON cannot
///     leave Transparent and this probe leaves it <b>wedged</b> (power-cycle to recover). It best-
///     effort retries the escape and reports the wedge loudly. Because entering Transparent is only
///     reversible while the escape works, the whole enter/escape pair is gated behind the doctor's
///     explicit interrupt/opt-in — never run by default.</item>
///   <item><b>baud-clean</b> — with two radios both in Transparent, loop a known frame through and
///     confirm it round-trips byte-for-byte (catches a command-vs-Transparent baud mismatch or an
///     FFSK-baud mismatch between the radios). Peer-dependent.</item>
/// </list>
/// </summary>
public static class TransparentReadinessDoctor
{
    /// <summary>Probe id: does the radio accept Transparent-mode entry.</summary>
    public const string EnabledProbe = "transparent-mode-enabled";

    /// <summary>Probe id: does the <c>+++</c> escape return the radio to Command mode.</summary>
    public const string EscapeProbe = "escape-recovers";

    /// <summary>Probe id: does a known frame round-trip byte-clean through both radios' FFSK.</summary>
    public const string BaudCleanProbe = "baud-clean";

    /// <summary>Verbatim remedy for a disabled Transparent mode (0/06).</summary>
    public const string EnabledRemedy =
        "Transparent Mode not enabled — enable it in the radio's Data form → General tab";

    /// <summary>Verbatim remedy for an ignored escape sequence (the wedge).</summary>
    public const string EscapeRemedy =
        "escape ignored — the radio cannot leave Transparent (power-cycle to recover); " +
        "uncheck 'Ignore Escape Sequence' in the Data form";

    /// <summary>Verbatim remedy for a garbled baud-clean round-trip.</summary>
    public const string BaudCleanRemedy =
        "data garbled — check Baud Rate (FFSK transparent mode) matches your terminal baud, " +
        "and FFSK Baud Rate matches on both radios";

    /// <summary>
    /// Run probes 1 (<c>transparent-mode-enabled</c>) and 2 (<c>escape-recovers</c>) against an
    /// already-open <paramref name="radio"/> that is currently in <b>Command mode</b>. On return
    /// the radio is left in Command mode when the escape works, or wedged in Transparent when it
    /// does not (see <see cref="TransparentReadinessResult.RadioWedged"/>).
    /// <para>
    /// <b>DISRUPTIVE / wedge-risk:</b> this enters Transparent mode, which is only reversible while
    /// the radio honours the escape. On a radio programmed "Ignore Escape Sequence" ON it will
    /// leave the radio stuck (recovery = power cycle). Callers must gate this behind an explicit
    /// interrupt/opt-in and document "may require a power cycle if the radio is misconfigured".
    /// </para>
    /// </summary>
    /// <param name="radio">An open Tait CCDI radio in Command mode.</param>
    /// <param name="options">Options; null = defaults.</param>
    /// <param name="onProbe">Optional live sink invoked once per probe as it completes.</param>
    /// <param name="cancellationToken">Cancels the run. Cancelling after entry can leave the radio
    /// in Transparent mode.</param>
    public static async Task<TransparentReadinessResult> RunEnableAndEscapeProbesAsync(
        TaitCcdiRadio radio,
        TransparentReadinessOptions? options = null,
        Action<DoctorProbe>? onProbe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radio);
        var opts = options ?? new TransparentReadinessOptions();
        var probes = new List<DoctorProbe>(2);
        void Add(DoctorProbe probe)
        {
            probes.Add(probe);
            onProbe?.Invoke(probe);
        }

        // Probe 1: attempt to enter Transparent mode. A radio with Transparent disabled rejects the
        // `t` command with error 0/06 and stays safely in Command mode (no wedge). A radio with it
        // enabled enters — and from here on the escape is the only way back out.
        try
        {
            await radio.EnterTransparentModeAsync(opts.EscapeChar, thsd: false, cancellationToken)
                .ConfigureAwait(false);
            Add(new DoctorProbe(
                EnabledProbe, DoctorOutcome.Pass,
                "the radio accepted the Transparent-mode entry command (t)", null));
        }
        catch (TaitCcdiException ex) when (ex.Error is { Category: '0', ErrorNumber: 0x06 })
        {
            Add(new DoctorProbe(
                EnabledProbe, DoctorOutcome.Fail,
                "the radio rejected Transparent-mode entry (error 0/06 — the feature is disabled in its programming)",
                EnabledRemedy));
            Add(new DoctorProbe(
                EscapeProbe, DoctorOutcome.Unknown,
                "not tested — the radio never entered Transparent mode", null));
            return new TransparentReadinessResult(probes, RadioWedged: false, EnteredAndRecovered: false);
        }
        catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
        {
            Add(new DoctorProbe(
                EnabledProbe, DoctorOutcome.Unknown,
                $"could not determine — the entry command did not complete ({ex.Message})", null));
            Add(new DoctorProbe(
                EscapeProbe, DoctorOutcome.Unknown,
                "not tested — Transparent-mode entry did not complete", null));
            return new TransparentReadinessResult(probes, RadioWedged: false, EnteredAndRecovered: false);
        }

        // Probe 2: the radio is now in Transparent mode. Verify the `+++` escape recovers Command
        // mode — best-effort retry before declaring the radio wedged.
        bool recovered = await radio.EscapeAndVerifyTransparentAsync(
            opts.EscapeAttempts, opts.EscapeGuard, opts.VerifyTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (recovered)
        {
            Add(new DoctorProbe(
                EscapeProbe, DoctorOutcome.Pass,
                "the +++ escape returned the radio to Command mode (a MODEL query answered)", null));
            return new TransparentReadinessResult(probes, RadioWedged: false, EnteredAndRecovered: true);
        }

        Add(new DoctorProbe(
            EscapeProbe, DoctorOutcome.Fail,
            string.Create(
                CultureInfo.InvariantCulture,
                $"the radio did NOT leave Transparent after {opts.EscapeAttempts} escape attempts — " +
                $"IT IS NOW WEDGED IN TRANSPARENT MODE and needs a POWER CYCLE to recover"),
            EscapeRemedy));
        return new TransparentReadinessResult(probes, RadioWedged: true, EnteredAndRecovered: false);
    }

    /// <summary>
    /// Run probe 3 (<c>baud-clean</c>): send a known frame from <paramref name="local"/> and
    /// confirm it arrives byte-for-byte at <paramref name="peer"/>. Both transports must already
    /// be in Transparent mode (e.g. two <see cref="TaitTransparentTransport"/>s). A byte-clean
    /// round-trip PASSes; an altered frame FAILs with the baud-mismatch remedy; nothing received
    /// within the timeout is <see cref="DoctorOutcome.Unknown"/> (the peer may be out of range or
    /// not in Transparent mode).
    /// </summary>
    /// <param name="local">The transmitting Transparent transport.</param>
    /// <param name="peer">The receiving Transparent transport (the peer radio).</param>
    /// <param name="options">Options; null = defaults.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public static async Task<DoctorProbe> RunBaudCleanProbeAsync(
        IAx25Transport local,
        IAx25Transport peer,
        TransparentReadinessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(peer);
        var opts = options ?? new TransparentReadinessOptions();

        var source = Callsign.Parse(opts.Callsign);
        // A UI frame whose info field spans diverse byte values (KISS FEND/FESC, NUL, high bytes)
        // so a baud/FFSK mismatch that garbles or drops bytes cannot round-trip clean by luck.
        byte[] sent = Ax25Frame.Ui(new Callsign("TXTEST"), source, BaudTestPattern()).ToBytes();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(opts.LoopbackTimeout);

        // Begin receiving before sending so a fast round-trip cannot race us.
        var receiveTask = FirstFrameAsync(peer, timeoutCts.Token);
        await local.SendAsync(sent, cancellationToken).ConfigureAwait(false);

        try
        {
            var received = await receiveTask.ConfigureAwait(false);
            byte[] got = received.Ax25.ToArray();
            if (got.AsSpan().SequenceEqual(sent))
            {
                return new DoctorProbe(
                    BaudCleanProbe, DoctorOutcome.Pass,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the {sent.Length}-byte test frame round-tripped byte-for-byte through both radios' FFSK"),
                    null);
            }

            int matching = CommonPrefix(sent, got);
            return new DoctorProbe(
                BaudCleanProbe, DoctorOutcome.Fail,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the test frame came back altered ({got.Length} bytes received vs {sent.Length} sent, " +
                    $"first {matching} bytes match) — the data is garbled"),
                BaudCleanRemedy);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DoctorProbe(
                BaudCleanProbe, DoctorOutcome.Unknown,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"no frame arrived at the peer within {opts.LoopbackTimeout.TotalSeconds:0}s — " +
                    $"the peer radio may be out of range, powered off, or not in Transparent mode"),
                null);
        }
    }

    /// <summary>The unknown row for the baud-clean probe when no peer radio is available.</summary>
    public static DoctorProbe BaudCleanNeedsPeer() => new(
        BaudCleanProbe, DoctorOutcome.Unknown,
        "needs a peer radio — pass a second Tait CCDI port so a known frame can be looped through both radios in Transparent mode",
        null);

    private static byte[] BaudTestPattern()
    {
        // "PDN-BAUD-CLEAN " marker then a spread of byte values including the KISS special bytes.
        byte[] marker = "PDN-BAUD-CLEAN "u8.ToArray();
        byte[] spread = new byte[64];
        for (int i = 0; i < spread.Length; i++)
        {
            spread[i] = (byte)(i * 4); // 0x00, 0x04, ... 0xFC — covers the full range in steps
        }
        byte[] specials = [0xC0, 0xDB, 0xDC, 0xDD, 0x00, 0x11, 0x13, 0xFF]; // FEND/FESC/XON/XOFF/…
        return [.. marker, .. spread, .. specials];
    }

    private static int CommonPrefix(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }
        return i;
    }

    private static async Task<Ax25InboundFrame> FirstFrameAsync(IAx25Transport transport, CancellationToken cancellationToken)
    {
        await foreach (var frame in transport.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            return frame;
        }
        throw new OperationCanceledException(cancellationToken);
    }
}
