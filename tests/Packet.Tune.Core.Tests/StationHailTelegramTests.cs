using Packet.Radio.Tait;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The <c>HAIL</c> / <c>STAT</c> telegram codec: request/response round-trips, the mandatory
/// callsign, optional-field and unknown-key handling, and the SDM budget boundary (a minimal
/// status fits a plain 32-character SDM; a full status needs the 128-character extended SDM).
/// </summary>
public class StationHailTelegramTests
{
    [Fact]
    public void Hail_round_trips_through_a_telegram_with_the_requester_callsign()
    {
        var hail = new StationHail { RequesterCallsign = "GB7XYZ-2" };
        var telegram = hail.ToTelegram(7);

        telegram.Verb.Should().Be(TuningVerb.Hail);
        telegram.Encode().Should().Be("V1|7|HAIL|GB7XYZ-2");

        TuningTelegram.TryParse(telegram.Encode(), out var parsed).Should().BeTrue();
        StationHail.TryFromTelegram(parsed!, out var back).Should().BeTrue();
        back!.RequesterCallsign.Should().Be("GB7XYZ-2");
    }

    [Fact]
    public void A_bare_hail_carries_no_callsign()
    {
        var telegram = new StationHail().ToTelegram(1);
        telegram.Encode().Should().Be("V1|1|HAIL");

        StationHail.TryFromTelegram(telegram, out var hail).Should().BeTrue();
        hail!.RequesterCallsign.Should().BeNull();
    }

    [Fact]
    public void TryFromTelegram_rejects_a_non_hail_verb()
    {
        StationHail.TryFromTelegram(new TuningTelegram(1, TuningVerb.Status, "cs:X"), out var hail).Should().BeFalse();
        hail.Should().BeNull();
    }

    [Fact]
    public void Status_round_trips_every_field()
    {
        var status = new StationStatus
        {
            Callsign = "GB7RDG-1",
            Mode = 6,
            BitRateHz = 1200,
            Channel = "0",
            SupportedModes = [0, 2, 6, 7],
            Capabilities = ["hail", "modecoord", "tune"],
            RssiOfHailDbm = -95.4,
        };

        var telegram = status.ToTelegram(12);
        telegram.Verb.Should().Be(TuningVerb.Status);

        TuningTelegram.TryParse(telegram.Encode(), out var parsed).Should().BeTrue();
        StationStatus.TryFromTelegram(parsed!, out var back).Should().BeTrue();

        back!.Callsign.Should().Be("GB7RDG-1");
        back.Mode.Should().Be(6);
        back.BitRateHz.Should().Be(1200);
        back.Channel.Should().Be("0");
        back.SupportedModes.Should().Equal([0, 2, 6, 7]);
        back.Capabilities.Should().Equal(["hail", "modecoord", "tune"]);
        back.RssiOfHailDbm.Should().Be(-95.4);
    }

    [Fact]
    public void Status_mode_name_is_derived_from_the_catalog_not_the_wire()
    {
        var status = new StationStatus { Callsign = "N0CALL", Mode = 6 };
        status.ModeName.Should().Be("1200 AFSK AX.25");

        // The name never appears on the wire — only the number does.
        status.ToArgs().Should().NotContain("AFSK");
    }

    [Fact]
    public void An_unknown_mode_number_yields_a_fallback_name()
    {
        new StationStatus { Callsign = "N0CALL", Mode = 200 }.ModeName.Should().Be("mode 200");
        new StationStatus { Callsign = "N0CALL", Mode = null }.ModeName.Should().BeNull();
    }

    [Fact]
    public void Parse_requires_the_callsign_token()
    {
        StationStatus.TryParse("m:6|b:1200", out var status).Should().BeFalse();
        status.Should().BeNull();
    }

    [Fact]
    public void Parse_tolerates_unknown_keys_and_missing_optionals()
    {
        StationStatus.TryParse("cs:GB7RDG|m:6|future:xyz", out var status).Should().BeTrue();
        status!.Callsign.Should().Be("GB7RDG");
        status.Mode.Should().Be(6);
        status.BitRateHz.Should().BeNull();
        status.Channel.Should().BeNull();
        status.SupportedModes.Should().BeEmpty();
        status.RssiOfHailDbm.Should().BeNull();
    }

    [Fact]
    public void A_minimal_status_fits_a_plain_32_character_SDM()
    {
        var status = new StationStatus { Callsign = "GB7RDG-1", Mode = 6 };
        status.ToTelegram(1).EncodeCompact().Length.Should().BeLessThanOrEqualTo(TaitSdmSideChannel.PayloadBudget);
    }

    [Fact]
    public void A_full_status_needs_the_extended_SDM_budget_and_fits_it()
    {
        var status = new StationStatus
        {
            Callsign = "GB7RDG-1",
            Mode = 6,
            BitRateHz = 1200,
            Channel = "0",
            SupportedModes = NinoTncStationStatusSource.DefaultSupportedModes(),
            Capabilities = NinoTncStationStatusSource.DefaultCapabilities,
            RssiOfHailDbm = -95.4,
        };

        int length = status.ToTelegram(9999).EncodeCompact().Length;
        length.Should().BeGreaterThan(TaitSdmSideChannel.PayloadBudget, "the rich status busts the plain SDM budget");
        length.Should().BeLessThanOrEqualTo(TaitSdmSideChannel.ExtendedPayloadBudget, "and fits the extended SDM budget");
    }
}
