namespace Packet.Node.Api;

/// <summary>
/// URL-safe, unpadded base64 (RFC 4648 §5) for the WebAuthn wire surface — credential
/// ids travel as base64url in JSON paths and bodies. <see cref="System.Buffers.Text.Base64Url"/>
/// (the BCL one) is the engine; this thin wrapper just gives a total
/// <see cref="TryDecode"/> for the untrusted <c>{id}</c> route segment.
/// </summary>
internal static class Base64Url
{
    /// <summary>Encode bytes as URL-safe, unpadded base64.</summary>
    public static string Encode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>Decode URL-safe base64 (padded or not). Returns false on malformed input
    /// rather than throwing — the <c>{id}</c> path segment is untrusted.</summary>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            bytes = [];
            return false;
        }
        try
        {
            bytes = System.Buffers.Text.Base64Url.DecodeFromChars(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
