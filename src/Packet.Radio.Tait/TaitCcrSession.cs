using System.Globalization;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait;

/// <summary>
/// An active CCR (Computer-Controlled Radio) session — the TM8100's run-time
/// channel-programming interpreter (manual §2), entered via
/// <see cref="TaitCcdiRadio.EnterCcrModeAsync"/>. While a session is live the radio accepts
/// only CCR commands; nothing configured here survives <see cref="ExitAsync"/> (a soft reset)
/// or a power cycle — the manual's model is that the controller is the persistent store, using
/// <see cref="PulseAsync"/> (recommended every 10 s) to notice a rebooted radio and reprogram
/// it.
/// </summary>
/// <remarks>
/// Command effect timing per the manual: receive frequency, RX CTCSS/DCS, bandwidth, volume
/// and monitor apply <b>immediately</b> (allow 20 ms synthesizer settling after a frequency
/// change); TX frequency, TX CTCSS/DCS and TX power are <b>latched</b> and apply at the next
/// PTT / Selcall-encode activity. A positive acknowledgement means <i>accepted</i>, not
/// <i>executed</i>.
/// </remarks>
public sealed class TaitCcrSession
{
    private readonly TaitCcdiRadio radio;

    internal TaitCcrSession(TaitCcdiRadio radio)
    {
        this.radio = radio;
        radio.MessageReceived += OnMessage;
    }

    /// <summary>Unsolicited Selcall decode reports (§2.9.3) — tones heard on channel, per the
    /// notify filter set by <see cref="SetSelcallParametersAsync"/>.</summary>
    public event EventHandler<CcrSelcallDecodeMessage>? SelcallDecoded;

    /// <summary>PTT-approaching-transmit-limit warnings (§2.9.2): fired 10 s before the radio
    /// force-unkeys.</summary>
    public event EventHandler? TransmitLimitWarning;

    /// <summary>Go to receive frequency (§2.8.2), in Hz. Immediate; the receiver retunes and
    /// needs ~20 ms to settle. The manual states the RX frequency can be changed at least every
    /// 20 ms — this is the frequency-agility primitive.</summary>
    public Task SetReceiveFrequencyAsync(long hz, CancellationToken cancellationToken = default) =>
        AckAsync('R', hz.ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>Load transmit frequency (§2.8.3), in Hz. Latched for the next transmission.</summary>
    public Task SetTransmitFrequencyAsync(long hz, CancellationToken cancellationToken = default) =>
        AckAsync('T', hz.ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>Set bandwidth (§2.8.14). Immediate; may be restricted by regulatory rules for
    /// the current TX frequency.</summary>
    public Task SetBandwidthAsync(TaitBandwidth bandwidth, CancellationToken cancellationToken = default) =>
        AckAsync('H', ((int)bandwidth).ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>Set volume level 0–255 (§2.8.4). Immediate; a physical volume control can
    /// override it afterwards (last control wins).</summary>
    public Task SetVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 255);
        return AckAsync('J', level.ToString("000", CultureInfo.InvariantCulture), cancellationToken);
    }

    /// <summary>Transmitter output power (§2.8.13). Latched for the next transmission. This is
    /// the software power control the CCDI-side FUNCTION 0/7 lacks on CCDI 03.02 firmware.</summary>
    public Task SetTransmitterPowerAsync(TaitTxPower power, CancellationToken cancellationToken = default) =>
        AckAsync('P', ((int)power).ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <summary>Receive CTCSS (§2.8.5): tone in Hz (67.0–254.1), 0 disables. Immediate.</summary>
    public Task SetReceiveCtcssAsync(float toneHz, CancellationToken cancellationToken = default) =>
        AckAsync('A', CtcssParam(toneHz), cancellationToken);

    /// <summary>Transmit CTCSS (§2.8.6): tone in Hz, 0 disables. Latched for next TX.</summary>
    public Task SetTransmitCtcssAsync(float toneHz, CancellationToken cancellationToken = default) =>
        AckAsync('B', CtcssParam(toneHz), cancellationToken);

    /// <summary>Receive DCS (§2.8.7): a valid octal code (e.g. "023"), "000" disables. Immediate.</summary>
    public Task SetReceiveDcsAsync(string octalCode, CancellationToken cancellationToken = default) =>
        AckAsync('C', ValidDcs(octalCode), cancellationToken);

    /// <summary>Transmit DCS (§2.8.8): a valid octal code, "000" disables. Latched for next TX.</summary>
    public Task SetTransmitDcsAsync(string octalCode, CancellationToken cancellationToken = default) =>
        AckAsync('D', ValidDcs(octalCode), cancellationToken);

    /// <summary>Encode (transmit) a Selcall sequence of 2–33 tones (§2.8.9). Keys the
    /// transmitter after the lead-in delay; requires RX/TX frequencies to be initialised.</summary>
    public Task EncodeSelcallAsync(string tones, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tones);
        if (tones.Length is < 2 or > 33)
        {
            throw new ArgumentException("Selcall sequences are 2-33 tones (§2.8.9)", nameof(tones));
        }
        return AckAsync('S', tones, cancellationToken);
    }

    /// <summary>Set Selcall toneset / tone period / decode-notify length (§2.8.10). Immediate
    /// re-initialisation of the Selcall modem.</summary>
    /// <param name="toneset">0 CCIR, 1 EIA, 2 EEA, 3 ZVEI-I, 4 ZVEI-II, 5 ZVEI-III, 6 PZVEI, 7 NATEL, 8 DZVEI.</param>
    /// <param name="tonePeriod">1=20ms, 2=33ms, 3=40ms, 4=50ms, 5=60ms, 6=70ms, 7=100ms.</param>
    /// <param name="notifyTones">Decode buffer/filter size: sequences shorter than this are
    /// discarded (keeps speech from producing garbage decode reports).</param>
    /// <param name="cancellationToken">Cancels waiting for the acknowledgement.</param>
    public Task SetSelcallParametersAsync(int toneset, int tonePeriod, int notifyTones, CancellationToken cancellationToken = default) =>
        AckAsync('I', string.Create(CultureInfo.InvariantCulture, $"{toneset}{tonePeriod}{notifyTones}"), cancellationToken);

    /// <summary>Set ANI (§2.8.11): mode 0 off / 1 leading / 2 trailing / 3 both, plus the tone
    /// sequence.</summary>
    public Task SetAniAsync(int mode, string tones = "", CancellationToken cancellationToken = default) =>
        AckAsync('N', string.Create(CultureInfo.InvariantCulture, $"{mode}{tones}"), cancellationToken);

    /// <summary>Monitor (§2.8.12): overrides the subaudible-signalling filters (not the squelch
    /// mute). Immediate; last control wins.</summary>
    public Task SetMonitorAsync(bool on, CancellationToken cancellationToken = default) =>
        AckAsync('M', on ? "D" : "E", cancellationToken);

    /// <summary>Pulse / ping (§2.8.15). Returns <c>true</c> when the radio has its minimum CCR
    /// configuration (an RX frequency has been set since CCR entry); <c>false</c> means the
    /// radio rebooted and is on defaults — reprogram it. The manual recommends pulsing every
    /// 10 s; <see cref="TaitCcdiRadio"/>'s watchdog uses this automatically in CCR mode.</summary>
    public async Task<bool> PulseAsync(CancellationToken cancellationToken = default)
    {
        var result = await radio.TransactCcrAsync(
            new CcdiFrame('Q', "P"), m => m is CcrPulseResultMessage, cancellationToken).ConfigureAwait(false);
        return ((CcrPulseResultMessage)result).HasMinimumConfiguration;
    }

    /// <summary>Exit CCR mode (§2.8.16): the radio performs a soft reset, discards everything
    /// configured in this session, and reboots into its programmed power-up state. Allow a few
    /// seconds before the next CCDI transaction.</summary>
    public async Task ExitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await radio.TransactCcrAsync(new CcdiFrame('E', ""), m => m is CcrAckMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The reset can win the race with the acknowledgement; the exit still happened.
        }
        finally
        {
            radio.MessageReceived -= OnMessage;
            radio.OnCcrExited();
        }
    }

