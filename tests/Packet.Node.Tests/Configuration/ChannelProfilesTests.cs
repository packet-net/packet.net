using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The pure channel-profile resolver: a profile fills only the fields the operator
/// left unset (explicit always wins), and no profile is a pass-through to spec
/// defaults. The slow-afsk1200 profile carries the #292 slow-channel tuning.
/// </summary>
public class ChannelProfilesTests
{
    private static PortConfig Port(string? profile = null, Ax25PortParams? ax25 = null, KissParams? kiss = null) => new()
    {
        Id = "p",
        Profile = profile,
        Ax25 = ax25,
        Kiss = kiss,
        Transport = new KissTcpTransport { Host = "h", Port = 1 },
    };

    [Fact]
    public void No_profile_is_a_pass_through_returning_the_ports_own_params()
    {
        var ax25 = new Ax25PortParams { T1Ms = 4000 };
        var kiss = new KissParams { TxDelay = 12 };
        var (resolvedAx25, resolvedKiss) = ChannelProfiles.Resolve(Port(ax25: ax25, kiss: kiss));

        resolvedAx25.Should().BeSameAs(ax25, "no profile means the port's own params pass straight through");
        resolvedKiss.Should().BeSameAs(kiss);
    }

    [Fact]
    public void No_profile_and_no_params_resolves_to_null_null_for_spec_defaults()
    {
        var (ax25, kiss) = ChannelProfiles.Resolve(Port());
        ax25.Should().BeNull();
        kiss.Should().BeNull();
    }

    [Fact]
    public void Slow_afsk1200_supplies_a_long_T1_and_sane_csma_when_nothing_is_set()
    {
        var (ax25, kiss) = ChannelProfiles.Resolve(Port(profile: "slow-afsk1200"));

        ax25.Should().NotBeNull();
        ax25!.T1Ms.Should().Be(10000, "the slow profile lengthens T1 above the spec 6 s to break phase-lock (#292)");
        ax25.N2.Should().Be(15);

        kiss.Should().NotBeNull();
        kiss!.TxDelay.Should().Be((byte)30);
        kiss.Persistence.Should().Be((byte)63);
        kiss.SlotTime.Should().Be((byte)10);
        kiss.TxTail.Should().BeNull(
            "a channel profile must NOT set a TX tail — the need for one is a modem + radio-audio-path "
            + "property (software modem / latency path = yes; fully analogue NinoTNC path = no), which the "
            + "node can't infer from the channel; it stays an explicit per-port operator setting");
    }

    [Fact]
    public void No_profile_supplies_no_tx_tail()
    {
        // A port with no profile asserts nothing — including no tail. TX tail is
        // never auto-defaulted (profile or global): the operator sets it per the
        // attached modem + radio audio path.
        var (_, kiss) = ChannelProfiles.Resolve(Port());
        kiss.Should().BeNull("no profile, no params → nothing is asserted, including TX tail");
    }

    [Fact]
    public void Explicit_values_always_win_over_the_profile()
    {
        var port = Port(
            profile: "slow-afsk1200",
            ax25: new Ax25PortParams { T1Ms = 4000 },   // operator overrides T1
            kiss: new KissParams { TxDelay = 5 });       // and TXDELAY

        var (ax25, kiss) = ChannelProfiles.Resolve(port);

        ax25!.T1Ms.Should().Be(4000, "the explicit T1 wins");
        ax25.N2.Should().Be(15, "but the profile still fills N2, which the operator left unset");
        kiss!.TxDelay.Should().Be((byte)5, "the explicit TXDELAY wins");
        kiss.Persistence.Should().Be((byte)63, "the profile still fills the unset CSMA fields");
    }

    [Theory]
    [InlineData("slow-afsk1200")]
    [InlineData("SLOW-AFSK1200")]
    [InlineData("slow_afsk1200")]
    public void Profile_name_match_is_case_and_separator_insensitive(string profile)
    {
        ChannelProfiles.Resolve(Port(profile: profile)).Ax25!.T1Ms.Should().Be(10000);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("slow-afsk1200", true)]
    [InlineData("nope", false)]
    public void IsKnown_accepts_absent_and_known_profiles_only(string? profile, bool expected)
    {
        ChannelProfiles.IsKnown(profile).Should().Be(expected);
    }
}
