namespace Packet.Tune.Core.Tests;

/// <summary>
/// The <c>MODE</c> telegram codec: per-action wire forms, round trips, the SDM
/// character budget, and reject-don't-crash parsing of malformed args.
/// </summary>
public class ModeCoordMessageTests
{
    [Fact]
    public void Propose_without_channel_encodes_per_the_protocol_sketch()
    {
        var message = new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = 2 };

        message.ToArgs().Should().Be("propose|2");
        message.ToTelegram(7).Encode().Should().Be("V1|7|MODE|propose|2");
    }

    [Fact]
    public void Propose_with_channel_carries_it_positionally()
    {
        var message = new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = 1, Channel = 1 };

        message.ToArgs().Should().Be("propose|1|1");
    }

    [Theory]
    [InlineData("propose|2", ModeCoordAction.Propose, (byte)2, null)]
    [InlineData("confirm|14|1", ModeCoordAction.Confirm, (byte)14, 1)]
    [InlineData("commit|6|0", ModeCoordAction.Commit, (byte)6, 0)]
    public void Mode_carrying_actions_round_trip(string wire, ModeCoordAction action, byte mode, int? channel)
    {
        ModeCoordMessage.TryParse(wire, out var message).Should().BeTrue();
        message!.Action.Should().Be(action);
        message.Mode.Should().Be(mode);
        message.Channel.Should().Be(channel);
        message.ToArgs().Should().Be(wire);
    }

    [Fact]
    public void Sent_report_and_revert_round_trip()
    {
        ModeCoordMessage.TryParse("sent|5|1234", out var sent).Should().BeTrue();
        sent!.Should().Be(new ModeCoordMessage { Action = ModeCoordAction.ProbesSent, Count = 5, MeanTxMs = 1234 });
        sent.ToArgs().Should().Be("sent|5|1234");

        ModeCoordMessage.TryParse("report|4/5", out var report).Should().BeTrue();
        report!.Should().Be(new ModeCoordMessage { Action = ModeCoordAction.ProbeReport, Decoded = 4, Count = 5 });
        report.ToArgs().Should().Be("report|4/5");

        ModeCoordMessage.TryParse("revert|probedead", out var revert).Should().BeTrue();
        revert!.Should().Be(new ModeCoordMessage { Action = ModeCoordAction.Revert, Reason = "probedead" });
        revert.ToArgs().Should().Be("revert|probedead");

        ModeCoordMessage.TryParse("revert", out var bareRevert).Should().BeTrue();
        bareRevert!.Reason.Should().BeNull();
    }

    [Fact]
    public void Reject_carries_mode_and_optional_reason()
    {
        ModeCoordMessage.TryParse("reject|15|unknown-mode", out var reject).Should().BeTrue();
        reject!.Action.Should().Be(ModeCoordAction.Reject);
        reject.Mode.Should().Be(15);
        reject.Reason.Should().Be("unknown-mode");
    }

    [Theory]
    [InlineData("")]
    [InlineData("propose")]
    [InlineData("propose|banana")]
    [InlineData("propose|2|1|extra")]
    [InlineData("sent|x")]
    [InlineData("report|4")]
    [InlineData("report|4/5/6")]
    [InlineData("upgrade|2")]
    public void Malformed_or_unknown_args_parse_false(string wire)
    {
        ModeCoordMessage.TryParse(wire, out var message).Should().BeFalse();
        message.Should().BeNull();
    }

    [Fact]
    public void Every_wire_form_fits_the_32_char_sdm_budget_at_realistic_sequence_numbers()
    {
        // Worst realistic case: 4-digit sequence numbers deep into a long session.
        var telegrams = new[]
        {
            new ModeCoordMessage { Action = ModeCoordAction.Propose, Mode = 14, Channel = 1 }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.Confirm, Mode = 14, Channel = 1 }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.Commit, Mode = 14, Channel = 1 }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.Reject, Mode = 14, Reason = "unknown-mode" }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.ProbesSent, Count = 20, MeanTxMs = 99999 }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.ProbeReport, Decoded = 20, Count = 20 }.ToTelegram(9999),
            new ModeCoordMessage { Action = ModeCoordAction.Revert, Reason = "probedead" }.ToTelegram(9999),
        };

        foreach (var telegram in telegrams)
        {
            telegram.EncodeCompact().Length.Should().BeLessThanOrEqualTo(
                TuningTelegram.SdmCharacterBudget, telegram.EncodeCompact());
        }
    }

    [Fact]
    public void A_long_or_piped_reason_is_sanitised_to_keep_the_budget()
    {
        var message = new ModeCoordMessage
        {
            Action = ModeCoordAction.Revert,
            Reason = "something|went|terribly|wrong in great detail",
        };

        message.ToArgs().Should().Be("revert|something");
    }

    [Fact]
    public void TryFromTelegram_only_accepts_MODE_verbs()
    {
        var mode = new TuningTelegram(3, TuningVerb.ModeCoordination, "propose|2");
        ModeCoordMessage.TryFromTelegram(mode, out var parsed).Should().BeTrue();
        parsed!.Mode.Should().Be(2);

        var hello = new TuningTelegram(4, TuningVerb.Hello, "coord");
        ModeCoordMessage.TryFromTelegram(hello, out var none).Should().BeFalse();
        none.Should().BeNull();
    }

    [Fact]
    public void The_MODE_verb_round_trips_through_the_telegram_codec()
    {
        TuningTelegram.TryParse("V1|12|MODE|commit|2|1", out var telegram).Should().BeTrue();
        telegram!.Verb.Should().Be(TuningVerb.ModeCoordination);
        telegram.Args.Should().Be("commit|2|1");
        telegram.Encode().Should().Be("V1|12|MODE|commit|2|1");
    }
}
