using System.Collections.Concurrent;
using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Packet.Kiss.Serial.Tests;

/// <summary>
/// Drives <see cref="KissSerialModem"/> over a scripted fake <see cref="ISerialPortIo"/>
/// (via the internal <c>OpenForTest</c> seam) so the behaviour that the hardware-free
/// shape tests can't reach is actually exercised: the read pump (framing, the
/// timeout-continue and zero-read-continue paths, terminal-exception fault), the
/// <see cref="IAx25Transport"/> seam (KISS-Data filtering + <c>ReceivedAt</c> stamping),
/// the KISS encoding of every send/parameter command, and dispose ordering.
/// </summary>
public class KissSerialModemPumpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static byte[] DataFrame(params byte[] payload) =>
        KissEncoder.Encode(0, KissCommand.Data, payload);

    [Fact]
    public async Task PortName_is_passed_through()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        modem.PortName.Should().Be(io.PortName);
    }

    [Fact]
    public async Task A_fed_KISS_data_frame_surfaces_through_ReadFramesAsync()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        io.FeedBytes(DataFrame(0x01, 0x02, 0x03));

        var frame = await FirstFrameAsync(modem);
        frame.Command.Should().Be(KissCommand.Data);
        frame.Port.Should().Be((byte)0);
        frame.Payload.Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public async Task An_inbound_frame_also_raises_FrameReceived()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        var received = new TaskCompletionSource<KissFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.FrameReceived += (_, f) => received.TrySetResult(f);

        io.FeedBytes(DataFrame(0xAB));

        var frame = await received.Task.WaitAsync(Timeout);
        frame.Payload.Should().Equal(0xAB);
    }

    [Fact]
    public async Task A_read_timeout_does_not_kill_the_pump()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        io.FeedTimeout();                       // pump must `continue`, not die
        io.FeedBytes(DataFrame(0x42));

        (await FirstFrameAsync(modem)).Payload.Should().Equal(0x42);
    }

    [Fact]
    public async Task A_zero_byte_read_does_not_kill_the_pump()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        io.FeedZeroRead();                       // read <= 0 must `continue`
        io.FeedBytes(DataFrame(0x7F));

        (await FirstFrameAsync(modem)).Payload.Should().Equal(0x7F);
    }

    [Fact]
    public async Task A_split_frame_across_two_reads_is_reassembled()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        var encoded = DataFrame(0x10, 0x20, 0x30, 0x40);
        io.FeedBytes(encoded[..3]);
        io.FeedBytes(encoded[3..]);

        (await FirstFrameAsync(modem)).Payload.Should().Equal(0x10, 0x20, 0x30, 0x40);
    }

    [Fact]
    public async Task A_read_exception_while_running_faults_the_inbound_stream()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        io.FeedThrow(new IOException("device unplugged"));

        var act = async () =>
        {
            using var cts = new CancellationTokenSource(Timeout);
            await foreach (var _ in modem.ReadFramesAsync(cts.Token))
            {
            }
        };
        await act.Should().ThrowAsync<IOException>().WithMessage("device unplugged");
    }

    [Fact]
    public async Task ReceiveAsync_drops_non_data_commands_and_stamps_ReceivedAt()
    {
        var when = new DateTimeOffset(2026, 6, 22, 7, 30, 0, TimeSpan.Zero);
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io, new FixedClock(when));

        io.FeedBytes(KissEncoder.Encode(0, KissCommand.TxDelay, [5]));   // non-Data → dropped
        io.FeedBytes(DataFrame(0x55, 0x66));                             // Data → surfaced

        using var cts = new CancellationTokenSource(Timeout);
        await using var e = modem.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue();

        e.Current.Ax25.ToArray().Should().Equal(0x55, 0x66);
        e.Current.ReceivedAt.Should().Be(when);
    }

    [Fact]
    public async Task SendFrameAsync_KISS_encodes_the_body_as_a_data_frame()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        await modem.SendFrameAsync(new byte[] { 0xC0, 0xDB, 0x01 });   // bytes that need KISS escaping

        io.Writes.Should().ContainSingle()
          .Which.Should().Equal(KissEncoder.Encode(0, KissCommand.Data, [0xC0, 0xDB, 0x01]));
    }

    [Fact]
    public async Task IAx25Transport_SendAsync_uses_the_same_data_path()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        await modem.SendAsync(new byte[] { 0x09, 0x08 });

        io.Writes.Should().ContainSingle()
          .Which.Should().Equal(KissEncoder.Encode(0, KissCommand.Data, [0x09, 0x08]));
    }

    [Fact]
    public async Task Parameter_setters_encode_the_right_KISS_command()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);

        await modem.SetTxDelayAsync(30);
        await modem.SetPersistenceAsync(63);
        await modem.SetSlotTimeAsync(10);
        await modem.SetTxTailAsync(0);
        await modem.SetFullDuplexAsync(true);

        io.Writes.Should().HaveCount(5);
        io.Writes[0].Should().Equal(KissEncoder.Encode(0, KissCommand.TxDelay, [30]));
        io.Writes[1].Should().Equal(KissEncoder.Encode(0, KissCommand.Persistence, [63]));
        io.Writes[2].Should().Equal(KissEncoder.Encode(0, KissCommand.SlotTime, [10]));
        io.Writes[3].Should().Equal(KissEncoder.Encode(0, KissCommand.TxTail, [0]));
        io.Writes[4].Should().Equal(KissEncoder.Encode(0, KissCommand.FullDuplex, [1]));
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_disposes_the_port()
    {
        var io = new FakeSerialPortIo();
        var modem = KissSerialModem.OpenForTest(io);

        await modem.DisposeAsync();
        await modem.DisposeAsync();   // second call is a no-op, must not throw or hang

        io.DisposeCount.Should().Be(1, "the serial handle is disposed exactly once");
    }

    private static async Task<KissFrame> FirstFrameAsync(KissSerialModem modem)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var e = modem.ReadFramesAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        (await e.MoveNextAsync()).Should().BeTrue("a frame was expected before the timeout");
        return e.Current;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A scripted, thread-safe <see cref="ISerialPortIo"/>. Each <c>Feed*</c> enqueues
    /// one read outcome (bytes / a timeout / a zero-read / a throw); the pump's blocking
    /// <see cref="Read"/> consumes them in order, then blocks until more are fed or the
    /// fake is disposed (which unblocks the read with an <see cref="IOException"/>, exactly
    /// as closing a real handle does). Writes are captured for assertion.
    /// </summary>
    private sealed class FakeSerialPortIo : ISerialPortIo
    {
        private readonly BlockingCollection<ReadStep> steps = new();
        private readonly List<byte[]> writes = [];
        private readonly object writeGate = new();

        public string PortName => "FAKE1";
        public int DisposeCount { get; private set; }

        public byte[][] Writes
        {
            get { lock (writeGate) { return writes.ToArray(); } }
        }

        public void FeedBytes(byte[] data) => steps.Add(new ReadStep(Data: data));
        public void FeedTimeout() => steps.Add(new ReadStep(Timeout: true));
        public void FeedZeroRead() => steps.Add(new ReadStep(ZeroRead: true));
        public void FeedThrow(Exception ex) => steps.Add(new ReadStep(Throw: ex));

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!steps.TryTake(out var step, System.Threading.Timeout.Infinite))
            {
                // Completed and empty — the handle is closed (dispose path).
                throw new IOException("port closed");
            }

            if (step.Throw is not null)
            {
                throw step.Throw;
            }
            if (step.Timeout)
            {
                throw new TimeoutException();
            }
            if (step.ZeroRead || step.Data is null)
            {
                return 0;
            }

            var n = Math.Min(count, step.Data.Length);
            Array.Copy(step.Data, 0, buffer, offset, n);
            return n;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (writeGate)
            {
                writes.Add(buffer[offset..(offset + count)]);
            }
        }

        public void Dispose()
        {
            DisposeCount++;
            steps.CompleteAdding();
        }

        private sealed record ReadStep(
            byte[]? Data = null, bool Timeout = false, bool ZeroRead = false, Exception? Throw = null);
    }
}
