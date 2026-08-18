using AwesomeAssertions;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// v2.2 arc V4a - REJ and SREJ loss recovery in the mod-128 (extended)
/// sequence space. These mirror the mod-8 recovery tests
/// (<see cref="Packet.Ax25.Tests.Session.DataLinkConnectedRetransmitTests"/>,
/// <see cref="Packet.Ax25.Tests.Session.DataLinkSrejUnderLossTests"/>,
/// <see cref="LossRecoveryProperties"/>) but drive an extended link
/// (<c>TwoStationHarness.Build(extended: true)</c>) so the 7-bit N(S)/N(R)
/// arithmetic is exercised end-to-end - including window-wrap across the
/// 0-127 boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding these guard.</b> The recovery logic in
/// <see cref="Ax25SessionBindings"/> is already mod-aware: every sequence
/// computation is <c>% context.Modulus</c> (the send-window check, the
/// Ax25Spec40 out-of-window discard guard, the N(S)/N(R) comparisons), and
/// the frame's <see cref="Ax25Frame.Ns"/>/<see cref="Ax25Frame.Nr"/> read
/// 7-bit values on extended frames (V1, #239). So SREJ/REJ recovery works
/// at modulo-128 with no recovery-path code change; these tests prove it and
/// stand as the regression guard.
/// </para>
/// <para>
/// The three SREJ figc4.x quirks (Ax25Spec40 out-of-window discard,
/// Ax25Spec41 Karn SRT sampling, Ax25Spec42 SREJ-targets-gap) are on by
/// default in <see cref="TwoStationHarness.Build"/>, so the heavy / window-
/// wrap selective-recovery cases below exercise them at mod-128 too.
/// </para>
/// </remarks>
public class Mod128LossRecoveryConformanceTests
{
    [Fact]
    public void Mod128_REJ_recovers_a_single_dropped_iframe()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);
        h.Connect();
        h.A.Context.IsExtended.Should().BeTrue("the harness must negotiate mod-128 via SABME");
        h.B.Context.IsExtended.Should().BeTrue();

        // Drop A's N(S)=2 I-frame exactly once. With SREJ disabled (default), the
        // gap drives B's figc4.4 REJ path (go-back-N).
        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped)
            {
                return false;
            }

            if (!f.Source.Callsign.Equals(h.A.Context.Local))
            {
                return false;
            }

            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
            {
                return false;
            }

            if (f.Ns != 2)
            {
                return false;            // mode-aware 7-bit N(S)
            }

            dropped = true;
            return true;
        };

        for (byte i = 0; i < 6; i++)
        {
            h.Submit(h.A, i);
        }

        for (int r = 0; r < 40 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 6).Select(i => (byte)i),
            "go-back-N recovery in the 7-bit space must deliver all six payloads in order");
        h.AssertConverged();
    }

    [Fact]
    public void Mod128_SREJ_recovers_a_single_dropped_iframe()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8);
        h.Connect();

        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped)
            {
                return false;
            }

            if (!f.Source.Callsign.Equals(h.A.Context.Local))
            {
                return false;
            }

            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
            {
                return false;
            }

            if (f.Ns != 3)
            {
                return false;
            }

            dropped = true;
            return true;
        };

        for (byte i = 0; i < 6; i++)
        {
            h.Submit(h.A, i);
        }

        for (int r = 0; r < 40 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 6).Select(i => (byte)i),
            "selective recovery in the 7-bit space must deliver all six payloads in order");
        h.AssertConverged();
    }

    // A multi-frame SREJ burst at mod-128 - the same shape as the mod-8
    // Srej_heavy_bidirectional_loss_burst_recovers regression (needs all three
    // figc4.x SREJ quirks), but in the extended sequence space.
    [Fact]
    public void Mod128_SREJ_recovers_a_multi_frame_loss_burst()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8, n2: 40);
        h.Connect();
        var rng = new Random(2);
        var dropsLeft = 4;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; } return false; };

        for (byte i = 0; i < 8; i++)
        {
            h.Submit(h.A, i);
        }

        for (int r = 0; r < 80 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        h.AssertConverged();
    }

    // The headline mod-128 property: a window that WRAPS the 0→127 boundary.
    // Pre-advance both sequence variables close to the top of the 7-bit space
    // (V(S)=V(A)=V(R)=124 on both ends - a valid "freshly connected at offset
    // 124" state), then transfer a burst that wraps past 127→0 with a single
    // drop. If any computation were not mod-128-aware (e.g. a stray `% 8`, or a
    // window check that didn't wrap), recovery would diverge here.
    [Theory]
    [InlineData(false)]   // REJ go-back-N
    [InlineData(true)]    // SREJ selective
    public void Mod128_recovery_converges_across_the_127_to_0_window_wrap(bool srej)
    {
        var h = TwoStationHarness.Build(extended: true, srej: srej, k: 8, n2: 40);
        h.Connect();

        // Seed both ends near the wrap. Both sides agree, so the link is
        // consistent - V(S)=V(A) (nothing outstanding), V(R) matches the peer's
        // V(S). This is indistinguishable from having already sent 124 frames.
        const byte seed = 124;
        h.A.Context.VS = h.A.Context.VA = seed; h.A.Context.VR = seed;
        h.B.Context.VS = h.B.Context.VA = seed; h.B.Context.VR = seed;

        // Drop the frame whose N(S) sits just past the wrap (126,127,0,1,...) so
        // recovery has to re-request a sequence number on the other side of 0.
        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped)
            {
                return false;
            }

            if (!f.Source.Callsign.Equals(h.A.Context.Local))
            {
                return false;
            }

            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
            {
                return false;
            }

            if (f.Ns != 0)
            {
                return false;            // N(S)=0 - the frame straddling the wrap
            }

            dropped = true;
            return true;
        };

        // Eight frames from seed: N(S) = 124,125,126,127,0,1,2,3 - wraps the ring.
        for (byte i = 0; i < 8; i++)
        {
            h.Submit(h.A, (byte)(0x40 + i));
        }

        for (int r = 0; r < 60 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 8).Select(i => (byte)(0x40 + i)),
            "recovery across the 127→0 window wrap must deliver every payload in order");
        h.A.Context.VS.Should().Be((byte)4, "V(S) must wrap: 124 + 8 = 132 mod 128 = 4");
        h.AssertConverged();
    }

    private static bool Converged(TwoStationHarness h) =>
        h.A.Context.VS == h.A.Context.VA &&
        h.B.Context.VS == h.B.Context.VA &&
        h.B.Delivered.Count == h.A.Submitted.Count &&
        h.A.Delivered.Count == h.B.Submitted.Count;
}
