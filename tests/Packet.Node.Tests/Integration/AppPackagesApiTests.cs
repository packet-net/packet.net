using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packet.Node.Core.Applications.Catalog;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Audit;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root and exercises the app-packages
/// management API (<c>docs/app-packages.md</c> § Surfaces): the inventory
/// (<c>GET /api/v1/apps/packages</c>) over a temp package root (the
/// <c>appPackageRoots:</c> override) holding healthy, service-bearing, external, and broken
/// packages plus an inline <c>applications:</c> entry; the enable/disable trust toggle
/// persisting through the live <c>FileConfigProvider</c> write seam; and the restart action.
/// The supervisor seam is swapped for a fake via <c>ConfigureTestServices</c> (the real one
/// would actually spawn daemons from the temp root) — or stripped entirely for the 503 path.
/// Auth is off here (an idle node), so the read/admin gates pass — the auth path itself is
/// covered by the auth suites.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppPackagesApiTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string packagesRoot;

    public AppPackagesApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-pkgapi-" + Guid.NewGuid().ToString("N"));
        packagesRoot = Path.Combine(dir, "apps");
        Directory.CreateDirectory(packagesRoot);

        // alpha: a healthy disabled package — ui only, no service.
        WriteManifest("alpha", """
            manifest: 1
            id: alpha
            name: Alpha
            version: "1.2.3"
            description: A test app.
            icon: rocket
            capabilities: [web]
            ui:
              upstream: http://127.0.0.1:59999
            """);

        // svc: a healthy package with a pdn-managed service (never spawned here — the
        // supervisor is faked / the package stays disabled).
        WriteManifest("svc", """
            manifest: 1
            id: svc
            name: Svc
            capabilities: [session, network]
            service:
              command: /bin/sh
              args: [run.sh]
            """);

        // ext: an owner-managed daemon — pdn reports it as External, never tracks health.
        WriteManifest("ext", """
            manifest: 1
            id: ext
            service:
              command: /usr/bin/somedaemon
              managed: external
            """);

        // broken: the manifest id contradicts the directory name → an Error entry.
        WriteManifest("broken", """
            manifest: 1
            id: mismatch
            ui:
              upstream: http://127.0.0.1:1
            """);

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
            applications:
              - id: wall
                match: WALL
                command: /bin/cat
                capabilities: [session]
            appPackageRoots:
              - {packagesRoot}
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private void WriteManifest(string id, string yaml)
    {
        var pkgDir = Path.Combine(packagesRoot, id);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, AppPackageCatalog.ManifestFileName), yaml);
    }

    /// <summary>A controllable supervisor stand-in: canned <see cref="Statuses"/>, recorded
    /// <see cref="Restarted"/> ids, optional restart refusal.</summary>
    private sealed class FakeSupervisor : IAppServiceSupervisor
    {
        public List<AppServiceStatus> StatusList { get; } = [];
        public List<string> Restarted { get; } = [];
        public InvalidOperationException? ThrowOnRestart { get; set; }

        public IReadOnlyList<AppServiceStatus> Statuses => StatusList;

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

    /// <summary>A controllable installer stand-in for the uninstall/upload endpoints: records
    /// the calls and returns canned outcomes (the real installer touches disk + the network).</summary>
    private sealed class FakeInstaller : IAppInstaller
    {
        public List<string> Uninstalled { get; } = [];
        public int Uploads { get; private set; }
        public InstallOutcome UninstallOutcome { get; set; } = InstallOutcome.Success("(unset)", null);
        public InstallOutcome UploadOutcome { get; set; } = InstallOutcome.Success("(unset)", null);

        public Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, string rid, CancellationToken cancellationToken) =>
            Task.FromResult(InstallOutcome.Failure(entry.Id, "unused"));

        public Task<InstallOutcome> InstallFromCatalogAsync(AppCatalogEntry entry, CancellationToken cancellationToken) =>
            Task.FromResult(InstallOutcome.Failure(entry.Id, "unused"));

        public Task<InstallOutcome> InstallFromUploadAsync(Stream pdnappTarGz, CancellationToken cancellationToken)
        {
            Uploads++;
            return Task.FromResult(UploadOutcome);
        }

        public Task<InstallOutcome> UninstallAsync(string id, CancellationToken cancellationToken)
        {
            Uninstalled.Add(id);
            return Task.FromResult(UninstallOutcome.Id == "(unset)" ? InstallOutcome.Success(id, null) : UninstallOutcome);
        }

        public InstalledApp? GetInstalled(string id) => null;
    }

    /// <summary>Boots the node with the REAL supervisor registration replaced by
    /// <paramref name="supervisor"/> — or removed entirely when null (the degraded host the
    /// 503 path covers). Replacing matters: the real supervisor would actually spawn any
    /// enabled service from the temp package root. An optional <paramref name="installer"/>
    /// swaps the catalog installer too (the uninstall/upload endpoints drive it).</summary>
    private sealed class NodeAppFactory(FakeSupervisor? supervisor, FakeInstaller? installer = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAppServiceSupervisor>();
                if (supervisor is not null)
                {
                    services.AddSingleton<IAppServiceSupervisor>(supervisor);
                }
                if (installer is not null)
                {
                    services.RemoveAll<IAppInstaller>();
                    services.AddSingleton<IAppInstaller>(installer);
                }
            });
        }
    }

    private static async Task<JsonElement> GetInventoryAsync(HttpClient client)
    {
        var json = await client.GetStringAsync("/api/v1/apps/packages");
        return JsonDocument.Parse(json).RootElement;
    }

    private static JsonElement Entry(JsonElement inventory, string id)
    {
        foreach (var item in inventory.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
            {
                return item;
            }
        }
        throw new Xunit.Sdk.XunitException($"no inventory entry with id '{id}'");
    }

    // --- the inventory -------------------------------------------------------------------

    [Fact]
    public async Task Inventory_lists_discovered_packages_and_inline_entries_with_their_shape()
    {
        var fake = new FakeSupervisor();
        fake.StatusList.Add(new AppServiceStatus("svc", AppServiceState.Running, Pid: 4321));
        await using var factory = new NodeAppFactory(fake);
        using var client = factory.CreateClient();

        var inventory = await GetInventoryAsync(client);

        // alpha: the healthy ui-only package, disabled by default (no apps: entry = no trust).
        var alpha = Entry(inventory, "alpha");
        alpha.GetProperty("name").GetString().Should().Be("Alpha");
        alpha.GetProperty("version").GetString().Should().Be("1.2.3");
        alpha.GetProperty("description").GetString().Should().Be("A test app.");
        alpha.GetProperty("icon").GetString().Should().Be("rocket");
        alpha.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("web");
        alpha.GetProperty("enabled").GetBoolean().Should().BeFalse();
        alpha.GetProperty("source").GetString().Should().Be("package");
        alpha.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        alpha.GetProperty("service").GetString().Should().Be("none");
        alpha.GetProperty("state").ValueKind.Should().Be(JsonValueKind.Null);

        // svc: a managed service whose state comes from the supervisor's status snapshot.
        var svc = Entry(inventory, "svc");
        svc.GetProperty("service").GetString().Should().Be("managed");
        svc.GetProperty("state").GetString().Should().Be("Running");
        svc.GetProperty("pid").GetInt32().Should().Be(4321);

        // ext: owner-managed — the state IS External, pdn never guesses at health.
        var ext = Entry(inventory, "ext");
        ext.GetProperty("service").GetString().Should().Be("external");
        ext.GetProperty("state").GetString().Should().Be("External");

        // broken: surfaced (not hidden) with the validation problem, never enabled.
        var broken = Entry(inventory, "broken");
        broken.GetProperty("error").GetString().Should().Contain("must equal the package directory name");
        broken.GetProperty("enabled").GetBoolean().Should().BeFalse();
        broken.GetProperty("source").GetString().Should().Be("package");

        // wall: the inline applications: entry, present in the same inventory.
        var wall = Entry(inventory, "wall");
        wall.GetProperty("source").GetString().Should().Be("inline");
        wall.GetProperty("enabled").GetBoolean().Should().BeTrue();
        wall.GetProperty("service").GetString().Should().Be("none");
        wall.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())
            .Should().Equal("session");
    }

    // --- the trust toggle ----------------------------------------------------------------

    [Fact]
    public async Task Enable_persists_the_override_through_the_config_write_seam_and_flips_the_next_get()
    {
        await using var factory = new NodeAppFactory(new FakeSupervisor());
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/alpha/enable", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("id").GetString().Should().Be("alpha");
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();

        // The toggle is a config write: the YAML on disk now carries the apps: override.
        var yaml = await File.ReadAllTextAsync(configPath);
        yaml.Should().Contain("apps:").And.Contain("alpha");

        // And the next inventory read reflects the flip.
        var alpha = Entry(await GetInventoryAsync(client), "alpha");
        alpha.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Disable_flips_an_existing_override_back_off()
    {
        await using var factory = new NodeAppFactory(new FakeSupervisor());
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/packages/alpha/enable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsync("/api/v1/apps/packages/alpha/disable", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("enabled").GetBoolean().Should().BeFalse();

        var alpha = Entry(await GetInventoryAsync(client), "alpha");
        alpha.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Enable_of_a_broken_package_is_409_with_the_error_text()
    {
        await using var factory = new NodeAppFactory(new FakeSupervisor());
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/broken/enable", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("must equal the package directory name");

        // The refused grant must not have written anything.
        var broken = Entry(await GetInventoryAsync(client), "broken");
        broken.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Enable_of_an_unknown_id_is_404_and_an_inline_id_is_404_too()
    {
        await using var factory = new NodeAppFactory(new FakeSupervisor());
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/packages/ghost/enable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // An inline applications: entry is config-authored — the apps: override surface
        // does not govern it (toggling it is a config edit, not an override write).
        (await client.PostAsync("/api/v1/apps/packages/wall/enable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- restart -------------------------------------------------------------------------

    [Fact]
    public async Task Restart_without_a_supervisor_is_503()
    {
        await using var factory = new NodeAppFactory(supervisor: null);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/svc/restart", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("supervisor");
    }

    [Fact]
    public async Task Restart_drives_the_supervisor_and_returns_the_fresh_entry()
    {
        var fake = new FakeSupervisor();
        fake.StatusList.Add(new AppServiceStatus("svc", AppServiceState.Running, Pid: 99));
        await using var factory = new NodeAppFactory(fake);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/svc/restart", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Restarted.Should().Equal("svc");

        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("id").GetString().Should().Be("svc");
        body.GetProperty("service").GetString().Should().Be("managed");
        body.GetProperty("state").GetString().Should().Be("Running");
    }

    [Fact]
    public async Task Restart_of_an_unknown_id_is_404()
    {
        await using var factory = new NodeAppFactory(new FakeSupervisor());
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/packages/ghost/restart", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Restart_of_a_package_without_a_pdn_managed_service_is_409()
    {
        var fake = new FakeSupervisor();
        await using var factory = new NodeAppFactory(fake);
        using var client = factory.CreateClient();

        // alpha has no service: block at all; ext's daemon is owner-managed.
        foreach (var id in new[] { "alpha", "ext" })
        {
            var resp = await client.PostAsync($"/api/v1/apps/packages/{id}/restart", content: null);
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("error").GetString().Should().Contain(id);
        }
        fake.Restarted.Should().BeEmpty();
    }

    [Fact]
    public async Task Restart_the_supervisor_refuses_is_409_with_its_message()
    {
        var fake = new FakeSupervisor
        {
            ThrowOnRestart = new InvalidOperationException("service 'svc' is not in the desired set"),
        };
        await using var factory = new NodeAppFactory(fake);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/svc/restart", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("not in the desired set");
    }

    // --- uninstall -----------------------------------------------------------------------

    [Fact]
    public async Task Uninstall_of_an_enabled_package_is_409_disable_first()
    {
        var installer = new FakeInstaller();
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        // Grant trust first, then try to uninstall the running app.
        (await client.PostAsync("/api/v1/apps/packages/alpha/enable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsync("/api/v1/apps/packages/alpha/uninstall", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("Disable the app before uninstalling");

        // Nothing was deleted, and the override survives.
        installer.Uninstalled.Should().BeEmpty();
        Entry(await GetInventoryAsync(client), "alpha").GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_of_a_disabled_package_is_200_and_strips_the_override()
    {
        var installer = new FakeInstaller { UninstallOutcome = InstallOutcome.Success("alpha", "1.2.3") };
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        // Enable then disable so a (disabled) apps: override exists to be stripped.
        (await client.PostAsync("/api/v1/apps/packages/alpha/enable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/v1/apps/packages/alpha/disable", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.PostAsync("/api/v1/apps/packages/alpha/uninstall", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("id").GetString().Should().Be("alpha");

        installer.Uninstalled.Should().Equal("alpha");

        // The override was stripped from the persisted config (so a reinstall starts fresh).
        var yaml = await File.ReadAllTextAsync(configPath);
        yaml.Should().NotContain("- id: alpha");
    }

    [Fact]
    public async Task Uninstall_of_an_unknown_id_is_404()
    {
        var installer = new FakeInstaller();
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/packages/ghost/uninstall", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        installer.Uninstalled.Should().BeEmpty();
    }

    [Fact]
    public async Task Uninstall_of_a_markerless_package_is_409_with_the_installer_refusal()
    {
        // The installer refuses a hand-sideloaded dir (no marker) — the API maps that to 409.
        var installer = new FakeInstaller
        {
            UninstallOutcome = InstallOutcome.Failure("alpha", "no install marker — sideloaded by hand."),
        };
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/apps/packages/alpha/uninstall", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("error").GetString().Should().Contain("no install marker");
        installer.Uninstalled.Should().Equal("alpha");
    }

    [Fact]
    public async Task Uninstall_records_an_audit_entry()
    {
        var installer = new FakeInstaller();
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        (await client.PostAsync("/api/v1/apps/packages/alpha/uninstall", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Services.GetRequiredService<IAuditLog>().Recent(50)
            .Should().Contain(e => e.Action == "uninstall_app" && e.Target == "alpha");
    }

    // --- upload --------------------------------------------------------------------------

    private static MultipartFormDataContent PdnappUpload(string fileName = "app.pdnapp")
    {
        var content = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(Encoding.ASCII.GetBytes("not-a-real-tarball-the-installer-is-faked"));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(bytes, "file", fileName);
        return content;
    }

    [Fact]
    public async Task Upload_success_is_200_with_the_ok_outcome()
    {
        var installer = new FakeInstaller { UploadOutcome = InstallOutcome.Success("uploaded", "9.9.9") };
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        using var content = PdnappUpload();
        var resp = await client.PostAsync("/api/v1/apps/packages/upload", content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeTrue();
        body.GetProperty("id").GetString().Should().Be("uploaded");
        installer.Uploads.Should().Be(1);
    }

    [Fact]
    public async Task Upload_failure_is_422_with_the_error()
    {
        var installer = new FakeInstaller
        {
            UploadOutcome = InstallOutcome.Failure("(upload)", "the uploaded .pdnapp has no pdn-app.yaml at its root."),
        };
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        using var content = PdnappUpload();
        var resp = await client.PostAsync("/api/v1/apps/packages/upload", content);
        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.GetProperty("error").GetString().Should().Contain("pdn-app.yaml");
    }

    [Fact]
    public async Task Upload_requires_admin_when_auth_is_on()
    {
        // Rewrite the config with auth ON for this boot: a tokenless upload hits the admin gate.
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
        await using var factory = new NodeAppFactory(new FakeSupervisor(), installer);
        using var client = factory.CreateClient();

        using var content = PdnappUpload();
        var resp = await client.PostAsync("/api/v1/apps/packages/upload", content);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        installer.Uploads.Should().Be(0);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
