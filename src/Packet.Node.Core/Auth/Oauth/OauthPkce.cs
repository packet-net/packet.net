using System.Security.Cryptography;
using System.Text;

namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// PKCE (RFC 7636) verification, S256 only. The authorize request carries a
/// <c>code_challenge</c> = base64url(SHA-256(code_verifier)); the token request presents the
/// <c>code_verifier</c>, which we hash and compare against the stored challenge. <c>plain</c>
/// is rejected (the MCP/OAuth-2.1 profile mandates S256 for public clients).
/// </summary>
public static class OauthPkce
{
    /// <summary>The only supported (and required) challenge method.</summary>
    public const string MethodS256 = "S256";

    /// <summary>Compute the S256 challenge for a verifier - base64url(SHA-256(verifier)).</summary>
    public static string ChallengeFor(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>True iff <paramref name="codeVerifier"/> hashes (S256) to
    /// <paramref name="storedChallenge"/>. Constant-time comparison.</summary>
    public static bool Verify(string? codeVerifier, string? storedChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier) || string.IsNullOrEmpty(storedChallenge))
        {
            return false;
        }
        // RFC 7636 verifier charset/length sanity (43-128 chars).
        if (codeVerifier.Length is < 43 or > 128)
        {
            return false;
        }
        var computed = Encoding.ASCII.GetBytes(ChallengeFor(codeVerifier));
        var expected = Encoding.ASCII.GetBytes(storedChallenge);
        return CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}
