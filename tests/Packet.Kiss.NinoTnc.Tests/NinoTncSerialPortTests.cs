using System.Collections.Concurrent;
using System.Diagnostics;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Packet.Kiss.NinoTnc.Tests;

/// <summary>
/// Drives <see cref="NinoTncSerialPort"/> over a scripted fake <see cref="ISerialPortIo"/>
/// (via <c>KissSerialModem.OpenForTest</c> + the internal <c>NinoTncSerialPort.OpenForTest</c>
/// seam) so the NinoTNC-specific behaviour the static-classifier tests can't reach is
/// exercised without hardware: inbound frame classification + fan-out, the ACKMODE
/// TX-completion correlation (<c>SendFrameWithAckAsync</c>), its timeout and duplicate-tag
/// guards, and the dispose-fails-pending-acks teardown.
/// </summary>
public class NinoTncSerialPortTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task An_inbound_data_frame_is_classified_and_fanned_out()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var raw = new TaskCompletionSource<KissFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var typed = new TaskCompletionSource<KissInboundEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        nino.FrameReceived += (_, f) => raw.TrySetResult(f);
        nino.InboundEvent += (_, e) => typed.TrySetResult(e);

        io.FeedBytes(KissEncoder.Encode(0, KissCommand.Data, [0x01, 0x02, 0x03]));

        (await raw.Task.WaitAsync(Timeout)).Payload.Should().Equal(0x01, 0x02, 0x03);
        (await typed.Task.WaitAsync(Timeout)).Should().NotBeNull("the dispatch loop classifies every inbound frame");
    }

    [Fact]
    public async Task SendFrameWithAckAsync_resolves_when_the_TNC_echoes_the_tag()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var sendTask = nino.SendFrameWithAckAsync(new byte[] { 0xAA, 0xBB }, Timeout, sequenceTag: 0x1234);

        // Once the ACKMODE frame is on the wire, the pending-ack for 0x1234 is registered.
        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(KissAckMode.BuildSendFrame(0, 0x1234, [0xAA, 0xBB]));

        // The TNC echoes command 0x0C with the 2-byte tag once the frame keyed the air.
        io.FeedBytes(KissEncoder.Encode(0, KissCommand.AckMode, [0x12, 0x34]));

        var completion = await sendTask;   // resolves (not a timeout) ⇒ the echo correlated
        completion.Completed.Should().BeOnOrAfter(completion.Queued);
    }

    [Fact]
    public async Task SendFrameWithAckAsync_times_out_when_no_echo_arrives()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var act = async () => await nino.SendFrameWithAckAsync(new byte[] { 0x01 }, timeout: TimeSpan.FromMilliseconds(200), sequenceTag: 0x42);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task SendFrameWithAckAsync_rejects_a_duplicate_pending_tag()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var first = nino.SendFrameWithAckAsync(new byte[] { 0x01 }, Timeout, sequenceTag: 0x77);
        await WaitUntil(() => io.Writes.Length >= 1);   // first is registered

        var act = async () => await nino.SendFrameWithAckAsync(new byte[] { 0x02 }, Timeout, sequenceTag: 0x77);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Resolve the first cleanly so it isn't left as an unobserved fault.
        io.FeedBytes(KissEncoder.Encode(0, KissCommand.AckMode, [0x00, 0x77]));
        await first;
    }

    [Fact]
    public async Task Dispose_fails_a_pending_ack()
    {
        var io = new FakeSerialPortIo();
        var modem = KissSerialModem.OpenForTest(io);
        var nino = NinoTncSerialPort.OpenForTest(modem);

        var sendTask = nino.SendFrameWithAckAsync(new byte[] { 0x01 }, TimeSpan.FromSeconds(30), sequenceTag: 0x55);
        await WaitUntil(() => io.Writes.Length >= 1);   // pending ack registered

        // Dispose awaits the dispatch loop, whose finally runs FailPendingAcks — so by the
        // time DisposeAsync returns the pending send is already faulted (no hang).
        await nino.DisposeAsync();

        var act = async () => await sendTask;
        await act.Should().ThrowAsync<Exception>("a pending ACK must not hang once the port is disposed");
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not met within the deadline");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A scripted, thread-safe <see cref="ISerialPortIo"/> (mirrors the Kiss.Serial test fake):
    /// each <c>Feed*</c> enqueues a read outcome the modem's pump consumes in order; writes are
    /// captured for assertion; disposal unblocks a parked read with an <see cref="IOException"/>.
    /// </summary>
    private sealed class FakeSerialPortIo : ISerialPortIo
    {
        private readonly BlockingCollection<byte[]?> steps = new();
        private readonly List<byte[]> writes = [];
        private readonly object writeGate = new();

        public string PortName => "FAKENINO";

        public byte[][] Writes
        {
            get { lock (writeGate) { return writes.ToArray(); } }
        }

        public void FeedBytes(byte[] data) => steps.Add(data);

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!steps.TryTake(out var data, System.Threading.Timeout.Infinite) || data is null)
            {
                throw new IOException("port closed");
            }
            var n = Math.Min(count, data.Length);
            Array.Copy(data, 0, buffer, offset, n);
            return n;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (writeGate)
            {
                writes.Add(buffer[offset..(offset + count)]);
            }
        }

        public void Dispose() => steps.CompleteAdding();
    }
}
