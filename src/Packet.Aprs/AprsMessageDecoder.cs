using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS message (DTI <c>:</c>) per APRS101 §14.
/// </summary>
/// <remarks>
/// <para>
/// On-wire format:
/// <code>
///   :NNNNNNNNN:message text[{messageId}]
/// </code>
/// where NNNNNNNNN is exactly 9 ASCII characters of addressee
/// (right-padded with spaces; the second <c>:</c> after the
/// addressee is the body separator). The trailing
/// <c>{messageId}</c> (1-5 chars per spec) is optional.
/// </para>
/// </remarks>
public static class AprsMessageDecoder
{
    private const int AddresseeLen = 9;
    private const int MaxMessageIdLen = 5;

    /// <summary>
    /// Try to decode an APRS message from <paramref name="info"/>.
    /// </summary>
    /// <param name="info">Info bytes, optionally prefixed with DTI byte <c>:</c>.</param>
    /// <param name="message">On success, the decoded message.</param>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsMessage message)
    {
        message = default;
        if (info.IsEmpty)
        {
            return false;
        }

        // Strip DTI byte if present.
        if (info[0] == (byte)':')
        {
            info = info[1..];
        }

        // Need at least: 9-char addressee + ':' separator = 10 bytes.
        if (info.Length < AddresseeLen + 1)
        {
            return false;
        }

        // Addressee separator byte.
        if (info[AddresseeLen] != (byte)':')
        {
            return false;
        }

        string addressee = Encoding.ASCII.GetString(info[..AddresseeLen]).TrimEnd();
        var body = info[(AddresseeLen + 1)..];

        // Split off a trailing {messageId}. The opening '{' must be in the
        // last 6 bytes (1-5 char ID + '{'). The spec uses '{' only as the
        // message-ID prefix so a plain text-body match is acceptable.
        string? messageId = null;
        string text = Encoding.UTF8.GetString(body).TrimEnd('\r', '\n');
        int braceIdx = text.LastIndexOf('{');
        if (braceIdx >= 0 && braceIdx < text.Length - 1)
        {
            var idCandidate = text[(braceIdx + 1)..];
            if (idCandidate.Length is >= 1 and <= MaxMessageIdLen)
            {
                messageId = idCandidate;
                text = text[..braceIdx];
            }
        }

        message = new AprsMessage(addressee, text, messageId);
        return true;
    }
}
