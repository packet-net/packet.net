using System.Text.Json.Nodes;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The forward-only schema-migration seam for the persisted <see cref="NodeConfig"/> JSON
/// blob (#488, the load-bearing follow-up to config-in-DB #473). The blob is stored in
/// <c>pdn.db</c> with a <c>schema_ver</c> column (<see cref="SqliteConfigStore"/>); when the
/// running code's <see cref="NodeConfig.CurrentSchemaVersion"/> is AHEAD of a stored blob,
/// the store runs the registered chain of <em>JSON transforms</em> here — each one rewriting
/// the blob from version N to N+1 — up to current, THEN deserialises. This means the first
/// real schema bump never has to fight an old C# type or degrade to a re-seed that would lose
/// the operator's edits.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why JSON, not the typed model.</b> A shape change is expressible as a
/// <see cref="JsonObject"/> transform <em>without</em> the old C# record type still existing
/// — rename a field, restructure a sub-object, supply a default for a newly-required member.
/// Deserialising an old blob through the <em>current</em> type would silently drop renamed
/// fields (lossy) or throw on a now-required one (degrade to re-seed). Transforming the JSON
/// first sidesteps both.
/// </para>
/// <para>
/// <b>Adding a future migration is ONE entry.</b> When a schema bump lands: bump
/// <see cref="NodeConfig.CurrentSchemaVersion"/> and add a single
/// <c>[fromVersion] = root =&gt; { … }</c> entry to <see cref="Registry"/>. The dispatch is
/// keyed by from-version and walks N → N+1 → … → current automatically. No other code moves.
/// </para>
/// <para>
/// <b>Fail-safe on a future schema.</b> A stored <c>schema_ver</c> GREATER than current is a
/// downgrade onto a newer schema (an operator rolled the binary back onto a DB a newer build
/// wrote). The chain THROWS rather than guess: the same never-run-on-an-unknown-config
/// invariant the provider's boot path already enforces. We never silently run on, nor clobber,
/// a newer schema.
/// </para>
/// </remarks>
public static class NodeConfigSchemaMigrations
{
    /// <summary>
    /// A single forward step: transform the blob's root object from schema version N to N+1.
    /// Operates on the parsed <see cref="JsonObject"/> in place (and returns it) so a shape
    /// change is expressible without the old typed model. A migration MUST NOT assume the
    /// <c>schemaVersion</c> field is any particular value — the dispatch owns the version
    /// bookkeeping and rewrites the field after each step.
    /// </summary>
    public delegate JsonObject Migration(JsonObject root);

    /// <summary>
    /// The production registry, keyed by from-version: <c>Registry[N]</c> migrates a v<c>N</c>
    /// blob to v<c>N+1</c>. The first (and currently only) entry is the v1→v2 alias unification
    /// (<see cref="MigrateV1ToV2"/>): a pre-v2 config carried a separate <c>netRom.alias</c>; v2
    /// unifies the node alias into <c>identity.alias</c> (the single node-name concept).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, Migration> Registry =
        new Dictionary<int, Migration>
        {
            [1] = MigrateV1ToV2,
        };

