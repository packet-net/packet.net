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
        // Nested fields a transport may carry: the multipoint AXUDP peer table, and the
        // soundmodem flex-slice tuning. All other transport fields are flat scalars.
        List<AxudpPeerConfig>? peers = null;
        SoundModemFlexConfig? flex = null;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            var normalisedKey = Normalise(key.Value);

            // 'peers:' is a sequence of mappings (the multipoint MAP table); every other
            // transport field is a flat scalar.
            if (normalisedKey == "peers")
            {
                peers = ReadPeers(parser, key);
                continue;
            }

            // 'flex:' is a single mapping (the soundmodem FlexRadio slice tuning).
            if (normalisedKey == "flex")
            {
                flex = ReadFlex(parser, key);
                continue;
            }

            // Transport fields are all scalars; a non-scalar value is a malformed
            // transport (e.g. unexpected nesting) and is rejected.
            if (!parser.TryConsume<Scalar>(out var value))
            {
                throw new YamlException(key.Start, key.End,
                    $"transport field '{key.Value}' must have a scalar value.");
            }
            fields[normalisedKey] = value.Value;
        }

        if (!fields.TryGetValue("kind", out var kind) || string.IsNullOrWhiteSpace(kind))
        {
            throw new YamlException(start, start, "a transport must declare a 'kind' (one of: " +
                $"{TransportKinds.SerialKiss}, {TransportKinds.NinoTnc}, {TransportKinds.NinoTncTcp}, {TransportKinds.KissTcp}, {TransportKinds.Axudp}, {TransportKinds.AxudpMultipoint}, {TransportKinds.TaitTransparent}, {TransportKinds.SoundModem}).");
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
            "ninotnctcp" => new NinoTncTcpTransport
            {
                HeadEndId = Required(fields, "headendid", kind, start),
                DeviceId = Required(fields, "deviceid", kind, start),
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
            "axudpmultipoint" => new AxudpMultipointTransport
            {
                LocalPort = Int(fields, "localport", 0, start),
                Peers = peers ?? [],
            },
            "soundmodem" => new SoundModemTransportConfig
            {
                Device = fields.GetValueOrDefault("device") ?? "default",
                CaptureRate = Int(fields, "capturerate", 48000, start),
                Mode = fields.GetValueOrDefault("mode") ?? "afsk1200",
                Frequency = Double(fields, "frequency", 0, start),
                OffsetPairs = NullableInt(fields, "offsetpairs", start),
                OffsetStepHz = NullableDouble(fields, "offsetstephz", start),
                PskDetector = fields.GetValueOrDefault("pskdetector"),
                Ptt = fields.GetValueOrDefault("ptt") ?? "",
                Flex = flex,
            },
            "taittransparent" => new TaitTransparentTransportConfig
            {
                Device = fields.GetValueOrDefault("device") ?? "",
                Serial = fields.GetValueOrDefault("serial") ?? "",
                // The head-end binding mode (#585) — all binding fields are optional here;
                // the validator enforces exactly-one mode across device/serial/headEnd pair.
                HeadEndId = fields.GetValueOrDefault("headendid") ?? "",
                DeviceId = fields.GetValueOrDefault("deviceid") ?? "",
                Baud = Int(fields, "baud", 28800, start),
                TransparentBaud = Int(fields, "transparentbaud", 28800, start),
                FfskBaud = Int(fields, "ffskbaud", 2400, start),
                LeadInMs = Int(fields, "leadinms", 100, start),
            },
            _ => throw new YamlException(start, start,
                $"unknown transport kind '{kind}' (expected one of: " +
                $"{TransportKinds.SerialKiss}, {TransportKinds.NinoTnc}, {TransportKinds.NinoTncTcp}, {TransportKinds.KissTcp}, {TransportKinds.Axudp}, {TransportKinds.AxudpMultipoint}, {TransportKinds.TaitTransparent}, {TransportKinds.SoundModem})."),
        };
    }

    // Read a 'peers:' value — a sequence of flat mappings (call/host/port/broadcast),
    // the multipoint AXUDP MAP table. A non-sequence value, or a peer that isn't a flat
    // mapping, is a malformed transport and is rejected with the document mark.
    private static List<AxudpPeerConfig> ReadPeers(IParser parser, Scalar key)
    {
        if (!parser.TryConsume<SequenceStart>(out _))
        {
            throw new YamlException(key.Start, key.End, "transport field 'peers' must be a sequence of peer mappings.");
        }

        var peers = new List<AxudpPeerConfig>();
        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            var itemStart = parser.Current?.Start ?? Mark.Empty;
            if (!parser.TryConsume<MappingStart>(out _))
            {
                throw new YamlException(itemStart, itemStart, "each 'peers' entry must be a mapping (call, host, port, broadcast).");
            }

            var peerFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var pk = parser.Consume<Scalar>();
                if (!parser.TryConsume<Scalar>(out var pv))
                {
                    throw new YamlException(pk.Start, pk.End, $"peer field '{pk.Value}' must have a scalar value.");
                }
                peerFields[Normalise(pk.Value)] = pv.Value;
            }

            peers.Add(new AxudpPeerConfig
            {
                Call = Required(peerFields, "call", "axudp-multipoint peer", itemStart),
                Host = Required(peerFields, "host", "axudp-multipoint peer", itemStart),
                Port = Int(peerFields, "port", 0, itemStart),
                Broadcast = Bool(peerFields, "broadcast", false, itemStart),
            });
        }
        return peers;
    }

    // Read a 'flex:' value — a single mapping of the FlexRadio slice tuning
    // (frequency/antenna/mode/daxChannel); any omitted field takes its default.
    private static SoundModemFlexConfig ReadFlex(IParser parser, Scalar key)
    {
        if (!parser.TryConsume<MappingStart>(out _))
        {
            throw new YamlException(key.Start, key.End,
                "transport field 'flex' must be a mapping (frequency, antenna, mode, daxChannel).");
        }

        var flexFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var fk = parser.Consume<Scalar>();
            if (!parser.TryConsume<Scalar>(out var fv))
            {
                throw new YamlException(fk.Start, fk.End, $"flex field '{fk.Value}' must have a scalar value.");
            }
            flexFields[Normalise(fk.Value)] = fv.Value;
        }

        var defaults = new SoundModemFlexConfig();
        return new SoundModemFlexConfig
        {
            Frequency = flexFields.GetValueOrDefault("frequency") ?? defaults.Frequency,
            Antenna = flexFields.GetValueOrDefault("antenna") ?? defaults.Antenna,
            Mode = flexFields.GetValueOrDefault("mode") ?? defaults.Mode,
            DaxChannel = flexFields.GetValueOrDefault("daxchannel") ?? defaults.DaxChannel,
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
            case NinoTncTcpTransport nt:
                EmitField(emitter, "kind", nt.Kind);
                EmitField(emitter, "headEndId", nt.HeadEndId);
                EmitField(emitter, "deviceId", nt.DeviceId);
                EmitField(emitter, "mode", nt.Mode.ToString(CultureInfo.InvariantCulture));
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
            case AxudpMultipointTransport m:
                EmitField(emitter, "kind", m.Kind);
                EmitField(emitter, "localPort", m.LocalPort.ToString(CultureInfo.InvariantCulture));
                emitter.Emit(new Scalar("peers"));
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
                foreach (var peer in m.Peers)
                {
                    emitter.Emit(new MappingStart());
                    EmitField(emitter, "call", peer.Call);
                    EmitField(emitter, "host", peer.Host);
                    EmitField(emitter, "port", peer.Port.ToString(CultureInfo.InvariantCulture));
                    EmitField(emitter, "broadcast", peer.Broadcast ? "true" : "false");
                    emitter.Emit(new MappingEnd());
                }
                emitter.Emit(new SequenceEnd());
                break;
            case SoundModemTransportConfig sm:
                EmitField(emitter, "kind", sm.Kind);
                EmitField(emitter, "device", sm.Device);
                EmitField(emitter, "captureRate", sm.CaptureRate.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "mode", sm.Mode);
                if (sm.Frequency != 0)
                {
                    EmitField(emitter, "frequency", sm.Frequency.ToString(CultureInfo.InvariantCulture));
                }
                if (sm.OffsetPairs is { } offsetPairs)
                {
                    EmitField(emitter, "offsetPairs", offsetPairs.ToString(CultureInfo.InvariantCulture));
                }
                if (sm.OffsetStepHz is { } offsetStepHz)
                {
                    EmitField(emitter, "offsetStepHz", offsetStepHz.ToString(CultureInfo.InvariantCulture));
                }
                if (!string.IsNullOrWhiteSpace(sm.PskDetector))
                {
                    EmitField(emitter, "pskDetector", sm.PskDetector);
                }
                if (!string.IsNullOrWhiteSpace(sm.Ptt))
                {
                    EmitField(emitter, "ptt", sm.Ptt);
                }
                if (sm.Flex is { } flex)
                {
                    emitter.Emit(new Scalar("flex"));
                    emitter.Emit(new MappingStart());
                    EmitField(emitter, "frequency", flex.Frequency);
                    EmitField(emitter, "antenna", flex.Antenna);
                    EmitField(emitter, "mode", flex.Mode);
                    EmitField(emitter, "daxChannel", flex.DaxChannel);
                    emitter.Emit(new MappingEnd());
                }
                break;
            case TaitTransparentTransportConfig t:
                EmitField(emitter, "kind", t.Kind);
                if (!string.IsNullOrWhiteSpace(t.Device))
                {
                    EmitField(emitter, "device", t.Device);
                }
                if (!string.IsNullOrWhiteSpace(t.Serial))
                {
                    EmitField(emitter, "serial", t.Serial);
                }
                if (!string.IsNullOrWhiteSpace(t.HeadEndId))
                {
                    EmitField(emitter, "headEndId", t.HeadEndId);
                }
                if (!string.IsNullOrWhiteSpace(t.DeviceId))
                {
                    EmitField(emitter, "deviceId", t.DeviceId);
                }
                EmitField(emitter, "baud", t.Baud.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "transparentBaud", t.TransparentBaud.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "ffskBaud", t.FfskBaud.ToString(CultureInfo.InvariantCulture));
                EmitField(emitter, "leadInMs", t.LeadInMs.ToString(CultureInfo.InvariantCulture));
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

    private static double Double(Dictionary<string, string?> fields, string key, double fallback, Mark mark)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return fallback;
        }
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        throw new YamlException(mark, mark, $"transport field '{key}' must be a number (got '{v}').");
    }

    private static int? NullableInt(Dictionary<string, string?> fields, string key, Mark mark)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return null;
        }
        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        throw new YamlException(mark, mark, $"transport field '{key}' must be an integer (got '{v}').");
    }

    private static double? NullableDouble(Dictionary<string, string?> fields, string key, Mark mark)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return null;
        }
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        throw new YamlException(mark, mark, $"transport field '{key}' must be a number (got '{v}').");
    }

    private static bool Bool(Dictionary<string, string?> fields, string key, bool fallback, Mark mark)
    {
        if (!fields.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
        {
            return fallback;
        }
        if (bool.TryParse(v, out var parsed))
        {
            return parsed;
        }
        throw new YamlException(mark, mark, $"transport field '{key}' must be true/false (got '{v}').");
    }
}
