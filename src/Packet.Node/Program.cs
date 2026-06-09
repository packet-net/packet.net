using System.Net;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;

// The composition root for the Packet.NET node. This IS a Generic Host (the
// WebApplication builder gives us DI, config, hosted services, and logging),
// but slice 1 maps ZERO authenticated endpoints — only GET /healthz. The web
// server is present-but-inert: Kestrel binds from config, and the API / auth /
// UI arrive in later slices.

var configPath = ResolveConfigPath(args);
var dbPath = ResolveDbPath(args);

// ContentRoot = the app's own directory (where the published web UI's wwwroot
// sits), NOT the working directory. The packaged node runs with a WorkingDirectory
// of the writable StateDirectory (/var/lib/packetnet) while the binary + wwwroot
// live in /opt/packetnet/app, so defaulting ContentRoot to the CWD would make
// UseStaticFiles look in the wrong place. (Config/DB paths still resolve against
// the CWD by design — see ResolveConfigPath/ResolveDbPath.)
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Build the config provider eagerly: it writes the first-start template if the
// file is absent and gives us the HTTP bind to hand to Kestrel before the host
// starts. Registered as the singleton IConfigProvider so the hosted service
// reuses this very instance (a single source of truth + a single file watcher).
// The eager provider logs to the bootstrap console; once the host is up,
// NodeHostedService logs through the configured pipeline.
using var bootstrapLoggers = LoggerFactory.Create(b => b.AddConsole());
var configProvider = new FileConfigProvider(
    configPath,
    TimeProvider.System,
    bootstrapLoggers.CreateLogger<FileConfigProvider>());
builder.Services.AddSingleton<IConfigProvider>(configProvider);

// The routing-table persistence store (pdn.db). Created eagerly so it can hydrate
// NetRomService at start; registered as the singleton INetRomRoutingStore the hosted
// service injects. A store fault degrades to in-memory — it never fails the node.
var routingStore = new SqliteNetRomRoutingStore(
    dbPath,
    bootstrapLoggers.CreateLogger<SqliteNetRomRoutingStore>());
builder.Services.AddSingleton<INetRomRoutingStore>(routingStore);

builder.Services.AddSingleton<ITransportFactory>(TransportFactory.Instance);
builder.Services.AddSingleton(TimeProvider.System);
// Register the hosted service as a singleton AND as the hosted service, so the
// control-API endpoint handlers can inject it and read its live Supervisor /
// NetRom handles (the read API projects the node's state from these).
builder.Services.AddSingleton<NodeHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeHostedService>());

// Bind Kestrel from the node config's management.http section.
var http = configProvider.Current.Management.Http;
builder.WebHost.ConfigureKestrel(options =>
{
    var address = IPAddress.TryParse(http.Bind, out var ip) ? ip : IPAddress.Loopback;
    options.Listen(address, http.Port);
});

var app = builder.Build();

// Unauthenticated liveness probe.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Slice 3 control API (read endpoints). Mapped BEFORE the SPA fallback so /api/*
// and /healthz win; everything else falls through to index.html for the React
// client router. (Auth is a later step — these are read-only and the node binds
// 127.0.0.1 by default. The live SSE feed for the monitor is step 1b.)
app.MapPdnReadApi();

// Slice 3 step 1b: the live SSE frame feed the web monitor's EventSource
// consumes (GET /api/v1/events). Mapped after the read API and before the
// catch-all; the specific route wins over /api/{**rest} regardless of order.
app.MapPdnEvents();

// An unknown /api/* path returns 404 — it must NOT fall through to the SPA
// index.html below (the catch-all is less specific than the real /api/v1/*
// routes, so those still win).
app.Map("/api/{**rest}", () => Results.NotFound());

// Serve the built web UI (web/packetnet-ui → wwwroot) + SPA client-side routing:
// any other unmatched, non-file route returns index.html so the React router
// can handle it (deep links like /monitor, /ports).
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

static string ResolveConfigPath(string[] args)
{
    // --config <path> wins, then PACKETNET_CONFIG, then a sensible default in the
    // working directory.
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--config" or "-c")
        {
            return args[i + 1];
        }
    }
    var env = Environment.GetEnvironmentVariable("PACKETNET_CONFIG");
    return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Directory.GetCurrentDirectory(), "packetnet.yaml");
}

static string ResolveDbPath(string[] args)
{
    // --db <path> wins, then PACKETNET_DB, then pdn.db in the working directory —
    // which on the packaged node is the writable StateDirectory (/var/lib/packetnet).
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--db")
        {
            return args[i + 1];
        }
    }
    var env = Environment.GetEnvironmentVariable("PACKETNET_DB");
    return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Directory.GetCurrentDirectory(), "pdn.db");
}

/// <summary>Exposed so the WebApplicationFactory-based host test can boot this
/// exact composition root.</summary>
public partial class Program;
