namespace Packet.Kiss;

/// <summary>
/// The surface area an adaptive controller / session-layer caller needs
/// from any KISS-speaking modem. Not NinoTNC-specific — works for any
/// modem that implements standard KISS plus the G8BPQ ACKMODE extension
/// (NinoTNC, QtSoundModem, Dire Wolf, etc.). The
/// <see cref="AdaptiveKissTransport"/> depends only on this interface so
/// alternate modems and fake-in-tests fixtures plug in cleanly.
/// </summary>
/// <remarks>
/// Mode-switching (KISS SETHW, byte semantics) is intentionally not on
/// this interface — it varies between modems. NinoTNC has a known mode
/// table and `+16` non-persist offset; Dire Wolf does not respond to
/// SETHW at all; QtSoundModem has its own scheme. Mode-aware helpers
/// live in modem-specific packages
/// (e.g. <c>Packet.Kiss.NinoTnc.NinoTncSerialPort.SetModeAsync</c>).
/// </remarks>
public interface IKissModem
{
    /// <summary>Send a KISS Data frame, fire-and-forget at this layer.</summary>
    Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Async stream of every inbound KISS frame the modem reports, until
    /// disposal or <paramref name="cancellationToken"/> fires. The default
    /// implementation returns an empty stream — modems that don't expose
    /// inbound frames (test stubs, write-only adapters) can leave this as
    /// the default and consumers that need RX simply won't see anything.
    /// </summary>
    /// <remarks>
    /// Surfaces all KISS commands (Data, TX-Test echoes, ACKMODE wrappers,
    /// modem diagnostics). Consumers typically filter to
    /// <see cref="KissCommand.Data"/> before parsing as AX.25.
    /// <see cref="Ax25Listener"/>-shape consumers expect this to be a
    /// long-running enumerable that yields frames as they arrive.
    /// </remarks>
    IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default)
#pragma warning disable CS1998 // async body, but the empty path doesn't need awaits
        => EmptyAsync(cancellationToken);
#pragma warning restore CS1998

    private static async IAsyncEnumerable<KissFrame> EmptyAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Yield zero frames. The await keeps the iterator state-machine
        // honest under cancellation — without it the compiler warns that
        // the async body has no awaits.
        await Task.CompletedTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        yield break;
    }

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
    /// this; the KISS TNC spec recommends 0. We expose a helper so the
    /// adaptive layer can drive it on experimental setups that care.
    /// </summary>
    Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default);
}
