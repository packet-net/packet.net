using System.Text;

namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// The native C# NinoTNC firmware flasher: drives the dsPIC bootloader
/// protocol that upstream <c>flashtnc.py</c> speaks, byte-for-byte compatible
/// with the sequence validated by repeated successful flashes on real
/// hardware (seven with the reference implementation on the bench rig, then
/// this implementation itself — see <c>docs/research/tait-ccdi-spike.md</c>).
/// </summary>
/// <remarks>
/// <para>The protocol, end to end:</para>
/// <list type="number">
///   <item>Open the port at 57 600 8N1, no flow control, 5 s read timeout.</item>
///   <item>Classify the hex image's target chip from its known
///     first-bootloader lines (<see cref="NinoTncFirmwareHexImage"/>) —
///     before touching the modem.</item>
///   <item>Drain the receive path until a full read-timeout of silence
///     (abort if it is still chattering after 15 s — busy radio channel).</item>
///   <item>Probe for a stranded bootloader: write <c>'R'</c>; a <c>'K'</c>
///     reply means the modem is already in the bootloader (a previous flash
///     was interrupted) and the entry handshake is skipped.</item>
///   <item>Otherwise: three payload-less GETALL probes (<c>C0 0B C0</c>,
///     0.5 s apart, discarding output) to exercise every buffer between the
///     dsPIC and us, drain again, then send the bootloader-entry command
///     (<c>C0 0D 37 C0</c>) and await the bootloader's <c>'K'</c> ready
///     signal (≤ 15 s).</item>
///   <item><c>'V'</c> queries the one-letter bootloader version: lowercase
///     means dsPIC33EP256GP, uppercase means dsPIC33EP512GP. A mismatch with
///     the image target aborts (<c>'R'</c> returns the modem to its current
///     firmware) — the wrong image bricks the chip.</item>
///   <item>The image is sent line-by-line as ASCII. The <em>first</em> line
///     goes char-by-char at 100 ms per character (the bootloader is erasing
///     the flash page). Each line is answered: <c>'K'</c> next line,
///     <c>'Z'</c> done, <c>'F'</c> flash failure, <c>'N'</c> bad line
///     checksum, <c>'X'</c> invalid character. A full image is ~16–17 k
///     lines, 2–4 minutes.</item>
///   <item>On <c>'Z'</c> the modem reboots into the new firmware. The first
///     boot also runs a bootloader self-update (~2 s), and the RAM operating
///     mode is cleared (boots mode 0) — re-verify with GETVER and re-apply
///     the mode via SETHW.</item>
/// </list>
/// <para>
/// <b>Cancellation:</b> honoured between protocol steps and between lines
/// (reads block up to the 5 s port timeout, so observation can lag by that
/// much). Cancelling <em>before</em> bootloader entry is completely safe.
/// Cancelling after entry — including mid-transfer — <b>strands the modem in
/// the bootloader</b> (LEDs dark, KISS silent, current firmware gone). That
/// state is recoverable without hardware tools: run <see cref="FlashAsync"/>
/// again and the stranded-bootloader probe resumes with a fresh transfer.
/// Where the flasher aborts by its own decision before any line was written
/// (entry timeout, version mismatch), it writes <c>'R'</c> to return the
/// modem to its existing firmware; it never writes <c>'R'</c> once the
/// transfer has begun, because the old firmware is already partially erased.
/// </para>
/// <para>
/// Failures throw <see cref="NinoTncFlashException"/> with the terminal
/// state classified (<see cref="NinoTncFlashFailure"/>), the accepted line
/// count, and the raw reply byte where one exists.
/// </para>
/// </remarks>
public sealed class BootloaderNinoTncFirmwareFlasher : INinoTncFirmwareFlasher
{
    private static readonly byte[] ResetProbe = [(byte)'R'];
    private static readonly byte[] VersionQuery = [(byte)'V'];

    private readonly Func<string, INinoTncBootloaderSerialPort> portFactory;
    private readonly NinoTncFlashTimings timings;
    private readonly TimeProvider clock;

