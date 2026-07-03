namespace Packet.Tune.Core.Tests;

/// <summary>The V1|seq|verb|args codec: encode/parse round trips (canonical
/// and compact), the SDM 32-character budget, and rejection of junk.</summary>
public class TuningTelegramTests
{
    [Theory]
    [InlineData(0, TuningVerb.Hello, "tuned", "V1|0|HI|tuned")]
    [InlineData(3, TuningVerb.BurstRequest, "5", "V1|3|RQ|5")]
    [InlineData(12, TuningVerb.Advice, "OK", "V1|12|AD|OK")]
    [InlineData(99, TuningVerb.Bye, "", "V1|99|BY")]
    public void Encodes_the_documented_wire_form(int seq, TuningVerb verb, string args, string expected)
    {
        new TuningTelegram(seq, verb, args).Encode().Should().Be(expected);
    }

    [Theory]
    [InlineData("V1|0|HI|tuned", 0, TuningVerb.Hello, "tuned")]
    [InlineData("V1|3|RQ|5", 3, TuningVerb.BurstRequest, "5")]
    [InlineData("V1|99|BY", 99, TuningVerb.Bye, "")]
    [InlineData("V1|7|MS|4/5|fec:12|clip:0|rssi:-90.4", 7, TuningVerb.Measurement, "4/5|fec:12|clip:0|rssi:-90.4")]
    public void Parses_the_documented_wire_form(string wire, int seq, TuningVerb verb, string args)
    {
        TuningTelegram.TryParse(wire, out var telegram).Should().BeTrue();
        telegram!.Sequence.Should().Be(seq);
        telegram.Verb.Should().Be(verb);
        telegram.Args.Should().Be(args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("V2|0|HI|tuned")]
    [InlineData("V1|x|HI|tuned")]
    [InlineData("V1|0|ZZ|tuned")]
    [InlineData("hello world")]
    [InlineData("V1|0")]
    public void Rejects_junk(string? wire)
    {
        TuningTelegram.TryParse(wire, out _).Should().BeFalse();
    }

    [Fact]
    public void Every_verb_round_trips()
    {
        foreach (TuningVerb verb in Enum.GetValues<TuningVerb>())
        {
            var original = new TuningTelegram(42, verb, verb == TuningVerb.Bye ? "" : "x");
            TuningTelegram.TryParse(original.Encode(), out var parsed).Should().BeTrue();
            parsed.Should().Be(original);
        }
    }

    [Fact]
    public void Compact_MS_fits_the_32_char_SDM_budget_and_round_trips()
    {
        var report = new MeterReport(10, 10, FecCorrectedBytesDelta: 480, LostAdcSamplesDelta: 0, RssiDbm: -90.4);
        var telegram = new TuningTelegram(12, TuningVerb.Measurement, report.ToArgs());

        // The canonical form is too long for one SDM — that is why the
        // compact form exists.
        telegram.Encode().Length.Should().BeGreaterThan(32);
        string compact = telegram.EncodeCompact();
        compact.Length.Should().BeLessThanOrEqualTo(32);

        TuningTelegram.TryParse(compact, out var parsed).Should().BeTrue();
        MeterReport.TryParse(parsed!.Args, out var reparsed).Should().BeTrue();
        reparsed.Should().Be(report);
    }

    [Fact]
    public void Compact_encoding_leaves_non_MS_verbs_alone()
    {
        var telegram = new TuningTelegram(1, TuningVerb.Hello, "meter");
        telegram.EncodeCompact().Should().Be(telegram.Encode());
    }

    [Fact]
    public void MS_args_with_unavailable_fields_round_trip_as_null()
    {
        var report = new MeterReport(3, 5, FecCorrectedBytesDelta: null, LostAdcSamplesDelta: null, RssiDbm: null);
        report.ToArgs().Should().Be("3/5|fec:na|clip:na|rssi:na");
        MeterReport.TryParse(report.ToArgs(), out var canonical).Should().BeTrue();
        canonical.Should().Be(report);

        // The compact form simply omits unavailable fields.
        report.ToCompactArgs().Should().Be("3/5");
        MeterReport.TryParse(report.ToCompactArgs(), out var compact).Should().BeTrue();
        compact.Should().Be(report);
    }

    [Fact]
    public void MS_args_reject_junk()
    {
        MeterReport.TryParse(null, out _).Should().BeFalse();
        MeterReport.TryParse("", out _).Should().BeFalse();
        MeterReport.TryParse("nope", out _).Should().BeFalse();
        MeterReport.TryParse("a/b", out _).Should().BeFalse();
        MeterReport.TryParse("3/5|bogus:1", out _).Should().BeFalse();
    }
}
