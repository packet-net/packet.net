using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Outbound CONNECT version-preference unit tests for <see cref="Ax25Listener"/>.
/// A default dial prefers AX.25 v2.2 (SABME / mod-128) so the link negotiates
/// SREJ + window against capable peers and degrades cleanly to v2.0/SABM for
/// peers that can't (FRMR — LinBPQ; DM — XRouter, exercised live in the interop
/// suite). The opt-out (<see cref="Ax25ListenerOptions.PreferExtendedConnect"/>
/// = <c>false</c>, or the per-call override) initiates a plain v2.0 (SABM) connect.
/// </summary>
/// <remarks>
/// These tests assert the <em>first frame on the wire</em>: a v2.2-preferred dial
/// emits a SABME, a v2.0 dial emits a SABM. The full SABME → UA → XID round-trip
/// and the FRMR/DM fallbacks are proven against real peers in the Interop suite
/// (Direwolf / LinBPQ / XRouter). The inbound answerer is deliberately untouched —
/// it adopts the peer's version from the SABM/SABME it receives (figc4.1).
/// </remarks>
public class Ax25ListenerPreferV22ConnectTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    // U-frame control octets (P/F masked out).
    private const byte SabmBase = 0x2F;
    private const byte SabmeBase = 0x6F;
    private const byte XidBase = 0xAF;

    private static byte UBase(Ax25Frame f) => (byte)(f.Control & 0xEF);

    [Fact]
    public async Task Default_dial_prefers_v22_and_emits_SABME()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            // PreferExtendedConnect defaults to true — assert that default, no override.
        });
        await listener.StartAsync();

        // Fire-and-forget: the connect awaits DL-CONNECT-confirm (which never
        // arrives — no peer), but the SABME hits the wire synchronously on dispatch.
        _ = listener.ConnectAsync(PeerCall);

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var first).Should().BeTrue();
        UBase(first!).Should().Be(SabmeBase,
            "a default (prefer-v2.2) outbound CONNECT must initiate an extended SABME, not a SABM");
    }

    [Fact]
    public async Task Listener_opt_out_emits_SABM()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreferExtendedConnect = false,   // opt out → plain v2.0 connect
            // Isolate the version choice from the pre-SABM SREJ XID exchange: with
            // the default-on PreConnectXidNegotiatesSrej, a mod-8 dial's FIRST frame
            // is the XID command, not the SABM (asserted separately below).
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        _ = listener.ConnectAsync(PeerCall);

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var first).Should().BeTrue();
        UBase(first!).Should().Be(SabmBase,
            "PreferExtendedConnect=false must initiate a plain v2.0 SABM connect");
    }

    [Fact]
    public async Task Per_call_override_false_forces_SABM_even_when_listener_prefers_v22()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            // Listener default prefers v2.2 …
            // Isolate the version override from the pre-SABM SREJ XID exchange (see
            // Listener_opt_out_emits_SABM): a mod-8 dial's first frame is otherwise XID.
            PreConnectXidNegotiatesSrej = false,
        });
        await listener.StartAsync();

        // … but this dial opts out per-call.
        _ = listener.ConnectAsync(PeerCall, LocalCall, extended: false);

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var first).Should().BeTrue();
        UBase(first!).Should().Be(SabmBase,
            "a per-call extended:false override must force a SABM even when the listener default prefers v2.2");
    }

    [Fact]
    public async Task Per_call_override_true_forces_SABME_even_when_listener_opts_out()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreferExtendedConnect = false,   // listener default is v2.0 …
        });
        await listener.StartAsync();

        // … but this dial prefers v2.2 per-call.
        _ = listener.ConnectAsync(PeerCall, LocalCall, extended: true);

        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var first).Should().BeTrue();
        UBase(first!).Should().Be(SabmeBase,
            "a per-call extended:true override must force a SABME even when the listener default opts out");
    }

    [Fact]
    public async Task Mod8_dial_with_PreConnectXid_emits_XID_before_SABM()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            PreferExtendedConnect = false,        // mod-8 dial …
            // … with the default-on pre-SABM SREJ negotiation (asserted explicitly here).
            PreConnectXidNegotiatesSrej = true,
        });
        await listener.StartAsync();

        _ = listener.ConnectAsync(PeerCall);

        // The FIRST frame on a mod-8 dial is now the XID command (the LinBPQ SREJ
        // accommodation), not the SABM — the SABM follows once the XID exchange
        // settles or times out. No peer answers here, so we only assert the lead XID.
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var first).Should().BeTrue();
        UBase(first!).Should().Be(XidBase,
            "with PreConnectXidNegotiatesSrej on, a mod-8 dial leads with an XID command to negotiate SREJ before the SABM");
    }
}
