using System.Text;
using Packet.Kiss.NinoTnc.Firmware;

namespace Packet.Kiss.NinoTnc.Tests.Firmware;

/// <summary>
/// Drives <see cref="BootloaderNinoTncFirmwareFlasher"/> over a scripted
/// fake of the dsPIC bootloader (via the internal
/// <see cref="INinoTncBootloaderSerialPort"/> seam) so every protocol path —
/// entry handshake, stranded-bootloader resume, chip-mismatch abort, the
/// N/F/X terminal replies, mid-transfer silence, cancellation stranding +
/// recovery — is exercised without hardware.
/// </summary>
public class BootloaderNinoTncFirmwareFlasherTests
{
    private static readonly byte[] TinyEp256Image = NinoTncFirmwareHexImageTests.TinyImage(NinoTncFirmwareHexImageTests.Ep256Magic);

    /// <summary>Timings shrunk so the scripted suite runs instantly. The
    /// elapsed-time-based budgets stay at their defaults — tests that need
    /// them use <see cref="ManualClock"/> so no real time passes either.</summary>
    private static NinoTncFlashTimings Instant => new()
    {
        GetAllProbeSpacing = TimeSpan.Zero,
        FirstLineCharDelay = TimeSpan.Zero,
        ResetSettleDelay = TimeSpan.Zero,
        LineReplyTimeout = TimeSpan.Zero,
    };

    [Fact]
    public async Task Happy_Path_Enters_The_Bootloader_And_Flashes_The_Image()
    {
        var tnc = new FakeBootloaderTnc();
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);
        var progress = new ProgressCollector();

