using Packet.Rig;

namespace Packet.Radio.Tait;

/// <summary>
/// The station-control (<see cref="IRigControl"/>) view of a Tait CCDI radio — the third,
/// deliberately-different implementation pressure-testing the <c>Packet.Rig</c> abstraction
/// from the channelised-PMR side (after rigctld and flrig; plan OQ-011). A thin adapter over
/// <see cref="TaitCcdiRadio"/>: the radio object stays the owner of the wire, the packet-medium
/// seam (<c>IRadioControl</c>: RSSI/DCD/SDM) and everything Tait-native; this class only
/// re-presents the slice CCDI can honestly serve through the CAT-shaped contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is (and isn't) advertised, and why:</b>
/// </para>
/// <list type="bullet">
/// <item><description><b>PTT set + get</b> — <c>FUNCTION 9</c> keying, with get served from
/// last-known state (see <see cref="GetPttAsync"/> for the PROGRESS dependency).</description></item>
/// <item><description><b>RF-power meter (relative)</b> — CCTM 318's forward-power detector as a
/// fraction of its 0–1200 mV full scale. A genuine meter-deflection fraction, which is exactly
/// this member's contract.</description></item>
/// <item><description><b>NOT watts, NOT SWR</b> — CCTM 318/319 are raw detector millivolts, "a
/// VSWR / antenna-health proxy", not calibrated power. Deriving watts or an SWR ratio needs a
/// detector-calibration decision that hasn't been made; until then advertising them would
/// launder detector volts into units the abstraction promises. Raw readings stay available on
/// <see cref="TaitCcdiRadio.ReadForwardPowerAsync"/> / <see cref="TaitCcdiRadio.ReadReversePowerAsync"/>.</description></item>
/// <item><description><b>NOT frequency</b> — the tuned frequency is not CCDI-readable at all
/// (only the band split is; see <see cref="TaitRadioIdentity.Band"/>), and frequency <em>set</em>
/// is a CCR-session retune that is still unproven on the bench (plan §5.11). The flags light up
/// when that lands.</description></item>
/// <item><description><b>NOT mode</b> — an FM PMR radio has no operating-mode concept to
/// control; this is precisely the divergence the capability flags exist for.</description></item>
/// </list>
/// </remarks>
public sealed class TaitRigControl : IRigControl
{
    /// <summary>CCTM 318/319 detector full scale in millivolts (manual: raw 0–1200 mV).</summary>
    public const double DetectorFullScaleMillivolts = 1200.0;

    private readonly TaitCcdiRadio radio;
    private readonly bool ownsRadio;
    private volatile bool lastKnownPtt;
    private bool keyedByUs;
    private bool disposed;

    private TaitRigControl(TaitCcdiRadio radio, bool ownsRadio, RigInfo info)
    {
        this.radio = radio;
        this.ownsRadio = ownsRadio;
        Info = info;
        radio.TransmitterStateChanged += OnTransmitterStateChanged;
    }

    /// <inheritdoc />
    public RigCapabilities Capabilities =>
        RigCapabilities.PttGet | RigCapabilities.PttSet | RigCapabilities.RfPowerMeter;

    /// <inheritdoc />
    public RigInfo Info { get; }

    /// <summary>
    /// Wrap <paramref name="radio"/>, querying its identity over the wire to populate
    /// <see cref="Info"/>. The radio must be in Command mode (identity queries don't run over a
    /// Transparent byte pipe). <paramref name="ownsRadio"/> transfers disposal: when true,
    /// disposing the adapter disposes the radio; when false (the node case — the port supervisor
    /// owns the radio) disposal only detaches and best-effort-unkeys anything this adapter keyed.
    /// </summary>
    public static async Task<TaitRigControl> CreateAsync(
        TaitCcdiRadio radio, bool ownsRadio = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(radio);
        var identity = await radio.QueryIdentityAsync(cancellationToken).ConfigureAwait(false);
        return new TaitRigControl(radio, ownsRadio, InfoFrom(identity));
    }

    /// <summary>Wrap <paramref name="radio"/> with an already-known identity (the node caches
    /// identity at adoption) — no wire traffic.</summary>
    public static TaitRigControl Create(TaitCcdiRadio radio, TaitRadioIdentity identity, bool ownsRadio = false)
    {
        ArgumentNullException.ThrowIfNull(radio);
        ArgumentNullException.ThrowIfNull(identity);
        return new TaitRigControl(radio, ownsRadio, InfoFrom(identity));
    }

    private static RigInfo InfoFrom(TaitRadioIdentity identity)
        => new("Tait CCDI", "Tait", identity.ProductName);

