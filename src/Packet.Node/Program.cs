using System.Net;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.Transports;

// The composition root for the Packet.NET node. This IS a Generic Host (the
// WebApplication builder gives us DI, config, hosted services, and logging),
// but slice 1 maps ZERO authenticated endpoints — only GET /healthz. The web
// server is present-but-inert: Kestrel binds from config, and the API / auth /
// UI arrive in later slices.

var configPath = ResolveConfigPath(args);

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<ITransportFactory>(TransportFactory.Instance);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<NodeHostedService>();

// Bind Kestrel from the node config's management.http section.
var http = configProvider.Current.Management.Http;
builder.WebHost.ConfigureKestrel(options =>
{
    var address = IPAddress.TryParse(http.Bind, out var ip) ? ip : IPAddress.Loopback;
    options.Listen(address, http.Port);
});

var app = builder.Build();

// Slice 1: the ONLY mapped endpoint. Unauthenticated liveness probe.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

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

/// <summary>Exposed so the WebApplicationFactory-based host test can boot this
/// exact composition root.</summary>
public partial class Program;
