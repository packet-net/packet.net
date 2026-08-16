using System.Text.Json;
using Packet.NetRom;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The config JSON <em>dialect</em>: what <see cref="NodeConfigJson"/> writes, and what it
/// still accepts. Two shapes are load-bearing because <c>docs/node-api.yaml</c> and the
/// control panel both assume them - an enum is its member name (<c>"Transit"</c>) and a
/// duration is a number of seconds (<c>60</c>) - and both must be <b>readable</b> in their
/// pre-converter form as well, because every deployed node's <c>pdn.db</c> holds a config
/// blob written before these converters existed (integer enums, <c>"hh:mm:ss"</c>
/// durations) and there is no migration.
/// </summary>
public class NodeConfigJsonDialectTests
{
    // A blob in the OLD dialect: enums as their integer ordinals, TimeSpans as invariant
    // duration strings. This is literally what a pdn.db written by an earlier build holds.
    private const string LegacyBlob = """
        {
          "schemaVersion": 1,
          "identity": { "callsign": "M0LTE-1" },
          "ports": [],
          "netRom": {
            "enabled": true,
            "routing": 2,
            "forwardMode": 0,
            "inp3": {
              "enabled": false,
              "l3RttInterval": "00:01:00",
              "l3RttResetWindow": "00:03:00",
              "rifInterval": "00:05:00",
              "positiveDebounce": "00:00:05"
            }
          },
          "applications": [
            { "id": "lobby", "command": "LOBBY", "kind": 1, "socketPath": "/run/lobby.sock",
              "ui": { "upstream": "http://127.0.0.1:9000", "mode": 2 } }
          ]
        }
        """;

    private static NodeConfig ConfigWith(NetRomConfig netRom) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [],
        NetRom = netRom,
    };

    // ---- reads: the legacy blob still loads ----

    [Fact]
    public void A_legacy_integer_enum_blob_still_reads()
    {
        var config = NodeConfigJson.Deserialize(LegacyBlob);

        config.NetRom.Routing.Should().Be(NetRomRouting.Transit);
        config.NetRom.ForwardMode.Should().Be(NetRomForwardMode.BestRoute);
        config.Applications.Single().Kind.Should().Be(ApplicationKind.Socket);
        config.Applications.Single().Ui!.Mode.Should().Be(AppUiMode.Slot);
    }

    [Fact]
    public void A_legacy_hh_mm_ss_duration_blob_still_reads()
    {
        var inp3 = NodeConfigJson.Deserialize(LegacyBlob).NetRom.Inp3;

        inp3.L3RttInterval.Should().Be(TimeSpan.FromSeconds(60));
        inp3.L3RttResetWindow.Should().Be(TimeSpan.FromSeconds(180));
        inp3.RifInterval.Should().Be(TimeSpan.FromSeconds(300));
        inp3.PositiveDebounce.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_duration_given_as_a_number_reads_as_seconds()
    {
        var json = LegacyBlob.Replace("\"l3RttInterval\": \"00:01:00\"", "\"l3RttInterval\": 120",
            StringComparison.Ordinal);

        NodeConfigJson.Deserialize(json).NetRom.Inp3.L3RttInterval
            .Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void An_enum_given_as_its_member_name_reads()
    {
        var json = LegacyBlob.Replace("\"routing\": 2", "\"routing\": \"Endpoint\"", StringComparison.Ordinal);

        NodeConfigJson.Deserialize(json).NetRom.Routing.Should().Be(NetRomRouting.Endpoint);
    }

    // ---- writes: the current dialect ----

    [Fact]
    public void A_fresh_serialisation_writes_enums_as_member_names()
    {
        var json = NodeConfigJson.Serialize(ConfigWith(new NetRomConfig
        {
            Routing = NetRomRouting.Transit,
            ForwardMode = NetRomForwardMode.PerFlow,
        }));

        json.Should().Contain("\"routing\":\"Transit\"");
        json.Should().Contain("\"forwardMode\":\"PerFlow\"");
        // The derived view the panel reads for the effective role is a string too.
        json.Should().Contain("\"effectiveRouting\":\"Transit\"");
    }

    [Fact]
    public void A_fresh_serialisation_writes_durations_as_whole_seconds()
    {
        var json = NodeConfigJson.Serialize(ConfigWith(new NetRomConfig()));

        json.Should().Contain("\"l3RttInterval\":60");
        json.Should().Contain("\"l3RttResetWindow\":180");
        json.Should().Contain("\"rifInterval\":300");
        json.Should().Contain("\"positiveDebounce\":5");
    }

    [Fact]
    public void The_legacy_blob_round_trips_into_the_current_dialect_without_changing_values()
    {
        var config = NodeConfigJson.Deserialize(LegacyBlob);

        var rewritten = NodeConfigJson.Serialize(config);
        rewritten.Should().Contain("\"routing\":\"Transit\"");
        rewritten.Should().Contain("\"l3RttInterval\":60");

        // The rewrite reads back to the same values - the dialect change is lossless.
        var again = NodeConfigJson.Deserialize(rewritten);
        again.NetRom.Routing.Should().Be(NetRomRouting.Transit);
        again.NetRom.Inp3.Should().Be(config.NetRom.Inp3);
    }

    [Fact]
    public void A_sub_second_duration_survives_the_round_trip_rather_than_truncating_to_zero()
    {
        // Nothing defaults to a fractional duration, but a hand-written YAML could carry
        // one; truncating it to 0 would make the validator reject the config on reload.
        var config = ConfigWith(new NetRomConfig
        {
            Inp3 = new() { PositiveDebounce = TimeSpan.FromMilliseconds(1500) },
        });

        var json = NodeConfigJson.Serialize(config);
        json.Should().Contain("\"positiveDebounce\":1.5");

        NodeConfigJson.Deserialize(json).NetRom.Inp3.PositiveDebounce
            .Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public void A_duration_that_is_neither_a_number_nor_a_duration_string_is_rejected()
    {
        var json = LegacyBlob.Replace("\"l3RttInterval\": \"00:01:00\"", "\"l3RttInterval\": \"soon\"",
            StringComparison.Ordinal);

        var act = () => NodeConfigJson.Deserialize(json);

        act.Should().Throw<JsonException>();
    }

    // ---- the HTTP layer gets the same dialect, by construction ----

    [Fact]
    public void ApplyTo_gives_a_foreign_options_instance_the_identical_dialect()
    {
        // Program.cs hands ConfigureHttpJsonOptions' own JsonSerializerOptions to ApplyTo;
        // this is the assertion that doing so cannot produce a second dialect.
        var httpLike = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        NodeConfigJson.ApplyTo(httpLike);

        var config = NodeConfigJson.Deserialize(LegacyBlob);

        JsonSerializer.Serialize(config, httpLike).Should().Be(NodeConfigJson.Serialize(config));
    }

    [Fact]
    public void A_duration_given_as_a_quoted_number_reads_as_seconds_not_days()
    {
        // The invariant TimeSpan grammar parses a lone number as days ("60" -> 60 days).
        // The converter must try the numeric-seconds reading first.
        var json = LegacyBlob.Replace("\"l3RttInterval\": \"00:01:00\"", "\"l3RttInterval\": \"60\"",
            StringComparison.Ordinal);

        var inp3 = NodeConfigJson.Deserialize(json).NetRom.Inp3;

        inp3.L3RttInterval.Should().Be(TimeSpan.FromSeconds(60));
    }
}
