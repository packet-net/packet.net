using System.Globalization;
using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>
/// Telemetry names (<c>PARM.</c>): a message to the telemetry station itself naming its 5 analog
/// and 8 digital channels (APRS12c §13 Parameter Name Message). The list may stop early.
/// </summary>
public sealed record AprsTelemetryParameterNames : AprsMessage
{
    /// <summary>Up to 13 names: A1-A5 then B1-B8.</summary>
    public required IReadOnlyList<string> Names { get; init => field = EquatableList<string>.Of(value); }

    private protected override void EncodeText(InfoWriter writer) => MessageCodec.WriteList(writer, "PARM.", Names, this);
}

/// <summary>
/// Telemetry units and labels (<c>UNIT.</c>): units for the 5 analog channels and labels for the
/// 8 digital ones (APRS12c §13 Unit/Label Message). The list may stop early.
/// </summary>
public sealed record AprsTelemetryUnits : AprsMessage
{
    /// <summary>Up to 13 entries: A1-A5 units then B1-B8 labels.</summary>
    public required IReadOnlyList<string> Units { get; init => field = EquatableList<string>.Of(value); }

    private protected override void EncodeText(InfoWriter writer) => MessageCodec.WriteList(writer, "UNIT.", Units, this);
}

/// <summary>
/// Telemetry scaling (<c>EQNS.</c>): three coefficients a, b, c per analog channel, giving
/// a x v^2 + b x v + c for raw value v (APRS12c §13 Equation Coefficients Message).
/// </summary>
public sealed record AprsTelemetryCoefficients : AprsMessage
{
    /// <summary>
    /// Up to 15 values in the order a1, b1, c1, a2, b2, c2 ... a5, b5, c5. The list may stop early.
    /// Each is a finite floating-point number (an exponent is allowed, <c>10E60</c>); a decoded
    /// report writes each back in the form it was sent.
    /// </summary>
    public required IReadOnlyList<double> Coefficients { get; init => field = EquatableList<double>.Of(value); }

    /// <summary>Applies channel <paramref name="channel"/>'s (1-5) coefficients to a raw value, as a
    /// telemetry report carries it; a missing coefficient counts as a=0, b=1, c=0.</summary>
    public double Scale(int channel, decimal raw)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channel, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 5);
        int i = (channel - 1) * 3;
        double a = i < Coefficients.Count ? Coefficients[i] : 0;
        double b = i + 1 < Coefficients.Count ? Coefficients[i + 1] : 1;
        double c = i + 2 < Coefficients.Count ? Coefficients[i + 2] : 0;
        double v = (double)raw;
        return (a * v * v) + (b * v) + c;
    }

    private protected override void EncodeText(InfoWriter writer)
    {
        if (Coefficients.Count is < 1 or > 15)
        {
            throw new ArgumentException("EQNS carries 1 to 15 coefficients", nameof(Coefficients));
        }

        if (Coefficients.Any(c => !double.IsFinite(c)))
        {
            throw new ArgumentException("an EQNS coefficient is a finite number", nameof(Coefficients));
        }

        IReadOnlyList<string>? sent = (Coefficients as EquatableList<double>)?.Text;
        MessageCodec.WriteList(writer, "EQNS.", sent ?? Coefficients.Select(c => c.ToString("R", CultureInfo.InvariantCulture)).ToList(), this);
    }
}

/// <summary>
/// Telemetry bit sense and project name (<c>BITS.</c>): which state of each digital channel
/// matches its label, then a project title (APRS12c §13 Bit Sense/Project Name Message).
/// </summary>
public sealed record AprsTelemetryBitSense : AprsMessage
{
    /// <summary>The 8 bit-sense flags; bit 0 is B1.</summary>
    public required byte Bits { get; init; }

    /// <summary>The project title, up to 23 characters (APRS12c ch. 13), or empty. Decoding accepts a longer one.</summary>
    public string ProjectTitle { get; init; } = "";

    private protected override void EncodeText(InfoWriter writer)
    {
        Internal.Text.RequireNoLineBreaks(ProjectTitle, nameof(ProjectTitle));
        MessageCodec.RequireNoBrace(ProjectTitle, nameof(ProjectTitle));
        if (ProjectTitle.EnumerateRunes().Count() > 23)
        {
            throw new ArgumentException("the telemetry project title is limited to 23 characters (APRS12c ch. 13)", nameof(ProjectTitle));
        }

        writer.Ascii("BITS.");
        for (int i = 0; i < 8; i++)
        {
            writer.Char((Bits & (1 << i)) != 0 ? '1' : '0');
        }

        if (ProjectTitle.Length > 0)
        {
            writer.Char(',').Utf8(ProjectTitle);
        }

        WriteMessageId(writer);
    }
}
