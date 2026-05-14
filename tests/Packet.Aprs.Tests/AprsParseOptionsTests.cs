using Packet.Aprs;

namespace Packet.Aprs.Tests;

public class AprsParseOptionsTests
{
    [Fact]
    public void Strict_Has_All_Pragmatic_Flags_Disabled()
    {
        var s = AprsParseOptions.Strict;
        s.AllowNonAsciiStatusText.Should().BeFalse();
        s.AllowNonIntegerTelemetry.Should().BeFalse();
        s.AllowMicELegacyDtiBytes.Should().BeFalse();
    }

    [Fact]
    public void Lenient_Has_All_Pragmatic_Flags_Enabled()
    {
        var l = AprsParseOptions.Lenient;
        l.AllowNonAsciiStatusText.Should().BeTrue();
        l.AllowNonIntegerTelemetry.Should().BeTrue();
        l.AllowMicELegacyDtiBytes.Should().BeTrue();
    }

    [Fact]
    public void Direwolf_Currently_Aliases_Lenient()
    {
        AprsParseOptions.Direwolf.Should().BeSameAs(AprsParseOptions.Lenient);
    }

    [Fact]
    public void AprsIs_Currently_Aliases_Lenient()
    {
        AprsParseOptions.AprsIs.Should().BeSameAs(AprsParseOptions.Lenient);
    }

    [Fact]
    public void With_Expression_Produces_Independent_Instance()
    {
        var custom = AprsParseOptions.Strict with { AllowNonAsciiStatusText = true };
        custom.AllowNonAsciiStatusText.Should().BeTrue();
        custom.AllowNonIntegerTelemetry.Should().BeFalse();
        AprsParseOptions.Strict.AllowNonAsciiStatusText.Should().BeFalse();
    }
}
