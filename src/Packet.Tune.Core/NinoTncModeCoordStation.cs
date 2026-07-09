using System.Diagnostics;
using System.Globalization;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>
/// The live <see cref="IModeCoordStation"/>: a NinoTNC (mode switching + probe
/// traffic) paired with a Tait CCDI radio (channel switching). Mode changes are
/// SETHW +16 (RAM-only — a negotiation must never burn the flash) followed by a
/// throwaway settle frame (the NinoTNC applies a changed setting from the SECOND
/// frame) and a best-effort GETALL verify; channel changes are GO_TO_CHANNEL with a
/// FUNCTION 0/5/2 verify and one retry.
/// </summary>
public sealed class NinoTncModeCoordStation : IModeCoordStation
{
    // A SETHW command briefly disrupts the NinoTNC's ACKMODE TX-completion echo path (#591): a
    // settle frame sent immediately after SETHW has its echo dropped ~60% of the time (bench-
    // measured 8/12 mode-change + 7/12 same-mode SETHW), while a ~750 ms settling delay first
    // takes that to 0/12. So wait out the disruption, THEN send the settle frame.
    private static readonly TimeSpan SethwSettleDelay = TimeSpan.FromSeconds(1);
    // With the settling delay above, the echo lands fast and reliably (~520 ms bench-measured), so
    // the wait for it is short — a rare residual miss is logged + tolerated, never a whole-apply
    // stall. (Was 8 s, which every miss paid in full before the settling delay was understood.)
    private static readonly TimeSpan SettleTxTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan InterProbeGap = TimeSpan.FromMilliseconds(400);

    private readonly NinoTncSerialPort tnc;
    private readonly TaitCcdiRadio radio;
    private readonly Callsign source;
    private byte currentMode;

