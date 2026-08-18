using Packet.Core;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// <see cref="ApplicationHost.ResolveServiceCommandCallsign"/> - the bare command verb → service
/// app loopback target. The node resolves a callsign for each packet app
/// (<c>PDN_APP_CALLSIGN</c>); a <b>migrated</b> app binds exactly that, but an <b>un-migrated,
/// self-deriving</b> app binds a different SSID of the node base (packet.net#476). When the live
/// app registry is wired the resolver bridges the verb to whatever the app actually bound, so a
/// self-deriving app is reachable by its verb again - without regressing a migrated app.
/// </summary>
[Trait("Category", "Node")]
public sealed class ServiceCommandCallsignTests
{
    private const string NodeCall = "M9YYY";

    private static NodeConfig Cfg() => new() { Identity = new Identity { Callsign = NodeCall } };

    // A service package (a packet-capable daemon with a command verb and NO session block) - the
    // shape ResolveServiceCommandCallsign resolves a loopback callsign for.
    private static AppPackageManifest ServiceManifest(string id, string verb) => new()
    {
        Manifest = 1,
        Id = id,
        Capabilities = [AppCapabilities.Packet],
        Packet = new AppPacketSpec { Command = verb },
        Service = new AppServiceSpec { Command = "/bin/sh", Args = ["run.sh"] },
    };

    private static ApplicationHost HostWith(FakeAppPackageCatalog catalog, ILocalAppRegistry? registry)
    {
        var host = new ApplicationHost(new TestConfigProvider(Cfg()), loggerFactory: null, catalog);
        host.LocalAppRegistry = registry;
        return host;
    }

    // A registry whose live key set is whatever callsigns we say are bound right now.
    private sealed class FakeRegistry(params Callsign[] bound) : ILocalAppRegistry
    {
        private readonly HashSet<Callsign> set = [.. bound];
        public bool IsRegistered(Callsign callsign) => set.Contains(callsign);
        public IReadOnlyCollection<Callsign> RegisteredCallsigns() => set.ToArray();
    }

    [Fact]
    public void With_no_registry_it_returns_the_node_resolved_callsign()
    {
        // The pre-#476 behaviour (older hosts / tests): no registry → the node-resolved callsign
        // verbatim. The app auto-assigns to the lowest free SSID of the node base (1).
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var host = HostWith(catalog, registry: null);

        host.ResolveServiceCommandCallsign("BBS").Should().Be(new Callsign(NodeCall, 1));
    }

    [Fact]
    public void A_migrated_app_binds_the_resolved_callsign_and_is_reached_at_it()
    {
        // The app bound exactly its PDN_APP_CALLSIGN (M9YYY-1) - the registry holds it → the
        // resolver returns it unchanged. No regression for the migrated core apps.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var resolved = new Callsign(NodeCall, 1);
        var host = HostWith(catalog, new FakeRegistry(resolved));

        host.ResolveServiceCommandCallsign("BBS").Should().Be(resolved);
    }

    [Fact]
    public void A_self_deriving_app_is_reached_at_the_ssid_it_actually_bound()
    {
        // The acceptance criterion (packet.net#476): the node resolves M9YYY-1 for BBS, but the
        // un-migrated app ignored PDN_APP_CALLSIGN and self-derived M9YYY-4. The resolved callsign
        // is NOT in the registry; the only stray binding on the node base is M9YYY-4 → the verb
        // bridges to it, so BBS at the prompt reaches the app.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var selfDerived = new Callsign(NodeCall, 4);
        var host = HostWith(catalog, new FakeRegistry(selfDerived));

        host.ResolveServiceCommandCallsign("BBS").Should().Be(selfDerived,
            "a self-deriving app that bound a different SSID is still reachable by its bare verb");
    }

    [Fact]
    public void A_stray_on_a_different_base_is_not_taken()
    {
        // A binding on some other base (a pinned cross-base app) is not the self-deriving app we
        // want - only same-node-base strays are candidates. With no same-base stray, fall back to
        // the node-resolved callsign.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var host = HostWith(catalog, new FakeRegistry(new Callsign("G7XXX", 4)));

        host.ResolveServiceCommandCallsign("BBS").Should().Be(new Callsign(NodeCall, 1));
    }

    [Fact]
    public void An_ambiguous_pair_of_strays_falls_back_to_the_resolved_callsign()
    {
        // Two self-deriving apps both bound strays on the node base: which is "BBS"? Unknowable
        // from the callsign-only registry, so the resolver never guesses - it returns the
        // node-resolved callsign (best-effort, the pre-#476 behaviour) rather than dialling the
        // wrong app.
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var host = HostWith(catalog, new FakeRegistry(new Callsign(NodeCall, 4), new Callsign(NodeCall, 5)));

        host.ResolveServiceCommandCallsign("BBS").Should().Be(new Callsign(NodeCall, 1));
    }

    [Fact]
    public void A_registered_callsign_belonging_to_another_resolved_app_is_not_a_stray()
    {
        // Two service apps: BBS resolves to M9YYY-1, CHAT to M9YYY-2 (deterministic walk order by
        // id). CHAT is migrated (bound M9YYY-2); BBS self-derived M9YYY-7. The resolver must not
        // mistake CHAT's bound M9YYY-2 for BBS's stray - M9YYY-2 IS a node-assigned callsign, so
        // only the genuine stray M9YYY-7 is BBS.
        using var bbs = new TempAppPackage("bbs");
        using var chat = new TempAppPackage("chat");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(
            bbs.Discovered(ServiceManifest("bbs", "BBS")),
            chat.Discovered(ServiceManifest("chat", "CHAT")));
        var chatBound = new Callsign(NodeCall, 2);    // CHAT's node-resolved callsign, migrated
        var bbsStray = new Callsign(NodeCall, 7);     // BBS self-derived
        var host = HostWith(catalog, new FakeRegistry(chatBound, bbsStray));

        host.ResolveServiceCommandCallsign("BBS").Should().Be(bbsStray);
        host.ResolveServiceCommandCallsign("CHAT").Should().Be(chatBound);
    }

    [Fact]
    public void An_unknown_verb_resolves_to_nothing()
    {
        using var pkg = new TempAppPackage("bbs");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(ServiceManifest("bbs", "BBS")));
        var host = HostWith(catalog, new FakeRegistry(new Callsign(NodeCall, 4)));

        host.ResolveServiceCommandCallsign("ZZZ").Should().BeNull();
    }
}