        var result = await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image, progress);

        result.Chip.Should().Be(NinoTncChipVariant.Dspic33Ep256);
        result.BootloaderVersion.Should().Be('a');
        result.LinesWritten.Should().Be(4);
        result.TotalLines.Should().Be(4);
        result.ResumedStrandedBootloader.Should().BeFalse();

        // The verified wire sequence: stranded probe, 3× bare GETALL,
        // bootloader entry, version query, then the image lines.
        tnc.Writes.Should().ContainSingle(w => w.SequenceEqual(new[] { (byte)'R' }));
        tnc.Writes.Count(w => w.SequenceEqual(NinoTncCommands.BuildBareGetAllKissFrame())).Should().Be(3);
        tnc.Writes.Should().ContainSingle(w => w.SequenceEqual(NinoTncCommands.BuildBootloaderEntryKissFrame()));
        tnc.Writes.Should().ContainSingle(w => w.SequenceEqual(new[] { (byte)'V' }));
        tnc.LinesReceived.Should().Equal(
            ":020000040000fa",
            NinoTncFirmwareHexImageTests.Ep256Magic,
            ":040008008606000068",
            ":00000001FF");

        progress.Reports[0].Should().Be(new NinoTncFlashProgress(0, 4));
        progress.Reports[^1].Should().Be(new NinoTncFlashProgress(4, 4));
        progress.Reports.Should().BeInAscendingOrder(r => r.LinesWritten);
    }

    [Fact]
    public async Task The_First_Line_Is_Sent_Char_By_Char_And_The_Rest_Whole()
    {
        var tnc = new FakeBootloaderTnc();
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        // The first image line (":020000040000fa" + '\n' = 16 chars) arrives
        // as 16 single-byte writes — the bootloader erases the flash page
        // while it trickles in. Later lines arrive as one write each.
        var lineWrites = tnc.Writes
            .SkipWhile(w => w.Length != 1 || w[0] != (byte)':')
            .ToList();
        lineWrites.Take(16).Should().OnlyContain(w => w.Length == 1);
        Encoding.ASCII.GetString(lineWrites.Take(16).SelectMany(w => w).ToArray())
            .Should().Be(":020000040000fa\n");
        lineWrites.Skip(16).First().Should().Equal(
            Encoding.ASCII.GetBytes(NinoTncFirmwareHexImageTests.Ep256Magic + "\n"));
    }

    [Fact]
    public async Task A_Stranded_Bootloader_Is_Resumed_Without_The_Entry_Handshake()
    {
        var tnc = new FakeBootloaderTnc { InBootloader = true };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var result = await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        result.ResumedStrandedBootloader.Should().BeTrue();
        result.LinesWritten.Should().Be(4);
        tnc.Writes.Should().NotContain(w => w.SequenceEqual(NinoTncCommands.BuildBareGetAllKissFrame()),
            "a stranded bootloader would swallow KISS probes");
        tnc.Writes.Should().NotContain(w => w.SequenceEqual(NinoTncCommands.BuildBootloaderEntryKissFrame()),
            "the modem is already in the bootloader");
    }

    [Fact]
    public async Task A_Chip_Mismatch_Aborts_With_R_Before_Any_Line_Is_Written()
    {
        // Bootloader says uppercase 'B' (dsPIC33EP512GP); image targets EP256.
        var tnc = new FakeBootloaderTnc { BootloaderVersion = 'B' };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(NinoTncFlashFailure.ChipMismatch);
        ex.BootloaderVersion.Should().Be('B');
        ex.BootloaderChip.Should().Be(NinoTncChipVariant.Dspic33Ep512);
        ex.HexTargetChip.Should().Be(NinoTncChipVariant.Dspic33Ep256);

        tnc.LinesReceived.Should().BeEmpty("nothing may be written to the wrong chip");
        tnc.Writes.Last().Should().Equal(new[] { (byte)'R' }, "the modem must be returned to its firmware");
    }

    [Fact]
    public async Task A_Non_Letter_Version_Reply_Aborts_With_R()
    {
        var tnc = new FakeBootloaderTnc { BootloaderVersion = '?' };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(NinoTncFlashFailure.BootloaderVersionUnsupported);
        tnc.LinesReceived.Should().BeEmpty();
        tnc.Writes.Last().Should().Equal(new[] { (byte)'R' });
    }

    [Theory]
    [InlineData('N', NinoTncFlashFailure.LineChecksumRejected)]
    [InlineData('F', NinoTncFlashFailure.FlashRejected)]
    [InlineData('X', NinoTncFlashFailure.InvalidCharacterRejected)]
    public async Task A_Terminal_Line_Reply_Fails_The_Flash_With_Context(char reply, NinoTncFlashFailure expected)
    {
        var tnc = new FakeBootloaderTnc
        {
            LineReply = (index, _) => index == 2 ? reply : null,
        };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(expected);
        ex.LinesWritten.Should().Be(2, "two lines were accepted before the rejection");
        ex.ResponseByte.Should().Be((byte)reply);
        ex.BootloaderVersion.Should().Be('a');
    }

    [Fact]
    public async Task Mid_Transfer_Silence_Fails_As_NoResponse()
    {
        var tnc = new FakeBootloaderTnc
        {
            LineReply = (index, _) => index == 1 ? '\0' : null, // '\0' = stay silent
        };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(NinoTncFlashFailure.NoResponse);
        ex.LinesWritten.Should().Be(1);
    }

    [Fact]
    public async Task No_Ready_Signal_After_Entry_Times_Out_And_Attempts_R()
    {
        var clock = new ManualClock();
        var tnc = new FakeBootloaderTnc
        {
            EnterBootloaderOnCommand = false,             // entry command is ignored
            OnReadWhenEmpty = () => clock.Advance(TimeSpan.FromSeconds(5)), // a real 5 s read timeout
        };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant, clock);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(NinoTncFlashFailure.BootloaderEntryTimeout);
        tnc.Writes.Last().Should().Equal(new[] { (byte)'R' }, "best-effort return to the application firmware");
        tnc.LinesReceived.Should().BeEmpty();
    }

    [Fact]
    public async Task A_TNC_That_Never_Goes_Quiet_Aborts_The_Drain()
    {
        var clock = new ManualClock();
        var port = new ChatteringPort(clock);
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => port, Instant, clock);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        var ex = (await act.Should().ThrowAsync<NinoTncFlashException>()).Which;
        ex.Failure.Should().Be(NinoTncFlashFailure.SerialBufferNeverQuiet);
        port.Writes.Should().BeEmpty("no command may be sent into an un-drained line");
    }

    [Fact]
    public async Task An_Invalid_Image_Never_Opens_The_Port()
    {
        bool opened = false;
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ =>
        {
            opened = true;
            return new FakeBootloaderTnc();
        }, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", new byte[] { 0x00, 0xC0 });

        (await act.Should().ThrowAsync<NinoTncFlashException>())
            .Which.Failure.Should().Be(NinoTncFlashFailure.HexImageInvalid);
        opened.Should().BeFalse();
    }

    [Fact]
    public async Task Cancelling_Mid_Transfer_Strands_The_Bootloader_And_A_Rerun_Recovers_It()
    {
        var tnc = new FakeBootloaderTnc();
        using var cts = new CancellationTokenSource();
        tnc.LineReply = (index, _) =>
        {
            if (index == 1)
            {
                cts.Cancel(); // operator abandons the flash mid-transfer
            }
            return null;
        };
        var flasher = new BootloaderNinoTncFirmwareFlasher(_ => tnc, Instant);

        var act = async () => await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image, cancellationToken: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        tnc.InBootloader.Should().BeTrue("an abandoned transfer strands the bootloader");
        tnc.Writes.Last().Should().NotEqual(new[] { (byte)'R' },
            "once the transfer began, 'R' must not be attempted — the old firmware is partially erased");

        // The documented recovery: run the flash again. The stranded probe
        // ('R' → 'K') resumes without the KISS-side entry handshake.
        tnc.LineReply = null;
        tnc.ResetTransferState();
        var result = await flasher.FlashAsync("/dev/ttyFAKE", TinyEp256Image);

        result.ResumedStrandedBootloader.Should().BeTrue();
        result.LinesWritten.Should().Be(4);
    }

    private sealed class ProgressCollector : IProgress<NinoTncFlashProgress>
    {
        private readonly List<NinoTncFlashProgress> reports = [];

        public List<NinoTncFlashProgress> Reports
        {
            get { lock (reports) { return reports.ToList(); } }
        }

        public void Report(NinoTncFlashProgress value)
        {
            lock (reports) { reports.Add(value); }
        }
    }

    /// <summary>A manually advanced <see cref="TimeProvider"/> so the
    /// elapsed-time budgets (drain abort, entry timeout) can be crossed
    /// deterministically without real waiting.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }

    /// <summary>A port that never goes quiet — every read produces another
    /// byte, one simulated second apart (a busy radio channel).</summary>
    private sealed class ChatteringPort(ManualClock clock) : INinoTncBootloaderSerialPort
    {
        public List<byte[]> Writes { get; } = [];

        public string PortName => "/dev/ttyFAKE";

        public int ReadByte()
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return 0x55;
        }

        public void Write(ReadOnlySpan<byte> bytes) => Writes.Add(bytes.ToArray());

        public void DiscardInBuffer()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A scripted NinoTNC + dsPIC bootloader: starts in KISS mode (ignores
    /// stray bytes, swallows GETALL probes, enters the bootloader on
    /// <c>C0 0D 37 C0</c>) — or already stranded in the bootloader — and
    /// then speaks the bootloader protocol ('R'→'K' probe, 'V'→version,
    /// per-line 'K', 'Z' on the end-of-file record, overridable per line).
    /// </summary>
    private sealed class FakeBootloaderTnc : INinoTncBootloaderSerialPort
    {
        private readonly Queue<byte> pendingReplies = new();
        private readonly StringBuilder lineBuffer = new();

        public bool InBootloader { get; set; }

        public bool EnterBootloaderOnCommand { get; set; } = true;

        public char BootloaderVersion { get; set; } = 'a';

        /// <summary>Per-line reply override: null = default ('K', or 'Z' on
        /// the end-of-file record); '\0' = stay silent; else that byte.</summary>
        public Func<int, string, char?>? LineReply { get; set; }

        /// <summary>Invoked when a read finds nothing — the fake's stand-in
        /// for one full read-timeout of silence (advance a ManualClock here).</summary>
        public Action? OnReadWhenEmpty { get; set; }

        public List<byte[]> Writes { get; } = [];

        public List<string> LinesReceived { get; } = [];

        public string PortName => "/dev/ttyFAKE";

        public void ResetTransferState()
        {
            LinesReceived.Clear();
            lineBuffer.Clear();
        }

        public int ReadByte()
        {
            if (pendingReplies.TryDequeue(out byte b))
            {
                return b;
            }
            OnReadWhenEmpty?.Invoke();
            return -1;
        }

        public void DiscardInBuffer() => pendingReplies.Clear();

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Writes.Add(bytes.ToArray());

            if (!InBootloader)
            {
                if (bytes.SequenceEqual(NinoTncCommands.BuildBootloaderEntryKissFrame()) && EnterBootloaderOnCommand)
                {
                    InBootloader = true;
                    pendingReplies.Enqueue((byte)'K');
                }
                // Everything else (stray 'R', GETALL probes) is swallowed —
                // the KISS firmware answers GETALL, but the flasher discards
                // the output anyway, so silence is equivalent here.
                return;
            }

            // Bootloader mode. Single-byte control probes only make sense
            // outside a line ('R'/'V' are not in the Intel-HEX alphabet).
            if (bytes.Length == 1 && lineBuffer.Length == 0 && bytes[0] == (byte)'R')
            {
                pendingReplies.Enqueue((byte)'K');
                return;
            }
            if (bytes.Length == 1 && lineBuffer.Length == 0 && bytes[0] == (byte)'V')
            {
                pendingReplies.Enqueue((byte)BootloaderVersion);
                return;
            }

            foreach (byte b in bytes)
            {
                if (b != (byte)'\n')
                {
                    lineBuffer.Append((char)b);
                    continue;
                }

                string line = lineBuffer.ToString();
                lineBuffer.Clear();
                int index = LinesReceived.Count;
                LinesReceived.Add(line);

                char? overridden = LineReply?.Invoke(index, line);
                char reply = overridden
                    ?? (string.Equals(line, NinoTncFirmwareHexImage.EndOfFileRecord, StringComparison.OrdinalIgnoreCase) ? 'Z' : 'K');
                if (reply != '\0')
                {
                    pendingReplies.Enqueue((byte)reply);
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
