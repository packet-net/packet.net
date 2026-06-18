using System.Text.Json;
using System.Text.Json.Nodes;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Tests for the forward schema-migration seam (#488): the registry/dispatch in
/// <see cref="NodeConfigSchemaMigrations"/> and the full pipeline the store invokes
/// (<see cref="NodeConfigJson.ParseObject"/> → <c>Migrate</c> →
/// <see cref="NodeConfigJson.Deserialize(JsonNode)"/>).
/// </summary>
/// <remarks>
/// The dispatch-mechanism tests drive it with SYNTHETIC registries and explicit from/to versions
/// (the test-only 4-arg <c>Migrate</c> overload) to prove the mechanism itself — ordered chaining,
/// idempotency, the version stamp, the future-schema fail-safe, and a real older-shape blob
/// migrating to a loadable <see cref="NodeConfig"/>. The production-registry tests at the end
/// exercise the real v1→v2 alias-unification migration.
/// </remarks>
public sealed class NodeConfigSchemaMigrationsTests
{
    // --- The dispatch mechanism ---

    [Fact]
    public void At_current_is_a_no_op_and_idempotent_no_migration_runs()
    {
        var ran = 0;
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [1] = root => { ran++; return root; },   // would run only on a v1→v2 step
        };
        var root = new JsonObject { ["schemaVersion"] = 2, ["x"] = "keep" };

        // from == to: returns the same object untouched, no registered migration invoked.
        var result = NodeConfigSchemaMigrations.Migrate(root, fromVersion: 2, toVersion: 2, registry);

        result.Should().BeSameAs(root, "a blob already at current is never re-transformed");
        ran.Should().Be(0, "no migration runs when the blob is already at the target version");
        ((string?)result["x"]).Should().Be("keep");
    }

    [Fact]
    public void An_older_blob_runs_the_chain_in_order_up_to_current_and_stamps_the_version()
    {
        // A two-step chain v0 → v1 → v2 must apply both, in order, leaving schemaVersion = 2.
        var order = new List<int>();
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [0] = root => { order.Add(0); root["addedAtV1"] = true; return root; },
            [1] = root => { order.Add(1); root["addedAtV2"] = true; return root; },
        };
        var root = new JsonObject { ["schemaVersion"] = 0 };

        var result = NodeConfigSchemaMigrations.Migrate(root, fromVersion: 0, toVersion: 2, registry);

        order.Should().Equal(new[] { 0, 1 }, "migrations run strictly in ascending from-version order");
        ((bool?)result["addedAtV1"]).Should().BeTrue();
        ((bool?)result["addedAtV2"]).Should().BeTrue();
        ((int?)result["schemaVersion"]).Should().Be(2, "the dispatch stamps the target version after the chain");
    }

    [Fact]
    public void Each_step_stamps_its_intermediate_version_so_a_migration_sees_the_prior_step()
    {
        // The version field is rewritten between steps; a later migration can rely on it.
        var seen = new List<int>();
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [0] = root => { seen.Add((int)root["schemaVersion"]!); return root; },
            [1] = root => { seen.Add((int)root["schemaVersion"]!); return root; },
        };
        var root = new JsonObject { ["schemaVersion"] = 0 };

        NodeConfigSchemaMigrations.Migrate(root, fromVersion: 0, toVersion: 2, registry);

        seen.Should().Equal(new[] { 0, 1 }, "the version is stamped to N+1 after step N, so step N+1 sees it");
    }

    [Fact]
    public void A_future_greater_version_throws_the_fail_safe_and_never_mutates_the_blob()
    {
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>();
        var root = new JsonObject { ["schemaVersion"] = 5, ["x"] = "untouched" };

        var act = () => NodeConfigSchemaMigrations.Migrate(root, fromVersion: 5, toVersion: 2, registry);

        act.Should().Throw<NodeConfigSchemaException>()
            .WithMessage("*NEWER than this build understands*");
        ((string?)root["x"]).Should().Be("untouched", "the fail-safe refuses before touching the blob");
    }

    [Fact]
    public void A_gap_in_the_chain_throws_rather_than_silently_skipping()
    {
        // Registered v0→v1 but NOT v1→v2; targeting v2 must fail loudly at the missing step.
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [0] = root => root,
        };
        var root = new JsonObject { ["schemaVersion"] = 0 };

        var act = () => NodeConfigSchemaMigrations.Migrate(root, fromVersion: 0, toVersion: 2, registry);

        act.Should().Throw<NodeConfigSchemaException>().WithMessage("*from v1 to v2*gap*");
    }

    [Fact]
    public void The_production_registry_at_current_is_a_no_op_for_a_current_blob()
    {
        // The real two-arg Migrate (production Registry, target = CurrentSchemaVersion):
        // a blob already at current passes through unchanged. This is what the store relies on.
        var root = new JsonObject { ["schemaVersion"] = NodeConfig.CurrentSchemaVersion, ["y"] = 7 };

        var result = NodeConfigSchemaMigrations.Migrate(root, NodeConfig.CurrentSchemaVersion);

        result.Should().BeSameAs(root);
        ((int?)result["y"]).Should().Be(7);
    }

    // --- The full store pipeline: an older-shape blob migrates to a LOADABLE NodeConfig ---

    [Fact]
    public void A_synthetic_older_shape_blob_migrates_to_current_and_deserialises_correctly()
    {
        // Model the real first-bump scenario: a v0 blob whose Identity used a "station" field
        // that v1 renamed to "callsign". The v0→v1 migration restructures the JSON; the
        // migrated blob must then deserialise through the CURRENT typed model losslessly. This
        // exercises exactly the pipeline SqliteConfigStore.Load runs for a stored_ver < current:
        // ParseObject → Migrate(registry) → Deserialize(JsonNode).
        const string v0Blob = """
            { "schemaVersion": 0,
              "identity": { "station": "M0LTE-7", "alias": "LONDON" } }
            """;

        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [0] = root =>
            {
                var identity = root["identity"]!.AsObject();
                identity["callsign"] = identity["station"]!.GetValue<string>();
                identity.Remove("station");
                return root;
            },
        };

        var parsed = NodeConfigJson.ParseObject(v0Blob);
        var migrated = NodeConfigSchemaMigrations.Migrate(parsed, fromVersion: 0, toVersion: 1, registry);
        var config = NodeConfigJson.Deserialize(migrated);

        config.Identity.Callsign.Should().Be("M0LTE-7", "the renamed field migrated and round-trips through the current type");
        config.Identity.Alias.Should().Be("LONDON");
        config.SchemaVersion.Should().Be(1, "the migrated blob carries the stamped current version");
    }

    [Fact]
    public void ParseObject_rejects_a_non_object_blob()
    {
        var act = () => NodeConfigJson.ParseObject("[1,2,3]");
        act.Should().Throw<JsonException>().WithMessage("*not a JSON object*");
    }

    // --- The production v1→v2 alias-unification migration ---

    private static JsonObject MigrateV1ToV2(string json) =>
        NodeConfigSchemaMigrations.Migrate(NodeConfigJson.ParseObject(json), fromVersion: 1);

    [Fact]
    public void V1_to_v2_folds_netrom_alias_into_identity_alias_and_drops_the_dead_field()
    {
        var root = MigrateV1ToV2("""
            { "schemaVersion": 1, "identity": { "callsign": "M0LTE-1" },
              "netRom": { "enabled": true, "alias": "LONDON" } }
            """);

        ((string?)root["identity"]!["alias"]).Should().Be("LONDON", "the on-air alias becomes the unified node alias");
        (root["netRom"]!.AsObject().ContainsKey("alias")).Should().BeFalse("the dead netRom.alias is removed");
        ((int?)root["schemaVersion"]).Should().Be(2);

        // …and the migrated blob deserialises to a loadable, valid config.
        var config = NodeConfigJson.Deserialize(root);
        config.Identity.Alias.Should().Be("LONDON");
        new NodeConfigValidator().Validate(config).IsValid.Should().BeTrue();
    }

    [Fact]
    public void V1_to_v2_keeps_an_existing_identity_alias_when_both_are_set()
    {
        var root = MigrateV1ToV2("""
            { "schemaVersion": 1, "identity": { "callsign": "M0LTE-1", "alias": "RDG" },
              "netRom": { "alias": "LONDON" } }
            """);

        ((string?)root["identity"]!["alias"]).Should().Be("RDG", "an existing identity.alias wins over netRom.alias");
        (root["netRom"]!.AsObject().ContainsKey("alias")).Should().BeFalse();
    }

    [Fact]
    public void V1_to_v2_caps_an_overlong_alias_to_six_so_the_migrated_config_is_valid()
    {
        // netRom.alias was never length-validated pre-v2 (it was silently truncated to 6 on the
        // wire). The migration brings it into the ≤6 the v2 schema requires, so a node never fails
        // to boot on an over-long legacy alias.
        var root = MigrateV1ToV2("""
            { "schemaVersion": 1, "identity": { "callsign": "M0LTE-1" },
              "netRom": { "alias": "LONDONBRIDGE" } }
            """);

        ((string?)root["identity"]!["alias"]).Should().Be("LONDON", "capped to the 6-octet wire field");
        new NodeConfigValidator().Validate(NodeConfigJson.Deserialize(root)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void V1_to_v2_is_a_no_op_when_there_was_no_alias_anywhere()
    {
        var root = MigrateV1ToV2("""
            { "schemaVersion": 1, "identity": { "callsign": "M0LTE-1" }, "netRom": { "enabled": true } }
            """);

        (root["identity"]!.AsObject().ContainsKey("alias")).Should().BeFalse("no alias to fold ⇒ none added");
        ((int?)root["schemaVersion"]).Should().Be(2);
    }
}
