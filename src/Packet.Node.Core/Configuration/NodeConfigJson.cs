using System.Text.Json;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The ONE canonical System.Text.Json (de)serialisation of <see cref="NodeConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// The management API serialises <see cref="NodeConfig"/> over the web JSON layer
/// (<c>ConfigureHttpJsonOptions</c> — <see cref="JsonSerializerDefaults.Web"/>:
/// camelCase, case-insensitive read) with the
/// <see cref="TransportConfigJsonConverter"/> registered for the polymorphic
/// <c>transport</c> union (see <c>Program.cs</c>). The
/// <see cref="SqliteConfigProvider"/> persists the config as a JSON blob using
/// <em>these exact options</em>, so the structured <c>PUT /config</c> body and the
/// persisted blob are byte-identical — there is a single canonical serialisation and
/// no second JSON dialect to drift.
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
    /// reads) plus the polymorphic transport-union converter. This static is
    /// immutable after construction and safe to share (the converter is stateless).</summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Build the canonical option set. Exposed so the composition root can
    /// hand the very same converter to <c>ConfigureHttpJsonOptions</c> (one canonical
    /// serialisation across the API and the store).</summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new TransportConfigJsonConverter());
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
    /// <c>kind</c> — the caller treats any throw as "this blob is unusable".</summary>
    public static NodeConfig Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<NodeConfig>(json, Options)
               ?? throw new JsonException("the persisted config blob deserialised to null.");
    }
}
