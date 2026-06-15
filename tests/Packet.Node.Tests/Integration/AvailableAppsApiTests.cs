using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packet.Node.Core.Applications.Catalog;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Audit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the "Available apps" API
/// (<c>docs/app-catalog.md</c> § Surfaces): the catalog left-joined with installed state
/// (<c>GET /api/v1/apps/available</c>) and the one-click install
/// (<c>POST /api/v1/apps/available/{id}/install</c>). The catalog seam (<see cref="IAppCatalog"/>)
/// and the installer seam (<see cref="IAppInstaller"/>) are swapped for fakes — NO live network —
/// while real package discovery runs over a temp <c>appPackageRoots:</c> root so the installed /
/// installed-version / update-available join is exercised end-to-end. Auth is off here (an idle
/// node), so the read/admin gates pass; the auth path itself is covered by the auth suites, and a
/// scope-gate denial is asserted separately by minting a read-scope token.
/// </summary>
[Trait("Category", "Node")]
public sealed class AvailableAppsApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string packagesRoot;

    public AvailableAppsApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-availapi-" + Guid.NewGuid().ToString("N"));
        packagesRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(packagesRoot);

        // dapps: installed, its in-repo manifest says 0.0.1 (lagging the release tag) but the
        // install marker records 0.34.1 == the catalog version → NO update badge (O4).
        WriteInstalledPackage("dapps", manifestVersion: "0.0.1", markerVersion: "0.34.1");

        // bpqchat: installed at the SAME version the catalog pins → no update.
        WriteInstalledPackage("bpqchat", manifestVersion: "0.1.0", markerVersion: "0.1.0");

        // convers is NOT installed (no dir) → installed=false in the join.

        // svc: a pdn-managed, service-bearing package, installed AND enabled (the apps: override
        // below). Updating it must bounce the running daemon onto the new binary, since a version
        // bump leaves the spawn fingerprint unchanged.
        WriteServicePackage("svc");

        // svcoff: the same shape but NOT enabled (no apps: override). Installing/updating it lands
        // the bits and runs nothing — there is no daemon to restart.
        WriteServicePackage("svcoff");

        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            management:
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            apps:
              - id: svc
                enabled: true
            appPackageRoots:
              - {packagesRoot}
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private void WriteInstalledPackage(string id, string manifestVersion, string? markerVersion)
    {
        var pkgDir = Path.Combine(packagesRoot, id);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, AppInstaller.ManifestFileName), $"""
            manifest: 1
            id: {id}
            name: {id}
            version: "{manifestVersion}"
            ui:
              upstream: http://127.0.0.1:59999
            """);
        if (markerVersion is not null)
        {
            File.WriteAllText(Path.Combine(pkgDir, AppInstaller.MarkerFileName),
                $$"""{"id":"{{id}}","source":"catalog","kind":"assets","version":"{{markerVersion}}","installedUtc":"2026-06-13T00:00:00+00:00","payload":["pdn-app.yaml"]}""");
        }
    }

    /// <summary>A discoverable, error-free package declaring a pdn-managed <c>service:</c> block
    /// (never actually spawned — the supervisor is faked). The whole point of the restart-on-update
    /// path: a service-bearing app whose binary must be swapped under a still-running daemon.</summary>
    private void WriteServicePackage(string id)
    {
        var pkgDir = Path.Combine(packagesRoot, id);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, AppInstaller.ManifestFileName), $"""
            manifest: 1
            id: {id}
            name: {id}
            version: "1.0.0"
            capabilities: [network]
            service:
              command: /bin/sh
              args: [run.sh]
              restart: never
            """);
    }

    /// <summary>The catalog the API reads: dapps (assets, installable on x64), bpqchat (deb),
    /// convers (deb, NOT installed), and noarch (assets but with NO binary for any RID → not
    /// installable). Versions are the curated release tags.</summary>
    private static readonly AppCatalogEntry[] CatalogEntries =
    [
        new AppCatalogEntry
        {
            Id = "dapps",
            Name = "DAPPS",
            Version = "0.34.1",
            Description = "Store-and-forward messaging.",
            Icon = "inbox",
            Capabilities = ["network", "web"],
            Homepage = "https://github.com/packet-net/dapps",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = "https://example.invalid/pdn-app.yaml", Sha256 = new string('a', 64) },
                    Binaries = new Dictionary<string, BinaryRef>
                    {
                        ["linux-x64"] = new() { Url = "https://example.invalid/dapps-x64", Sha256 = new string('b', 64), Dest = "dapps", Mode = "0755" },
                        ["linux-arm64"] = new() { Url = "https://example.invalid/dapps-arm64", Sha256 = new string('c', 64), Dest = "dapps", Mode = "0755" },
                        ["linux-arm"] = new() { Url = "https://example.invalid/dapps-arm", Sha256 = new string('d', 64), Dest = "dapps", Mode = "0755" },
                    },
                },
            },
        },
        new AppCatalogEntry
        {
            Id = "bpqchat",
            Name = "BPQ Chat",
            Version = "0.1.0",
            Capabilities = ["network", "web"],
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Deb,
                Deb = new DebArtifact
                {
                    Debs = new Dictionary<string, ArtifactRef>
                    {
                        ["linux-x64"] = new() { Url = "https://example.invalid/bpqchat_amd64.deb", Sha256 = new string('e', 64) },
                        ["linux-arm64"] = new() { Url = "https://example.invalid/bpqchat_arm64.deb", Sha256 = new string('f', 64) },
                        ["linux-arm"] = new() { Url = "https://example.invalid/bpqchat_armhf.deb", Sha256 = new string('1', 64) },
                    },
                },
            },
        },
        new AppCatalogEntry
        {
            Id = "convers",
            Name = "Convers",
            Version = "0.1.2",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Deb,
                Deb = new DebArtifact
                {
                    Debs = new Dictionary<string, ArtifactRef>
                    {
                        ["linux-x64"] = new() { Url = "https://example.invalid/convers_amd64.deb", Sha256 = new string('2', 64) },
                        ["linux-arm64"] = new() { Url = "https://example.invalid/convers_arm64.deb", Sha256 = new string('3', 64) },
                        ["linux-arm"] = new() { Url = "https://example.invalid/convers_armhf.deb", Sha256 = new string('4', 64) },
                    },
                },
            },
        },
        new AppCatalogEntry
        {
            Id = "noarch",
            Name = "No Arch",
            Version = "1.0.0",
            // assets kind but no binaries at all → not installable on any RID.
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = "https://example.invalid/pdn-app.yaml", Sha256 = new string('a', 64) },
                    Binaries = new Dictionary<string, BinaryRef>(),
                },
            },
        },
        // svc / svcoff: pdn-managed, service-bearing apps that exist on disk (svc enabled, svcoff
        // disabled). Catalog version 2.0.0 (an "update" over the installed 1.0.0) — installable on
        // every RID so the test runs on any CI box.
        ServiceCatalogEntry("svc"),
        ServiceCatalogEntry("svcoff"),
    ];

    private static AppCatalogEntry ServiceCatalogEntry(string id) => new()
    {
        Id = id,
        Name = id,
        Version = "2.0.0",
        Capabilities = ["network"],
        Artifact = new ArtifactSpec
        {
            Kind = ArtifactKind.Assets,
            Assets = new AssetsArtifact
            {
                Manifest = new ArtifactRef { Url = "https://example.invalid/pdn-app.yaml", Sha256 = new string('a', 64) },
                Binaries = new Dictionary<string, BinaryRef>
                {
                    ["linux-x64"] = new() { Url = "https://example.invalid/" + id + "-x64", Sha256 = new string('b', 64), Dest = id, Mode = "0755" },
                    ["linux-arm64"] = new() { Url = "https://example.invalid/" + id + "-arm64", Sha256 = new string('c', 64), Dest = id, Mode = "0755" },
                    ["linux-arm"] = new() { Url = "https://example.invalid/" + id + "-arm", Sha256 = new string('d', 64), Dest = id, Mode = "0755" },
                },
            },
        },
    };

    /// <summary>A fixed catalog seam returning <see cref="CatalogEntries"/>.</summary>
    private sealed class FakeCatalog : IAppCatalog
    {
        public IReadOnlyList<AppCatalogEntry> List() => CatalogEntries;
    }

    /// <summary>A controllable installer stand-in: records install calls, returns canned
    /// outcomes, and answers <see cref="GetInstalled"/> from a per-id map (so the version-join
    /// is driven without touching disk). Uninstall/upload are unused here.</summary>
    private sealed class FakeInstaller : IAppInstaller
    {
        public List<string> Installed { get; } = [];
        public InstallOutcome NextOutcome { get; set; } = InstallOutcome.Success("dapps", "0.34.1");
        public Dictionary<string, InstalledApp?> Markers { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dapps"] = new InstalledApp("dapps", "0.34.1", "catalog", "assets"),
            ["bpqchat"] = new InstalledApp("bpqchat", "0.1.0", "catalog", "deb"),
        };

        public Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, string rid, CancellationToken cancellationToken) =>
            InstallFromCatalogAsync(entry, cancellationToken);

        public Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, CancellationToken cancellationToken)
        {
            Installed.Add(entry.Id);
            return Task.FromResult(NextOutcome);
        }

        public Task<InstallOutcome> InstallFromUploadAsync(Stream pdnappTarGz, CancellationToken cancellationToken) =>
            Task.FromResult(InstallOutcome.Failure("(upload)", "unused"));

        public Task<InstallOutcome> UninstallAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(InstallOutcome.Failure(id, "unused"));

        public InstalledApp? GetInstalled(string id) =>
            Markers.TryGetValue(id, out var m) ? m : null;
    }

    /// <summary>A controllable supervisor stand-in: records the ids it was asked to restart and
    /// can be told to refuse a restart (the post-install bounce must swallow that and still report
    /// install success). The real supervisor would actually spawn daemons from the temp root.</summary>
    private sealed class FakeSupervisor : IAppServiceSupervisor
    {
        public List<string> Restarted { get; } = [];
        public Exception? ThrowOnRestart { get; set; }

        public IReadOnlyList<AppServiceStatus> Statuses => [];

        public Task ReconcileAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAsync(string id, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRestart is not null)
            {
                throw ThrowOnRestart;
            }
            Restarted.Add(id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Boots the node with the catalog + installer seams faked, and — when
    /// <paramref name="supervisor"/> is supplied — the supervisor swapped too (so the post-install
    /// restart drives the fake instead of the real one, which would spawn from the temp root). A
    /// null supervisor removes the registration entirely (the degraded-host path the install must
    /// tolerate without failing).</summary>
    private sealed class NodeAppFactory(FakeInstaller installer, FakeSupervisor? supervisor = null, bool removeSupervisor = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAppCatalog>();
                services.AddSingleton<IAppCatalog>(new FakeCatalog());
                services.RemoveAll<IAppInstaller>();
                services.AddSingleton<IAppInstaller>(installer);
                if (supervisor is not null)
                {
                    services.RemoveAll<IAppServiceSupervisor>();
                    services.AddSingleton<IAppServiceSupervisor>(supervisor);
                }
                else if (removeSupervisor)
                {
                    services.RemoveAll<IAppServiceSupervisor>();
                }
            });
        }
    }

    private static JsonElement Entry(JsonElement available, string id)
    {
        foreach (var item in available.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
            {
                return item;
            }
        }
        throw new Xunit.Sdk.XunitException($"no available-app entry with id '{id}'");
    }

    // --- the list + the join -----------------------------------------------------------------

    [Fact]
    public async Task Available_projects_the_catalog_with_installed_version_and_update_flags()
    {
        await using var factory = new NodeAppFactory(new FakeInstaller());
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/api/v1/apps/available");
        var available = JsonDocument.Parse(json).RootElement;

        // dapps: installed, the MARKER's 0.34.1 (not the manifest's lagging 0.0.1) == catalog →
        // no spurious update badge (O4).
        var dapps = Entry(available, "dapps");
        dapps.GetProperty("name").GetString().Should().Be("DAPPS");
        dapps.GetProperty("version").GetString().Should().Be("0.34.1");
        dapps.GetProperty("kind").GetString().Should().Be("assets");
        // The API display-normalises the catalog's `network` declaration to `packet` (the
        // back-compat rename) — the raw catalog entry still says network; the wire says packet.
        dapps.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("packet", "web");
        dapps.GetProperty("installed").GetBoolean().Should().BeTrue();
        dapps.GetProperty("installedVersion").GetString().Should().Be("0.34.1");
        dapps.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
        dapps.GetProperty("installable").GetBoolean().Should().BeTrue();

        // bpqchat: installed at the catalog version → no update; deb kind.
        var bpqchat = Entry(available, "bpqchat");
        bpqchat.GetProperty("kind").GetString().Should().Be("deb");
        bpqchat.GetProperty("installed").GetBoolean().Should().BeTrue();
        bpqchat.GetProperty("installedVersion").GetString().Should().Be("0.1.0");
        bpqchat.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
        bpqchat.GetProperty("installable").GetBoolean().Should().BeTrue();

        // convers: not installed → installed=false, no installedVersion, no update.
        var convers = Entry(available, "convers");
        convers.GetProperty("installed").GetBoolean().Should().BeFalse();
        convers.GetProperty("installedVersion").ValueKind.Should().Be(JsonValueKind.Null);
        convers.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
        convers.GetProperty("installable").GetBoolean().Should().BeTrue();

        // noarch: assets kind with no binary for this RID → installable=false.
        var noarch = Entry(available, "noarch");
        noarch.GetProperty("installable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Available_flags_an_update_when_the_marker_version_differs_from_the_catalog()
    {
        var installer = new FakeInstaller();
        // dapps installed at an OLDER version than the catalog's 0.34.1.
        installer.Markers["dapps"] = new InstalledApp("dapps", "0.30.0", "catalog", "assets");
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        var available = JsonDocument.Parse(await client.GetStringAsync("/api/v1/apps/available")).RootElement;
        var dapps = Entry(available, "dapps");
        dapps.GetProperty("installed").GetBoolean().Should().BeTrue();
        dapps.GetProperty("installedVersion").GetString().Should().Be("0.30.0");
        dapps.GetProperty("updateAvailable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Available_falls_back_to_the_manifest_version_for_a_markerless_package()
    {
        // bpqchat has no marker (hand-sideloaded) — the join uses the discovered manifest's
        // version. The manifest says 0.1.0 == the catalog → still no update.
        var installer = new FakeInstaller();
        installer.Markers["bpqchat"] = null;
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        var available = JsonDocument.Parse(await client.GetStringAsync("/api/v1/apps/available")).RootElement;
        var bpqchat = Entry(available, "bpqchat");
        bpqchat.GetProperty("installed").GetBoolean().Should().BeTrue();
        bpqchat.GetProperty("installedVersion").GetString().Should().Be("0.1.0");
        bpqchat.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
    }

    // --- install -----------------------------------------------------------------------------

    [Fact]
    public async Task Install_of_an_unknown_id_is_404()
    {
        await using var factory = new NodeAppFactory(new FakeInstaller());
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/available/ghost/install", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Install_of_a_not_installable_entry_is_409()
    {
        var installer = new FakeInstaller();
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        // noarch has no binary for this RID — refused before any fetch.
        var resp = await client.PostAsync("/api/v1/apps/available/noarch/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("runtime");
        installer.Installed.Should().BeEmpty();
    }

    [Fact]
    public async Task Install_success_is_200_with_the_ok_outcome()
    {
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("convers", "0.1.2") };
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/convers/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("id").GetString().Should().Be("convers");
        body.GetProperty("version").GetString().Should().Be("0.1.2");
        installer.Installed.Should().Equal("convers");
    }

    [Fact]
    public async Task Install_failure_is_422_with_the_error()
    {
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Failure("convers", "sha256 mismatch") };
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/convers/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should().Contain("mismatch");
    }

    // --- restart-on-update (an enabled, pdn-managed app) -------------------------------------

    [Fact]
    public async Task Update_of_an_enabled_managed_app_restarts_it_so_the_new_binary_runs()
    {
        // svc is on disk, pdn-managed, and enabled (the apps: override). A version bump leaves the
        // spawn fingerprint unchanged, so without this the old process would keep running — the
        // endpoint must drive the supervisor's RestartAsync after the committed install.
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("svc", "2.0.0") };
        var supervisor = new FakeSupervisor();
        await using var factory = new NodeAppFactory(installer, supervisor);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/svc/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("id").GetString().Should().Be("svc");
        body.GetProperty("restarted").GetBoolean().Should().BeTrue();

        installer.Installed.Should().Equal("svc");
        supervisor.Restarted.Should().Equal("svc");
    }

    [Fact]
    public async Task Install_of_a_disabled_managed_app_does_not_restart_anything()
    {
        // svcoff is on disk and pdn-managed but NOT enabled (no apps: override) → install lands the
        // bits and runs nothing, so there is no daemon to bounce. restarted must be false.
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("svcoff", "2.0.0") };
        var supervisor = new FakeSupervisor();
        await using var factory = new NodeAppFactory(installer, supervisor);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/svcoff/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("restarted").GetBoolean().Should().BeFalse();

        installer.Installed.Should().Equal("svcoff");
        supervisor.Restarted.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_of_an_enabled_non_service_app_does_not_restart()
    {
        // dapps is enabled in neither config nor — more to the point — has no service: block, so
        // there is no pdn-managed daemon. Updating it must not attempt a restart.
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("dapps", "0.35.0") };
        var supervisor = new FakeSupervisor();
        await using var factory = new NodeAppFactory(installer, supervisor);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/dapps/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .GetProperty("restarted").GetBoolean().Should().BeFalse();
        supervisor.Restarted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_restart_failure_does_not_demote_a_committed_install_to_a_failure()
    {
        // The payload is already committed when the restart runs — a supervisor refusal (or any
        // throw) must stay a 200 install success with restarted=false, never a 422.
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("svc", "2.0.0") };
        var supervisor = new FakeSupervisor
        {
            ThrowOnRestart = new InvalidOperationException("service 'svc' is not in the desired set"),
        };
        await using var factory = new NodeAppFactory(installer, supervisor);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/svc/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("restarted").GetBoolean().Should().BeFalse();
        installer.Installed.Should().Equal("svc");

        // The failed restart is audited (error outcome) — the install audit line stays too.
        var audit = factory.Services.GetRequiredService<IAuditLog>().Recent(50);
        audit.Should().Contain(e => e.Action == "install_app" && e.Target == "svc");
        audit.Should().Contain(e => e.Action == "restart_app" && e.Target == "svc" && e.Outcome == "error");
    }

    [Fact]
    public async Task An_update_with_no_supervisor_wired_still_succeeds_without_a_restart()
    {
        // A degraded boot with no supervisor: the install stands (the new binary runs on the next
        // reconcile). restarted=false, status still 200.
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("svc", "2.0.0") };
        await using var factory = new NodeAppFactory(installer, supervisor: null, removeSupervisor: true);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/svc/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .GetProperty("restarted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Update_of_an_enabled_managed_app_records_a_restart_audit_entry()
    {
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("svc", "2.0.0") };
        var supervisor = new FakeSupervisor();
        await using var factory = new NodeAppFactory(installer, supervisor);
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/available/svc/install", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Services.GetRequiredService<IAuditLog>().Recent(50)
            .Should().Contain(e => e.Action == "restart_app" && e.Target == "svc" && e.Outcome == "ok");
    }

    // --- the admin gate (auth ON) ------------------------------------------------------------

    [Fact]
    public async Task Install_requires_admin_when_auth_is_on()
    {
        // Rewrite the config with auth ON for this boot. A tokenless install hits the admin gate
        // (401) and the installer never runs.
        File.WriteAllText(configPath, $"""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            management:
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
              auth:
                enabled: true
            appPackageRoots:
              - {packagesRoot}
            """);
        var installer = new FakeInstaller();
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/available/dapps/install", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        installer.Installed.Should().BeEmpty();
    }

    // --- audit -------------------------------------------------------------------------------

    [Fact]
    public async Task Install_records_an_audit_entry()
    {
        var installer = new FakeInstaller { NextOutcome = InstallOutcome.Success("convers", "0.1.2") };
        await using var factory = new NodeAppFactory(installer);
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/available/convers/install", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = factory.Services.GetRequiredService<IAuditLog>();
        audit.Recent(50).Should().Contain(e => e.Action == "install_app" && e.Target == "convers");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
