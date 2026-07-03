using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core.Tests;

/// <summary>Sweep mode selection (the --strict-bandwidth gate) and the result-table rendering.</summary>
public class ModeCoordReportTests
{
    [Fact]
    public void Lenient_sweep_takes_every_il2p_crc_mode_regardless_of_width()
    {
        var modes = ModeCoordReport.SelectSweepModes(strictBandwidth: false, channelIsWide: false);

        modes.Select(m => m.Mode).Should().Equal(1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 14);
    }

    [Fact]
    public void Strict_bandwidth_on_a_narrow_channel_drops_the_25_kHz_modes()
    {
        var modes = ModeCoordReport.SelectSweepModes(strictBandwidth: true, channelIsWide: false);

        // Modes 1 and 2 are 25 kHz modes per the OARC wiki (mode 0 is not IL2P+CRC).
        modes.Select(m => m.Mode).Should().Equal(3, 4, 5, 7, 8, 9, 10, 11, 14);
    }

    [Fact]
    public void Strict_bandwidth_on_a_wide_channel_keeps_everything()
    {
        var modes = ModeCoordReport.SelectSweepModes(strictBandwidth: true, channelIsWide: true);

        modes.Select(m => m.Mode).Should().Equal(1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 14);
    }

    [Fact]
    public void The_wide_channel_catalog_set_is_modes_0_1_2()
    {
        NinoTncCatalog.WideChannelModes.Order().Should().Equal((byte)0, (byte)1, (byte)2);
        NinoTncCatalog.RequiresWideChannel(2).Should().BeTrue();
        NinoTncCatalog.RequiresWideChannel(3).Should().BeFalse();
    }

    [Fact]
    public void A_probed_attempt_renders_two_direction_rows()
    {
        var attempt = new ModeCoordAttempt
        {
            Mode = 7,
            ModeName = "1200 AFSK IL2P+CRC",
            ChannelInEffect = 0,
            Outcome = ModeCoordOutcome.Switched,
            CoordinatorToResponder = new ModeProbeCell(5, 5, 1234.4),
            ResponderToCoordinator = new ModeProbeCell(4, 5, 1301.0),
        };

        string table = ModeCoordReport.RenderMarkdown([attempt]);

        table.Should().Contain("| 0 | 7 | 1200 AFSK IL2P+CRC | C→R | 5/5 | 1234 ms | switched (MARGINAL) |");
        table.Should().Contain("| 0 | 7 | 1200 AFSK IL2P+CRC | R→C | 4/5 | 1301 ms | switched (MARGINAL) |");
    }

    [Fact]
    public void A_pre_probe_failure_renders_one_row_with_the_outcome()
    {
        var attempt = new ModeCoordAttempt
        {
            Mode = 15,
            ModeName = "Set from KISS",
            ChannelInEffect = 0,
            Outcome = ModeCoordOutcome.Rejected,
            Detail = "unkmode",
        };

        string table = ModeCoordReport.RenderMarkdown([attempt]);

        table.Should().Contain("| 0 | 15 | Set from KISS | — | n/a | n/a | rejected (unkmode) |");
    }

    [Fact]
    public void A_reverted_dead_probe_describes_the_home_link_state()
    {
        var attempt = new ModeCoordAttempt
        {
            Mode = 1,
            ModeName = "19200 4FSK IL2P+CRC",
            ChannelInEffect = 0,
            Outcome = ModeCoordOutcome.ProbeDead,
            CoordinatorToResponder = new ModeProbeCell(0, 5, null),
            ResponderToCoordinator = new ModeProbeCell(0, 5, null),
            Reverted = true,
            HomeLinkAlive = true,
        };

        ModeCoordReport.DescribeOutcome(attempt).Should().Be("PROBE DEAD — reverted, home link alive");
        ModeCoordReport.RenderMarkdown([attempt]).Should().Contain("| C→R | 0/5 | n/a |");
    }

    [Fact]
    public void Solid_both_ways_reads_as_solid()
    {
        var attempt = new ModeCoordAttempt
        {
            Mode = 2,
            ModeName = "9600 GFSK IL2P+CRC",
            ChannelInEffect = 1,
            Channel = 1,
            Outcome = ModeCoordOutcome.Switched,
            CoordinatorToResponder = new ModeProbeCell(5, 5, 640.0),
            ResponderToCoordinator = new ModeProbeCell(5, 5, 655.0),
        };

        ModeCoordReport.DescribeOutcome(attempt).Should().Be("switched (solid both ways)");
    }
}
