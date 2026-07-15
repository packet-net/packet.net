using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// System.Text.Json converter for the <see cref="TransportConfig"/> discriminated
/// union. STJ can serialise the concrete record fine (the <see cref="TransportConfig.Kind"/>
/// getter emits the <c>kind</c> discriminator), but it can't <em>deserialise</em> an
/// abstract record — so a <c>PUT /config</c> body needs this to turn
/// <c>{ "kind": "kiss-tcp", "host": …, "port": … }</c> back into the right concrete
/// subtype. The YAML layer has its own equivalent (<see cref="TransportConfigYamlConverter"/>);
/// this is the JSON twin for the web API.
/// </summary>
/// <remarks>
/// Read keys on <c>kind</c> then delegates to the concrete type's default
/// deserialisation (which enforces the <c>required</c> members). Write delegates to
/// the concrete runtime type's default serialisation, so the emitted JSON is
/// byte-identical to what GET <c>/config</c> produced before this converter existed
/// (the converter only matches the base type, never a subtype, so no recursion).
/// </remarks>
public sealed class TransportConfigJsonConverter : JsonConverter<TransportConfig>
{
    public override TransportConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("transport is missing the required string 'kind' discriminator.");
        }

        var kind = kindEl.GetString();
        return kind switch
        {
            TransportKinds.SerialKiss => root.Deserialize<SerialKissTransport>(options)!,
            TransportKinds.NinoTnc => root.Deserialize<NinoTncTransport>(options)!,
            TransportKinds.NinoTncTcp => root.Deserialize<NinoTncTcpTransport>(options)!,
            TransportKinds.KissTcp => root.Deserialize<KissTcpTransport>(options)!,
            TransportKinds.Axudp => root.Deserialize<AxudpTransport>(options)!,
            TransportKinds.AxudpMultipoint => root.Deserialize<AxudpMultipointTransport>(options)!,
            TransportKinds.TaitTransparent => root.Deserialize<TaitTransparentTransportConfig>(options)!,
            TransportKinds.SoundModem => root.Deserialize<SoundModemTransportConfig>(options)!,
            _ => throw new JsonException($"unknown transport kind '{kind}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, TransportConfig value, JsonSerializerOptions options)
    {
        // Serialise as the concrete runtime type: its default serialisation emits
        // `kind` (the getter) plus the subtype's properties. typeToConvert here is
        // the base type, so this converter does not re-enter for the concrete type.
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
