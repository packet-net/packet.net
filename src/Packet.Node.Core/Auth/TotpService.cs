using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Packet.Node.Core.Auth;

/// <summary>
/// RFC 6238 TOTP - the rolling one-time code a sysop presents to elevate a session
/// <em>over the air</em>, where AX.25 has no authentication and the channel is
/// eavesdroppable and replayable. A static password would be captured and replayed off
/// the air; a time-based code that is accepted <b>at most once</b> cannot be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Self-contained, no new dependency.</b> RFC 6238 is HMAC-SHA1 over a 30-second time
/// step with a dynamic-truncation reduction to N digits - small and well-specified, so it
/// is implemented here and validated against the RFC 6238 Appendix-B test vectors rather
/// than pulling a package. Web-free (lives in <c>Packet.Node.Core</c>); all time rides the
/// injected <see cref="TimeProvider"/> (repo rule §2.7 - no wall-clock), so drift and the
/// replay window are deterministically testable on <c>FakeTimeProvider</c>.
/// </para>
/// <para>
/// <b>The replay guard is the load-bearing security property.</b> <see cref="TryVerify"/>
/// accepts a code only for a counter <em>strictly greater</em> than the last one already
/// accepted for that user (<c>candidate &gt; lastAcceptedCounter</c>), and returns the
/// counter it accepted so the caller can persist it. A code is therefore usable exactly
/// once: presenting the same code again (same 30-second window ⇒ same counter, now
/// <c>== lastAccepted</c>, not <c>&gt;</c>) is rejected, and so is any code from an earlier
/// window. This is what makes a captured-off-air code worthless to an attacker. The drift
/// window (±<paramref name="driftSteps"/> steps) tolerates a slow clock on either side
/// without ever re-opening an already-consumed counter.
/// </para>
/// <para>
/// <b>Comparisons are constant-time</b> (<see cref="CryptographicOperations.FixedTimeEquals"/>)
/// so a near-miss code can't be teased out by timing. The secret is base32 at the public
/// boundary (what the store persists and the authenticator app consumes); raw key bytes
/// never leave this type except inside the <c>otpauth://</c> enrolment URI.
/// </para>
/// </remarks>
public sealed class TotpService
{
    /// <summary>The RFC 6238 time step. 30 s is the universal default every authenticator
    /// app assumes.</summary>
    public const int StepSeconds = 30;

    /// <summary>Production code length. 6 digits is the authenticator-app default.</summary>
    public const int DefaultDigits = 6;

    /// <summary>Default acceptance drift: ±1 step (±30 s) covers a slow clock either way
    /// while keeping the live-code window tight.</summary>
    public const int DefaultDriftSteps = 1;

    /// <summary>Default shared-secret size. 160 bits (20 bytes) is the RFC 4226/6238
    /// reference width for HMAC-SHA1 and what authenticator apps expect.</summary>
    public const int DefaultSecretBytes = 20;

    private readonly TimeProvider clock;

    /// <summary>Construct over the injected clock (no wall-clock - testable on
    /// <c>FakeTimeProvider</c>).</summary>
    public TotpService(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        this.clock = clock;
    }

    /// <summary>The time-step counter for an instant: floor(unix-seconds / step). The
    /// value HMAC'd to produce a code. Monotonic in time, so it doubles as the replay
    /// high-water mark.</summary>
    public static long CounterAt(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The counter for "now" off the injected clock.</summary>
    public long CurrentCounter() => CounterAt(clock.GetUtcNow());

    /// <summary>
    /// Mint a fresh random shared secret and return it base32-encoded (RFC 4648, no
    /// padding - the form an <c>otpauth://</c> URI and every authenticator app use).
    /// </summary>
    public static string GenerateSecret(int bytes = DefaultSecretBytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Secret size must be positive.");
        }
        return Base32Encode(RandomNumberGenerator.GetBytes(bytes));
    }

    /// <summary>
    /// Build the <c>otpauth://totp/...</c> provisioning URI an authenticator app scans as a
    /// QR code. <paramref name="account"/> is the per-user label (e.g. the callsign or
    /// username) and <paramref name="issuer"/> the node identity; both are URI-escaped.
    /// Algorithm/digits/period are pinned to this service's parameters so a scanned secret
    /// always matches what <see cref="TryVerify"/> expects.
    /// </summary>
    public static string BuildOtpAuthUri(string base32Secret, string account, string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);

