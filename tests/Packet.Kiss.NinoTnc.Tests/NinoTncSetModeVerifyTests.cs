using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Packet.Kiss.NinoTnc.Tests;

/// <summary>
/// SETHW mode verification (#633). KISS SETHW is unacknowledged and <em>does</em> silently fail to
/// apply — bench-observed twice on firmware 3.44 with DIP 1111, the TNC carrying on in its previous
/// mode. Nothing errors; the traffic just scores zero in both directions, which reads as broken RF
/// rather than an ignored command, and cost real debugging time on both occasions.
/// <para>
/// These tests drive <see cref="NinoTncSerialPort.SetModeAsync"/> against a scripted fake TNC that
/// answers each GETALL with a firmware mode byte of the test's choosing — so "the TNC lies about
/// its mode" is reproducible without hardware. The bench evidence in the issue is the script:
/// <c>BrdSwchMod:040F0091</c> (0x91 = mode 8) when mode 11 was asked for, then
/// <c>BrdSwchMod:040F00A2</c> (0xA2 = mode 11) after a retry.
/// </para>
/// </summary>
public class NinoTncSetModeVerifyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Firmware mode bytes from the #633 bench capture, via <see cref="NinoTncCatalog"/>.</summary>
    private const byte Mode8Byte = 0x91;
    private const byte Mode11Byte = 0xA2;

    /// <summary>Verification with the settle removed — the settle itself is pinned separately.</summary>
    private static NinoTncModeVerification Instant(int attempts = 3) =>
        new() { SettleTime = TimeSpan.Zero, Attempts = attempts, ReadBackTimeout = Timeout };

    [Fact]
    public async Task SetModeAsync_returns_once_the_GETALL_readback_confirms_the_mode()
    {
        var io = new FakeTncIo(Mode11Byte);
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(11, persistToFlash: false, Instant()).WaitAsync(Timeout);

        io.SetHwCount(11).Should().Be(1, "the mode took first time — no re-send needed");
        io.GetAllCount.Should().Be(1, "one readback proved it");
        nino.CurrentMode.Should().Be((byte)11);
    }

    [Fact]
    public async Task SetModeAsync_resends_the_SETHW_when_the_TNC_is_still_running_the_old_mode()
    {
        // The #633 bench sequence exactly: asked for 11, TNC reports 8, retry lands 11.
        var io = new FakeTncIo(Mode8Byte, Mode11Byte);
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(11, persistToFlash: false, Instant()).WaitAsync(Timeout);

        io.SetHwCount(11).Should().Be(2, "the first SETHW was silently ignored, so it is sent again");
        io.GetAllCount.Should().Be(2);
        nino.CurrentMode.Should().Be((byte)11);
    }

    [Fact]
    public async Task SetModeAsync_throws_when_the_mode_never_takes()
    {
        var io = new FakeTncIo(Mode8Byte, Mode8Byte, Mode8Byte);
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        var act = async () => await nino.SetModeAsync(11, persistToFlash: false, Instant());

        var thrown = (await act.Should().ThrowAsync<NinoTncModeNotAppliedException>(
            "a silently-ignored SETHW must not look like success — that is the whole bug"))
            .Which;
        thrown.RequestedMode.Should().Be((byte)11);
        thrown.RunningMode!.Value.Mode.Should().Be((byte)8, "the TNC told us what it is actually running");
        thrown.FirmwareModeByte.Should().Be(Mode8Byte);
        thrown.Attempts.Should().Be(3);
        thrown.Message.Should().Contain("running mode 8", "the message must name the mode it is stuck in");

        io.SetHwCount(11).Should().Be(3, "every attempt re-sent the SETHW");
        nino.CurrentMode.Should().Be((byte)8,
            "CurrentMode must not keep asserting a mode the TNC just contradicted — CurrentBitRateHz feeds off it");
    }

    [Fact]
    public async Task SetModeAsync_accepts_the_firmware_341_alias_for_mode_14()
    {
        // 3.41 reports mode 14 as 0x90 where 3.44 reports 0x23. Verification goes through the
        // catalog, so the alias resolves to mode 14 and verifies — a raw byte comparison would
        // call this a mis-set and retry forever against a TNC that is already in the right mode.
        var io = new FakeTncIo(0x90) { FirmwareVersion = "3.41" };
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(14, persistToFlash: false, Instant()).WaitAsync(Timeout);

        io.SetHwCount(14).Should().Be(1, "0x90 IS mode 14 on 3.41 — nothing to retry");
        nino.CurrentMode.Should().Be((byte)14);
    }

    [Fact]
    public async Task SetModeAsync_waits_out_the_settle_time_before_reading_back()
    {
        var clock = new FakeTimeProvider();
        var io = new FakeTncIo(Mode11Byte);
        await using var modem = KissSerialModem.OpenForTest(io, clock);
        await using var nino = NinoTncSerialPort.OpenForTest(modem, clock);

        var task = nino.SetModeAsync(11, persistToFlash: false, new NinoTncModeVerification
        {
            SettleTime = TimeSpan.FromSeconds(1.5),
            Attempts = 1,
            ReadBackTimeout = Timeout,
        });

        await WaitUntil(() => io.SetHwCount(11) == 1, "the SETHW goes out immediately");
        await Task.Delay(50);
        io.GetAllCount.Should().Be(0, "the readback waits — the modem needs a moment after a mode change");

        clock.Advance(TimeSpan.FromSeconds(1.4));
        await Task.Delay(50);
        io.GetAllCount.Should().Be(0, "1.4 s is not yet the 1.5 s settle");

        clock.Advance(TimeSpan.FromMilliseconds(200));
        await task.WaitAsync(Timeout);
        io.GetAllCount.Should().Be(1, "the readback goes out once the settle has elapsed");
    }

    [Fact]
    public void The_default_settle_is_the_bench_measured_one_and_a_half_seconds()
    {
        NinoTncModeVerification.Default.Enabled.Should().BeTrue("the failure this catches is invisible otherwise");
        NinoTncModeVerification.Default.SettleTime.Should().Be(TimeSpan.FromSeconds(1.5),
            "what the pdn-soundmodem bench rig settled on empirically");
        NinoTncModeVerification.Default.Attempts.Should().Be(3);
        NinoTncModeVerification.None.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task SetModeAsync_retries_when_the_readback_never_answers()
    {
        // A GETALL that times out says nothing about the mode — so it is a failed attempt (re-send),
        // not a hard error. Script: no reply, then the mode we asked for.
        var io = new FakeTncIo(null, Mode11Byte);
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(11, persistToFlash: false, new NinoTncModeVerification
        {
            SettleTime = TimeSpan.Zero,
            Attempts = 3,
            ReadBackTimeout = TimeSpan.FromMilliseconds(200),
        }).WaitAsync(Timeout);

        io.SetHwCount(11).Should().Be(2, "the silent readback was retried, and the second answered");
    }

    [Fact]
    public async Task Verification_None_is_the_old_fire_and_forget_send()
    {
        var io = new FakeTncIo();   // answers nothing — nothing should be asked
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(11, persistToFlash: false, NinoTncModeVerification.None).WaitAsync(Timeout);

        io.SetHwCount(11).Should().Be(1);
        io.GetAllCount.Should().Be(0, "callers that opt out of verification pay for no readback");
    }

    [Fact]
    public async Task Mode_15_is_sent_but_not_verified()
    {
        // 15 is the "Set from KISS" escape — a statement about where the mode comes from, not an
        // operating mode to run — so there is no meaningful readback to compare against.
        var io = new FakeTncIo();
        await using var modem = KissSerialModem.OpenForTest(io);
        await using var nino = NinoTncSerialPort.OpenForTest(modem);

        await nino.SetModeAsync(15).WaitAsync(Timeout);

        io.SetHwCount(15).Should().Be(1);
        io.GetAllCount.Should().Be(0, "verifying 'set from KISS' would be comparing against a placeholder");
    }

    private static async Task WaitUntil(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException($"condition not met within the deadline: {because}");
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A fake NinoTNC: captures writes, and answers each GETALL with the next firmware mode byte
    /// from a script (a <c>null</c> entry = stay silent, modelling a dropped/ignored query; running
    /// off the end of the script = keep repeating the last answer). The reply is fed synchronously
    /// from <see cref="Write"/>, which is safe because <c>SendAwaitingReplyAsync</c> subscribes its
    /// handler before it sends.
    /// </summary>
    private sealed class FakeTncIo : ISerialPortIo
    {
        private readonly BlockingCollection<byte[]?> reads = new();
        private readonly List<byte[]> writes = [];
        private readonly Queue<byte?> modeByteScript;
        private readonly object gate = new();
        private byte? lastAnswer;
        private int getAllCount;

        public FakeTncIo(params byte?[] modeBytesPerGetAll)
        {
            modeByteScript = new Queue<byte?>(modeBytesPerGetAll);
        }

        public string PortName => "FAKENINO";

        public string FirmwareVersion { get; init; } = "3.44";

        public int GetAllCount => Volatile.Read(ref getAllCount);

        /// <summary>How many SETHW frames for <paramref name="mode"/> (+16, non-persist) were written.</summary>
        public int SetHwCount(byte mode)
        {
            byte[] expected = NinoTncSetHardware.BuildKissFrame(mode, persistToFlash: false);
            lock (gate)
            {
                return writes.Count(w => w.AsSpan().SequenceEqual(expected));
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (!reads.TryTake(out var data, System.Threading.Timeout.Infinite) || data is null)
            {
                throw new IOException("port closed");
            }
            int n = Math.Min(count, data.Length);
            Array.Copy(data, 0, buffer, offset, n);
            return n;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            byte[] frame = buffer[offset..(offset + count)];
            lock (gate)
            {
                writes.Add(frame);
            }

            if (!frame.AsSpan().SequenceEqual(NinoTncCommands.BuildGetAllKissFrame()))
            {
                return;
            }

            Interlocked.Increment(ref getAllCount);
            byte? answer;
            lock (gate)
            {
                answer = modeByteScript.Count > 0 ? modeByteScript.Dequeue() : lastAnswer;
                lastAnswer = answer;
            }
            if (answer is { } modeByte)
            {
                reads.Add(BuildStatusReply(FirmwareVersion, modeByte));
            }
        }

        /// <summary>
        /// The labelled GETALL reply: <c>BrdSwchMod</c> packs board rev 04, DIP 0F (software
        /// control — the #633 bench setup), then the 16-bit firmware mode whose LOW byte is the
        /// running-mode index.
        /// </summary>
        private static byte[] BuildStatusReply(string firmware, byte modeByte) =>
            KissEncoder.Encode(14, KissCommand.Data,
                Encoding.ASCII.GetBytes($"=FirmwareVr:{firmware}=BrdSwchMod:040F00{modeByte:X2}=TxPktCount:00000000=PreamblCnt:00000000"));

        public void Dispose() => reads.CompleteAdding();
    }
}