    private async Task AckAsync(char ident, string parameters, CancellationToken cancellationToken)
    {
        var result = await radio.TransactCcrAsync(
            new CcdiFrame(ident, parameters),
            m => m is CcrAckMessage ack && ack.EchoedCommand == ident,
            cancellationToken).ConfigureAwait(false);
        _ = result;
    }

    private void OnMessage(object? sender, CcdiMessage message)
    {
        switch (message)
        {
            case CcrSelcallDecodeMessage selcall:
                SelcallDecoded?.Invoke(this, selcall);
                break;
            case CcrNotificationMessage { Kind: 'P' }:
                TransmitLimitWarning?.Invoke(this, EventArgs.Empty);
                break;
            default:
                break;
        }
    }

    private static string CtcssParam(float toneHz)
    {
        if (toneHz != 0f && toneHz is < 67f or > 254.1f)
        {
            throw new ArgumentOutOfRangeException(nameof(toneHz), "CTCSS range is 67.0-254.1 Hz, or 0 to disable");
        }
        return ((int)MathF.Round(toneHz * 10)).ToString("0000", CultureInfo.InvariantCulture);
    }

    private static string ValidDcs(string octalCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(octalCode);
        foreach (char c in octalCode)
        {
            if (c is < '0' or > '7')
            {
                throw new ArgumentException("DCS codes are octal digits (§2.8.7)", nameof(octalCode));
            }
        }
        return octalCode.PadLeft(3, '0');
    }
}

/// <summary>CCR bandwidth selections (§2.8.14).</summary>
public enum TaitBandwidth
{
    /// <summary>Narrowband (12.5 kHz).</summary>
    Narrow = 1,

    /// <summary>Mediumband (20 kHz).</summary>
    Medium = 2,

    /// <summary>Wideband (25 kHz).</summary>
    Wide = 3,
}

/// <summary>CCR transmitter power levels (§2.8.13).</summary>
public enum TaitTxPower
{
    /// <summary>Very low power.</summary>
    VeryLow = 1,

    /// <summary>Low power.</summary>
    Low = 2,

    /// <summary>Medium power.</summary>
    Medium = 3,

    /// <summary>High power.</summary>
    High = 4,
}
