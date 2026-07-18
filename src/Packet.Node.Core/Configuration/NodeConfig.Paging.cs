namespace Packet.Node.Core.Configuration;

/// <summary>
/// POCSAG paging service: a TCP line server (PAGE/HEARD) that transmits and receives POCSAG pages
/// over a dedicated soundmodem audio device. Mirrors the pdn-soundmodem daemon's <c>paging</c>
/// block. Off by default.
/// </summary>
public sealed record PagingConfig
{
    /// <summary>Enable the POCSAG paging service.</summary>
    public bool Enabled { get; init; }

    /// <summary>Audio device: an ALSA device (e.g. <c>default</c>, <c>plughw:1,0</c>) or a
    /// <c>flex:&lt;radio&gt;[:slice][@station]</c> FlexRadio device.</summary>
    public string Device { get; init; } = "default";

    /// <summary>Capture sample rate (ALSA only; a <c>flex:</c> device supplies its own DAX clock).
    /// Must be a positive multiple of the 12000 Hz paging DSP rate.</summary>
    public int CaptureRate { get; init; } = 48000;

    /// <summary>TCP bind address for the paging line server.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port for the paging line server.</summary>
    public int Port { get; init; } = 8106;

    /// <summary>POCSAG baud: 512, 1200 (DAPNET) or 2400.</summary>
    public int Baud { get; init; } = 1200;

    /// <summary>Invert the baseband polarity.</summary>
    public bool InvertPolarity { get; init; }

    /// <summary>PTT control spec (ALSA only; empty = VOX). A <c>flex:</c> device keys itself.</summary>
    public string Ptt { get; init; } = "";

    /// <summary>FlexRadio slice tuning when <see cref="Device"/> is a <c>flex:</c> device.</summary>
    public SoundModemFlexConfig? Flex { get; init; }
}
