using System.Text;

namespace Packet.Aprs.Tests;

/// <summary>
/// Shared given / when / then steps. Each test reads as a short scenario:
/// <code>
/// GivenPacket("N0CALL>APZ001:!4903.50N/07201.75W-Test 001234");
/// WhenDecoded();
/// ThenPositionIs(49.058333, -72.029166);
/// ThenReEncodesExactly();
/// </code>
/// Steps keep state in fields, so later steps check what earlier ones produced.
/// </summary>
public abstract class AprsSpec
{
    private const double Tolerance = 1e-6;

    private byte[]? line;
    private AprsParseOptions options = AprsParseOptions.Lenient;
    private AprsPacket? packet;
    private byte[]? encoded;
    private Exception? thrown;

    /// <summary>The packet produced by <see cref="WhenDecoded"/>.</summary>
    protected AprsPacket Packet => packet ?? throw new InvalidOperationException("call WhenDecoded first");

    /// <summary>The information field produced by <see cref="WhenEncoded"/>.</summary>
    protected byte[] Encoded => encoded ?? throw new InvalidOperationException("call WhenEncoded first");

    // ------------------------------------------------------------------ given

    /// <summary>A whole TNC2 line. <c>&lt;0xNN&gt;</c> inserts a raw byte, as in the spec documents.</summary>
    protected void GivenPacket(string tnc2) => line = Bytes(tnc2);

    /// <summary>Just an information field, with a neutral header.</summary>
    protected void GivenInformationField(string info, string destination = "APZ001") =>
        GivenPacket($"N0CALL>{destination}:{info}");

    /// <summary>A packet as raw bytes (e.g. an AX.25 frame for <see cref="WhenDecodedAsAx25"/>).</summary>
    protected void GivenBytes(byte[] bytes) => line = bytes;

    protected void GivenStrictDecoding() => options = AprsParseOptions.Strict;

    protected void GivenParseOptions(AprsParseOptions parseOptions) => options = parseOptions;

    // ------------------------------------------------------------------ when

    protected void WhenDecoded() => packet = AprsPacket.Decode(line ?? throw new InvalidOperationException("no packet given"), options);

    protected void WhenDecodedAsAx25() => packet = AprsPacket.DecodeAx25(line ?? throw new InvalidOperationException("no frame given"), options);

