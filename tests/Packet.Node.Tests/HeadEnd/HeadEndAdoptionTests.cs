using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.HeadEnd;

/// <summary>
/// The pure config transform behind the adopt endpoint (<see cref="HeadEndAdoption.BuildCandidate"/>):
/// it declares the head-end (if new) and creates ONE matched port — a <c>nino-tnc-tcp</c> transport +
/// a head-end-bound <c>tait-ccdi</c> radio on the same instance — and the result passes the real
/// <see cref="NodeConfigValidator"/> (the co-location pairing rule + declared-reference rule).
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndAdoptionTests
{
    private static NodeConfig Empty() => new() { Identity = new Identity { Callsign = "M0LTE-1" } };

    [Fact]
    public void BuildCandidate_creates_a_matched_port_and_declares_the_head_end()
    {
        var candidate = HeadEndAdoption.BuildCandidate(
            Empty(), "pi-shack", new HeadEndAdoptRequest("nino0", "tait0", Mode: 6));

        candidate.HeadEnds.Should().ContainSingle(h => h.Id == "pi-shack" && h.Address == "",
            "an undeclared head-end is added in discover mode (blank address ⇒ re-resolve via mDNS)");

        var port = candidate.Ports.Should().ContainSingle().Subject;
        port.Id.Should().Be("pi-shack");
        port.Enabled.Should().BeTrue();

        var transport = port.Transport.Should().BeOfType<NinoTncTcpTransport>().Subject;
        transport.HeadEndId.Should().Be("pi-shack");
        transport.DeviceId.Should().Be("nino0");
        transport.Mode.Should().Be(6);

        port.Radio.Should().NotBeNull();
        port.Radio!.Kind.Should().Be(RadioKinds.TaitCcdi);
        port.Radio.HeadEndId.Should().Be("pi-shack");
        port.Radio.DeviceId.Should().Be("tait0");
        port.Radio.IsHeadEndBound.Should().BeTrue();

        new NodeConfigValidator().Validate(candidate).IsValid.Should().BeTrue();
    }

    [Fact]
    public void BuildCandidate_honours_a_supplied_port_id_address_and_disabled_flag()
    {
        var candidate = HeadEndAdoption.BuildCandidate(
            Empty(), "pi-shack",
            new HeadEndAdoptRequest("nino0", "tait0", PortId: "vhf-remote", Enabled: false, Address: "10.0.0.5:7300"));

        candidate.HeadEnds.Should().ContainSingle(h => h.Id == "pi-shack" && h.Address == "10.0.0.5:7300");
        var port = candidate.Ports.Single();
        port.Id.Should().Be("vhf-remote");
        port.Enabled.Should().BeFalse();
        new NodeConfigValidator().Validate(candidate).IsValid.Should().BeTrue();
    }

    [Fact]
    public void BuildCandidate_labels_the_port_and_mqtt_instance_by_amateur_band_when_known()
    {
        var candidate = HeadEndAdoption.BuildCandidate(
            Empty(), "pi-shack", new HeadEndAdoptRequest("nino0", "tait0", AmateurBand: "2m"));

        var port = candidate.Ports.Should().ContainSingle().Subject;
        // No explicit port id ⇒ the band names the port and the MQTT {instance} label.
        port.Id.Should().Be("2m");
        port.MqttInstance.Should().Be("2m");
        new NodeConfigValidator().Validate(candidate).IsValid.Should().BeTrue();
    }

    [Fact]
    public void BuildCandidate_lets_an_explicit_port_id_win_but_the_band_still_labels_mqtt()
    {
        var candidate = HeadEndAdoption.BuildCandidate(
            Empty(), "pi-shack", new HeadEndAdoptRequest("nino0", "tait0", PortId: "vhf-remote", AmateurBand: "2m"));

        var port = candidate.Ports.Single();
        port.Id.Should().Be("vhf-remote", "an explicit port id is not overridden by the band");
        port.MqttInstance.Should().Be("2m", "the band still labels the MQTT instance segment for collector continuity");
    }

    [Fact]
    public void BuildCandidate_without_a_band_leaves_the_mqtt_instance_unset_and_ids_by_instance()
    {
        var candidate = HeadEndAdoption.BuildCandidate(
            Empty(), "pi-shack", new HeadEndAdoptRequest("nino0", "tait0"));

        var port = candidate.Ports.Single();
        port.Id.Should().Be("pi-shack");
        port.MqttInstance.Should().BeNull();
    }

    [Fact]
    public void BuildCandidate_reuses_an_already_declared_head_end_without_clobbering_its_address()
    {
        var current = Empty() with { HeadEnds = [new HeadEndConfig { Id = "pi-shack", Address = "10.0.0.5:7300" }] };

        var candidate = HeadEndAdoption.BuildCandidate(
            current, "pi-shack", new HeadEndAdoptRequest("nino0", "tait0"));

        candidate.HeadEnds.Should().ContainSingle()
            .Which.Address.Should().Be("10.0.0.5:7300", "an existing pinned address is not overwritten");
    }
}
