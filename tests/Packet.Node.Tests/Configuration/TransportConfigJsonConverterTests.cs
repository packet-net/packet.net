using System.Text.Json;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Unit tests for the <see cref="TransportConfigJsonConverter"/> — the JSON twin of
/// the YAML transport converter that lets a <c>PUT /config</c> body deserialise the
/// polymorphic <c>transport</c> union. Each concrete kind must survive a
/// serialise → deserialise round-trip back to its own subtype with its fields
/// intact, an unknown / missing <c>kind</c> must throw, and a bare transport
/// element must deserialise on the <c>kind</c> discriminator alone.
/// </summary>
public class TransportConfigJsonConverterTests
{
    // Web defaults (camelCase) + the converter, mirroring what Program.cs registers
    // via ConfigureHttpJsonOptions for the minimal-API binder.
    private static readonly JsonSerializerOptions Opts = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var opts = new JsonSerializerOptions(JsonSerializerOptions.Web);
        opts.Converters.Add(new TransportConfigJsonConverter());
        return opts;
    }

    private static NodeConfig ConfigWith(TransportConfig transport) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [new PortConfig { Id = "p0", Transport = transport }],
    };

    private static TransportConfig RoundTrip(TransportConfig transport)
    {
        var json = JsonSerializer.Serialize(ConfigWith(transport), Opts);
        var back = JsonSerializer.Deserialize<NodeConfig>(json, Opts);
        back.Should().NotBeNull();
        return back!.Ports.Single().Transport;
    }

    [Fact]
    public void KissTcp_round_trips_to_its_subtype_with_host_and_port()
    {
        var result = RoundTrip(new KissTcpTransport { Host = "10.0.0.5", Port = 8101 });

        result.Should().BeOfType<KissTcpTransport>()
            .Which.Should().BeEquivalentTo(new { Host = "10.0.0.5", Port = 8101 });
    }

    [Fact]
    public void SerialKiss_round_trips_to_its_subtype_with_device_and_baud()
    {
        var result = RoundTrip(new SerialKissTransport { Device = "/dev/ttyACM0", Baud = 115200 });

        result.Should().BeOfType<SerialKissTransport>()
            .Which.Should().BeEquivalentTo(new { Device = "/dev/ttyACM0", Baud = 115200 });
    }

    [Fact]
    public void NinoTnc_round_trips_to_its_subtype_with_device_and_mode()
    {
        var result = RoundTrip(new NinoTncTransport { Device = "/dev/ttyACM1", Baud = 57600, Mode = 3 });

        result.Should().BeOfType<NinoTncTransport>()
            .Which.Should().BeEquivalentTo(new { Device = "/dev/ttyACM1", Baud = 57600, Mode = 3 });
    }

    [Fact]
    public void Axudp_round_trips_to_its_subtype_with_host_port_and_local_port()
    {
        var result = RoundTrip(new AxudpTransport { Host = "peer.example", Port = 10093, LocalPort = 10093 });

        result.Should().BeOfType<AxudpTransport>()
            .Which.Should().BeEquivalentTo(new { Host = "peer.example", Port = 10093, LocalPort = 10093 });
    }

    [Fact]
    public void A_bare_element_deserialises_on_the_kind_discriminator()
    {
        var transport = JsonSerializer.Deserialize<TransportConfig>(
            """{"kind":"kiss-tcp","host":"x","port":1}""", Opts);

        transport.Should().BeOfType<KissTcpTransport>()
            .Which.Should().BeEquivalentTo(new { Host = "x", Port = 1 });
    }

    [Fact]
    public void An_unknown_kind_throws()
    {
        var act = () => JsonSerializer.Deserialize<TransportConfig>(
            """{"kind":"smoke-signals","host":"x","port":1}""", Opts);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_missing_kind_throws()
    {
        var act = () => JsonSerializer.Deserialize<TransportConfig>(
            """{"host":"x","port":1}""", Opts);

        act.Should().Throw<JsonException>();
    }
}
