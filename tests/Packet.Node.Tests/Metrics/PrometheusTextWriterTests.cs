using Packet.Node.Api;

namespace Packet.Node.Tests.Metrics;

/// <summary>
/// Unit tests for the hand-rolled <see cref="PrometheusTextWriter"/> (#457): the exposition
/// line shapes (HELP/TYPE/sample), label rendering, integral-vs-float value formatting, and the
/// label-value escaping the exposition format requires (backslash, double-quote, newline). These
/// pin the formatter independently of the node so a regression in the wire shape is caught here.
/// </summary>
[Trait("Category", "Node")]
public sealed class PrometheusTextWriterTests
{
    [Fact]
    public void Help_type_and_unlabelled_sample_render_the_expected_lines()
    {
        var w = new PrometheusTextWriter();
        w.Help("pdn_uptime_seconds", "Node process uptime in seconds.");
        w.Type("pdn_uptime_seconds", "gauge");
        w.Sample("pdn_uptime_seconds", 42);

        w.ToString().Should().Be(
            "# HELP pdn_uptime_seconds Node process uptime in seconds.\n" +
            "# TYPE pdn_uptime_seconds gauge\n" +
            "pdn_uptime_seconds 42\n");
    }

    [Fact]
    public void Labelled_sample_renders_a_braced_labelset()
    {
        var w = new PrometheusTextWriter();
        w.Sample("pdn_port_up", 1, ("port", "vhf"));

        w.ToString().Should().Be("pdn_port_up{port=\"vhf\"} 1\n");
    }

    [Fact]
    public void Multiple_labels_are_comma_separated_in_order()
    {
        var w = new PrometheusTextWriter();
        w.Sample("pdn_build_info", 1, ("version", "0.1.0"), ("callsign", "M0LTE-1"));

        w.ToString().Should().Be("pdn_build_info{version=\"0.1.0\",callsign=\"M0LTE-1\"} 1\n");
    }

    [Fact]
    public void Integral_values_render_without_a_decimal_point_and_floats_round_trip()
    {
        var w = new PrometheusTextWriter();
        w.Sample("a", 7);
        w.Sample("b", 7.0);     // integral double - no decimal point
        w.Sample("c", 1.5);

        var lines = w.ToString().Split('\n');
        lines[0].Should().Be("a 7");
        lines[1].Should().Be("b 7");
        lines[2].Should().Be("c 1.5");
    }

    [Fact]
    public void Non_finite_values_render_as_zero_rather_than_NaN_or_Inf()
    {
        var w = new PrometheusTextWriter();
        w.Sample("a", double.NaN);
        w.Sample("b", double.PositiveInfinity);

        w.ToString().Should().Be("a 0\nb 0\n");
    }

    [Fact]
    public void Label_values_are_escaped_per_the_exposition_format()
    {
        var w = new PrometheusTextWriter();
        // A backslash, a double-quote, and a newline in a label value must all be escaped.
        w.Sample("m", 1, ("k", "a\\b\"c\nd"));

        w.ToString().Should().Be("m{k=\"a\\\\b\\\"c\\nd\"} 1\n");
    }
}
