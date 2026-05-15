using System.IO.Pipelines;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Agw.Tests;

/// <summary>
/// End-to-end behaviour of <see cref="AgwClient"/> against an
/// in-memory paired-pipe stub server. Tests run without TCP — the
/// stub plays the role of LinBPQ / direwolf at the protocol level
/// (read frames, emit canned responses).
/// </summary>
public class AgwClientInMemoryTests
{
    [Fact]
    public async Task RegisterCallsign_completes_when_server_acks()
    {
        await using var pair = new InMemoryAgwPair();

        // Server: read the X frame and reply with an X-ack containing
        // a single status byte (BPQ uses 0x01).
        var serverTask = Task.Run(async () =>
        {
            var first = await pair.ServerReadFrame();
            first.Kind.Should().Be(AgwCommandKind.RegisterCallsign);
            first.From.Should().Be("M0LTE");
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0,
                Kind: AgwCommandKind.RegisterCallsign,
                Pid: 0,
                From: "M0LTE",
                To: "",
                Data: new byte[] { 0x01 }));
        });

        await pair.Client.RegisterCallsignAsync("M0LTE");
        await serverTask;
    }

    [Fact]
    public async Task OpenSessionAsync_returns_when_server_acks_connect()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            // Client sends X first if it registered; for this test the
            // caller skips registration and goes straight to connect.
            var c = await pair.ServerReadFrame();
            c.Kind.Should().Be(AgwCommandKind.Connect);
            c.From.Should().Be("M0LTE");
            c.To.Should().Be("PN0TST");

            // Reply with the server's 'C' ack — From=remote, To=us
            // (server-side convention).
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0,
                Kind: AgwCommandKind.Connect,
                Pid: 0,
                From: "PN0TST",
                To: "M0LTE",
                Data: Encoding.ASCII.GetBytes("CONNECTED To Station PN0TST\r")));
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await serverTask;
        session.From.Should().Be("M0LTE");
        session.To.Should().Be("PN0TST");
    }

    [Fact]
    public async Task Session_write_sends_a_data_frame_with_correct_callsigns_and_pid()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();    // 'C' connect
            await pair.ServerWriteFrame(MakeConnectAck());
            var dataFrame = await pair.ServerReadFrame();
            dataFrame.Kind.Should().Be(AgwCommandKind.Data);
            dataFrame.From.Should().Be("M0LTE");
            dataFrame.To.Should().Be("PN0TST");
            dataFrame.Pid.Should().Be(0xF0);
            Encoding.ASCII.GetString(dataFrame.Data.Span).Should().Be("ports\r");
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await session.WriteAsync(Encoding.ASCII.GetBytes("ports\r"));
        await serverTask;
    }

    [Fact]
    public async Task Session_read_drains_server_data_frames_concatenated()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();    // 'C' connect
            await pair.ServerWriteFrame(MakeConnectAck());
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0, Kind: AgwCommandKind.Data, Pid: 0xF0,
                From: "PN0TST", To: "M0LTE",
                Data: Encoding.ASCII.GetBytes("Welcome to PN0TST\r")));
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0, Kind: AgwCommandKind.Data, Pid: 0xF0,
                From: "PN0TST", To: "M0LTE",
                Data: Encoding.ASCII.GetBytes("CMD: ")));
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        var buffer = new byte[200];
        var totalRead = 0;
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (totalRead < 23) // banner + prompt
        {
            int n = await session.ReadAsync(buffer.AsMemory(totalRead), readCts.Token);
            if (n == 0) break;
            totalRead += n;
        }
        await serverTask;

        Encoding.ASCII.GetString(buffer, 0, totalRead).Should().Be("Welcome to PN0TST\rCMD: ");
    }

    [Fact]
    public async Task Session_read_returns_zero_when_server_disconnects()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();
            await pair.ServerWriteFrame(MakeConnectAck());
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0, Kind: AgwCommandKind.Disconnect, Pid: 0,
                From: "PN0TST", To: "M0LTE", Data: ReadOnlyMemory<byte>.Empty));
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await serverTask;

        var buf = new byte[10];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int n = await session.ReadAsync(buf.AsMemory(), cts.Token);
        n.Should().Be(0, "server-initiated disconnect surfaces as EOF on the next read");
    }

    [Fact]
    public async Task Session_disconnect_sends_d_frame_and_marks_session_disconnected()
    {
        await using var pair = new InMemoryAgwPair();

        var receivedFrames = new List<AgwFrame>();
        var serverTask = Task.Run(async () =>
        {
            receivedFrames.Add(await pair.ServerReadFrame());   // 'C'
            await pair.ServerWriteFrame(MakeConnectAck());
            receivedFrames.Add(await pair.ServerReadFrame());   // 'd'
        });

        var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await session.DisconnectAsync();
        await serverTask;

        receivedFrames[1].Kind.Should().Be(AgwCommandKind.Disconnect);
        session.DisconnectedTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Session_writes_split_payload_larger_than_paclen_into_chunks()
    {
        await using var pair = new InMemoryAgwPair();

        var dataFrames = new List<AgwFrame>();
        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();    // 'C'
            await pair.ServerWriteFrame(MakeConnectAck());
            // Expect 3 data frames for a 600-byte send at 256-byte chunk.
            for (int i = 0; i < 3; i++)
            {
                dataFrames.Add(await pair.ServerReadFrame());
            }
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await session.WriteAsync(new byte[600]);
        await serverTask;

        dataFrames.Should().HaveCount(3);
        dataFrames[0].Data.Length.Should().Be(256);
        dataFrames[1].Data.Length.Should().Be(256);
        dataFrames[2].Data.Length.Should().Be(88);
    }

    private static AgwFrame MakeConnectAck() => new(
        Port: 0,
        Kind: AgwCommandKind.Connect,
        Pid: 0,
        From: "PN0TST",
        To: "M0LTE",
        Data: Encoding.ASCII.GetBytes("CONNECTED\r"));

    /// <summary>
    /// Two paired pipes forming a duplex byte channel: writes from
    /// the client come out at the server's read side, and vice versa.
    /// Lets the test drive AgwClient against a controlled "server"
    /// that produces canned responses, without spinning up a real
    /// TCP listener.
    /// </summary>
    private sealed class InMemoryAgwPair : IAsyncDisposable
    {
        private readonly Pipe clientToServer = new();
        private readonly Pipe serverToClient = new();
        private readonly DuplexStream clientStream;
        private readonly DuplexStream serverStream;
        private readonly AgwFrameStream serverFraming;

        public InMemoryAgwPair()
        {
            clientStream = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
            serverStream = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
            // Disable keepalive — the in-memory server doesn't service the R-ping and the unit tests are short-lived.
            Client = AgwClient.FromStream(clientStream, keepaliveInterval: TimeSpan.Zero);
            serverFraming = new AgwFrameStream(serverStream, ownsStream: false);
        }

        public AgwClient Client { get; }

        public async Task<AgwFrame> ServerReadFrame()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await serverFraming.Inbound.ReadAsync(cts.Token);
        }

        public ValueTask ServerWriteFrame(AgwFrame frame)
            => serverFraming.WriteAsync(frame);

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await serverFraming.DisposeAsync();
            clientToServer.Writer.Complete();
            serverToClient.Writer.Complete();
        }
    }

    /// <summary>
    /// Bidirectional stream backed by separate read / write halves.
    /// Lets us thread one pipe in each direction so two participants
    /// can talk over the pair without a real socket.
    /// </summary>
    private sealed class DuplexStream : Stream
    {
        private readonly Stream readSide;
        private readonly Stream writeSide;

        public DuplexStream(Stream readSide, Stream writeSide)
        {
            this.readSide = readSide;
            this.writeSide = writeSide;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => writeSide.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => writeSide.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => readSide.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => readSide.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => readSide.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => writeSide.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => writeSide.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => writeSide.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