    /// <summary>Create over an open TNC + radio pair (lifetimes stay the caller's).</summary>
    /// <param name="tnc">The NinoTNC under mode control (also carries the probes).</param>
    /// <param name="radio">The paired radio (channel switching).</param>
    /// <param name="source">Source callsign for settle/probe frames.</param>
    /// <param name="initialMode">The mode the TNC is in now (scales probe TX timeouts
    /// until the first <see cref="ApplyModeAsync"/>).</param>
    public NinoTncModeCoordStation(NinoTncSerialPort tnc, TaitCcdiRadio radio, Callsign source, byte initialMode = 6)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        ArgumentNullException.ThrowIfNull(radio);
        this.tnc = tnc;
        this.radio = radio;
        this.source = source;
        currentMode = initialMode;
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc/>
    public async Task ApplyModeAsync(byte mode, CancellationToken cancellationToken = default)
    {
        try
        {
            await tnc.SetModeAsync(mode, persistToFlash: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
        {
            throw new ModeCoordException($"SETHW {mode} failed: {ex.Message}", ex);
        }
        currentMode = mode;

        // Let the SETHW settle before the settle frame: sending it immediately races the TNC's
        // SETHW processing and the ACKMODE echo is dropped ~60% of the time (#591); this delay
        // takes that to ~0. The mode is already applied — this only steadies the echo path.
        await Task.Delay(SethwSettleDelay, cancellationToken).ConfigureAwait(false);

        // Settle frame: the changed mode applies from the SECOND frame. With the settling delay
        // above the ACKMODE echo now lands reliably; a rare miss is still logged and tolerated
        // (the frame keyed regardless) but no longer stalls the apply for the old 8 s.
        byte[] settle = ModeSurvey.BuildSettleFrame(source, mode).ToBytes();
        try
        {
            await tnc.SendFrameWithAckAsync(settle, SettleTxTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke($"station: settle frame TX-completion not echoed within {SettleTxTimeout.TotalSeconds:0} s (continuing)");
        }

        // Best-effort verify — informational, never fatal (3.41 reports some
        // modes under firmware bytes the catalog aliases handle).
        try
        {
            var status = await tnc.GetAllAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            string running = status.RunningMode is { } rm
                ? string.Create(CultureInfo.InvariantCulture, $"{rm.Mode} ({rm.Name})")
                : string.Create(CultureInfo.InvariantCulture, $"unrecognised firmware byte 0x{status.FirmwareModeByte:X2}");
            Log?.Invoke($"station: SETHW {mode}+16 → GETALL running mode {running}" +
                        (status.RunningMode?.Mode == mode ? string.Empty : " — MISMATCH"));
        }
        catch (TimeoutException)
        {
            Log?.Invoke($"station: SETHW {mode}+16 sent; GETALL verify timed out");
        }
    }

    /// <inheritdoc/>
    public async Task ApplyChannelAsync(int channel, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await radio.GoToChannelAsync(channel, zone: null, cancellationToken).ConfigureAwait(false);
                var report = await radio.QueryCurrentChannelAsync(cancellationToken).ConfigureAwait(false);
                bool ok = int.TryParse(report.ChannelId, NumberStyles.None, CultureInfo.InvariantCulture, out int reported) &&
                          reported == channel;
                Log?.Invoke($"station: GO_TO_CHANNEL {channel} → reports kind '{report.Kind}' channel '{report.ChannelId}'" +
                            (ok ? string.Empty : " — MISMATCH" + (attempt == 1 ? ", retrying" : string.Empty)));
                if (ok)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or TaitCcdiException or IOException)
            {
                if (attempt == 2)
                {
                    throw new ModeCoordException($"channel switch to {channel} failed: {ex.Message}", ex);
                }
                Log?.Invoke($"station: channel switch attempt {attempt} failed ({ex.Message}) — retrying");
            }
        }
        throw new ModeCoordException($"radio did not verify on channel {channel} after 2 attempts");
    }

    /// <inheritdoc/>
    public async Task<ModeProbeTxStats> TransmitProbesAsync(
        int attemptTag, int count, CancellationToken cancellationToken = default)
    {
        var mode = NinoTncCatalog.TryGetByMode(currentMode) ?? NinoTncCatalog.ByMode[6];
        TimeSpan txTimeout = ModeSurvey.ReceiveTimeout(mode);
        var latencies = new List<double>();
        int confirmed = 0;
        for (int i = 1; i <= count; i++)
        {
            byte[] wire = ModeProbe.BuildFrame(source, attemptTag, i, count).ToBytes();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await tnc.SendFrameWithAckAsync(wire, txTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
                confirmed++;
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (TimeoutException)
            {
                // No TX-completion echo — the frame may still have keyed
                // (bench-observed sporadic echo absence). The receiver's decode
                // count is the verdict that matters.
                Log?.Invoke($"station: probe {i}/{count} TX-completion not echoed within {txTimeout.TotalSeconds:0} s");
            }
            await Task.Delay(InterProbeGap, cancellationToken).ConfigureAwait(false);
        }
        return new ModeProbeTxStats(count, confirmed, latencies.Count == 0 ? null : latencies.Average());
    }

    /// <inheritdoc/>
    public IModeProbeCounter BeginProbeCount(int attemptTag) => new ProbeCounter(tnc, attemptTag);

    private sealed class ProbeCounter : IModeProbeCounter
    {
        private readonly NinoTncSerialPort tnc;
        private readonly int attemptTag;
        private int count;
        private int disposed;

        public ProbeCounter(NinoTncSerialPort tnc, int attemptTag)
        {
            this.tnc = tnc;
            this.attemptTag = attemptTag;
            tnc.FrameReceived += OnFrame;
        }

        public int Count => Volatile.Read(ref count);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                tnc.FrameReceived -= OnFrame;
            }
        }

        private void OnFrame(object? sender, KissFrame frame)
        {
            if (ModeProbe.IsProbeFrame(frame, attemptTag))
            {
                Interlocked.Increment(ref count);
            }
        }
    }
}
