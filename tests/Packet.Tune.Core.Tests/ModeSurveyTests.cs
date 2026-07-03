using System.Text.Json;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The pure pieces of the IL2P+CRC mode survey: mode selection (Tom's
/// directive — "IL2P+CRC" exactly; plain IL2P and legacy AX.25 excluded),
/// verdicts, deadlines, RSSI reduction, and the Markdown/JSON renderings.
/// </summary>
public class ModeSurveyTests
{
    [Fact]
    public void Selects_exactly_the_il2p_crc_catalog_modes()
    {
        var modes = ModeSurvey.SelectIl2pCrcModes();
        modes.Select(m => m.Mode).Should().Equal(2, 4, 5, 7, 8, 9, 10, 11, 14);
    }

    [Fact]
    public void Plain_il2p_mode_13_is_not_selected()
    {
        // "300 AFSKPLL IL2P" contains "IL2P" but not "IL2P+CRC".
        ModeSurvey.SelectIl2pCrcModes().Should().NotContain(m => m.Mode == 13);
    }

    [Fact]
    public void Legacy_ax25_modes_are_not_selected()
    {
        ModeSurvey.SelectIl2pCrcModes()
            .Select(m => m.Mode)
            .Should().NotContain([(byte)0, (byte)1, (byte)3, (byte)6, (byte)12, (byte)15]);
    }

    [Fact]
    public void Every_selected_mode_name_says_il2p_crc()
    {
        ModeSurvey.SelectIl2pCrcModes()
            .Should().OnlyContain(m => m.Name.Contains("IL2P+CRC", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 5, ModeSurveyVerdict.Dead)]
    [InlineData(1, 5, ModeSurveyVerdict.Marginal)]
    [InlineData(4, 5, ModeSurveyVerdict.Marginal)]
    [InlineData(5, 5, ModeSurveyVerdict.Solid)]
    public void Verdict_reflects_success_ratio(int successes, int attempts, ModeSurveyVerdict expected)
    {
        Cell(successes: successes, attempts: attempts).Verdict.Should().Be(expected);
    }

    [Fact]
    public void Receive_timeout_scales_with_bit_rate_and_is_capped()
    {
        var fast = ModeSurvey.ReceiveTimeout(NinoTncCatalog.ByMode[2]);  // 9600
        var slow = ModeSurvey.ReceiveTimeout(NinoTncCatalog.ByMode[8]);  // 300
        fast.Should().BeLessThan(slow);
        fast.Should().BeGreaterThan(TimeSpan.FromSeconds(4));
        slow.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(20));

        // Mode 15 ("Set from KISS") has no bit rate — falls back to the cap.
        ModeSurvey.ReceiveTimeout(NinoTncCatalog.ByMode[15]).Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Settle_frame_is_never_mistaken_for_a_probe()
    {
        var settle = ModeSurvey.BuildSettleFrame(Callsign.Parse("N0CALL"), 7);
        var asKiss = new KissFrame(0, KissCommand.Data, settle.ToBytes());
        TuningBurst.IsBurstFrame(asKiss).Should().BeFalse();

        // ...while a real probe frame is.
        var probe = TuningBurst.BuildFrame(Callsign.Parse("N0CALL"), 1, 5);
        TuningBurst.IsBurstFrame(new KissFrame(0, KissCommand.Data, probe.ToBytes())).Should().BeTrue();
    }

    [Fact]
    public void Rssi_reduction_prefers_busy_gated_median()
    {
        double? picked = ModeSurvey.PickRssi(
        [
            (-129.0, false), // idle noise floor
            (-90.2, true),
            (-90.4, true),
            (-90.0, true),
            (-128.5, false),
        ]);
        picked.Should().Be(-90.2);
    }

    [Fact]
    public void Rssi_reduction_falls_back_to_strongest_sample_without_dcd()
    {
        ModeSurvey.PickRssi([(-129.0, false), (-90.4, false), (-128.0, false)]).Should().Be(-90.4);
        ModeSurvey.PickRssi([]).Should().BeNull();
    }

    [Fact]
    public void Mean_latency_is_null_with_no_successes()
    {
        ModeSurvey.MeanLatencyMs([]).Should().BeNull();
        ModeSurvey.MeanLatencyMs([100.0, 200.0]).Should().Be(150.0);
    }

    [Fact]
    public void Markdown_renders_one_row_per_cell_with_na_for_missing_signals()
    {
        string table = ModeSurvey.RenderMarkdown(
        [
            Cell(successes: 5, attempts: 5, latency: 1432.4, rssi: -90.25, rx: 5, uncorrectable: 0),
            Cell(successes: 0, attempts: 5),
        ]);

        table.Should().Contain("| Ch | Mode | Name | Dir | Decoded | Mean latency | RSSI @ RX | IL2P rx Δ | IL2P uncorr Δ | Verdict |");
        table.Should().Contain("| 5/5 | 1432 ms | -90.3 dBm | 5 | 0 | solid |");
        table.Should().Contain("| 0/5 | n/a | n/a | n/a | n/a | dead |");
    }

    [Fact]
    public void Marginal_cells_stand_out_in_the_markdown()
    {
        string table = ModeSurvey.RenderMarkdown([Cell(successes: 3, attempts: 5)]);
        table.Should().Contain("MARGINAL");
    }

    [Fact]
    public void Json_rendering_carries_the_cells_and_string_verdicts()
    {
        string json = ModeSurvey.RenderJson([Cell(successes: 2, attempts: 5, rssi: -90.0)]);
        using var doc = JsonDocument.Parse(json);
        var cell = doc.RootElement[0];
        cell.GetProperty("channel").GetInt32().Should().Be(1);
        cell.GetProperty("mode").GetInt32().Should().Be(2);
        cell.GetProperty("direction").GetString().Should().Be("A→B");
        cell.GetProperty("successes").GetInt32().Should().Be(2);
        cell.GetProperty("attempts").GetInt32().Should().Be(5);
        cell.GetProperty("receiverRssiDbm").GetDouble().Should().Be(-90.0);
        cell.GetProperty("verdict").GetString().Should().Be("marginal");
    }

    private static ModeSurveyCell Cell(
        int successes,
        int attempts,
        double? latency = null,
        double? rssi = null,
        long? rx = null,
        long? uncorrectable = null) => new()
        {
            Channel = 1,
            Mode = 2,
            ModeName = "9600 GFSK IL2P+CRC",
            Direction = "A→B",
            Successes = successes,
            Attempts = attempts,
            MeanLatencyMs = latency,
            ReceiverRssiDbm = rssi,
            Il2pRxPacketsDelta = rx,
            Il2pRxUncorrectableDelta = uncorrectable,
        };
}
