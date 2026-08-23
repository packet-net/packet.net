using M0LTE.Tait.Codeplug;
using Packet.Node.Core.Radios.Programming;

namespace Packet.Node.Tests.Radios;

/// <summary>
/// The pure half of codeplug programming (#779): turning what the operator typed into a plan, and
/// applying that plan to a codeplug. Both run before the radio is touched, so a bad form field is a
/// 400 rather than a port that has been taken off the air for nothing.
/// </summary>
/// <remarks>
/// The codeplug images here are built the way <c>M0LTE.Tait.Codeplug</c>'s own channel-table tests
/// build them - a channel table (0x05), a CIB index (0x07) and a data/signalling record (0x09) at
/// the sizes a real radio carries - so the assertions are about <em>our</em> plan logic. What the
/// individual field writes put on the wire is the library's business, and is hardware-validated
/// there.
/// </remarks>
[Trait("Category", "Node")]
public sealed class TaitProgramPlanTests
{
    private const long TwoMetres = 144_812_500;

    [Fact]
    public void A_complete_request_parses()
    {
        var request = new TaitProgramRequest(TwoMetres, null, "narrow", "high", "pdn-extra");

        TaitProgramPlan.TryParse(request, out var plan, out string error).Should().BeTrue(error);

        plan.RxFrequencyHz.Should().Be(TwoMetres);
        plan.TxFrequencyHz.Should().Be(TwoMetres, "an omitted TX frequency means simplex");
        plan.Bandwidth.Should().Be(Bandwidth.Narrow);
        plan.Power.Should().Be(PowerLevel.High);
        plan.Profile.Should().Be(TaitPdnProfile.Extra);
        plan.ToWire().Should().Be(new TaitProgramPlanInfo(TwoMetres, TwoMetres, "narrow", "high", "pdn-extra", true));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    public void An_absent_profile_means_none(string? profile)
    {
        var request = new TaitProgramRequest(TwoMetres, null, "wide", "low", profile);

        TaitProgramPlan.TryParse(request, out var plan, out _).Should().BeTrue();

        plan.Profile.Should().Be(TaitPdnProfile.None);
    }

    [Fact]
    public void A_split_channel_keeps_both_frequencies()
    {
        var request = new TaitProgramRequest(145_625_000, 145_025_000, "narrow", "medium", "none");

        TaitProgramPlan.TryParse(request, out var plan, out _).Should().BeTrue();

        plan.RxFrequencyHz.Should().Be(145_625_000);
        plan.TxFrequencyHz.Should().Be(145_025_000);
    }

    [Theory]
    // No frequency at all, and frequencies no Tait band split reaches.
    [InlineData(null, "rxFrequencyHz")]
    [InlineData(14_100_000L, "rxFrequencyHz")]
    [InlineData(2_400_000_000L, "rxFrequencyHz")]
    public void An_implausible_rx_frequency_is_refused(long? hz, string expected)
    {
        var request = new TaitProgramRequest(hz, null, "narrow", "high", "none");

        TaitProgramPlan.TryParse(request, out _, out string error).Should().BeFalse();

        error.Should().Contain(expected);
    }

    [Theory]
    [InlineData("12.5", "bandwidth")]
    [InlineData(null, "bandwidth")]
    public void An_unknown_bandwidth_is_refused(string? bandwidth, string expected)
    {
        var request = new TaitProgramRequest(TwoMetres, null, bandwidth, "high", "none");

        TaitProgramPlan.TryParse(request, out _, out string error).Should().BeFalse();

        error.Should().Contain(expected);
    }

    [Fact]
    public void Power_off_is_refused_even_though_the_codeplug_has_it()
    {
        // A channel the radio cannot transmit on is never what this panel is being asked for, so
        // `off` is deliberately not one of the accepted spellings.
        var request = new TaitProgramRequest(TwoMetres, null, "narrow", "off", "none");

        TaitProgramPlan.TryParse(request, out _, out string error).Should().BeFalse();

        error.Should().Contain("power must be one of");
    }

    [Fact]
    public void An_unknown_profile_is_refused()
    {
        var request = new TaitProgramRequest(TwoMetres, null, "narrow", "high", "pdn-everything");

        TaitProgramPlan.TryParse(request, out _, out string error).Should().BeFalse();

        error.Should().Contain("pdn-basic");
    }

    [Fact]
    public void A_null_request_is_refused_rather_than_thrown()
    {
        TaitProgramPlan.TryParse(null, out _, out string error).Should().BeFalse();

        error.Should().Contain("required");
    }

    [Fact]
    public void A_frequency_outside_the_radios_own_band_split_is_refused()
    {
        // TMAB12-B100_0201 is a B1 split: 136-174 MHz. 70 cm does not fit, and this is the mistake
        // actually worth catching - the operator has the wrong radio in front of them.
        var plan = Plan(433_400_000);

        plan.CheckBand("TMAB12-B100_0201").Should().Contain("B1").And.Contain("433.4 MHz");
    }

    [Fact]
    public void A_frequency_inside_the_radios_band_split_is_allowed()
    {
        Plan(TwoMetres).CheckBand("TMAB12-B100_0201").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something-that-is-not-a-tait-product-code")]
    public void An_unreadable_product_code_does_not_block_the_write(string? productCode)
    {
        // The write path has its own database-version guard. Refusing to program a radio whose model
        // string we could not parse would be a worse failure than trusting a deliberate operator.
        Plan(433_400_000).CheckBand(productCode).Should().BeNull();
    }

    [Fact]
    public void Applying_a_plan_leaves_exactly_one_channel()
    {
        var fields = Codeplug(channels: 6);

        Plan(TwoMetres).ApplyTo(fields);

        fields.ChannelCount.Should().Be(1);
        fields.GetRxFrequencyHz(0).Should().Be(TwoMetres);
        fields.GetTxFrequencyHz(0).Should().Be(TwoMetres);
        fields.GetSeparateTxFrequency(0).Should().BeFalse("a simplex channel has no separate TX");
        fields.GetBandwidth(0).Should().Be(Bandwidth.Narrow);
        fields.GetPowerLevel(0).Should().Be(PowerLevel.High);
    }

    [Fact]
    public void A_split_plan_sets_the_separate_tx_flag()
    {
        var fields = Codeplug();

        Plan(145_625_000, tx: 145_025_000).ApplyTo(fields);

        fields.GetSeparateTxFrequency(0).Should().BeTrue();
        fields.GetRxFrequencyHz(0).Should().Be(145_625_000);
        fields.GetTxFrequencyHz(0).Should().Be(145_025_000);
    }

    [Fact]
    public void The_written_channel_is_carrier_squelch()
    {
        // Whatever the radio used to do, the channel this panel writes is a packet channel: a
        // leftover RX tone would mute every frame from a peer that does not send it, with no error
        // anywhere. Deliberate, documented, and asserted here so it cannot quietly stop happening.
        var fields = Codeplug();
        fields.SetRxSubaudibleType(0, SubaudibleType.Ctcss);
        fields.SetTxSubaudibleType(0, SubaudibleType.Ctcss);

        Plan(TwoMetres).ApplyTo(fields);

        fields.GetRxSubaudibleType(0).Should().Be(SubaudibleType.None);
        fields.GetTxSubaudibleType(0).Should().Be(SubaudibleType.None);
    }

    [Fact]
    public void The_basic_profile_turns_the_ccdi_command_channel_on()
    {
        var fields = Codeplug();

        Plan(TwoMetres, profile: "pdn-basic").ApplyTo(fields);

        fields.CcdiModeAllowed.Should().BeTrue();
        fields.PowerupState.Should().Be(DataPowerupMode.CommandMode);
        fields.CcdiProgressMessageEnabled.Should().BeTrue();
        fields.CommandModeBaud.Should().Be(FfskBaud.Baud28800);
        fields.TransparentModeEnabled.Should().BeFalse("pdn-basic stops short of the internal modem");
    }

    [Fact]
    public void The_extra_profile_adds_the_internal_modem_and_the_sdm_side_channel()
    {
        var fields = Codeplug();

        Plan(TwoMetres, profile: "pdn-extra").ApplyTo(fields);

        fields.CcdiModeAllowed.Should().BeTrue("pdn-extra includes pdn-basic");
        fields.TransparentModeEnabled.Should().BeTrue();
        fields.IgnoreEscapeSequence.Should().BeFalse("without this the radio cannot escape back to command mode");
        fields.SdmEnabled.Should().BeTrue();
        fields.CcdiSdmOutputEnabled.Should().BeTrue();
    }

    [Fact]
    public void Profile_none_leaves_the_data_record_alone()
    {
        var fields = Codeplug();
        byte[] before = [.. fields.Image.Require(0x09, 0).Data];

        Plan(TwoMetres).ApplyTo(fields);

        fields.Image.Require(0x09, 0).Data.Should().Equal(before);
    }

    private static TaitProgramPlan Plan(long rx, long? tx = null, string profile = "none")
    {
        TaitProgramPlan.TryParse(
            new TaitProgramRequest(rx, tx, "narrow", "high", profile), out var plan, out string error)
            .Should().BeTrue(error);
        return plan;
    }

    /// <summary>A minimal codeplug of the shape a real radio carries: a 181-bit-per-channel table
    /// (0x05), the CIB channel index (0x07) and the 32-byte data/signalling record (0x09).</summary>
    private static CodeplugFields Codeplug(int channels = 1)
    {
        var image = new CodeplugImage(
            [new KeyValuePair<string, string>("DBVer", "0095")],
            [
                new CodeplugRecord(0x05, 0, new byte[23]),
                new CodeplugRecord(0x07, 0, new byte[2]),
                new CodeplugRecord(0x09, 0, new byte[32]),
            ]);
        var fields = CodeplugFields.Open(image);
        while (fields.ChannelCount < channels)
        {
            fields.AddChannel();
        }

        return fields;
    }
}