    /// <summary>
    /// v1 → v2: unify the node alias. Folds a pre-v2 <c>netRom.alias</c> into
    /// <c>identity.alias</c> (the single node-name concept — the NET/ROM broadcast now takes its
    /// alias from there) unless <c>identity.alias</c> is already set, then drops the dead
    /// <c>netRom.alias</c> field. Lossless for the common case (only one was ever set); when both
    /// were set the display alias (<c>identity.alias</c>) wins and the on-air alias converges to it.
    /// </summary>
    private static JsonObject MigrateV1ToV2(JsonObject root)
    {
        var identityAlias = root["identity"] is JsonObject id ? AsString(id["alias"]) : null;
        var netromAlias = root["netRom"] is JsonObject nr ? AsString(nr["alias"]) : null;

        // The unified alias: a pre-existing identity.alias wins; otherwise fold in netRom.alias.
        var unified = !string.IsNullOrWhiteSpace(identityAlias) ? identityAlias : netromAlias;

        if (!string.IsNullOrWhiteSpace(unified))
        {
            // Normalise to the ≤6 the v2 schema requires. This is faithful: the NODES wire field is
            // 6 octets, so a longer netRom.alias (never length-validated pre-v2) only ever reached
            // the air as its first 6 chars — and a long identity.alias from the old unvalidated UI
            // is brought into range too, so the migrated config always passes v2 validation (a node
            // never boots on an over-long alias).
            var trimmed = unified.Trim();
            var capped = trimmed.Length > 6 ? trimmed[..6] : trimmed;

            if (root["identity"] is not JsonObject identity)
            {
                identity = new JsonObject();
                root["identity"] = identity;
            }
            identity["alias"] = capped;
        }

        // Drop the dead netRom.alias field regardless.
        if (root["netRom"] is JsonObject netrom)
        {
            netrom.Remove("alias");
        }

        return root;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Migrate <paramref name="root"/> from <paramref name="fromVersion"/> up to the running
    /// code's <see cref="NodeConfig.CurrentSchemaVersion"/>, using the production
    /// <see cref="Registry"/>. See the registry overload for the full contract.
    /// </summary>
    public static JsonObject Migrate(JsonObject root, int fromVersion) =>
        Migrate(root, fromVersion, NodeConfig.CurrentSchemaVersion, Registry);

    /// <summary>
    /// Migrate <paramref name="root"/> from <paramref name="fromVersion"/> up to
    /// <paramref name="toVersion"/> using <paramref name="registry"/>. The dispatch core,
    /// parameterised so tests can exercise the mechanism with a synthetic registry while
    /// production calls the two-arg overload.
    /// <list type="bullet">
    /// <item><b>Equal</b> (from == to): returns <paramref name="root"/> unchanged — no
    /// migration runs (idempotent; a blob already at current is never re-transformed).</item>
    /// <item><b>Less</b> (from &lt; to): walks N → N+1 → … → to, applying
    /// <c>registry[N]</c> at each step and stamping <c>schemaVersion</c> after each, then
    /// returns the transformed root.</item>
    /// <item><b>Greater</b> (from &gt; to): THROWS <see cref="NodeConfigSchemaException"/> —
    /// the fail-safe for a downgrade onto a future schema.</item>
    /// </list>
    /// Throws <see cref="NodeConfigSchemaException"/> if a step in the chain has no registered
    /// migration (a gap), so a half-defined chain fails loudly rather than silently skipping.
    /// </summary>
    public static JsonObject Migrate(JsonObject root, int fromVersion, int toVersion, IReadOnlyDictionary<int, Migration> registry)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(registry);

        if (fromVersion == toVersion)
        {
            return root;   // already current — nothing to do (idempotent)
        }

        if (fromVersion > toVersion)
        {
            throw new NodeConfigSchemaException(
                $"the persisted config schema (v{fromVersion}) is NEWER than this build understands (v{toVersion}); " +
                "refusing to run on or downgrade a future schema. Upgrade the node, or restore a matching DB.");
        }

        var current = root;
        for (var v = fromVersion; v < toVersion; v++)
        {
            if (!registry.TryGetValue(v, out var migration))
            {
                throw new NodeConfigSchemaException(
                    $"no registered config-schema migration from v{v} to v{v + 1} (target v{toVersion}); the migration chain has a gap.");
            }

            current = migration(current)
                ?? throw new NodeConfigSchemaException($"the config-schema migration from v{v} to v{v + 1} returned null.");

            // The dispatch owns the version bookkeeping: stamp the new version so the blob is
            // self-describing after each step and a partial chain can't be misread as current.
            current["schemaVersion"] = v + 1;
        }

        return current;
    }
}

/// <summary>
/// Raised when a persisted config blob cannot be brought to the current schema: a future
/// (greater) <c>schema_ver</c>, or a gap in the migration chain. Distinct from
/// <see cref="System.Text.Json.JsonException"/> (a corrupt/unreadable blob) so the store can
/// keep the corrupt-blob degrade path (re-seed) while a schema mismatch is a fail-safe error.
/// </summary>
public sealed class NodeConfigSchemaException : Exception
{
    public NodeConfigSchemaException(string message) : base(message) { }

    public NodeConfigSchemaException(string message, Exception inner) : base(message, inner) { }
}
