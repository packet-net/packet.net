using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// The <c>pdn-app/1</c> socket rung (<see cref="SocketApplication"/>). A stub Unix-domain socket
/// server stands in for a long-running app daemon: it echoes whatever the node writes (the
/// connect header, then each assembled user line) straight back, so we can assert the header
/// format, the newline translation, and teardown without a bespoke daemon — the socket analogue
/// of the <c>/bin/cat</c> process-bridge test. Linux-only (Unix sockets; CI is Linux).
/// </summary>
[Trait("Category", "Node")]
public sealed class SocketApplicationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly string socketPath;

    public SocketApplicationTests()
        => socketPath = TestPaths.NewPath("pdn-sock", ".sock");

    public void Dispose()
    {
        try { File.Delete(socketPath); } catch { /* best effort */ }
    }

    // A one-connection echo daemon on the Unix socket: accept, then mirror bytes until EOF.
    private (Socket listener, Task serve) StartEchoDaemon()
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);
        var serve = Task.Run(async () =>
        {
            using var conn = await listener.AcceptAsync();
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = await conn.ReceiveAsync(buf); } catch { break; }
                if (n == 0)
                {
                    break;   // node closed the connection (user gone)
                }

                await conn.SendAsync(buf.AsMemory(0, n));
            }
        });
        return (listener, serve);
    }

    private SocketApplication App() => new(
        new ApplicationConfig { Id = "lobby", Command = "LOBBY", Kind = ApplicationKind.Socket, SocketPath = socketPath },
        NullLogger.Instance);

    private static NodeAppContext Ctx() => new()
    {
        Callsign = "M0LTE-7",
        Transport = NodeTransportKind.Ax25,
        PortId = "gb7rdg",
        Args = ["hi"],
    };

    [SkippableFact]
    public async Task Writes_the_connect_header_then_bridges_user_lines_over_the_socket()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the app bridge needs a Unix domain socket");

        var (listener, serve) = StartEchoDaemon();
        using (listener)
        {
            var conn = new DriveableConnection("M0LTE-7", NodeTransportKind.Ax25);
            var run = App().RunAsync(conn, Ctx());

            await Wait.ForAsync(() => conn.Output.Contains("pdn-app: 1"), "header echoed back");
            await Wait.ForAsync(() => conn.Output.Contains("callsign: M0LTE-7"), "callsign header");
            await Wait.ForAsync(() => conn.Output.Contains("args: hi"), "args header");

            conn.Inject("hello lobby\r");
            await Wait.ForAsync(() => conn.Output.Contains("hello lobby"), "user line echoed back");
            conn.Output.Should().NotContain("\n");      // every \n translated to CR for AX.25
            conn.Output.Should().Contain("\r");

            conn.Drop();                  // user disconnects → bridge closes the socket → daemon EOF
            await run.WaitAsync(Timeout); // RunAsync returns
            await serve.WaitAsync(Timeout);
        }
    }

    [SkippableFact]
    public async Task A_daemon_that_is_not_listening_throws_ApplicationStartException()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "the app bridge needs a Unix domain socket");

        // No daemon bound at socketPath → connect refused.
        var conn = new DriveableConnection("M0LTE-7", NodeTransportKind.Ax25);
        var act = () => App().RunAsync(conn, Ctx());
        await act.Should().ThrowAsync<ApplicationStartException>();
    }

    [Fact]
    public void Constructing_without_a_socket_path_throws()
    {
        var act = () =>
            new SocketApplication(new ApplicationConfig { Id = "x", Command = "X", Kind = ApplicationKind.Socket, SocketPath = null });
        act.Should().Throw<ArgumentException>();
    }
}