    /// <summary>Create a flasher that opens real serial ports with the
    /// hardware-validated protocol timings.</summary>
    public BootloaderNinoTncFirmwareFlasher()
        : this(portName => SystemBootloaderSerialPort.Open(portName, NinoTncFlashTimings.Default.ReadTimeout))
    {
    }

    /// <summary>Test seam (InternalsVisibleTo <c>Packet.Kiss.NinoTnc.Tests</c>):
    /// substitute the port, shrink the timings, and/or fake the clock used
    /// for elapsed-time measurement.</summary>
    internal BootloaderNinoTncFirmwareFlasher(
        Func<string, INinoTncBootloaderSerialPort> portFactory,
        NinoTncFlashTimings? timings = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(portFactory);
        this.portFactory = portFactory;
        this.timings = timings ?? NinoTncFlashTimings.Default;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public Task<NinoTncFlashResult> FlashAsync(
        string portName,
        ReadOnlyMemory<byte> hexImage,
        IProgress<NinoTncFlashProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(portName);

        // Validate + classify before touching the modem: a bad image must
        // never get as far as opening the port.
        var image = NinoTncFirmwareHexImage.Parse(hexImage);

        // The protocol is blocking serial IO with real-time pacing — run it
        // on a worker so the caller's context stays responsive.
        return Task.Run(() => FlashCoreAsync(portName, image, progress, cancellationToken), CancellationToken.None);
    }

    private async Task<NinoTncFlashResult> FlashCoreAsync(
        string portName,
        NinoTncFirmwareHexImage image,
        IProgress<NinoTncFlashProgress>? progress,
        CancellationToken cancellationToken)
    {
        long startedAt = clock.GetTimestamp();
        using var port = portFactory(portName);

        // 1. Quiesce: nothing the modem says from before this moment matters.
        port.DiscardInBuffer();
        DrainUntilSilent(port, cancellationToken);

        // 2. Stranded-bootloader probe: 'R' answers 'K' only from the
        //    bootloader; the KISS firmware ignores a stray byte outside
        //    FEND framing.
        port.Write(ResetProbe);
        bool stranded = port.ReadByte() == 'K';

        if (!stranded)
        {
            // 3. Fill-and-flush: force the TNC to produce output while we
            //    discard it, to reset every buffer between the dsPIC and us.
            for (int i = 0; i < 3; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                port.Write(NinoTncCommands.BuildBareGetAllKissFrame());
                await Delay(timings.GetAllProbeSpacing, cancellationToken).ConfigureAwait(false);
                port.DiscardInBuffer();
            }
            DrainUntilSilent(port, cancellationToken);

            // 4. Bootloader entry. From here on an abandoned session leaves
            //    the modem stranded in the bootloader (recoverable: re-run,
            //    the stranded probe above picks it up).
            cancellationToken.ThrowIfCancellationRequested();
            port.Write(NinoTncCommands.BuildBootloaderEntryKissFrame());
            AwaitBootloaderReady(port, cancellationToken);
        }

        // 5. Version / chip-variant check.
        char version = ReadBootloaderVersion(port, cancellationToken);
        var bootloaderChip =
            char.IsAsciiLetterLower(version) ? NinoTncChipVariant.Dspic33Ep256 :
            char.IsAsciiLetterUpper(version) ? NinoTncChipVariant.Dspic33Ep512 :
            NinoTncChipVariant.Unknown;
        if (bootloaderChip == NinoTncChipVariant.Unknown)
        {
            await AbortToApplicationFirmwareAsync(port).ConfigureAwait(false);
            throw new NinoTncFlashException(
                NinoTncFlashFailure.BootloaderVersionUnsupported,
                $"Unsupported bootloader version reply 0x{(byte)version:X2} — expected a letter " +
                "(lowercase = dsPIC33EP256GP, uppercase = dsPIC33EP512GP). The modem was told to " +
                "return to its current firmware; nothing was written.",
                responseByte: (byte)version);
        }
        if (bootloaderChip != image.TargetChip)
        {
            await AbortToApplicationFirmwareAsync(port).ConfigureAwait(false);
            throw new NinoTncFlashException(
                NinoTncFlashFailure.ChipMismatch,
                $"Chip mismatch: the bootloader (version '{version}') reports {bootloaderChip} but the " +
                $"hex image targets {image.TargetChip}. Flashing the wrong variant bricks the modem — " +
                "refused. The modem was told to return to its current firmware; nothing was written.",
                bootloaderVersion: version,
                bootloaderChip: bootloaderChip,
                hexTargetChip: image.TargetChip);
        }

        // 6. The transfer. Point of no return: from the first line the old
        //    firmware is being erased/overwritten. Do NOT write 'R' on any
        //    failure past this point.
        int linesWritten = 0;
        var lines = image.Lines;
        progress?.Report(new NinoTncFlashProgress(0, lines.Count));
        for (int i = 0; i < lines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] lineBytes = Encoding.ASCII.GetBytes(lines[i] + "\n");
            if (i == 0)
            {
                // The bootloader erases the flash page while the first line
                // trickles in — pace it at one character per delay tick.
                foreach (byte ch in lineBytes)
                {
                    port.Write([ch]);
                    await Delay(timings.FirstLineCharDelay, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                port.Write(lineBytes);
            }

            int reply = ReadReplyWithin(port, timings.LineReplyTimeout, cancellationToken);
            switch (reply)
            {
                case 'K':
                    linesWritten++;
                    progress?.Report(new NinoTncFlashProgress(linesWritten, lines.Count));
                    continue;

                case 'Z':
                    // Done — the modem is rebooting into the new firmware
                    // (first boot: bootloader self-update, ~2 s, then KISS).
                    linesWritten++;
                    progress?.Report(new NinoTncFlashProgress(lines.Count, lines.Count));
                    return new NinoTncFlashResult(
                        bootloaderChip,
                        version,
                        linesWritten,
                        lines.Count,
                        clock.GetElapsedTime(startedAt),
                        stranded);

                case 'F':
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.FlashRejected,
                        $"The bootloader reported a flash write failure ('F') at line {i + 1} of {lines.Count}. " +
                        "Per upstream guidance the dsPIC may need replacement or an ICSP reflash. The modem is " +
                        "stranded in the bootloader; re-running the flash is safe to try once.",
                        linesWritten: linesWritten, responseByte: (byte)'F', bootloaderVersion: version);

                case 'N':
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.LineChecksumRejected,
                        $"The bootloader rejected line {i + 1} of {lines.Count} with a checksum failure ('N'). " +
                        "The hex file may be corrupt — re-download it. The modem is stranded in the bootloader; " +
                        "re-run the flash with a good image to recover.",
                        linesWritten: linesWritten, responseByte: (byte)'N', bootloaderVersion: version);

                case 'X':
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.InvalidCharacterRejected,
                        $"The bootloader rejected line {i + 1} of {lines.Count}: invalid character ('X'). " +
                        "The hex file may be corrupt — re-download it. The modem is stranded in the bootloader; " +
                        "re-run the flash with a good image to recover.",
                        linesWritten: linesWritten, responseByte: (byte)'X', bootloaderVersion: version);

                case < 0:
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.NoResponse,
                        $"The bootloader stopped answering at line {i + 1} of {lines.Count} " +
                        $"(no reply within {timings.LineReplyTimeout}). The modem is most likely stranded in the " +
                        "bootloader — re-run the flash to recover; if it keeps happening the dsPIC may need an " +
                        "ICSP reflash.",
                        linesWritten: linesWritten, bootloaderVersion: version);

                default:
                    throw new NinoTncFlashException(
                        NinoTncFlashFailure.UnexpectedResponse,
                        $"The bootloader answered line {i + 1} of {lines.Count} with unexpected byte 0x{reply:X2} " +
                        "(expected K/Z/F/N/X). The modem is most likely stranded in the bootloader — re-run the " +
                        "flash to recover.",
                        linesWritten: linesWritten, responseByte: (byte)reply, bootloaderVersion: version);
            }
        }

        // Unreachable for a valid image: the end-of-file record elicits 'Z'
        // (and NinoTncFirmwareHexImage refuses images without one).
        throw new NinoTncFlashException(
            NinoTncFlashFailure.ImageEndedWithoutCompletion,
            $"All {lines.Count} lines were accepted but the bootloader never signalled completion ('Z'). " +
            "The modem may be stranded in the bootloader — re-run the flash to recover.",
            linesWritten: linesWritten, bootloaderVersion: version);
    }

    /// <summary>
    /// Read until a full read-timeout passes with nothing received. Throws
    /// <see cref="NinoTncFlashFailure.SerialBufferNeverQuiet"/> if bytes are
    /// still arriving after <see cref="NinoTncFlashTimings.DrainAbortAfter"/>.
    /// </summary>
    private void DrainUntilSilent(INinoTncBootloaderSerialPort port, CancellationToken cancellationToken)
    {
        long start = clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (port.ReadByte() < 0)
            {
                return; // one full read-timeout of silence
            }
            if (clock.GetElapsedTime(start) > timings.DrainAbortAfter)
            {
                throw new NinoTncFlashException(
                    NinoTncFlashFailure.SerialBufferNeverQuiet,
                    $"The TNC was still producing serial data after {timings.DrainAbortAfter} of draining. " +
                    "Ensure it is not receiving traffic (quieten or detach the radio) and retry. " +
                    "Nothing was written.");
            }
        }
    }

    /// <summary>Await the bootloader's 'K' ready signal after the entry command.</summary>
    private void AwaitBootloaderReady(INinoTncBootloaderSerialPort port, CancellationToken cancellationToken)
    {
        long start = clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (port.ReadByte() == 'K')
            {
                return;
            }
            if (clock.GetElapsedTime(start) > timings.BootloaderEntryTimeout)
            {
                // Best-effort return to the application firmware, in case the
                // modem did enter the bootloader and only the 'K' was lost.
                TryWrite(port, ResetProbe);
                throw new NinoTncFlashException(
                    NinoTncFlashFailure.BootloaderEntryTimeout,
                    $"No bootloader ready signal ('K') within {timings.BootloaderEntryTimeout} of the entry " +
                    "command. Nothing was written. If the modem's LEDs are now dark it is stranded in the " +
                    "bootloader — re-running the flash will recover it via the stranded-bootloader probe.");
            }
        }
    }

    /// <summary>Query and read the bootloader's one-letter version, skipping
    /// any residual 'K' ready signals.</summary>
    private char ReadBootloaderVersion(INinoTncBootloaderSerialPort port, CancellationToken cancellationToken)
    {
        port.Write(VersionQuery);
        long start = clock.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int b = port.ReadByte();
            if (b >= 0 && b != 'K')
            {
                return (char)b;
            }
            if (clock.GetElapsedTime(start) > timings.VersionReplyTimeout)
            {
                TryWrite(port, ResetProbe);
                throw new NinoTncFlashException(
                    NinoTncFlashFailure.BootloaderVersionUnreadable,
                    $"The bootloader did not answer the version query ('V') within {timings.VersionReplyTimeout}. " +
                    "Nothing was written. Try detaching and re-attaching the modem's USB cable, then re-run " +
                    "the flash (the stranded-bootloader probe will find it if it is still in the bootloader).");
            }
        }
    }

    /// <summary>Wait up to <paramref name="timeout"/> for a single reply byte;
    /// -1 when none arrived.</summary>
    private int ReadReplyWithin(INinoTncBootloaderSerialPort port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long start = clock.GetTimestamp();
        while (true)
        {
            int b = port.ReadByte();
            if (b >= 0)
            {
                return b;
            }
            if (clock.GetElapsedTime(start) >= timeout)
            {
                return -1;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Pre-transfer abort: tell the bootloader to return to the
    /// application firmware ('R') and give it a moment to settle.</summary>
    private async Task AbortToApplicationFirmwareAsync(INinoTncBootloaderSerialPort port)
    {
        TryWrite(port, ResetProbe);
        await Delay(timings.ResetSettleDelay, CancellationToken.None).ConfigureAwait(false);
    }

    private static void TryWrite(INinoTncBootloaderSerialPort port, byte[] bytes)
    {
        try
        {
            port.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            // Best-effort only — the abort path must surface the original
            // protocol failure, not a secondary write error.
        }
    }

    private static Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
}
