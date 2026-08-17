using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Auth;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// Validates <see cref="TotpService"/> against the RFC 6238 Appendix-B SHA-1 test vectors
/// (the authoritative correctness check) and locks the security-critical behaviour: the
/// drift window, and the single-use replay guard that makes a captured-off-air code
/// worthless.
/// </summary>
[Trait("Category", "Node")]
public sealed class TotpServiceTests
{
    // RFC 6238 Appendix B reference seed for HMAC-SHA1: the ASCII string
    // "12345678901234567890" (20 bytes), base32-encoded for the service boundary.
    private static readonly byte[] Seed = Encoding.ASCII.GetBytes("12345678901234567890");
    private static string Seed32 => TotpService.Base32Encode(Seed);

    // RFC 6238 Appendix B, SHA-1 column: (unix time T, expected 8-digit TOTP).
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void ComputeCode_matches_the_rfc6238_appendixB_sha1_vectors(long unixTime, string expected)
    {
        long counter = TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        TotpService.ComputeCode(Seed, counter, digits: 8).Should().Be(expected);
        // and via the base32 string boundary
        TotpService.ComputeCode(Seed32, counter, digits: 8).Should().Be(expected);
    }

    [Fact]
    public void CounterAt_is_floor_of_unix_seconds_over_step()
    {
        TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(0)).Should().Be(0);
        TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(29)).Should().Be(0);
        TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(30)).Should().Be(1);
        TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(59)).Should().Be(1);
        TotpService.CounterAt(DateTimeOffset.FromUnixTimeSeconds(60)).Should().Be(2);
    }

    [Fact]
    public void Base32_round_trips_arbitrary_bytes()
    {
        var data = new byte[] { 0x00, 0x01, 0x7F, 0x80, 0xFF, 0xAB, 0xCD, 0xEF, 0x10 };
        TotpService.Base32Decode(TotpService.Base32Encode(data)).Should().Equal(data);
    }

    [Fact]
    public void GenerateSecret_is_valid_base32_of_the_requested_width()
    {
        var s = TotpService.GenerateSecret();   // 20 bytes
        TotpService.Base32Decode(s).Length.Should().Be(20);
    }

    [Fact]
    public void BuildOtpAuthUri_pins_the_parameters_and_escapes_the_label()
    {
        var uri = TotpService.BuildOtpAuthUri("JBSWY3DPEHPK3PXP", "M0LTE-7", "pdn node");
        uri.Should().StartWith("otpauth://totp/");
        uri.Should().Contain("secret=JBSWY3DPEHPK3PXP");
        uri.Should().Contain("algorithm=SHA1");
        uri.Should().Contain("digits=6");
        uri.Should().Contain("period=30");
        uri.Should().Contain("issuer=pdn%20node");      // space escaped
        uri.Should().Contain("pdn%20node%3AM0LTE-7");   // label "issuer:account" escaped
    }

    private static FakeTimeProvider At(long unixSeconds) =>
        new(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    [Fact]
    public void TryVerify_accepts_the_current_code_and_returns_its_counter()
    {
        var clock = At(1111111111L);                // counter 0x23523ED
        var svc = new TotpService(clock);
        long counter = TotpService.CounterAt(clock.GetUtcNow());
        string code = TotpService.ComputeCode(Seed32, counter);   // 6-digit "now" code

        bool ok = svc.TryVerify(Seed32, code, lastAcceptedCounter: -1, out long accepted);

        ok.Should().BeTrue();
        accepted.Should().Be(counter);
    }

    [Fact]
    public void TryVerify_rejects_a_replay_of_the_same_code()
    {
        var clock = At(1111111111L);
        var svc = new TotpService(clock);
        long counter = TotpService.CounterAt(clock.GetUtcNow());
        string code = TotpService.ComputeCode(Seed32, counter);

        // First use accepted...
        svc.TryVerify(Seed32, code, lastAcceptedCounter: -1, out long accepted).Should().BeTrue();
        // ...and the caller persists `accepted`; presenting the SAME code again (still the
        // same window ⇒ same counter, now == lastAccepted, not >) is rejected.
        svc.TryVerify(Seed32, code, lastAcceptedCounter: accepted, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_rejects_an_earlier_window_code_after_a_later_one_was_used()
    {
        // Accept a code at T, persist its counter; a code from a PRIOR window must not be
        // accepted afterwards even though it is within drift of some earlier instant.
        var clock = At(1111111111L);
        var svc = new TotpService(clock);
        long counter = TotpService.CounterAt(clock.GetUtcNow());
        string priorCode = TotpService.ComputeCode(Seed32, counter - 1);

        // lastAccepted is the current counter; the prior window's counter is <= it.
        svc.TryVerify(Seed32, priorCode, lastAcceptedCounter: counter, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_tolerates_one_step_of_drift_each_side()
    {
        var clock = At(1111111111L);
        var svc = new TotpService(clock);
        long now = TotpService.CounterAt(clock.GetUtcNow());

        // A code generated for the PREVIOUS step (authenticator slightly ahead of us).
        string prevStep = TotpService.ComputeCode(Seed32, now - 1);
        svc.TryVerify(Seed32, prevStep, lastAcceptedCounter: -1, out long a1).Should().BeTrue();
        a1.Should().Be(now - 1);

        // A code for the NEXT step (authenticator slightly behind), with a fresh high-water.
        string nextStep = TotpService.ComputeCode(Seed32, now + 1);
        svc.TryVerify(Seed32, nextStep, lastAcceptedCounter: -1, out long a2).Should().BeTrue();
        a2.Should().Be(now + 1);
    }

    [Fact]
    public void TryVerify_rejects_a_code_two_steps_away()
    {
        var clock = At(1111111111L);
        var svc = new TotpService(clock);
        long now = TotpService.CounterAt(clock.GetUtcNow());
        string twoAhead = TotpService.ComputeCode(Seed32, now + 2);
        svc.TryVerify(Seed32, twoAhead, lastAcceptedCounter: -1, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_tolerates_spaces_in_the_typed_code()
    {
        var clock = At(1111111111L);
        var svc = new TotpService(clock);
        long now = TotpService.CounterAt(clock.GetUtcNow());
        string code = TotpService.ComputeCode(Seed32, now);
        string spaced = code[..3] + " " + code[3..];   // "123 456"
        svc.TryVerify(Seed32, spaced, lastAcceptedCounter: -1, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("000000")]   // a wrong code
    [InlineData("not-digits")]
    public void TryVerify_rejects_blank_or_wrong_codes(string? code)
    {
        var svc = new TotpService(At(1111111111L));
        svc.TryVerify(Seed32, code, lastAcceptedCounter: -1, out long accepted).Should().BeFalse();
        accepted.Should().Be(-1);
    }

    [Fact]
    public void TryVerify_rejects_when_the_secret_is_missing_or_malformed()
    {
        var svc = new TotpService(At(1111111111L));
        svc.TryVerify(null, "123456", -1, out _).Should().BeFalse();
        svc.TryVerify("", "123456", -1, out _).Should().BeFalse();
        svc.TryVerify("not base32 ‽", "123456", -1, out _).Should().BeFalse();   // out-of-alphabet
    }
}
