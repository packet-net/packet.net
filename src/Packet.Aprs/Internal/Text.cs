using System.Text;

namespace Packet.Aprs.Internal;

/// <summary>Converts information-field bytes to and from text.</summary>
internal static class Text
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes free text. APRS 1.2 text is ASCII or UTF-8 (APRS12c §5 Comment Field). Bytes that
    /// are not valid UTF-8 (a Latin-1 or code page 437 degree sign, Kenwood 0xFF bursts) are a real
    /// defect (UAP §5.16); when tolerated they are mapped one byte per character as Latin-1 so no
    /// information is lost.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, DecodeContext ctx, int offset, out string text)
    {
        if (IsAscii(bytes))
        {
            text = Encoding.ASCII.GetString(bytes);
            return true;
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
            return ctx.Tolerate(
                ctx.Options.AllowNonUtf8Text,
                AprsDiagnosticCode.NonUtf8Text,
                "text is not valid UTF-8; decoded byte-for-byte as Latin-1",
                offset);
        }
    }

    /// <summary>UTF-8 if valid, otherwise Latin-1; never fails. For display and diagnostics.</summary>
    public static string ForDisplay(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Decodes bytes that the format defines as printable ASCII; anything else is Latin-1 mapped
    /// and left for the caller to validate.</summary>
    public static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    public static bool IsAscii(ReadOnlySpan<byte> bytes) => System.Text.Ascii.IsValid(bytes);

    public static bool IsPrintableAscii(byte b) => b is >= 0x20 and <= 0x7E;

    public static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    public static bool IsUpper(byte b) => b is >= (byte)'A' and <= (byte)'Z';

    public static bool IsLower(byte b) => b is >= (byte)'a' and <= (byte)'z';

    public static bool IsAlphanumeric(byte b) => IsDigit(b) || IsUpper(b) || IsLower(b);

    public static bool AllDigits(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (!IsDigit(b))
            {
                return false;
            }
        }

        return bytes.Length > 0;
    }

    public static int ParseDigits(ReadOnlySpan<byte> bytes)
    {
        int n = 0;
        foreach (byte b in bytes)
        {
            n = (n * 10) + (b - '0');
        }

        return n;
    }

    /// <summary>For encoders: rejects text containing CR or LF, which would end the packet on
    /// APRS-IS (APRS12c §5 Comment Field).</summary>
    public static void RequireNoLineBreaks(string text, string paramName)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException("text must not contain carriage return or line feed", paramName);
        }
    }
}
