using System.IO.Pipelines;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Agw.Tests;

/// <summary>
/// End-to-end behaviour of <see cref="AgwClient"/> against an
/// in-memory paired-pipe stub server. Tests run without TCP - the
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
    public async Task OpenSessionAsync_succeeds_when_the_connect_ack_races_registration()
    {
        // Regression test for the connect-ack ordering race. clientWriteDelay holds
        // the client's C-frame write open for 300 ms AFTER its bytes are flushed to
        // the server, so the server receives the connect and the client's read-loop
        // + dispatch loop deliver the 'C' ack while OpenSessionAsync is still
        // suspended inside its write. If the connect waiter is registered AFTER the
        // write (the prior bug) that ack lands in an empty waiter list and the
        // connect times out; registering before the write (the fix) means the
        // dispatch loop finds the waiter already present. The 3 s budget makes a
        // regression fail fast rather than hang.
        await using var pair = new InMemoryAgwPair(clientWriteDelay: TimeSpan.FromMilliseconds(300));

        var serverTask = Task.Run(async () =>
        {
            var c = await pair.ServerReadFrame();
            c.Kind.Should().Be(AgwCommandKind.Connect);
            await pair.ServerWriteFrame(MakeConnectAck());
        });

        await using var session = await pair.Client.OpenSessionAsync(
            "M0LTE", "PN0TST", connectTimeout: TimeSpan.FromSeconds(3));
        await serverTask;
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
            if (n == 0)
            {
                break;
            }

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

    [Fact]
    public async Task GetPortInfoAsync_parses_the_semicolon_list_and_skips_the_count_field()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            var g = await pair.ServerReadFrame();
            g.Kind.Should().Be(AgwCommandKind.AskPortInfo);
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0, Kind: AgwCommandKind.AskPortInfo, Pid: 0, From: "", To: "",
                Data: Encoding.ASCII.GetBytes("2;Packet Radio Port;VHF Port;\0\0")));
        });

        var ports = await pair.Client.GetPortInfoAsync();
        await serverTask;

        ports.Should().Equal("Packet Radio Port", "VHF Port");
    }

    [Fact]
    public async Task GetPortInfoAsync_returns_empty_for_an_empty_reply_body()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();
            await pair.ServerWriteFrame(new AgwFrame(
                Port: 0, Kind: AgwCommandKind.AskPortInfo, Pid: 0, From: "", To: "",
                Data: ReadOnlyMemory<byte>.Empty));
        });

        var ports = await pair.Client.GetPortInfoAsync();
        await serverTask;

        ports.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenSessionAsync_times_out_when_the_server_never_acks_the_connect()
    {
        await using var pair = new InMemoryAgwPair();

        // Server reads the 'C' (so the client's write completes) but never acks.
        var serverTask = Task.Run(async () => await pair.ServerReadFrame());

        var act = async () => await pair.Client.OpenSessionAsync(
            "M0LTE", "PN0TST", connectTimeout: TimeSpan.FromMilliseconds(300));

        await act.Should().ThrowAsync<TimeoutException>();
        await serverTask;

        // The failed session was removed from the table, so a retry doesn't collide
        // with a phantom "already exists".
        var retry = async () => await pair.Client.OpenSessionAsync(
            "M0LTE", "PN0TST", connectTimeout: TimeSpan.FromMilliseconds(100));
        await retry.Should().ThrowAsync<TimeoutException>("the slot is free to retry, not an InvalidOperationException");
    }

    [Fact]
    public async Task OpenSessionAsync_rejects_a_duplicate_live_session()
    {
        await using var pair = new InMemoryAgwPair();

        var serverTask = Task.Run(async () =>
        {
            await pair.ServerReadFrame();
            await pair.ServerWriteFrame(MakeConnectAck());
        });

        await using var session = await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await serverTask;

        // A second open for the same (from, to, port) while the first is live is refused.
        var act = async () => await pair.Client.OpenSessionAsync("M0LTE", "PN0TST");
        await act.Should().ThrowAsync<InvalidOperationException>();
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

        // clientWriteDelay holds the CLIENT's WriteAsync open for a beat AFTER its
        // bytes are flushed to the server. That deterministically reproduces the
        // connect-ack ordering race: the server receives the request and replies,
        // and the client's read-loop + dispatch loop deliver that reply, all while
        // the caller is still suspended inside its write — i.e. before it could
        // register a waiter, had it (as the prior bug did) registered AFTER the
        // write. Default zero, so the other tests keep a realistic handoff.
        public InMemoryAgwPair(TimeSpan clientWriteDelay = default)
        {
            clientStream = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream(), clientWriteDelay);
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
        private readonly TimeSpan writeDelay;

        public DuplexStream(Stream readSide, Stream writeSide, TimeSpan writeDelay = default)
        {
            this.readSide = readSide;
            this.writeSide = writeSide;
            this.writeDelay = writeDelay;
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
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writeSide.WriteAsync(buffer, cancellationToken);
            if (writeDelay > TimeSpan.Zero)
            {
                // Make the bytes visible to the reader before holding, so the peer
                // can respond during the delay — that's the point (see InMemoryAgwPair).
                await writeSide.FlushAsync(cancellationToken);
                await Task.Delay(writeDelay, cancellationToken);
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
