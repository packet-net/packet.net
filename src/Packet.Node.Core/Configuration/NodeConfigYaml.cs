using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The YAML (de)serialisation of <see cref="NodeConfig"/>. This is the ONLY
/// place that knows the config is stored as YAML — every consumer talks to the
/// format-agnostic <see cref="NodeConfig"/> record tree and the
/// <see cref="IConfigProvider"/> seam. A later slice can persist the same text
/// in a SQLite column and reuse this class unchanged.
/// </summary>
public static class NodeConfigYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TransportConfigYamlConverter())
        // The public NodeConfig contract exposes Ports as IReadOnlyList (immutable
        // surface); YamlDotNet has no deserializer for the interface, so map it to
        // the concrete List it will populate then expose read-only.
        .WithTypeMapping<IReadOnlyList<PortConfig>, List<PortConfig>>()
        // Same for management.auth.webAuthn.allowedOrigins (an IReadOnlyList<string>).
        .WithTypeMapping<IReadOnlyList<string>, List<string>>()
        // The nested netRom.inp3 block (a Packet.NetRom.Wire.NetRomInp3Options
        // record) needs NO custom converter: it is pure durations / ints / bools, so
        // the camel-case mapping + YamlDotNet's built-in TimeSpan converter bind it
        // directly. An absent inp3: key leaves the record's C# default (Disabled) in
        // place under IgnoreUnmatchedProperties. See NodeConfig.Inp3 + the design §2.
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TransportConfigYamlConverter())
        // OmitNull drops netRom.inp3.advertiseIpAccept when unset; the rest of the
        // nested inp3: record serialises through the default (camel-case + TimeSpan)
        // path — symmetrical with the read side, no custom converter needed.
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Parse YAML text into a <see cref="NodeConfig"/>. Throws on malformed YAML
    /// or an unrecognised transport <c>kind:</c> (see
    /// <see cref="TransportConfigYamlConverter"/>); the caller treats any throw
    /// as "reject this candidate whole".
    /// </summary>
    public static NodeConfig Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var parsed = Deserializer.Deserialize<NodeConfig>(yaml);
        // An empty / all-comments document deserialises to null — normalise that
        // into a default-shaped config so downstream never sees null. Validation
        // still rejects it (no Identity), which is the correct outcome.
        return parsed ?? new NodeConfig { Identity = new Identity { Callsign = "" } };
    }

    /// <summary>Serialise a <see cref="NodeConfig"/> back to YAML. Used by the
    /// round-trip property test and any future config-export path.</summary>
    public static string Serialize(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Serializer.Serialize(config);
    }
}
