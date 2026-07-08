namespace Packet.Tune.Core.Tests;

/// <summary>
/// The passive excess-TXDELAY layer: the bounded rolling median window
/// (<see cref="PreDataCarrierWindow"/>) and the threshold advisory
/// (<see cref="ExcessTxDelayAdvisor"/>).
/// </summary>
public class ExcessTxDelayAdvisorTests
{
    // ─── the rolling window ─────────────────────────────────────────────────

    [Fact]
    public void Median_is_null_while_empty_then_tracks_the_samples()
    {
        var window = new PreDataCarrierWindow();
        window.MedianMs.Should().BeNull();
        window.Count.Should().Be(0);

        window.Add(300);
        window.MedianMs.Should().Be(300);

        window.Add(500);
        window.MedianMs.Should().Be(400);   // even count → midpoint average

        window.Add(320);
        window.MedianMs.Should().Be(320);   // odd count → middle
        window.Count.Should().Be(3);
    }

    [Fact]
    public void The_window_keeps_only_the_last_capacity_samples()
    {
        var window = new PreDataCarrierWindow(capacity: 4);
        // Four old high readings, then four new low ones — the old must age out.
        foreach (double ms in new double[] { 900, 900, 900, 900, 100, 110, 120, 130 })
        {
            window.Add(ms);
        }
        window.Count.Should().Be(4);
        window.MedianMs.Should().Be(115);   // median of 100/110/120/130 only
    }

    [Fact]
    public void Default_capacity_is_32()
    {
        var window = new PreDataCarrierWindow();
        for (int i = 0; i < 100; i++)
        {
            window.Add(i);
        }
        window.Capacity.Should().Be(32);
        window.Count.Should().Be(32);
    }

    [Fact]
    public void Garbage_samples_are_discarded_not_medianed()
    {
        var window = new PreDataCarrierWindow();
        window.Add(-5);                    // a mis-attributed window can go negative
        window.Add(double.NaN);
        window.Add(double.PositiveInfinity);
        window.Count.Should().Be(0);

        window.Add(250);
        window.MedianMs.Should().Be(250);
    }

    // ─── the advisory ───────────────────────────────────────────────────────

    [Fact]
    public void A_peer_over_the_threshold_with_enough_samples_is_flagged()
    {
        var advice = ExcessTxDelayAdvisor.Assess("GB7XXX", medianPreDataCarrierMs: 412, sampleCount: 12);

        advice.Should().NotBeNull();
        advice!.Peer.Should().Be("GB7XXX");
        advice.MedianPreDataCarrierMs.Should().Be(412);
        advice.Message.Should().Contain("GB7XXX keys ~412 ms before data");
        advice.Message.Should().Contain("TXDELAY likely too high, wasting airtime");
        advice.Message.Should().Contain("n=12");
    }

    [Fact]
    public void A_healthy_peer_is_not_flagged()
    {
        ExcessTxDelayAdvisor.Assess("M0LTE-1", 180, 32).Should().BeNull();
    }

    [Fact]
    public void At_the_threshold_is_not_over_it()
    {
        ExcessTxDelayAdvisor.Assess("M0LTE-1", 250, 32).Should().BeNull();
    }

    [Fact]
    public void Too_few_samples_never_flag_even_when_high()
    {
        // One long keying (first-after-power-on, a manual test) must not flag a peer.
        ExcessTxDelayAdvisor.Assess("GB7XXX", 900, sampleCount: 3).Should().BeNull();
        ExcessTxDelayAdvisor.Assess("GB7XXX", 900, sampleCount: 4).Should().NotBeNull();
    }

    [Fact]
    public void No_measurement_means_no_advice()
    {
        ExcessTxDelayAdvisor.Assess("GB7XXX", null, 0).Should().BeNull();
    }

    [Fact]
    public void The_threshold_is_configurable()
    {
        var strict = new ExcessTxDelayAdvisorOptions { ThresholdMs = 150 };
        ExcessTxDelayAdvisor.Assess("M0LTE-1", 180, 32, strict).Should().NotBeNull();

        var lax = new ExcessTxDelayAdvisorOptions { ThresholdMs = 500 };
        ExcessTxDelayAdvisor.Assess("GB7XXX", 412, 32, lax).Should().BeNull();
    }
}
