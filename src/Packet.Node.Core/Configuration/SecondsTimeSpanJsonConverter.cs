using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The canonical JSON wire form of a config <see cref="TimeSpan"/>: a <b>number of
/// seconds</b>, not a <c>"hh:mm:ss"</c> duration string.
/// </summary>
/// <remarks>
/// <para>
/// Registered in <see cref="NodeConfigJson"/>'s canonical option set, so it applies
/// identically to the persisted <c>pdn.db</c> blob and to the management API's
/// <c>GET</c>/<c>PUT /api/v1/config</c> bodies. Today the only <see cref="TimeSpan"/>
/// knobs that reach that wire are the four INP3 timers
/// (<c>netRom.inp3.l3RttInterval</c> / <c>l3RttResetWindow</c> / <c>rifInterval</c> /
/// <c>positiveDebounce</c> - see <see cref="Packet.NetRom.Wire.NetRomInp3Options"/>),
/// which the control panel edits as plain numeric seconds.
/// </para>
/// <para>
/// <b>Write:</b> a whole number of seconds as a JSON integer
/// (<c>"l3RttInterval": 60</c>). A value carrying sub-second precision (nothing
/// defaults to one, but an operator could hand-write <c>00:00:00.5</c> in YAML)
/// writes as a fractional number rather than being truncated to zero - the write is
/// lossless in both directions.
/// </para>
/// <para>
/// <b>Read:</b> either form. A number is seconds. A string is parsed as an invariant
/// <see cref="TimeSpan"/> first - that is the <em>legacy</em> shape, the one already
/// sitting in every deployed node's <c>pdn.db</c> config blob from before this
/// converter existed - and falls back to a numeric string (the web JSON defaults
/// allow numbers quoted as strings elsewhere, so accepting <c>"60"</c> here keeps
/// the dialect consistent). So no blob migration is needed: an existing node reads
/// its stored <c>"00:01:00"</c> and rewrites it as <c>60</c> on the next config
/// write.
/// </para>
/// <para>
/// The YAML form is unaffected: <see cref="NodeConfigYaml"/> keeps YamlDotNet's
/// built-in duration scalars (<c>l3RttInterval: 00:01:00</c>), which is what
/// <c>docs/netrom-inp3-host-integration-design.md</c> §2.2 locks for the
/// human-facing import/export path.
/// </para>
/// </remarks>
public sealed class SecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    /// <inheritdoc />
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return FromSeconds(reader.GetDouble());

            case JsonTokenType.String:
            {
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new JsonException("a duration must be a number of seconds, not an empty string.");
                }
                // A bare numeric string ("60", "0.5") is seconds. It MUST be tried before
                // TimeSpan.TryParse: the invariant TimeSpan grammar reads a lone number as
                // DAYS ("60" -> 60.00:00:00), which would silently turn a quoted 60 s into
                // two months.
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    return FromSeconds(seconds);
                }
                // Otherwise the legacy "hh:mm:ss" duration blobs written before this
                // converter existed.
                if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
                {
                    return span;
                }
                throw new JsonException(
                    $"'{text}' is not a duration: expected a number of seconds (60) or an invariant TimeSpan (00:01:00).");
            }

            default:
                throw new JsonException(
                    $"expected a number of seconds or a duration string for a TimeSpan, got {reader.TokenType}.");
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            writer.WriteNumberValue(value.Ticks / TimeSpan.TicksPerSecond);
            return;
        }
        // Sub-second precision survives rather than truncating to a value the
        // validator would then reject as non-positive.
        writer.WriteNumberValue(value.TotalSeconds);
    }

    private static TimeSpan FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            throw new JsonException($"'{seconds}' is not a usable number of seconds.");
        }
        return TimeSpan.FromSeconds(seconds);
    }
}
