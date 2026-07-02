using System.Text;
using Packet.Ax25;
using Packet.Core;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Factories for the NinoTNC's CQBEEP remote air-test responder: the
/// <c>[TARPNstat</c> arming frame and the <c>CQBEEP-N</c> beep request.
/// </summary>
/// <remarks>
/// <para>
/// The firmware ships a remote test-tone responder that is <em>disarmed</em>
/// until the TNC transmits a TARPN status frame — a UI frame whose info text
/// starts <c>[TARPNstat</c> (how the firmware learns it is part of a TARPN
/// system). Send <see cref="BuildArmingFrame"/> through the TNC's own serial
/// port to arm it. Arming is volatile: it does not survive a reset, so
/// re-arm after power-cycling.
/// </para>
/// <para>
/// Once armed, receiving a UI frame addressed to <c>CQBEEP-N</c> makes the
/// TNC key its transmitter and send N seconds of 440 Hz tone (bench-verified
/// 2026-07-02 on firmware 3.41: N=7 measured 6.99 s). The front-panel
/// TX-Test button emits the same frame shape with SSID 5 — see
/// <see cref="NinoTncAirTestFrame"/>, which recognises any CQBEEP-N.
/// The tone is the remote half of a deviation/level tuning loop: trigger a
/// beep, meter the received audio (e.g. GETRSSI on the listening TNC),
/// adjust, repeat.
/// </para>
/// </remarks>
public static class NinoTncCqBeep
{
    /// <summary>The destination callsign base that addresses the beep responder.</summary>
    public const string ResponderCallsignBase = "CQBEEP";

    /// <summary>The info-text prefix the firmware's arming check looks for.</summary>
    public const string TarpnStatusPrefix = "[TARPNstat";

    /// <summary>The number of pattern bytes in a TX-Test-shaped info field.</summary>
    public const int PatternLength = 50;

    /// <summary>
    /// Build the arming frame: a UI frame whose info text starts
    /// <c>[TARPNstat</c>. Transmit it through the TNC to arm that TNC's
    /// CQBEEP responder (volatile — re-arm after reset).
    /// </summary>
    /// <param name="source">Source callsign for the frame.</param>
    /// <param name="destination">
    /// Destination address. The firmware keys on the info text, not the
    /// destination; defaults to <c>IDENT</c>, the fake destination the
    /// firmware itself uses for status frames.
    /// </param>
    /// <param name="statusText">
    /// Full info text. Defaults to <c>"[TARPNstat]"</c>; a caller-supplied
    /// value must start with <see cref="TarpnStatusPrefix"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="statusText"/> lacks the required prefix.</exception>
    public static Ax25Frame BuildArmingFrame(Callsign source, Callsign? destination = null, string? statusText = null)
    {
        string text = statusText ?? (TarpnStatusPrefix + "]");
        if (!text.StartsWith(TarpnStatusPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"arming info text must start with \"{TarpnStatusPrefix}\" (got \"{statusText}\")", nameof(statusText));
        }
        return Ax25Frame.Ui(
            destination: destination ?? new Callsign("IDENT"),
            source: source,
            info: Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// Build a beep request: a UI frame addressed to <c>CQBEEP-N</c>, which
    /// makes any armed NinoTNC that hears it transmit
    /// <paramref name="seconds"/> seconds of 440 Hz tone. The info field
    /// carries the same deterministic <c>{N </c>+pattern shape the TX-Test
    /// button uses, so receivers recognise it via
    /// <see cref="NinoTncAirTestFrame.TryRecognise"/> (and can count bit
    /// errors against the known pattern).
    /// </summary>
    /// <param name="source">Source callsign for the frame.</param>
    /// <param name="seconds">Seconds of tone to request, 1–15 (the destination SSID).</param>
    /// <param name="sequenceCounter">The per-request counter digit, 0–9.</param>
    /// <exception cref="ArgumentOutOfRangeException">Seconds or counter out of range.</exception>
    public static Ax25Frame BuildBeepRequest(Callsign source, int seconds, int sequenceCounter = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 15);
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceCounter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequenceCounter, 9);

        var info = new byte[3 + PatternLength];
        info[0] = (byte)'{';
        info[1] = (byte)('0' + sequenceCounter);
        info[2] = (byte)' ';
        byte next = (byte)(0x20 + sequenceCounter);
        for (int i = 3; i < info.Length; i++)
        {
            info[i] = next;
            next = (byte)(next + 1);
        }

        return Ax25Frame.Ui(
            destination: new Callsign(ResponderCallsignBase, (byte)seconds),
            source: source,
            info: info);
    }
}
