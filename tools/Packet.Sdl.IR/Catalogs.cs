using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Sdl.IR;

/// <summary>
/// Resolved action-verb catalog. Built from <c>spec-sdl/actions.yaml</c>;
/// empty when the file is absent (soft passthrough mode — every verb
/// passes through verbatim).
/// </summary>
public sealed class ActionCatalog
{
    /// <summary>Map from any known spelling (canonical or alias) to canonical name.</summary>
    public Dictionary<string, string> CanonicalLookup { get; } = new(StringComparer.Ordinal);

    /// <summary>Map from canonical name to its declared SDL kind (signal_upper, signal_lower, etc.).</summary>
    public Dictionary<string, string> CanonicalKind { get; } = new(StringComparer.Ordinal);

    /// <summary>Every alias declared in the catalog (i.e. non-canonical spellings).</summary>
    public HashSet<string> DeclaredAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>Aliases that any <see cref="Validation"/>-driven path-step normalisation actually substituted on.</summary>
    public HashSet<string> SeenAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Load and resolve <c>actions.yaml</c>. Returns an empty catalog
    /// (passthrough mode) when the file is absent.
    /// </summary>
    /// <exception cref="InvalidDataException">Malformed catalog: unknown kind group, duplicate canonical, alias claimed twice, etc.</exception>
    public static ActionCatalog Load(string path)
    {
        var catalog = new ActionCatalog();
        if (!File.Exists(path)) return catalog;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<ActionCatalogEntry>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<ActionCatalogEntry>>(StringComparer.Ordinal);

        foreach (var (kind, entries) in raw)
        {
            if (!ValidActionKinds.Contains(kind))
                throw new InvalidDataException($"{path}: unknown action kind group `{kind}`. Valid: {string.Join(", ", ValidActionKinds)}.");

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    throw new InvalidDataException($"{path}: entry under `{kind}:` is missing `name:`");

                if (!catalog.CanonicalKind.TryAdd(entry.Name, kind))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` declared twice");

                if (!catalog.CanonicalLookup.TryAdd(entry.Name, entry.Name))
                    throw new InvalidDataException($"{path}: canonical name `{entry.Name}` collides with an alias declared earlier");

                foreach (var alias in entry.Aliases ?? new List<string>())
                {
                    if (string.IsNullOrWhiteSpace(alias))
                        throw new InvalidDataException($"{path}: empty alias under canonical name `{entry.Name}`");
                    if (!catalog.CanonicalLookup.TryAdd(alias, entry.Name))
                        throw new InvalidDataException($"{path}: alias `{alias}` is claimed by two canonical names");
                    catalog.DeclaredAliases.Add(alias);
                }
            }
        }

        return catalog;
    }

    private static readonly HashSet<string> ValidActionKinds = new(
        new[] { "signal_upper", "signal_lower", "processing", "subroutine", "internal_out" },
        StringComparer.Ordinal);
}

/// <summary>One entry under a kind group in <c>actions.yaml</c>.</summary>
public sealed class ActionCatalogEntry
{
    public string Name { get; set; } = "";
    public List<string>? Aliases { get; set; }
}

/// <summary>Helpers around the events catalog.</summary>
public static class EventCatalog
{
    /// <summary>
    /// Load the flat set of event names from <c>events.yaml</c>. Returns
    /// empty when absent (events.yaml is documentation-only; the codegen
    /// only consults it for transcription-typo detection).
    /// </summary>
    public static HashSet<string> Load(string path)
    {
        var events = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return events;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(LowerCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var raw = deserializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path))
                  ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (_, group) in raw)
        {
            foreach (var name in group)
            {
                if (!string.IsNullOrWhiteSpace(name)) events.Add(name);
            }
        }
        return events;
    }
}
