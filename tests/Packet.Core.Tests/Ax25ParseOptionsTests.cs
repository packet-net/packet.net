using Packet.Core;

namespace Packet.Core.Tests;

public class Ax25ParseOptionsTests
{
    [Fact]
    public void Strict_Has_All_Pragmatic_Flags_Disabled()
    {
        var s = Ax25ParseOptions.Strict;
        s.AllowEmptyCallsignBase.Should().BeFalse();
        s.AllowInfoOnSupervisoryFrames.Should().BeFalse();
    }

    [Fact]
    public void Lenient_Has_All_Pragmatic_Flags_Enabled()
    {
        var l = Ax25ParseOptions.Lenient;
        l.AllowEmptyCallsignBase.Should().BeTrue();
        l.AllowInfoOnSupervisoryFrames.Should().BeTrue();
    }

    [Fact]
    public void Bpq_Currently_Aliases_Lenient()
    {
        // May diverge in future; today they're the same instance.
        Ax25ParseOptions.Bpq.Should().BeSameAs(Ax25ParseOptions.Lenient);
    }

    [Fact]
    public void Xrouter_Currently_Aliases_Strict()
    {
        // No Xrouter-specific quirks observed yet; populated when we
        // have an interop corpus.
        Ax25ParseOptions.Xrouter.Should().BeSameAs(Ax25ParseOptions.Strict);
    }

    [Fact]
    public void Direwolf_Currently_Aliases_Lenient()
    {
        Ax25ParseOptions.Direwolf.Should().BeSameAs(Ax25ParseOptions.Lenient);
    }

    [Fact]
    public void With_Expression_Produces_Independent_Instance()
    {
        // Records support non-destructive mutation via `with` —
        // callers can derive custom options from any preset.
        var custom = Ax25ParseOptions.Strict with { AllowEmptyCallsignBase = true };
        custom.AllowEmptyCallsignBase.Should().BeTrue();
        custom.AllowInfoOnSupervisoryFrames.Should().BeFalse();   // unchanged
        Ax25ParseOptions.Strict.AllowEmptyCallsignBase.Should().BeFalse();  // preset is immutable
    }
}