    /// <inheritdoc />
    public ValueTask<long> GetFrequencyAsync(CancellationToken cancellationToken = default)
        => throw FrequencyNotSupported();

    /// <inheritdoc />
    public ValueTask SetFrequencyAsync(long frequencyHz, CancellationToken cancellationToken = default)
        => throw FrequencyNotSupported();

    private static NotSupportedException FrequencyNotSupported() => new(
        "CCDI cannot read the tuned frequency (only the band split — see TaitRadioIdentity.Band), " +
        "and frequency programming is a CCR-session operation not yet wired to this seam.");

    /// <inheritdoc />
    public ValueTask<RigModeState> GetModeAsync(CancellationToken cancellationToken = default)
        => throw ModeNotSupported();

    /// <inheritdoc />
    public ValueTask SetModeAsync(RigMode mode, int? passbandHz = null, CancellationToken cancellationToken = default)
        => throw ModeNotSupported();

    private static NotSupportedException ModeNotSupported()
        => new("A Tait PMR radio has no operating-mode control — it is an FM transceiver.");

    /// <inheritdoc />
    /// <remarks>
    /// Served from last-known state, not a wire query (CCDI has no TX-state poll): fused from
    /// this adapter's own <see cref="SetPttAsync"/> calls and the radio's unsolicited PROGRESS
    /// transmit/receive edges. External keying (the fist mic) is therefore only observed when
    /// PROGRESS output is enabled (<see cref="TaitCcdiRadio.SetProgressMessagesAsync"/> — the
    /// node does this at port bring-up). Fresh state reads false.
    /// </remarks>
    public ValueTask<bool> GetPttAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ValueTask.FromResult(lastKnownPtt);
    }

    /// <inheritdoc />
    /// <remarks>CCDI-forced TX ignores the radio's TX timer, hence the dispose-unkey guarantee
    /// (also enforced by <see cref="TaitCcdiRadio"/> itself when it owns the keying).</remarks>
    public async ValueTask SetPttAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await MapAsync(async () =>
        {
            await radio.SetTransmitterAsync(transmit, cancellationToken).ConfigureAwait(false);
            return true;
        }, "FUNCTION 9 (transmitter)").ConfigureAwait(false);
        keyedByUs = transmit;
        lastKnownPtt = transmit;
    }

    /// <inheritdoc />
    /// <remarks>CCTM 318 forward-power detector reading over its 0–1200 mV full scale —
    /// meaningful while transmitting; idle reads ~0.</remarks>
    public async ValueTask<double> ReadRfPowerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var millivolts = await MapAsync(
            () => radio.ReadForwardPowerAsync(cancellationToken), "CCTM 318 (forward power)")
            .ConfigureAwait(false)
            ?? throw new RigProtocolException("CCTM 318 answered without a parseable forward-power value.");
        return Math.Clamp(millivolts / DetectorFullScaleMillivolts, 0.0, 1.0);
    }

    /// <inheritdoc />
    public ValueTask<double> ReadRfPowerWattsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "CCTM 318 is a raw detector reading (0–1200 mV), not calibrated watts — use " +
            "ReadRfPowerAsync (relative) or the raw TaitCcdiRadio.ReadForwardPowerAsync.");

    /// <inheritdoc />
    public ValueTask<double> ReadSwrAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "SWR needs a detector-calibration decision before it can be reported as a ratio — " +
            "the raw forward/reverse detector readings are on TaitCcdiRadio.ReadForwardPowerAsync/" +
            "ReadReversePowerAsync (CCTM 318/319).");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        radio.TransmitterStateChanged -= OnTransmitterStateChanged;

        if (keyedByUs && !ownsRadio)
        {
            // The radio outlives this adapter, so its own dispose-unkey won't run — unkey here.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await radio.SetTransmitterAsync(false, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TaitCcdiException or TimeoutException or IOException
                or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // Best effort only — the link (or the radio object) may already be gone.
            }
        }

        if (ownsRadio)
        {
            await radio.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnTransmitterStateChanged(object? sender, TransmitterStateChange change)
        => lastKnownPtt = change.Transmitting;

    /// <summary>Run a driver operation, translating Tait-driver failures into the
    /// <see cref="RigException"/> taxonomy so <see cref="IRigControl"/> callers handle one
    /// error model across backends.</summary>
    private static async Task<T> MapAsync<T>(Func<Task<T>> operation, string what)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (TaitCcdiException ex)
        {
            throw new RigCommandException(
                $"The radio rejected {what}: {ex.Message}", ex.Error.ErrorNumber);
        }
        catch (TimeoutException ex)
        {
            throw new RigTimeoutException($"The radio gave no reply to {what}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new RigConnectionException($"The CCDI link failed during {what}.", ex);
        }
    }
}
