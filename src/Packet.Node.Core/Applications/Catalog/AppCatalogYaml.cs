using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Node.Core.Applications.Catalog;

/// <summary>
/// The YAML (de)serialisation of <c>catalog/apps.yaml</c> into an
/// <see cref="AppCatalogDocument"/>. Mirrors <c>AppPackageManifestYaml</c>: camelCase property
/// naming, unmatched keys ignored (forward-compatible — the <c>catalog:</c> version is the
/// gate), interface-typed collections mapped to concrete types so YamlDotNet can bind them,
/// and the closed <see cref="ArtifactKind"/> enum bound by a dedicated converter so an unknown
/// <c>kind</c> is a clear error rather than a silent default.
/// </summary>
public static class AppCatalogYaml
{
    // The catalog writes the artifact sub-keys FLAT under `artifact:` (kind + manifest +
    // binaries, or kind + debs, or kind + pdnapp/variants) rather than nesting them under an
    // `assets:`/`deb:`/`pdnapp:` key. The dedicated ArtifactSpec converter reads that flat
    // mapping and routes the siblings into the kind-specific sub-object of the record.
    private static readonly IDeserializer LeafDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeMapping<IReadOnlyDictionary<string, BinaryRef>, Dictionary<string, BinaryRef>>()
        .WithTypeMapping<IReadOnlyDictionary<string, ArtifactRef>, Dictionary<string, ArtifactRef>>()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new ArtifactSpecYamlConverter(LeafDeserializer))
        .WithTypeMapping<IReadOnlyList<string>, List<string>>()
        .WithTypeMapping<IReadOnlyList<AppCatalogEntry>, List<AppCatalogEntry>>()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parse <c>catalog/apps.yaml</c> text into an <see cref="AppCatalogDocument"/>. Throws a
    /// descriptive <see cref="InvalidDataException"/> on malformed YAML, a non-mapping or empty
    /// document, or an unknown artifact <c>kind</c>. Field-level validation (id shape, https
    /// urls, sha256 hex, kind-required sub-objects) is <see cref="Validate"/>'s job — this
    /// method only gets the text into the record shape.
    /// </summary>
    public static AppCatalogDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        AppCatalogDocument? parsed;
        try
        {
            parsed = Deserializer.Deserialize<AppCatalogDocument>(yaml);
        }
        catch (YamlException ex)
        {
            // Surface the innermost message — YamlDotNet wraps converter/typing faults in
            // generic layers and the useful text (which field, which value, the mark) is at
            // the bottom.
            throw new InvalidDataException($"apps.yaml is not a valid catalog: {Innermost(ex)}", ex);
        }

        return parsed ?? throw new InvalidDataException(
            "apps.yaml is empty — a catalog must declare `catalog: 1` and an `apps:` list.");
    }

    /// <summary>
    /// Total, never-throwing field-level validation of one catalog entry. Returns the list of
    /// human-readable problems (empty = valid). Rules: id is lowercase <c>[a-z0-9-]</c>,
    /// version non-empty, the kind-required sub-object is present, every url is
    /// <c>https://</c>, every sha256 is 64-char lowercase hex, and every <c>assets</c> binary
    /// names a non-empty <c>dest</c>.
    /// </summary>
    public static IReadOnlyList<string> Validate(AppCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            problems.Add("id: is required.");
        }
        else if (!IsValidId(entry.Id))
        {
            problems.Add($"id: '{entry.Id}' must be lowercase [a-z0-9-].");
        }

        if (string.IsNullOrWhiteSpace(entry.Version))
        {
            problems.Add("version: is required.");
        }

        if (entry.Artifact is not { } artifact)
        {
            problems.Add("artifact: is required.");
            return problems;
        }

        switch (artifact.Kind)
        {
            case ArtifactKind.Assets:
                if (artifact.Assets is not { } assets)
                {
                    problems.Add("artifact.assets: required when kind is assets.");
                    break;
                }
                ValidateRef(problems, "artifact.assets.manifest", assets.Manifest);
                if (assets.Binaries.Count == 0)
                {
                    problems.Add("artifact.assets.binaries: at least one rid binary is required.");
                }
                foreach (var (rid, bin) in assets.Binaries)
                {
                    var where = $"artifact.assets.binaries.{rid}";
                    ValidateUrl(problems, where, bin.Url);
                    ValidateSha(problems, where, bin.Sha256);
                    if (string.IsNullOrWhiteSpace(bin.Dest))
                    {
                        problems.Add($"{where}.dest: is required.");
                    }
                }
                break;

            case ArtifactKind.Deb:
                if (artifact.Deb is not { } deb)
                {
                    problems.Add("artifact.debs: required when kind is deb.");
                    break;
                }
                if (deb.Debs.Count == 0)
                {
                    problems.Add("artifact.debs: at least one rid .deb is required.");
                }
                foreach (var (rid, @ref) in deb.Debs)
                {
                    ValidateRef(problems, $"artifact.debs.{rid}", @ref);
                }
                break;

            case ArtifactKind.Pdnapp:
                if (artifact.Pdnapp is not { } pdnapp)
                {
                    problems.Add("artifact.pdnapp: required when kind is pdnapp.");
                    break;
                }
                bool any = false;
                if (pdnapp.Pdnapp is { } single)
                {
                    ValidateRef(problems, "artifact.pdnapp", single);
                    any = true;
                }
                if (pdnapp.Variants is { } variants)
                {
                    foreach (var (rid, @ref) in variants)
                    {
                        ValidateRef(problems, $"artifact.pdnapp.variants.{rid}", @ref);
                    }
                    any |= variants.Count > 0;
                }
                if (!any)
                {
                    problems.Add("artifact.pdnapp: a single pdnapp or at least one variant is required.");
                }
                break;

            default:
                problems.Add($"artifact.kind: '{artifact.Kind}' is not a known artifact kind.");
                break;
        }

        return problems;
    }

    private static void ValidateRef(List<string> problems, string where, ArtifactRef? @ref)
    {
        if (@ref is null)
        {
            problems.Add($"{where}: is required.");
            return;
        }
        ValidateUrl(problems, where, @ref.Url);
        ValidateSha(problems, where, @ref.Sha256);
    }

    private static void ValidateUrl(List<string> problems, string where, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.Ordinal))
        {
            problems.Add($"{where}.url: '{url}' must be an https:// URL.");
        }
    }

    private static void ValidateSha(List<string> problems, string where, string? sha256)
    {
        if (!IsSha256Hex(sha256))
        {
            problems.Add($"{where}.sha256: '{sha256}' must be a 64-char lowercase hex string.");
        }
    }

    /// <summary>True for a lowercase <c>[a-z0-9-]</c> id (no regex object needed — the set is
    /// tiny and this stays allocation-free).</summary>
    private static bool IsValidId(string id)
    {
        foreach (var c in id)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return id.Length > 0;
    }

    /// <summary>True for exactly 64 lowercase hex characters.</summary>
    private static bool IsSha256Hex(string? sha)
    {
        if (sha is null || sha.Length != 64)
        {
            return false;
        }
        foreach (var c in sha)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static string Innermost(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }
        return e.Message;
    }

    /// <summary>
    /// Binds the <c>artifact:</c> mapping. The catalog writes the kind-specific fields FLAT
    /// (e.g. <c>kind: assets</c> + <c>manifest:</c> + <c>binaries:</c>) rather than nested under
    /// an <c>assets:</c> key, so this converter reads the whole mapping, dispatches on the
    /// <c>kind</c> scalar, and binds the relevant sibling keys into the matching sub-object of
    /// <see cref="ArtifactSpec"/>. An unknown kind names the closed set.
    /// </summary>
    private sealed class ArtifactSpecYamlConverter(IDeserializer leaf) : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(ArtifactSpec);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var start = parser.Current?.Start ?? Mark.Empty;

            // Buffer the artifact mapping's keys so we can read `kind` first, then re-bind the
            // kind-specific sub-objects from the already-parsed leaf nodes.
            parser.Consume<MappingStart>();
            ArtifactKind? kind = null;
            ArtifactRef? manifest = null;
            Dictionary<string, BinaryRef>? binaries = null;
            Dictionary<string, ArtifactRef>? debs = null;
            ArtifactRef? pdnapp = null;
            Dictionary<string, ArtifactRef>? variants = null;

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = parser.Consume<Scalar>().Value;
                switch (key)
                {
                    case "kind":
                        kind = ReadKind(parser);
                        break;
                    case "manifest":
                        manifest = leaf.Deserialize<ArtifactRef>(parser);
                        break;
                    case "binaries":
                        binaries = leaf.Deserialize<Dictionary<string, BinaryRef>>(parser);
                        break;
                    case "debs":
                        debs = leaf.Deserialize<Dictionary<string, ArtifactRef>>(parser);
                        break;
                    case "pdnapp":
                        pdnapp = leaf.Deserialize<ArtifactRef>(parser);
                        break;
                    case "variants":
                        variants = leaf.Deserialize<Dictionary<string, ArtifactRef>>(parser);
                        break;
                    default:
                        parser.SkipThisAndNestedEvents();   // forward-compat: ignore unknown keys.
                        break;
                }
            }

            if (kind is null)
            {
                throw new YamlException(start, start, "artifact: is missing the required `kind`.");
            }

            return kind switch
            {
                ArtifactKind.Assets => new ArtifactSpec
                {
                    Kind = ArtifactKind.Assets,
                    Assets = manifest is null
                        ? null
                        : new AssetsArtifact { Manifest = manifest, Binaries = binaries ?? new() },
                },
                ArtifactKind.Deb => new ArtifactSpec
                {
                    Kind = ArtifactKind.Deb,
                    Deb = new DebArtifact { Debs = debs ?? new() },
                },
                ArtifactKind.Pdnapp => new ArtifactSpec
                {
                    Kind = ArtifactKind.Pdnapp,
                    Pdnapp = new PdnappArtifact { Pdnapp = pdnapp, Variants = variants },
                },
                _ => new ArtifactSpec { Kind = kind.Value },
            };
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer) =>
            throw new NotSupportedException("the app catalog is read-only; serialization is not supported.");

        private static ArtifactKind ReadKind(IParser parser)
        {
            var scalar = parser.Consume<Scalar>();
            var text = scalar.Value;
            if (!string.IsNullOrWhiteSpace(text)
                && !char.IsAsciiDigit(text[0])  // never accept ordinals ("1") as enum values
                && Enum.TryParse<ArtifactKind>(text, ignoreCase: true, out var value)
                && Enum.IsDefined(value))
            {
                return value;
            }

            var expected = string.Join(", ", Enum.GetNames<ArtifactKind>().Select(n => n.ToLowerInvariant()));
            throw new YamlException(scalar.Start, scalar.End,
                $"'{scalar.Value}' is not a valid artifact kind (expected one of: {expected}).");
        }
    }
}
