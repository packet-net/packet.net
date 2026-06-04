using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// YamlDotNet converter for the <see cref="TransportConfig"/> discriminated
/// union. Reads/writes a YAML mapping keyed by a <c>kind:</c> discriminator,
/// dispatching to the concrete subtype.
/// </summary>
/// <remarks>
/// <para>
/// The transport records are flat (scalar fields only), so the converter reads
/// the mapping's key/value scalars into a small dictionary and builds the
/// concrete record from it — no nested-object re-entrancy needed. An unknown or
/// missing <c>kind:</c> throws a <see cref="YamlException"/> with the document
/// mark, which the loader surfaces as a clear parse error (the candidate is
/// then rejected whole, never partially applied).
/// </para>
/// <para>
/// Keys are matched case-insensitively and hyphen-insensitively so both
/// <c>kiss-tcp</c>/<c>KissTcp</c> on the discriminator and <c>baud</c>/<c>Baud</c>
/// on fields bind, matching the camelCase naming convention used for the rest
/// of the document.
/// </para>
/// </remarks>
public sealed class TransportConfigYamlConverter : IYamlTypeConverter
{
    /// <inheritdoc/>
    public bool Accepts(Type type) => type == typeof(TransportConfig);

    /// <inheritdoc/>
    public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var start = parser.Current?.Start ?? Mark.Empty;
        if (!parser.TryConsume<MappingStart>(out _))
        {
            throw new YamlException(start, start, "a transport must be a mapping with a 'kind' field.");
        }

        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            // Transport fields are all scalars; a non-scalar value is a malformed
            // transport (e.g. nesting) and is rejected.
            if (!parser.TryConsume<Scalar>(out var value))
            {
                throw new YamlException(key.Start, key.End,
                    $"transport field '{key.Value}' must have a scalar value.");
            }
            fields[Normalise(key.Value)] = value.Value;
        }

        if (!fields.TryGetValue("kind", out var kind) || string.IsNullOrWhiteSpace(kind))
        {
            throw new YamlException(start, start, "a transport must declare a 'kind' (one of: " +
                $"{TransportKinds.SerialKiss}, {TransportKinds.NinoTnc}, {TransportKinds.KissTcp}, {TransportKinds.Axudp}).");
        }

        return Normalise(kind) switch
        {
            "serialkiss" => new SerialKissTransport
            {
                Device = Required(fields, "device", kind, start),
                Baud = Int(fields, "baud", 57600, start),
            },
            "ninotnc" => new NinoTncTransport
            {
                Device = Required(fields, "device", kind, start),
                Baud = Int(fields, "baud", 57600, start),
                Mode = Int(fields, "mode", 0, start),
            },
            "kisstcp" => new KissTcpTransport
            {
                Host = Required(fields, "host", kind, start),
                Port = Int(fields, "port", 0, start),
            },
            "axudp" => new AxudpTransport
            {
                Host = Required(fields, "host", kind, start),
                Port = Int(fields, "port", 0, start),
                LocalPort = Int(fields, "localport", 0, start),
                // AXUDP always carries the 2-octet AX.25 FCS (the de-facto wire form —
                // RFC 1226 + ax25ipd + BPQAXIP + XRouter all require it; see
                // AxudpTransport). There is no FCS knob. A stale 'includeFcs:' key from
                // an old config lands in 'fields' unread (harmless), so a pre-removal
                // config still loads.
            },
            _ => throw new YamlException(start, start,
                $"unknown transport kind '{kind}' (expected one of: " +
                $"{TransportKinds.SerialKiss}, {TransportKinds.NinoTnc}, {TransportKinds.KissTcp}, {TransportKinds.Axudp})."),
        };
    }

    /// <inheritdoc/>
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        emitter.Emit(new MappingStart());
        switch (value)
        {
            case SerialKissTransport s:
                EmitField(emitter, "kind", s.Kind);
                EmitField(emitter, "device", s.Device);
                EmitField(emitter, "baud", s.Baud.ToString(CultureInfo.InvariantCulture));
                break;
            case NinoTncTransport n:
                EmitField(emitter, "kind", n.Kind);
                EmitField(emitter, "device", n.Device);
                EmitField(emitter, "baud", n.Baud.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "mode", n.Mode.ToString(CultureInfo.InvariantCulture));
                break;
            case KissTcpTransport k:
                EmitField(emitter, "kind", k.Kind);
                EmitField(emitter, "host", k.Host);
                EmitField(emitter, "port", k.Port.ToString(CultureInfo.InvariantCulture));
                break;
            case AxudpTransport a:
                EmitField(emitter, "kind", a.Kind);
                EmitField(emitter, "host", a.Host);
                EmitField(emitter, "port", a.Port.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "localPort", a.LocalPort.ToString(CultureInfo.InvariantCulture));
                break;
            case null:
                break;
            default:
                throw new YamlException($"cannot serialise transport of type {value.GetType().Name}.");
        }
        emitter.Emit(new MappingEnd());
    }

    private static void EmitField(IEmitter emitter, string key, string val)
    {
        emitter.Emit(new Scalar(key));
        emitter.Emit(new Scalar(val));
    }

    // Lower-case and drop hyphens/underscores so "kiss-tcp", "kissTcp", and
    // "KISS_TCP" all collapse to the same comparison key.
    private static string Normalise(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal)
           .Replace("_", "", StringComparison.Ordinal)
           .ToLowerInvariant();

    private static string Required(Dictionary<string, string?> fields, string key, string kind, Mark mark)
    {
        if (fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            return v;
        }
        throw new YamlException(mark, mark, $"transport kind '{kind}' requires a '{key}' field.");
    }

    private static int Int(Dictionary<string, string?> fields, string key, int fallback, Mark mark)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return fallback;
        }
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        throw new YamlException(mark, mark, $"transport field '{key}' must be an integer (got '{v}').");
    }
}
