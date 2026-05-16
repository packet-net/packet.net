namespace Packet.Kiss;

/// <summary>
/// KISS command codes — the low nibble of the KISS frame's command byte.
/// (The high nibble is the multi-drop port number, 0–15, per the multi-drop
/// KISS extension.)
/// </summary>
public enum KissCommand : byte
{
    /// <summary>Data frame — HDLC payload to transmit / received from radio.</summary>
    Data = 0x0,

    /// <summary>TXDELAY, units of 10 ms. Default 50 (= 500 ms).</summary>
    TxDelay = 0x1,

    /// <summary>Persistence parameter (0–255). Default 63.</summary>
    Persistence = 0x2,

    /// <summary>Slot time, units of 10 ms. Default 10 (= 100 ms).</summary>
    SlotTime = 0x3,

    /// <summary>TX tail (obsolete on most modern TNCs).</summary>
    TxTail = 0x4,

    /// <summary>Full duplex flag (0 = half duplex, non-zero = full duplex).</summary>
    FullDuplex = 0x5,

    /// <summary>TNC-specific set-hardware payload.</summary>
    SetHardware = 0x6,

    /// <summary>ACKMODE (G8BPQ extension) — data with a 2-byte ack-tag prefix.</summary>
    AckMode = 0xC,

    /// <summary>Poll request (polled-mode extension).</summary>
    Poll = 0xE,
}
