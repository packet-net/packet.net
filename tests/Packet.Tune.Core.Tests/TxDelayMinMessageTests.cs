using Packet.Core;
using Packet.Kiss;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// The <c>TXD</c> telegram codec: per-action wire forms, round trips, the SDM character
/// budget for the routine forms, reject-don't-crash parsing of malformed args — and the
/// probe/settle frame factories (tag matching; a settle frame is never a probe).
/// </summary>
public class TxDelayMinMessageTests
{
    [Theory]
    [InlineData("propose|5|500|40", TxDelayMinAction.Propose)]
    [InlineData("confirm|5", TxDelayMinAction.Confirm)]
    [InlineData("reject|badk", TxDelayMinAction.Reject)]
    [InlineData("reject", TxDelayMinAction.Reject)]
    [InlineData("step|300|5", TxDelayMinAction.Step)]
    [InlineData("sent|300|5", TxDelayMinAction.ProbesSent)]
    [InlineData("report|300|5/5", TxDelayMinAction.StepReport)]
    [InlineData("report|300|4/5|352", TxDelayMinAction.StepReport)]
    [InlineData("apply|380|5", TxDelayMinAction.Apply)]
    [InlineData("done|380", TxDelayMinAction.Done)]
    [InlineData("done", TxDelayMinAction.Done)]
    [InlineData("abort|linkfail", TxDelayMinAction.Abort)]
    [InlineData("abort", TxDelayMinAction.Abort)]
    public void Every_action_round_trips_its_wire_form(string wire, TxDelayMinAction action)
    {
        TxDelayMinMessage.TryParse(wire, out var message).Should().BeTrue();
        message!.Action.Should().Be(action);
        message.ToArgs().Should().Be(wire);
    }

    [Fact]
    public void The_fields_land_where_the_protocol_says()
    {
        TxDelayMinMessage.TryParse("propose|5|500|40", out var propose).Should().BeTrue();
        propose!.Should().Be(new TxDelayMinMessage
        {
            Action = TxDelayMinAction.Propose,
            Count = 5,
            StartTxDelayMs = 500,
            StepMs = 40,
        });

        TxDelayMinMessage.TryParse("report|300|4/5|352", out var report).Should().BeTrue();
        report!.Should().Be(new TxDelayMinMessage
        {
            Action = TxDelayMinAction.StepReport,
            TxDelayMs = 300,
            Decoded = 4,
            Count = 5,
            MedianPreDataMs = 352,
        });

        TxDelayMinMessage.TryParse("done|380", out var done).Should().BeTrue();
        done!.RecommendedTxDelayMs.Should().Be(380);
    }

    [Theory]
    [InlineData("")]
    [InlineData("propose|5|500")]        // start without step
    [InlineData("propose|5|500|40|9")]   // too many fields
    [InlineData("step|300")]             // ms without k
    [InlineData("step|abc|5")]           // non-numeric
    [InlineData("report|300|45")]        // no fraction
    [InlineData("report|300|4/5|x")]     // non-numeric pre-data
    [InlineData("done|x")]               // non-numeric recommendation
    [InlineData("nonsense|1|2")]         // unknown action (forward compat: ignore)
    public void Malformed_args_parse_false_never_throw(string wire)
    {
        TxDelayMinMessage.TryParse(wire, out var message).Should().BeFalse();
        message.Should().BeNull();
    }

    [Fact]
    public void Only_txd_telegrams_are_ours()
    {
        var txd = new TuningTelegram(3, TuningVerb.TxDelay, "step|300|5");
        TxDelayMinMessage.TryFromTelegram(txd, out var message).Should().BeTrue();
        message!.Action.Should().Be(TxDelayMinAction.Step);

        var mode = new TuningTelegram(3, TuningVerb.ModeCoordination, "step|300|5");
        TxDelayMinMessage.TryFromTelegram(mode, out _).Should().BeFalse();
    }

    [Fact]
    public void The_txd_verb_round_trips_through_the_telegram_codec()
    {
        var telegram = new TxDelayMinMessage { Action = TxDelayMinAction.Step, TxDelayMs = 300, Count = 5 }
            .ToTelegram(41);
        telegram.Encode().Should().Be("V1|41|TXD|step|300|5");

        TuningTelegram.TryParse("V1|41|TXD|step|300|5", out var parsed).Should().BeTrue();
        parsed!.Verb.Should().Be(TuningVerb.TxDelay);
        parsed.Args.Should().Be("step|300|5");
    }

    [Theory]
    [InlineData("propose|5|500|40")]
    [InlineData("confirm|5")]
    [InlineData("step|2550|5")]
    [InlineData("sent|2550|5")]
    [InlineData("report|300|5/5|999")]
    [InlineData("apply|380|5")]
    [InlineData("done|2550")]
    [InlineData("abort|stnfail")]
    public void Routine_wire_forms_fit_the_plain_sdm_budget_at_three_digit_sequences(string args)
    {
        // A worst-case report (4-digit ms + 4-digit pre-data + 4-digit seq) rides an
        // extended SDM, exactly like STAT — the ROUTINE forms must fit a plain SDM.
        var telegram = new TuningTelegram(999, TuningVerb.TxDelay, args);
        telegram.EncodeCompact().Length.Should().BeLessThanOrEqualTo(TuningTelegram.SdmCharacterBudget);
    }

    // ─── probe / settle frame factories ─────────────────────────────────────

    [Fact]
    public void A_probe_frame_matches_only_its_own_step_tag()
    {
        var source = Callsign.Parse("M0LTE-7");
        var probe = new KissFrame(0, KissCommand.Data, TxDelayProbe.BuildFrame(source, 41, 2, 5).ToBytes());

        TxDelayProbe.IsProbeFrame(probe, 41).Should().BeTrue();
        TxDelayProbe.IsProbeFrame(probe, 4).Should().BeFalse("tag 4 must not prefix-match tag 41");
        TxDelayProbe.IsProbeFrame(probe, 42).Should().BeFalse();
    }

    [Fact]
    public void A_settle_frame_is_never_counted_as_a_probe()
    {
        var source = Callsign.Parse("M0LTE-7");
        var settle = new KissFrame(0, KissCommand.Data, TxDelayProbe.BuildSettleFrame(source, 300).ToBytes());

        TxDelayProbe.IsProbeFrame(settle, 41).Should().BeFalse();
    }

    [Fact]
    public void Non_data_kiss_frames_are_never_probes()
    {
        var source = Callsign.Parse("M0LTE-7");
        var wire = TxDelayProbe.BuildFrame(source, 41, 1, 5).ToBytes();
        TxDelayProbe.IsProbeFrame(new KissFrame(0, KissCommand.SetHardware, wire), 41).Should().BeFalse();
    }

    // ─── recommendation arithmetic ──────────────────────────────────────────

    [Fact]
    public void Recommendation_takes_the_larger_of_steps_and_fraction_and_rounds_up_to_ten()
    {
        var options = new TxDelayMinOptions { StepMs = 40, MarginSteps = 2, MarginFraction = 0.25 };

        // knee 300: 2 steps = 80 > 25% = 75 → 380.
        TxDelayMinReport.Recommend(300, options).Should().Be(380);

        // knee 400: 25% = 100 > 80 → 500.
        TxDelayMinReport.Recommend(400, options).Should().Be(500);

        // knee 25: 2 steps = 80 > 7 → 105 → rounds UP to 110.
        TxDelayMinReport.Recommend(25, options).Should().Be(110);
    }
}
