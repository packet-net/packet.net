namespace Packet.Node.Core.Configuration;

/// <summary>
/// ARDOP virtual-TNC service: an ardopcf-compatible TCP host interface (command socket + data
/// socket on <c>port+1</c>) backed by a dedicated soundmodem audio device, so external ARDOP hosts
/// (BPQ's <c>DRIVER=ARDOP</c>, Pat, Winlink Express) can drive this node's soundcard/FlexRadio as an
/// ARDOP modem. Mirrors the pdn-soundmodem daemon's <c>ardop</c> block. Off by default.
/// </summary>
public sealed record ArdopConfig
{
    /// <summary>Enable the ARDOP virtual TNC.</summary>
    public bool Enabled { get; init; }

    /// <summary>Audio device: an ALSA device (e.g. <c>default</c>, <c>plughw:1,0</c>) or a
    /// <c>flex:&lt;radio&gt;[:slice][@station]</c> FlexRadio device.</summary>
    public string Device { get; init; } = "default";

    /// <summary>Capture sample rate (ALSA only; a <c>flex:</c> device supplies its own DAX clock).
    /// Must be a positive multiple of ARDOP's 12000 Hz DSP rate.</summary>
    public int CaptureRate { get; init; } = 48000;

    /// <summary>TCP bind address for the ARDOP host interface.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>Command-socket TCP port (the data socket listens on <c>Port+1</c>). Default 8515.</summary>
    public int Port { get; init; } = 8515;

    /// <summary>PTT control spec (ALSA only; empty = VOX). A <c>flex:</c> device keys itself.</summary>
    public string Ptt { get; init; } = "";

    /// <summary>FlexRadio slice tuning when <see cref="Device"/> is a <c>flex:</c> device.</summary>
    public SoundModemFlexConfig? Flex { get; init; }
}
