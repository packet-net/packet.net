using FsCheck.Xunit;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Tests for <see cref="SqliteConfigStore"/> — the JSON-blob singleton row in pdn.db.
/// The marquee is the full-tree round-trip property: save → load returns a
/// <see cref="NodeConfig"/> EQUAL to the input across the whole tree, incl. the
/// polymorphic transport union and the sequence-equality collection records
/// (WebAuthn.AllowedOrigins, Tailscale.Tags). A reference-equality regression in any
/// list/dict member would make a reconcile diff see spurious changes — this catches it.
/// </summary>
public sealed class SqliteConfigStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteConfigStoreTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-cfgstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteConfigStore NewStore() => new(dbPath, new FakeTimeProvider());

    [Property(Arbitrary = [typeof(NodeConfigArbitraries)], MaxTest = 200)]
    public void Save_then_load_round_trips_the_full_tree(NodeConfig config)
    {
        // Each property case gets its own db so the singleton row can't collide.
        var path = Path.Combine(dir, "rt-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteConfigStore(path, new FakeTimeProvider());

        store.Save(config).Should().BeTrue();
        var loaded = store.Load();

        loaded.Should().NotBeNull();
        var (got, schemaVer) = loaded!.Value;
        schemaVer.Should().Be(config.SchemaVersion);

        // Compare the pieces explicitly: NodeConfig's record equality compares the Ports /
        // Applications / Apps lists by REFERENCE, so a structural reparse is never reference-
        // equal. The sub-records (Tailscale, Management incl. WebAuthn) override Equals with
        // sequence-equality, so those compare by value directly.
        got.Identity.Should().Be(config.Identity);
        got.Ports.Should().Equal(config.Ports, "the polymorphic transport union must round-trip");
        got.Services.Should().Be(config.Services);
        got.Management.Should().Be(config.Management);
        got.NetRom.Should().Be(config.NetRom);
        got.Traffic.Should().Be(config.Traffic);
        got.Tailscale.Should().Be(config.Tailscale, "the tailscale tags list must round-trip by value");

        // And the canonical JSON is stable: re-serialising the loaded config equals the
        // serialisation of the input — one canonical form, byte-for-byte.
        NodeConfigJson.Serialize(got).Should().Be(NodeConfigJson.Serialize(config));
    }

    [Fact]
    public void Load_on_a_fresh_db_returns_null()
    {
        var store = NewStore();
        store.Load().Should().BeNull("an absent row is the first-boot migration signal");
    }

    [Fact]
    public void Save_is_an_upsert_keeping_exactly_one_row()
    {
        var store = NewStore();
        var a = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };
        var b = new NodeConfig { Identity = new Identity { Callsign = "G0ABC-2" } };

        store.Save(a).Should().BeTrue();
        store.Save(b).Should().BeTrue();   // ON CONFLICT(id=1) DO UPDATE — replaces, not appends

        var loaded = store.Load();
        loaded!.Value.Config.Identity.Callsign.Should().Be("G0ABC-2");
    }

    [Fact]
    public void AllowedOrigins_and_Tags_round_trip_by_value()
    {
        // The two explicit sequence-equality lists in the tree — pin them directly.
        var store = NewStore();
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Management = new ManagementConfig
            {
                Auth = new AuthConfig
                {
                    WebAuthn = new WebAuthnConfig { AllowedOrigins = ["https://a.example", "https://b.example"] },
                },
            },
            Tailscale = new TailscaleConfig { Tags = ["tag:server", "tag:packetnet"] },
        };

        store.Save(config).Should().BeTrue();
        var got = store.Load()!.Value.Config;

        got.Management.Auth.WebAuthn.AllowedOrigins.Should().Equal("https://a.example", "https://b.example");
        got.Tailscale.Tags.Should().Equal("tag:server", "tag:packetnet");
        got.Management.Auth.WebAuthn.Should().Be(config.Management.Auth.WebAuthn);
        got.Tailscale.Should().Be(config.Tailscale);
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
