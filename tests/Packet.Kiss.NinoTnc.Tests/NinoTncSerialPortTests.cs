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

    [Fact]
    public async Task GetVersionAsync_sends_GETVER_and_returns_the_ascii_reply()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetVersionAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildGetVersionKissFrame());

        // The firmware replies with the bare ASCII version on raw command
        // byte 0xE0 (= port 14 + command 0 through the generic encoder).
        io.FeedBytes(KissEncoder.Encode(14, KissCommand.Data, "3.41"u8.ToArray()));

        (await task).Should().Be("3.41");
    }

    [Fact]
    public async Task GetRssiAsync_sends_GETRSSI_and_parses_the_level()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetRssiAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildGetRssiKissFrame());

        io.FeedBytes(KissEncoder.Encode(14, KissCommand.Data, "RSSI:-62.54"u8.ToArray()));

        (await task).Should().BeApproximately(-62.54f, 1e-4f);
    }

    [Fact]
    public async Task GetSerialNumberAsync_sends_GETSERNO_and_returns_the_register_as_ascii()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetSerialNumberAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildGetSerialNumberKissFrame());

        io.FeedBytes(KissEncoder.Encode(14, KissCommand.Data, "PDN00001"u8.ToArray()));

        (await task).Should().Be("PDN00001");
    }

    [Fact]
    public async Task GetSerialNumberAsync_maps_the_all_zero_register_to_null()
    {
        // Bench capture 2026-07-03 (firmware 3.41, unset register): 8 zero bytes on 0xE0.
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetSerialNumberAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);

        io.FeedBytes(KissEncoder.Encode(14, KissCommand.Data, new byte[8]));

        (await task).Should().BeNull("an all-zero KAUP8R register means unset");
    }

    [Fact]
    public async Task SetSerialNumber_and_ClearSerialNumber_put_the_documented_bytes_on_the_wire()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetSerialNumberAsync("PDN00001");
        await nino.ClearSerialNumberAsync();

        await WaitUntil(() => io.Writes.Length >= 2);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildSetSerialNumberKissFrame("PDN00001"));
        io.Writes[1].Should().Equal(NinoTncCommands.BuildClearSerialNumberKissFrame());
    }

    [Fact]
    public async Task GetAllAsync_accepts_the_labelled_diagnostic_reply_of_firmware_341()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetAllAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildGetAllKissFrame());

        io.FeedBytes(KissEncoder.Encode(14, KissCommand.Data,
            "=FirmwareVr:3.41=BrdSwchMod:040F0002=TxPktCount:00000049=PreamblCnt:00000016"u8.ToArray()));

        var status = await task;
        status.FirmwareVersionRaw.Should().Be("3.41");
        status.DipSwitches.Should().Be((byte)0x0F);
        status.TxPackets.Should().Be(0x49);
        status.PreambleWordCount.Should().Be(0x16);
        status.PttOnMs.Should().BeNull("the labelled reply has no PTT-on register");
    }

    [Fact]
    public async Task GetAllAsync_accepts_the_numeric_status_report()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var task = nino.GetAllAsync(Timeout);
        await WaitUntil(() => io.Writes.Length >= 1);

        io.FeedBytes(KissEncoder.Encode(0, KissCommand.Data,
            "=00:3.41=01:\0\0\0\0\0\0\0\0=02:000003E8=0B:00000016=0D:0000F4F6"u8.ToArray()));

        var status = await task;
        status.UptimeMs.Should().Be(1000);
        status.PreambleWordCount.Should().Be(0x16);
        status.PttOnMs.Should().Be(0xF4F6);
    }

    [Fact]
    public async Task GetAllAsync_times_out_when_nothing_answers()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var act = async () => await nino.GetAllAsync(TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task StopTx_and_SetBeaconInterval_put_the_documented_bytes_on_the_wire()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.StopTxAsync();
        await nino.SetBeaconIntervalAsync(minutes: 5);

        await WaitUntil(() => io.Writes.Length >= 2);
        io.Writes[0].Should().Equal(NinoTncCommands.BuildStopTxKissFrame());
        io.Writes[1].Should().Equal(NinoTncCommands.BuildSetBeaconIntervalKissFrame(5));
    }

    [Fact]
    public async Task ArmCqBeepResponderAsync_transmits_the_TARPNstat_frame()
    {
        var io = new FakeSerialPortIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var source = new Packet.Core.Callsign("M0LTE", 1);
        await nino.ArmCqBeepResponderAsync(source);

        await WaitUntil(() => io.Writes.Length >= 1);
        io.Writes[0].Should().Equal(
            KissEncoder.Encode(0, KissCommand.Data, NinoTncCqBeep.BuildArmingFrame(source).ToBytes()));
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
