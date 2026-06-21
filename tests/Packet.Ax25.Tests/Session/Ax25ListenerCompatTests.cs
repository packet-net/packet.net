using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Coverage for the listener's per-port compatibility knobs (#366):
/// <see cref="Ax25ListenerOptions.ParseOptions"/> gating the inbound parse, and
/// <see cref="Ax25ListenerOptions.Quirks"/> seeding each new session's
/// <see cref="Ax25SessionContext.Quirks"/>. Paired strict-rejects /
/// lenient-accepts per the CLAUDE.md flag discipline.
/// </summary>
public class Ax25ListenerCompatTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCallA = new("G7XYZ", 7);
    private static readonly Callsign PeerCallB = new("M5ABC", 3);

    /// <summary>
    /// Build the wire bytes of a SABM whose address C-bits mark it a
    /// <em>response</em> — the AX.25 v1.x-flavoured connect that
    /// <c>AllowCommandFrameAsResponse</c> (#142) gates. Same bit-twiddle as
    /// <c>Ax25ListenerRejectAndEdgeTests</c>: construct a normal (command)
    /// SABM, clear the destination C-bit, set the source C-bit.
    /// </summary>
    private static byte[] ResponseSabmBytes(Callsign dest, Callsign source)
    {
        var bytes = Ax25Frame.Sabm(dest, source).ToBytes().ToArray();
        bytes[6] &= 0x7F;   // destination SSID octet: clear C-bit
        bytes[13] |= 0x80;  // source SSID octet: set C-bit → response shape
        return bytes;
    }

    private static void InjectRaw(LoopbackModem modem, byte[] bytes) =>
        modem.InjectInboundRaw(bytes);

    /// <summary>
    /// A Strict port is deaf to a response-direction SABM: dropped at decode,
    /// so no session opens, no UA/DM goes out, and the frame never even
    /// reaches the monitor trace — exactly as if it had failed CRC.
    /// </summary>
    [Fact]
    public async Task Strict_Port_Drops_Response_Sabm_Entirely()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ParseOptions = Ax25ParseOptions.Strict,
        });

        var accepted = 0;
        var traced = 0;
        listener.SessionAccepted += (_, _) => Interlocked.Increment(ref accepted);
        listener.FrameTraced += (_, _) => Interlocked.Increment(ref traced);

        await listener.StartAsync();
        InjectRaw(modem, ResponseSabmBytes(LocalCall, PeerCallA));

        await Task.Delay(500);
        accepted.Should().Be(0, "Strict drops a response-SABM at decode — it can never open a session");
        traced.Should().Be(0, "the drop happens before the monitor trace — the port is deaf to the frame");
        modem.SentFrames.Count.Should().Be(0, "no session machinery ran, so nothing (UA or DM) went out");
        listener.IsRunning.Should().BeTrue();
    }

    /// <summary>
    /// The paired accept: the same wire bytes on a Lenient port (the default —
    /// <c>ParseOptions</c> null) open a session and the UA goes back, the
    /// v1.x-interop behaviour the lenient default exists for.
    /// </summary>
    [Fact]
    public async Task Lenient_Port_Accepts_The_Same_Response_Sabm()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        InjectRaw(modem, ResponseSabmBytes(LocalCall, PeerCallA));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));

        session.CurrentState.Should().Be("Connected");
        Ax25Frame.TryParse(modem.SentFrames[0].Span, out var reply).Should().BeTrue();
        (reply!.Control & 0xEF).Should().Be(0x63, "the lenient accept completes the handshake with UA");
    }

    /// <summary>
    /// Strict gates pragmatism, not spec traffic: a spec-valid command SABM
    /// still connects on a Strict port.
    /// </summary>
    [Fact]
    public async Task Strict_Port_Still_Accepts_A_Spec_Valid_Sabm()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            ParseOptions = Ax25ParseOptions.Strict,
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.CurrentState.Should().Be("Connected");
    }

    /// <summary>
    /// <see cref="Ax25ListenerOptions.Quirks"/> seeds each newly-built session's
    /// context; null means <see cref="Ax25SessionQuirks.Default"/> (the existing
    /// behaviour, asserted by the paired test below).
    /// </summary>
    [Fact]
    public async Task Configured_Quirks_Seed_Onto_New_Sessions()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            Quirks = Ax25SessionQuirks.StrictlyFaithful,
        });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.Context.Quirks.Should().BeSameAs(Ax25SessionQuirks.StrictlyFaithful);
    }

    [Fact]
    public async Task Null_Quirks_Leave_The_Spec_Correct_Default()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCallA));

        var session = await accepted.Task.WithTimeout(TimeSpan.FromSeconds(2));
        session.Context.Quirks.Should().Be(Ax25SessionQuirks.Default);
    }

    /// <summary>
    /// <see cref="Ax25Listener.UpdateSessionParameters"/> reseeds the parse
    /// options live: the pump reads them per inbound frame, so a port flipped
    /// to Strict goes deaf to non-spec frames from the very next frame —
    /// no listener rebuild, existing sessions untouched.
    /// </summary>
    [Fact]
    public async Task Reseeding_ParseOptions_Applies_To_The_Next_Inbound_Frame()
    {
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var acceptedPeers = new List<Callsign>();
        var firstAccept = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            lock (acceptedPeers) acceptedPeers.Add(e.Session.Context.Remote);
            firstAccept.TrySetResult(true);
        };

        await listener.StartAsync();

        // Lenient (the construction default): peer A's response-SABM connects.
        InjectRaw(modem, ResponseSabmBytes(LocalCall, PeerCallA));
        await firstAccept.Task.WithTimeout(TimeSpan.FromSeconds(2));

        // Flip the port to Strict, keeping everything else as constructed.
        listener.UpdateSessionParameters(
            listener.CurrentSessionParameters with { ParseOptions = Ax25ParseOptions.Strict });

        // Peer B's identical response-SABM is now dropped at decode.
        InjectRaw(modem, ResponseSabmBytes(LocalCall, PeerCallB));
        await Task.Delay(500);

        lock (acceptedPeers)
        {
            acceptedPeers.Should().Equal([PeerCallA],
                "the reseed gates inbound parsing from the next frame; peer A's session is untouched");
        }
        listener.IsRunning.Should().BeTrue();
    }
}
