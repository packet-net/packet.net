namespace Packet.Tait.Codeplug;

/// <summary>Per-channel bandwidth. Stored as a 2-bit index in the channel bit-stream.</summary>
public enum Bandwidth : byte
{
    /// <summary>12.5 kHz (narrow).</summary>
    Narrow = 0,

    /// <summary>20 kHz (medium).</summary>
    Medium = 1,

    /// <summary>25 kHz (wide).</summary>
    Wide = 2,
}

/// <summary>Per-channel transmit power level (a 3-bit index). Actual watts depend on band,
/// calibration and radio variant.</summary>
public enum PowerLevel : byte
{
    Off = 0,
    VeryLow = 1,
    Low = 2,
    Medium = 3,
    High = 4,
}

/// <summary>Transparent-mode terminal serial baud rate (a 3-bit index in the data/signalling block).
/// The CPS "Baud Rate (FFSK transparent mode)" / DataTMBaudRate field - the rate the serial terminal
/// clocks bytes in Transparent mode, not the over-air modem rate (see <see cref="FfskModemRate"/>).</summary>
public enum FfskBaud : byte
{
    Baud1200 = 0,
    Baud2400 = 1,
    Baud4800 = 2,
    Baud9600 = 3,
    Baud14400 = 4,
    Baud19200 = 5,
    Baud28800 = 6,
}

/// <summary>Over-air FFSK modem data rate (the CPS "FFSK Baud Rate" / FFSKBaudRate field, a 2-bit
/// index). Must match at both ends of a Transparent link. Distinct from the terminal serial rate
/// (<see cref="FfskBaud"/>).</summary>
public enum FfskModemRate : byte
{
    Baud1200 = 0,
    Baud1200A75 = 1,
    Baud2400 = 2,
}

/// <summary>Which data mode the radio boots into (the CPS "Power-up state" field, a 2-bit index).
/// Command Mode gives a CCDI host command channel; the transparent modes bring the radio up as a byte
/// pipe.</summary>
public enum DataPowerupMode : byte
{
    CommandMode = 0,
    FfskTransparent = 1,
    ThsdTransparent = 2,
}

/// <summary>Serial flow control for a data mode (the CPS "Flow Control" combo, a 2-bit index). Applies
/// per mode: command, FFSK transparent, and THSD transparent each have their own.</summary>
public enum DataFlowControl : byte
{
    None = 0,
    Software = 1,
    Hardware = 2,
}

/// <summary>THSD layer-2 protocol (the CPS "Layer 2 Protocol" combo, a 2-bit index).</summary>
public enum ThsdLayer2 : byte
{
    None = 0,
    Simple = 1,
    Total = 2,
}

/// <summary>TOTAL transparent-mode service class (the CPS "TOTAL Service" combo, a 1-bit index).</summary>
public enum TotalModeService : byte
{
    Unconfirmed = 0,
    Confirmed = 1,
}

/// <summary>GPS poll-response channel selection (the CPS "Poll Response Channel Type" combo, a 1-bit
/// index): reply on the current channel, or on a dedicated one.</summary>
public enum GpsPollResponseChannelType : byte
{
    Current = 0,
    Dedicated = 1,
}

/// <summary>Data-port routing: where the data modem's audio is sourced and sunk. Low two bits
/// of a byte in the data/signalling block.</summary>
public enum DataPort : byte
{
    /// <summary>Front-panel microphone connector.</summary>
    Mic = 0,

    /// <summary>Auxiliary / options connector.</summary>
    Aux = 1,

    /// <summary>Internal options board.</summary>
    InternalOptions = 2,
}

/// <summary>RX tap-out unmute condition. Stored in bits [3:1] of a byte in the audio block.</summary>
public enum TapOutUnmute : byte
{
    BusyDetect = 0x02,
    BusyDetectSubaudible = 0x04,
    RxMuteOpen = 0x06,
    ExceptOnPtt = 0x08,
}

/// <summary>Per-channel subaudible signalling type (a 2-bit index): no tone, CTCSS (analogue tone),
/// or DCS (digital code). The specific tone/code is a separate index into the radio's tone table.</summary>
public enum SubaudibleType : byte
{
    None = 0,
    Ctcss = 1,
    Dcs = 2,
}

/// <summary>Per-channel squelch / busy-detect tightness (the CPS "Squelch" column; the schema field
/// is RxBusyDetect). A 2-bit index.</summary>
public enum Squelch : byte
{
    Country = 0,
    City = 1,
    Hard = 2,
}

/// <summary>Per-channel transmit inhibit (a 2-bit index): never inhibit, inhibit while the channel is
/// busy, or inhibit unless the subaudible mute is open.</summary>
public enum TxInhibit : byte
{
    None = 0,
    Busy = 1,
    Mute = 2,
}
