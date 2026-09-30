using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Packet.Node.Api;

namespace Packet.Node.Tests.Auth;

/// <summary>
/// The clone-versus-generic classification of a failed passkey assertion, for the audit
/// log, keys off Fido2NetLib's typed error code and not its message text (WebAuthn review
/// O-4, #414): a library upgrade that rewords a message must not turn clone detections into
/// generic failures, and a message that happens to mention the counter must not turn a
/// generic failure into a clone alarm. Classification only: the assertion is rejected either way.
/// </summary>
public sealed class PasskeyCloneClassificationTests
{
    [Fact]
    public void A_sign_count_regression_is_a_clone_whatever_the_message_says()
    {
        var regressed = new Fido2VerificationException(Fido2ErrorCode.InvalidSignCount, "assertion rejected");
        PdnWebAuthnApi.CounterRegressed(5, regressed).Should().BeTrue();
    }

    [Fact]
    public void An_authenticator_that_never_counted_cannot_show_a_regression()
    {
        // A stored count of 0 is an authenticator that does not implement counters (or has
        // never been used); the library's code alone is not a clone signal for it.
        var regressed = new Fido2VerificationException(Fido2ErrorCode.InvalidSignCount, "assertion rejected");
        PdnWebAuthnApi.CounterRegressed(0, regressed).Should().BeFalse();
    }

    [Fact]
    public void Any_other_failure_is_generic_even_when_its_message_mentions_the_counter()
    {
        var other = new Fido2VerificationException(Fido2ErrorCode.InvalidSignature,
            "signature over the authenticator data and counter did not verify");
        PdnWebAuthnApi.CounterRegressed(5, other).Should().BeFalse();
    }
}
