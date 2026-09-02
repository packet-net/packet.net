using System.Text;

namespace Packet.Ax25.Monitor;

/// <summary>
/// Reads an information field as text when it is text, for display.
/// </summary>
public static class Ax25InfoText
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The information field as a string, or null when it is not something a person would
    /// read: empty, carrying a layer-3 protocol (anything but PID 0xF0, or no PID), not valid
    /// UTF-8, or holding control characters other than tab, CR and LF. Trailing CR and LF are
    /// trimmed; ones in the middle stay, so a multi-line BBS prompt keeps its lines.
    /// </summary>
    public static string? TryRead(ReadOnlySpan<byte> info, byte? pid)
    {
        if (pid != Ax25Frame.PidNoLayer3 || info.IsEmpty)
        {
            return null;
        }

        string text;
        try
        {
            text = Strict.GetString(info);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        foreach (var ch in text)
        {
            if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
            {
                return null;
            }
        }

        var trimmed = text.TrimEnd('\r', '\n');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