        var issuerEsc = Uri.EscapeDataString(issuer);
        var label = Uri.EscapeDataString(issuer + ":" + account);
        // Secret is base32 with no padding; query params spell out the parameters so a
        // strict authenticator app doesn't fall back to defaults that differ from ours.
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuerEsc}"
            + $"&algorithm=SHA1&digits={DefaultDigits}&period={StepSeconds}";
    }

    /// <summary>
    /// Compute the TOTP code for a base32 secret at a given counter. Exposed mainly for
    /// the RFC-vector tests; normal callers use <see cref="TryVerify"/>.
    /// </summary>
    public static string ComputeCode(string base32Secret, long counter, int digits = DefaultDigits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base32Secret);
        return ComputeCode(Base32Decode(base32Secret), counter, digits);
    }

    /// <summary>
    /// The RFC 6238 / RFC 4226 core: HMAC-SHA1 of the 8-byte big-endian counter under the
    /// raw key, dynamic-truncated to a 31-bit integer, reduced mod 10^digits and
    /// zero-padded. Static + key-bytes overload so the Appendix-B vectors (raw ASCII seed)
    /// test it directly.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 / RFC 4226 mandate HMAC-SHA1 as the TOTP MAC. This is a "
            + "keyed message-authentication use, not a collision-sensitive digest, and every "
            + "authenticator app requires SHA-1 for interop. SHA-1's collision weakness does "
            + "not apply to HMAC-SHA1.")]
    public static string ComputeCode(byte[] key, long counter, int digits = DefaultDigits)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (digits is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), "TOTP digit count must be 1..9.");
        }

        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message, (ulong)counter);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes]; // 20
        HMACSHA1.HashData(key, message, hash);

        // Dynamic truncation (RFC 4226 §5.3): low nibble of the last byte is the offset.
        int offset = hash[^1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24)
            | ((hash[offset + 1] & 0xFF) << 16)
            | ((hash[offset + 2] & 0xFF) << 8)
            | (hash[offset + 3] & 0xFF);

        int modulo = 1;
        for (int i = 0; i < digits; i++)
        {
            modulo *= 10;
        }
        int otp = binary % modulo;
        return otp.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// Verify a presented code against a base32 secret, enforcing the single-use replay
    /// guard. Accepts the code only if it matches some counter within ±<paramref
    /// name="driftSteps"/> of "now" AND that counter is <b>strictly greater than</b>
    /// <paramref name="lastAcceptedCounter"/> (so a code is usable at most once, and no
    /// already-consumed window can be reopened). On success, <paramref
    /// name="acceptedCounter"/> is the counter that matched - the caller MUST persist it as
    /// the new high-water mark.
    /// </summary>
    /// <param name="base32Secret">The user's stored base32 secret.</param>
    /// <param name="code">The code the operator typed (whitespace tolerated).</param>
    /// <param name="lastAcceptedCounter">The highest counter already accepted for this
    /// user, or a value &lt; 0 (e.g. -1) if none has ever been accepted.</param>
    /// <param name="acceptedCounter">The accepted counter, on success.</param>
    /// <param name="driftSteps">Acceptance window in steps either side of now.</param>
    /// <returns>True if accepted (and not a replay); false otherwise.</returns>
    public bool TryVerify(
        string? base32Secret,
        string? code,
        long lastAcceptedCounter,
        out long acceptedCounter,
        int driftSteps = DefaultDriftSteps)
    {
        acceptedCounter = -1;
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code) || driftSteps < 0)
        {
            return false;
        }

        // Normalise the typed code: strip spaces an operator may have inserted. A code that
        // isn't all-digits of the right length can never match - but we still walk the
        // window so timing doesn't distinguish "wrong shape" from "wrong value".
        var presented = code.Replace(" ", string.Empty, StringComparison.Ordinal);

        byte[] key;
        try
        {
            key = Base32Decode(base32Secret);
        }
        catch (FormatException)
        {
            return false;
        }

        long now = CurrentCounter();
        bool matched = false;
        long matchedCounter = -1;

        // Walk the whole window even after a match so acceptance time doesn't leak which
        // counter hit. Only counters past the high-water mark are eligible (replay guard).
        for (int delta = -driftSteps; delta <= driftSteps; delta++)
        {
            long candidate = now + delta;
            if (candidate <= lastAcceptedCounter)
            {
                continue; // already consumed (or older) - the single-use guard
            }
            string expected = ComputeCode(key, candidate, DefaultDigits);
            if (FixedTimeStringEquals(expected, presented) && !matched)
            {
                matched = true;
                matchedCounter = candidate;
            }
        }

        if (matched)
        {
            acceptedCounter = matchedCounter;
        }
        return matched;
    }

    // Constant-time string compare over UTF-8 bytes. A length difference short-circuits
    // (the code length is not a secret), but equal-length compares run in fixed time so a
    // partial match can't be timed out.
    private static bool FixedTimeStringEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    }

    // ---- Base32 (RFC 4648, no padding) -------------------------------------------------
    // The on-the-wire form for TOTP secrets: A-Z, 2-7, case-insensitive, '=' padding
    // stripped. Small enough to hand-roll rather than depend on a package.

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Base32-encode bytes (RFC 4648, no <c>=</c> padding).</summary>
    public static string Base32Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            return string.Empty;
        }
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
        {
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }
        return sb.ToString();
    }

    /// <summary>Decode an RFC 4648 base32 string (case-insensitive, optional <c>=</c>
    /// padding and inner spaces tolerated). Throws <see cref="FormatException"/> on an
    /// out-of-alphabet character.</summary>
    public static byte[] Base32Decode(string base32)
    {
        ArgumentNullException.ThrowIfNull(base32);
        var clean = base32.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (clean.Length == 0)
        {
            return [];
        }
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (char c in clean)
        {
            int v = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                throw new FormatException($"Invalid base32 character '{c}'.");
            }
            buffer = (buffer << 5) | v;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return [.. output];
    }
}