    protected void WhenDecodingIsAttempted()
    {
        packet = null;
        thrown = null;
        try
        {
            WhenDecoded();
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
    }

    protected void WhenEncoded(AprsData data) => encoded = data.ToInformationField();

    protected void WhenEncodingIsAttempted(AprsData data)
    {
        try
        {
            WhenEncoded(data);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
    }

    // ------------------------------------------------------------------ then: data

    /// <summary>Asserts the decoded data type and returns it for further checks.</summary>
    protected T ThenDataIs<T>()
        where T : AprsData
    {
        Packet.Data.Should().BeOfType<T>(because: "diagnostics: {0}", string.Join(" | ", Packet.Diagnostics));
        return (T)Packet.Data;
    }

    protected void ThenUnrecognized(AprsUnrecognizedReason reason) =>
        ThenDataIs<AprsUnrecognizedData>().Reason.Should().Be(reason);

    protected void ThenPositionIs(double latitude, double longitude, int ambiguity = 0, double tolerance = Tolerance)
    {
        AprsPosition p = PositionOf(Packet.Data);
        p.Latitude.Should().BeApproximately(latitude, tolerance);
        p.Longitude.Should().BeApproximately(longitude, tolerance);
        p.Ambiguity.Should().Be(ambiguity);
    }

    protected void ThenSymbolIs(string symbol) => ((AprsPositionedData)Packet.Data).Symbol.ToString().Should().Be(symbol);

    protected void ThenCommentIs(string comment) => ((AprsPositionedData)Packet.Data).Comment.Should().Be(comment);

    protected void ThenCourseAndSpeedAre(int? courseDegrees, double? speedKnots, double tolerance = Tolerance)
    {
        var d = (AprsPositionedData)Packet.Data;
        d.CourseDegrees.Should().Be(courseDegrees);
        if (speedKnots is null)
        {
            d.SpeedKnots.Should().BeNull();
        }
        else
        {
            d.SpeedKnots.Should().BeApproximately(speedKnots.Value, tolerance);
        }
    }

    protected void ThenAltitudeFeetIs(double feet, double tolerance = 0.5) =>
        ((AprsPositionedData)Packet.Data).AltitudeFeet.Should().BeApproximately(feet, tolerance);

    protected void ThenTimestampIs(string onAir)
    {
        AprsTimestamp? ts = Packet.Data switch
        {
            AprsPositionReport p => p.Timestamp,
            AprsObjectReport o => o.Timestamp,
            AprsStatusReport s => s.Timestamp,
            AprsWeatherReport w => w.Timestamp,
            _ => throw new InvalidOperationException($"{Packet.Data.GetType().Name} has no timestamp"),
        };
        ts.Should().NotBeNull();
        ts!.Value.ToString().Should().Be(onAir);
    }

    // ------------------------------------------------------------------ then: diagnostics

    protected void ThenNoDiagnostics() => Packet.Diagnostics.Should().BeEmpty();

    /// <summary>No warnings or errors; informational notes (e.g. obsolete format) are allowed.</summary>
    protected void ThenNoProblems() =>
        Packet.Diagnostics.Where(d => d.Severity != AprsDiagnosticSeverity.Info).Should().BeEmpty();

    protected void ThenDiagnostic(AprsDiagnosticCode code, AprsDiagnosticSeverity severity) =>
        Packet.Diagnostics.Should().Contain(d => d.Code == code && d.Severity == severity,
            because: "diagnostics were: {0}", string.Join(" | ", Packet.Diagnostics));

    /// <summary>The tolerated-defect pattern: decoded, with a warning for exactly this code.</summary>
    protected void ThenToleratedWith(AprsDiagnosticCode code)
    {
        Packet.HasErrors.Should().BeFalse(because: "diagnostics were: {0}", string.Join(" | ", Packet.Diagnostics));
        ThenDiagnostic(code, AprsDiagnosticSeverity.Warning);
    }

    /// <summary>The strict pattern: not decoded, with an error for exactly this code.</summary>
    protected void ThenRejectedWith(AprsDiagnosticCode code)
    {
        ThenDiagnostic(code, AprsDiagnosticSeverity.Error);
        Packet.Data.Should().BeOfType<AprsUnrecognizedData>();
    }

    protected void ThenDecodingFailed() => thrown.Should().BeOfType<AprsFormatException>();

    protected void ThenFailureDiagnostic(AprsDiagnosticCode code) =>
        ((AprsFormatException)thrown!).Diagnostics.Should().Contain(d => d.Code == code && d.Severity == AprsDiagnosticSeverity.Error);

    // ------------------------------------------------------------------ then: encoding

    /// <summary>Re-encoding the decoded data reproduces the information field byte for byte
    /// (ignoring any trailing CR/LF the decoder stripped).</summary>
    protected void ThenReEncodesExactly()
    {
        byte[] original = Packet.Information.ToArray();
        int end = original.Length;
        while (end > 0 && original[end - 1] is (byte)'\r' or (byte)'\n')
        {
            end--;
        }

        string expected = Show(original.AsSpan(0, end));
        string actual = Show(Packet.Data.ToInformationField());
        actual.Should().Be(expected);
    }

    /// <summary>Re-encoding and decoding again gives equal data (semantic round trip).</summary>
    protected void ThenRoundTrips()
    {
        byte[] reencoded = Packet.Data.ToInformationField();
        AprsPacket again = AprsPacket.Decode(Packet.Source, Packet.Destination, Packet.Path, reencoded, options);
        again.HasErrors.Should().BeFalse(because: "re-encoded {0} gave {1}", Show(reencoded), string.Join(" | ", again.Diagnostics));
        again.Data.Should().Be(Packet.Data, because: "re-encoded as {0}", Show(reencoded));
    }

    protected void ThenEncodedIs(string info) => Show(Encoded).Should().Be(Show(Bytes(info)));

    protected void ThenEncodingFailed() => thrown.Should().BeAssignableTo<ArgumentException>();

    /// <summary>Encodes, decodes the result, and checks the decoded data equals what was encoded.</summary>
    protected void ThenEncodedDecodesBackTo(AprsData data, string destination = "APZ001")
    {
        AprsPacket decoded = AprsPacket.Decode(AprsAddress.Parse("N0CALL"), AprsAddress.Parse(destination), [], Encoded);
        decoded.HasErrors.Should().BeFalse(because: string.Join(" | ", decoded.Diagnostics));
        decoded.Data.Should().Be(data, because: "encoded as {0}", Show(Encoded));
    }

    // ------------------------------------------------------------------ helpers

    protected static AprsPosition PositionOf(AprsData data) => data switch
    {
        AprsPositionedData p => p.Position,
        AprsNmeaReport n => n.Position ?? throw new InvalidOperationException("no position"),
        _ => throw new InvalidOperationException($"{data.GetType().Name} has no position"),
    };

    /// <summary>Text to bytes: UTF-8, with <c>&lt;0xNN&gt;</c> escapes for raw bytes (the notation
    /// the spec and UAP use for non-printing characters).</summary>
    protected static byte[] Bytes(string text)
    {
        var bytes = new List<byte>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<' && i + 6 <= text.Length && text[i + 1] == '0' && text[i + 2] == 'x' && text[i + 5] == '>')
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 3, 2), 16));
                i += 6;
                continue;
            }

            int next = text.IndexOf('<', i + 1);
            int end = next < 0 ? text.Length : next;
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(text[i..end]));
            i = end;
        }

        return [.. bytes];
    }

    /// <summary>Bytes to readable text with non-printing bytes as <c>&lt;0xNN&gt;</c>.</summary>
    protected static string Show(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (byte b in bytes)
        {
            sb.Append(b is >= 0x20 and < 0x7F ? ((char)b).ToString() : $"<0x{b:x2}>");
        }

        return sb.ToString();
    }
}
