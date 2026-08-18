using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The ONE canonical System.Text.Json (de)serialisation of <see cref="NodeConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// The management API serialises <see cref="NodeConfig"/> over the web JSON layer
/// (<c>ConfigureHttpJsonOptions</c> - <see cref="JsonSerializerDefaults.Web"/>:
/// camelCase, case-insensitive read) with <em>this type's</em> converter set applied
/// by <see cref="ApplyTo"/> (see <c>Program.cs</c>): the polymorphic <c>transport</c>
/// union, enums as their member names, and durations as integer seconds. The
/// <see cref="SqliteConfigProvider"/> persists the config as a JSON blob using
/// <em>these exact options</em>, so the structured <c>PUT /config</c> body and the
/// persisted blob are byte-identical - there is a single canonical serialisation and
/// no second JSON dialect to drift.
/// </para>
/// <para>
/// The two shapes that are <em>not</em> the .NET defaults are deliberate, and are what
/// <c>docs/node-api.md</c> and the control panel's <c>src/lib/types.ts</c> have always
/// documented: an enum is its member name (<c>"routing": "Transit"</c>, not <c>2</c>) and
/// a <see cref="TimeSpan"/> is a number of seconds (<c>"l3RttInterval": 60</c>, not
/// <c>"00:01:00"</c>). Both converters still <b>read</b> the older form, so config blobs
/// persisted by an earlier build load unchanged and no DB migration is needed.
/// </para>
/// <para>
/// JSON, not YAML, is the on-disk DB form deliberately: it dodges YAML
/// comment/formatting fragility (which would otherwise be load-bearing bytes) and
/// keeps the store comparing config <em>values</em>, not text. YAML stays exclusively
/// for the human-facing import/export paths (<see cref="NodeConfigYaml"/>): migration
/// in, the <c>/config/raw</c> editor, and the <c>pdn config export/import</c> CLI.
/// </para>
/// </remarks>
public static class NodeConfigJson
{
    /// <summary>The canonical options: web defaults (camelCase, case-insensitive
    /// reads) plus the converters <see cref="ApplyTo"/> registers. This static is
    /// immutable after construction and safe to share (every converter is stateless).</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Register the canonical converter set on an existing
    /// <see cref="JsonSerializerOptions"/>. This is the single definition of the config
    /// JSON dialect: the composition root hands the very same set to
    /// <c>ConfigureHttpJsonOptions</c>, so the HTTP dialect and the DB dialect cannot
    /// drift apart.</summary>
    /// <param name="options">The (still mutable) options to add the converters to.</param>
    public static void ApplyTo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The polymorphic `transport` union: a `kind`-discriminated read/write.
        options.Converters.Add(new TransportConfigJsonConverter());
        // Enums by member name ("Transit", "PerFlow", "BestRoute"), matching
        // docs/node-api.md and web/packetnet-ui/src/lib/types.ts. Integer values are
        // still accepted on read, so blobs persisted before this converter load as-is.
        options.Converters.Add(new JsonStringEnumConverter());
        // Durations as integer seconds; the legacy "hh:mm:ss" string still reads.
        options.Converters.Add(new SecondsTimeSpanJsonConverter());
    }

    /// <summary>Build the canonical option set. Exposed so a caller that needs its own
    /// mutable instance (rather than the shared <see cref="Options"/>) gets exactly the
    /// same dialect.</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApplyTo(options);
        return options;
    }

    /// <summary>Serialise a <see cref="NodeConfig"/> to the canonical JSON blob.</summary>
    public static string Serialize(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.Serialize(config, Options);
    }

    /// <summary>Deserialise a canonical JSON blob back into a <see cref="NodeConfig"/>.
    /// Throws (<see cref="JsonException"/>) on malformed JSON or an unknown transport
    /// <c>kind</c> - the caller treats any throw as "this blob is unusable".</summary>
    public static NodeConfig Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<NodeConfig>(json, Options)
               ?? throw new JsonException("the persisted config blob deserialised to null.");
    }

    /// <summary>Parse a canonical JSON blob into a mutable <see cref="JsonObject"/> - the form
    /// the schema-migration chain (<see cref="NodeConfigSchemaMigrations"/>) transforms before
    /// the blob is deserialised through the current typed model. Throws
    /// (<see cref="JsonException"/>) on malformed JSON or a non-object root.</summary>
    public static JsonObject ParseObject(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = JsonNode.Parse(json)
                   ?? throw new JsonException("the persisted config blob parsed to null.");
        return node as JsonObject
               ?? throw new JsonException("the persisted config blob is not a JSON object.");
    }

    /// <summary>Deserialise an already-parsed (and possibly schema-migrated)
    /// <see cref="JsonNode"/> into a <see cref="NodeConfig"/>, using the SAME canonical
    /// options as the string overload. Throws (<see cref="JsonException"/>) on an
    /// incompatible shape or an unknown transport <c>kind</c>.</summary>
    public static NodeConfig Deserialize(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Deserialize<NodeConfig>(Options)
               ?? throw new JsonException("the persisted config node deserialised to null.");
    }
}
