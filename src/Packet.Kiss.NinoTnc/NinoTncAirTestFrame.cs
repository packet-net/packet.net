using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The over-air UI frame the NinoTNC transmits when its operator presses
/// the front-panel TX-Test button. This is the on-air signal — the
/// receiving modem will deliver it to its host as a normal KISS Data
/// frame. <see cref="NinoTncTxTestFrame"/> is the *other* frame the
/// pressed modem emits (synthetic, host-side only, never on the air).
/// </summary>
/// <remarks>
/// <para>
/// Observed shape (firmware v3.44, mode 6 and mode 7 verified, claimed
/// "any mode" by the firmware author):
/// </para>
/// <list type="bullet">
///   <item>UI frame, control = 0x03, PID = 0xF0</item>
///   <item>Source = the modem's <em>learned</em> callsign (the first
///         callsign it saw transmitted through itself since power-on;
///         persists across mode changes)</item>
///   <item>Destination = <c>CQBEEP-N</c> — the button press uses SSID 5;
///         host-built frames may use any SSID (see below)</item>
///   <item>INFO = <c>"{N "</c> followed by 50 bytes of printable ASCII
///         starting at byte <c>0x20 + N</c> and stepping +1 — total INFO
///         length always 53 bytes</item>
///   <item><c>N</c> is a per-press counter (digit 1..9, presumably
///         wraps; not yet observed past 2 in this codebase)</item>
/// </list>
/// <para>
/// The destination SSID is significant on the receive side: an <em>armed</em>
/// NinoTNC (one that has transmitted a <c>[TARPNstat</c> status frame — see
/// <see cref="NinoTncCqBeep"/>) answers a received CQBEEP-N frame with N
/// seconds of 440 Hz tone. Any SSID is therefore recognised here, and
/// exposed as <see cref="DestinationSsid"/>.
/// </para>
/// <para>
/// Useful for zero-config link-up probing, per-link RX-quality samples
/// (deterministic INFO bytes → bit-error counting), missed-press
/// detection via the counter, and remote audio-level tuning (the tone).
/// </para>
/// </remarks>
public sealed record NinoTncAirTestFrame
{
    /// <summary>The callsign the transmitting modem has learned (its `src=` on the frame).</summary>
    public required Callsign LearnedCallsign { get; init; }

    /// <summary>
    /// The destination SSID (the N in <c>CQBEEP-N</c>): the seconds of
    /// 440 Hz tone an armed receiving TNC will transmit in response
    /// (bench-verified: N=7 → 6.99 s). The front-panel TX-Test button
    /// always emits SSID 5.
    /// </summary>
    public required byte DestinationSsid { get; init; }

    /// <summary>The per-press counter (the digit between `{` and ` `).</summary>
    public required int SequenceCounter { get; init; }

    /// <summary>The 50-byte printable-ASCII window after the `{N ` prefix.</summary>
    public required ReadOnlyMemory<byte> Pattern { get; init; }

    /// <summary>
    /// Try to recognise <paramref name="frame"/> as a NinoTNC over-air
    /// TX-Test frame.
    /// </summary>
    public static bool TryRecognise(Ax25Frame frame, out NinoTncAirTestFrame? recognised)
    {
        recognised = null;
        if (!frame.IsUi)
        {
            return false;
        }
        if (frame.Destination.Callsign.Base != NinoTncCqBeep.ResponderCallsignBase)
        {
            return false;
        }
        var info = frame.Info.Span;
        // "{N " + 50 bytes pattern = 53 bytes minimum (the observed length).
        if (info.Length != 53 || info[0] != (byte)'{' || info[2] != (byte)' ')
        {
            return false;
        }
        if (info[1] < (byte)'0' || info[1] > (byte)'9')
        {
            return false;
        }
        int n = info[1] - (byte)'0';
        // Pattern bytes should start at 0x20 + N and increment by 1, all printable.
        byte expected = (byte)(0x20 + n);
        for (int i = 3; i < info.Length; i++)
        {
            if (info[i] != expected)
            {
                return false;
            }
            expected = (byte)(expected + 1);
        }

        recognised = new NinoTncAirTestFrame
        {
            LearnedCallsign = frame.Source.Callsign,
            DestinationSsid = frame.Destination.Callsign.Ssid,
            SequenceCounter = n,
            Pattern = frame.Info.Slice(3),
        };
        return true;
    }

    /// <summary>Render the INFO pattern as the printable-ASCII string it is.</summary>
    public string PatternAsAscii() => Encoding.ASCII.GetString(Pattern.Span);

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"NinoTncAirTestFrame src={LearnedCallsign} dst=CQBEEP-{DestinationSsid} seq={SequenceCounter} pattern[{Pattern.Length}]");
}
