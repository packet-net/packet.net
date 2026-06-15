using Packet.Node.Core.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// The YAML (de)serialisation of an app package's <c>pdn-app.yaml</c>
/// (<see cref="AppPackageManifest"/>). Mirrors <see cref="NodeConfigYaml"/>: camelCase
/// property naming, unmatched keys ignored (a manifest may carry future keys — the
/// <c>manifest:</c> version gate is the forward-compat boundary, enforced by the catalog),
/// and the interface-typed collections mapped to concrete types so YamlDotNet can bind them.
/// </summary>
/// <remarks>
/// Unlike <see cref="NodeConfigYaml.Parse"/>, an empty / null document here is an error, not
/// a default: there is no meaningful "default manifest". <see cref="Parse"/> throws a
/// descriptive exception for malformed YAML or an empty document; the catalog
/// (<see cref="AppPackageCatalog"/>) catches it into a <see cref="DiscoveredAppPackage.Error"/>
/// entry rather than letting one broken package take down discovery.
/// </remarks>
public static class AppPackageManifestYaml
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        // The contract writes the manifest enums in kebab-case (`restart: on-failure`,
        // `managed: external`, `kind: socket`). YamlDotNet's built-in enum binding is
        // case-insensitive but not hyphen-tolerant ("on-failure" would never match
        // OnFailure), so a dedicated converter binds — and emits — the kebab forms.
        .WithTypeConverter(KebabCaseEnumYamlConverter.Instance)
        // ui.mode is the one closed set the contract makes lenient: an unknown/missing value
        // binds to the safe default (standalone) rather than erroring the whole manifest, so an
        // app authored against a newer mode set still loads on an older node. A dedicated,
        // non-throwing converter handles it (vs the strict KebabCaseEnumYamlConverter above).
        .WithTypeConverter(SafeDefaultUiModeYamlConverter.Instance)
        // Interface-typed collections need concrete types to bind (same trick as
        // NodeConfigYaml): capabilities / args lists + the service environment map + the
        // forward: list of AppForwardSpec.
        .WithTypeMapping<IReadOnlyList<string>, List<string>>()
        .WithTypeMapping<IReadOnlyList<AppForwardSpec>, List<AppForwardSpec>>()
        .WithTypeMapping<IReadOnlyDictionary<string, string>, Dictionary<string, string>>()
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(KebabCaseEnumYamlConverter.Instance)
        .WithTypeConverter(SafeDefaultUiModeYamlConverter.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>
    /// Parse <c>pdn-app.yaml</c> text into an <see cref="AppPackageManifest"/>. Throws a
    /// descriptive <see cref="InvalidDataException"/> on malformed YAML, a non-mapping or
    /// empty document, or an enum value outside its closed set. Structural validation
    /// (id ↔ directory, required per-kind fields, verb collisions) is the catalog's job —
    /// this method only gets the text into the record shape.
    /// </summary>
    public static AppPackageManifest Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        AppPackageManifest? parsed;
        try
        {
            parsed = Deserializer.Deserialize<AppPackageManifest>(yaml);
        }
        catch (YamlException ex)
        {
            // Surface the innermost message — YamlDotNet wraps converter/typing faults in
            // generic "Exception during deserialization" layers and the useful text (which
            // field, which value, the document mark) is at the bottom.
            throw new InvalidDataException($"pdn-app.yaml is not a valid manifest: {Innermost(ex)}", ex);
        }

        return parsed ?? throw new InvalidDataException(
            "pdn-app.yaml is empty — a manifest must declare `manifest: 1`, an `id`, and at " +
            "least one of session / service / ui.");
    }

    /// <summary>Serialise a manifest back to YAML (kebab-case enums, camelCase keys). Used by
    /// the round-trip tests and any future package-authoring tooling; pdn itself never writes
    /// a manifest (they are authored by the app).</summary>
    public static string Serialize(AppPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Serializer.Serialize(manifest);
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
    /// Binds the manifest's closed enum sets from the contract's kebab-case spelling
    /// (<c>on-failure</c> → <see cref="AppServiceRestart.OnFailure"/>) as well as the plain
    /// case-insensitive forms (<c>onFailure</c>, <c>OnFailure</c>), and emits kebab-case.
    /// Numeric scalars are rejected — the YAML surface is names, not ordinals.
    /// </summary>
    private sealed class KebabCaseEnumYamlConverter : IYamlTypeConverter
    {
        public static readonly KebabCaseEnumYamlConverter Instance = new();

        private static readonly Type[] HandledEnums =
        [
            typeof(ApplicationKind),
            typeof(AppServiceRestart),
            typeof(AppServiceManaged),
            typeof(ForwardTls),
        ];

        public bool Accepts(Type type) => Array.IndexOf(HandledEnums, type) >= 0;

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            var text = scalar.Value?.Replace("-", "", StringComparison.Ordinal)
                                    .Replace("_", "", StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(text)
                && !char.IsAsciiDigit(text[0])  // never accept ordinals ("1") as enum values
                && Enum.TryParse(type, text, ignoreCase: true, out var value)
                && Enum.IsDefined(type, value!))
            {
                return value!;
            }

            var expected = string.Join(", ", Enum.GetNames(type).Select(ToKebabCase));
            throw new YamlException(scalar.Start, scalar.End,
                $"'{scalar.Value}' is not a valid {ToCamelCase(type.Name)} value (expected one of: {expected}).");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            emitter.Emit(new Scalar(ToKebabCase(value!.ToString()!)));
        }

        // OnFailure -> on-failure, External -> external.
        private static string ToKebabCase(string name) =>
            string.Concat(name.Select((c, i) =>
                char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));

        // For error messages: the type name as it reads in context (ApplicationKind -> applicationKind
        // is unhelpful; use the simple lowercase-first form which matches the YAML key style).
        private static string ToCamelCase(string name) =>
            string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Binds <see cref="AppUiMode"/> from its (kebab/camel/pascal, case-insensitive) names but —
    /// unlike <see cref="KebabCaseEnumYamlConverter"/> — never throws: an unknown value falls back
    /// to <see cref="AppUiMode.Standalone"/>, the contract's safe default (so an app written
    /// against a future mode set still loads). Emits kebab-case, like the other manifest enums.
    /// </summary>
    private sealed class SafeDefaultUiModeYamlConverter : IYamlTypeConverter
    {
        public static readonly SafeDefaultUiModeYamlConverter Instance = new();

        public bool Accepts(Type type) => type == typeof(AppUiMode);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            var scalar = parser.Consume<Scalar>();
            var text = scalar.Value?.Replace("-", "", StringComparison.Ordinal)
                                    .Replace("_", "", StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(text)
                && !char.IsAsciiDigit(text[0])  // names, not ordinals
                && Enum.TryParse<AppUiMode>(text, ignoreCase: true, out var value)
                && Enum.IsDefined(value))
            {
                return value;
            }

            // Unknown / empty → the safe default rather than an error (the contract's rule).
            return AppUiMode.Standalone;
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            // Standalone -> standalone, Embedded -> embedded, Slot -> slot.
            emitter.Emit(new Scalar(value!.ToString()!.ToLowerInvariant()));
        }
    }
}
