using AwesomeAssertions;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Phase H — happy-path conformance. Drives the real two-station stack through
/// its normal operating envelope (no channel disruption) and asserts the
/// <see cref="InvariantChecker"/> oracle holds after every step + the link
/// fully converges. Proves both the stack and the oracle on known-answer
/// scenarios before adversarial generation (docs/conformance-harness-plan.md,
/// Phase H). The harness runs the safety invariants automatically after each
/// drive call, so these tests mostly assert the converged end-state.
/// </summary>
public class HappyPathConformanceTests
{
    [Fact]
    public void Connect_then_clean_disconnect()
    {
        var h = TwoStationHarness.Build();
        h.Connect();
        h.A.State.Should().Be("Connected");
        h.B.State.Should().Be("Connected");

        h.Disconnect(h.A);
        h.A.State.Should().Be("Disconnected");
        h.B.State.Should().Be("Disconnected");
    }

    [Fact]
    public void Single_I_frame_A_to_B_delivers_and_acks()
    {
        var h = TwoStationHarness.Build();
        h.Connect();

        h.Submit(h.A, 0xAA);
        h.FlushAcks();

        h.B.Delivered.Should().ContainSingle().Which.Should().Equal(new byte[] { 0xAA });
        h.AssertConverged();
        h.A.State.Should().Be("Connected");
    }

    [Fact]
    public void Full_window_A_to_B_delivers_in_order()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        for (byte i = 0; i < 4; i++) h.Submit(h.A, i);
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0, 1, 2, 3 });
        h.AssertConverged();
    }

    [Fact]
    public void Bidirectional_simultaneous_data_delivers_both_ways()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        h.Submit(h.A, 0xA0);
        h.Submit(h.B, 0xB0);
        h.Submit(h.A, 0xA1);
        h.Submit(h.B, 0xB1);
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0xA0, 0xA1 });
        h.A.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0xB0, 0xB1 });
        h.AssertConverged();
    }

    [Fact]
    public void Multi_window_transfer_wraps_the_modulus()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // 12 frames > the mod-8 window — V(s) must wrap 7→0. Flush acks each
        // window so the send window keeps reopening.
        for (byte i = 0; i < 12; i++)
        {
            h.Submit(h.A, i);
            if ((i + 1) % 4 == 0) h.FlushAcks();
        }
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 12).Select(i => (byte)i));
        h.AssertConverged();
        // Sanity: V(s) wrapped (12 mod 8 = 4).
        h.A.Context.VS.Should().Be((byte)4);
    }

    [Fact]
    public void Stop_and_wait_k1_delivers_each_frame()
    {
        var h = TwoStationHarness.Build(k: 1);
        h.Connect();

        // k=1: at most one unacked frame outstanding — each must be acked
        // before the next can go, the tightest window edge.
        for (byte i = 0; i < 5; i++)
        {
            h.Submit(h.A, i);
            h.FlushAcks();
        }

        h.B.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0, 1, 2, 3, 4 });
        h.AssertConverged();
    }

    [Fact]
    public void Max_window_k7_full_window_delivers()
    {
        var h = TwoStationHarness.Build(k: 7);   // largest mod-8 send window
        h.Connect();

        for (byte i = 0; i < 7; i++) h.Submit(h.A, i);
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 7).Select(i => (byte)i));
        h.AssertConverged();
        h.A.Context.VS.Should().Be((byte)7);
    }

    [Fact]
    public void Sustained_transfer_wraps_the_modulus_repeatedly()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // 40 frames over a k=4 window = ten window cycles; V(s) laps the mod-8
        // modulus five times. Exercises sustained operation + repeated wrap.
        const int n = 40;
        for (byte i = 0; i < n; i++)
        {
            h.Submit(h.A, i);
            if ((i + 1) % 4 == 0) h.FlushAcks();
        }
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, n).Select(i => (byte)i));
        h.AssertConverged();
        h.A.Context.VS.Should().Be((byte)(n % 8));   // 40 mod 8 = 0
    }
}
