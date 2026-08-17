using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// The cross-language proof of the socket rung: the C# <see cref="SocketApplication"/> bridge
/// driving the <b>actual shipped</b> Python LOBBY daemon (<c>examples/lobby/lobby.py</c>) over a
/// Unix socket, with <b>two</b> bridged sessions at once — and a message one user broadcasts
/// reaches the other. That shared-in-memory-state-across-users behaviour is the entire reason the
/// rung exists (the spawn-per-connect floor can't do it). Linux + python3 only (CI is Linux).
/// </summary>
[Trait("Category", "Node")]
public sealed class LobbyAppIntegrationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string socketPath;
    private Process? daemon;

    public LobbyAppIntegrationTests()
        => socketPath = TestPaths.NewPath("pdn-lobby", ".sock");

    public void Dispose()
    {
        try { if (daemon is { HasExited: false }) { daemon.Kill(entireProcessTree: true); } } catch { }
        try { daemon?.Dispose(); } catch { }
        try { File.Delete(socketPath); } catch { }
    }

    [SkippableFact]
    public async Task Two_users_share_state_a_broadcast_from_one_reaches_the_other()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the app fixture is a Linux python3 script");
        var python = FindPython();
        Skip.If(python is null, "python3 not installed");

        var lobbyPy = Path.Combine(RepoRoot(), "examples", "lobby", "lobby.py");
        File.Exists(lobbyPy).Should().BeTrue("LOBBY daemon not found at {0}", lobbyPy);

        // Start the long-running daemon and wait for it to bind the socket.
        daemon = Process.Start(new ProcessStartInfo(python, $"\"{lobbyPy}\" \"{socketPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        })!;
        await Wait.ForAsync(() => File.Exists(socketPath), "LOBBY bound its socket");

        SocketApplication App() => new(
            new ApplicationConfig { Id = "lobby", Command = "LOBBY", Kind = ApplicationKind.Socket, SocketPath = socketPath },
            NullLogger.Instance);

        // Two users connect — two separate Unix-socket connections to the one daemon.
        var alice = new DriveableConnection("M0LTE-7", NodeTransportKind.Ax25);
        var bob = new DriveableConnection("G0ABC-1", NodeTransportKind.Ax25);
        var runAlice = App().RunAsync(alice, Ctx("M0LTE-7"));
        var runBob = App().RunAsync(bob, Ctx("G0ABC-1"));

        await Wait.ForAsync(() => alice.Output.Contains("LOBBY") && bob.Output.Contains("LOBBY"), "both got the banner");

        // Alice broadcasts; Bob — a *different* session/connection — receives it. Only possible
        // because the daemon holds both connections and shares state across them.
        alice.Inject("SAY hello from alice\r");
        await Wait.ForAsync(() => bob.Output.Contains("hello from alice"), "bob received alice's broadcast");
        bob.Output.Should().Contain("M0LTE-7");   // attributed to alice

        // Alice's WHO sees both users (shared presence).
        alice.Inject("WHO\r");
        await Wait.ForAsync(() => alice.Output.Contains("G0ABC-1"), "alice's WHO lists bob");

        alice.Drop();
        bob.Drop();
        await runAlice.WaitAsync(Timeout);
        await runBob.WaitAsync(Timeout);
    }

    private static NodeAppContext Ctx(string callsign) => new()
    {
        Callsign = callsign,
        Transport = NodeTransportKind.Ax25,
        PortId = "gb7rdg",
    };

    private static string? FindPython() =>
        new[] { "/usr/bin/python3", "/usr/local/bin/python3", "/bin/python3" }.FirstOrDefault(File.Exists);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "examples", "lobby")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate the repo root (no examples/lobby above the test assembly).");
    }
}
