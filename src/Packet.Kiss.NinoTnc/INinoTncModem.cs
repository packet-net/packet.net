using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// The surface area an adaptive controller / session-layer caller needs
/// from a NinoTNC. Pulled out as an interface mainly to let
/// <see cref="AdaptiveNinoTncTransport"/> be unit-tested without opening a
/// real serial port, and to give a seam for alternate modem backings
/// (a fake hardware fixture in tests, a TCP-based modem proxy, etc.).
/// </summary>
public interface INinoTncModem
{
    /// <summary>Send a KISS Data frame, fire-and-forget at this layer.</summary>
    Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a frame in ACKMODE and await the TNC's TX-completion echo.
    /// </summary>
    Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? timeout = null,
        ushort? sequenceTag = null,
        CancellationToken cancellationToken = default);

    /// <summary>KISS TXDELAY (0x01), units of 10 ms.</summary>
    Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default);

    /// <summary>KISS PERSIST (0x02), 0..255.</summary>
    Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default);

    /// <summary>KISS SLOTTIME (0x03), units of 10 ms.</summary>
    Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default);

    /// <summary>
    /// KISS TXTAIL (0x04), units of 10 ms. Modern modems generally ignore
    /// this; the KISS TNC spec recommends 0 and the NinoTNC follows that.
    /// We surface a helper so the adaptive layer can drive it on
    /// experimental setups that care.
    /// </summary>
    Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default);
}
