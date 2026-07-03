namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// Every timing constant of the bootloader flash protocol, in one place.
/// The defaults are the hardware-validated values from upstream
/// <c>flashtnc.py</c> (seven successful flashes on the bench rig before this
/// implementation) — do not change them casually. Tests shrink them to keep
/// the scripted-fake suite fast; that is the only intended reason to deviate.
/// </summary>
internal sealed record NinoTncFlashTimings
{
    /// <summary>The port's per-read timeout. One full read timeout with
    /// nothing received is the "line is silent" signal the drain waits for.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Give up draining if the TNC is still chattering after this
    /// long (e.g. the radio channel keeps delivering frames).</summary>
    public TimeSpan DrainAbortAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Pause between the fill-and-flush GETALL probes.</summary>
    public TimeSpan GetAllProbeSpacing { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Maximum wait for the bootloader's ready signal
    /// (<c>'K'</c>) after the entry command.</summary>
    public TimeSpan BootloaderEntryTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum wait for the bootloader's one-letter reply to the
    /// <c>'V'</c> version query.</summary>
    public TimeSpan VersionReplyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Delay after each character of the image's <em>first</em>
    /// line — the bootloader performs the flash page erase while receiving
    /// it and must not be rushed.</summary>
    public TimeSpan FirstLineCharDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Maximum wait for the bootloader's per-line reply. Normal
    /// replies land in ~10 ms; upstream flashtnc waits indefinitely, we
    /// prefer a generous-but-finite budget.</summary>
    public TimeSpan LineReplyTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Settle time after telling an aborted bootloader to return
    /// to the application firmware (<c>'R'</c>).</summary>
    public TimeSpan ResetSettleDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The hardware-validated defaults.</summary>
    public static NinoTncFlashTimings Default { get; } = new();
}
