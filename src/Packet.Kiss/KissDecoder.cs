namespace Packet.Kiss;

/// <summary>
/// Stateful KISS frame decoder. Push bytes as they arrive; pull completed
/// frames out. Maintains the in-progress frame buffer + escape state across
/// calls so callers can push arbitrarily small chunks.
/// </summary>
/// <remarks>
/// The in-progress buffer is bounded by <see cref="MaxFrameLength"/>. KISS has
/// no length field - a frame ends at the next FEND - so a peer (or a noise burst,
/// or a mis-set baud rate) that never sends one would otherwise grow the buffer
/// without limit, and <see cref="List{T}.Clear"/> keeps the grown capacity, so the
/// memory stayed retained afterwards (packet-net/packet.net#696). Over the bound
/// the partial frame is dropped, the buffer released, and bytes are discarded
/// until the next FEND resynchronises the stream.
/// </remarks>
public sealed class KissDecoder
{
    /// <summary>
    /// Default bound on a single KISS frame's decoded length, in octets. Comfortably
    /// above anything AX.25 produces - a maximum-size frame is 8 digipeaters (56) +
    /// addresses/control/PID (18) + the §6.7.2 maximum N1 of 256, so ~330 - while
    /// still small enough that a frameless stream cannot exhaust memory.
    /// </summary>
    public const int DefaultMaxFrameLength = 4096;

    private const int InitialCapacity = 256;

    private readonly List<byte> currentFrame = new(InitialCapacity);
    private bool inEscape;

    // True while discarding a stream we have lost sync with (an oversize frame):
    // every byte is dropped until the next FEND starts a fresh frame.
    private bool resynchronising;
    private long oversizeFramesDropped;

    /// <summary>Create a decoder bounded by <see cref="DefaultMaxFrameLength"/>.</summary>
    public KissDecoder()
        : this(DefaultMaxFrameLength)
    {
    }

    /// <summary>Create a decoder with an explicit maximum frame length, in octets.</summary>
    /// <param name="maxFrameLength">Largest decoded frame to accept; must be positive.</param>
    public KissDecoder(int maxFrameLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameLength);
        MaxFrameLength = maxFrameLength;
    }

    /// <summary>Largest decoded frame this decoder will accept, in octets.</summary>
    public int MaxFrameLength { get; }

    /// <summary>
    /// How many partial frames have been discarded for exceeding
    /// <see cref="MaxFrameLength"/>. A non-zero, growing count means the stream is
    /// not KISS (wrong baud rate, a raw-serial peer, line noise) - worth logging by
    /// a driver that wants to surface it.
    /// </summary>
    public long OversizeFramesDropped => oversizeFramesDropped;

    /// <summary>
    /// Push a chunk of received bytes through the decoder. Each completed
    /// KISS frame (anything between two FENDs that isn't empty) is added to
    /// the returned list. Empty frames (FEND FEND) are silently dropped, as
    /// KISS implementations commonly use leading FENDs as a re-sync prefix.
    /// </summary>
    public IReadOnlyList<KissFrame> Push(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<KissFrame>();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];

            if (resynchronising)
            {
                // Nothing between the overrun and the next FEND can be a frame.
                if (b == KissFraming.Fend)
                {
                    resynchronising = false;
                    inEscape = false;
                }
                continue;
            }

            if (inEscape)
            {
                inEscape = false;
                switch (b)
                {
                    case KissFraming.Tfend:
                        currentFrame.Add(KissFraming.Fend);
                        break;
                    case KissFraming.Tfesc:
                        currentFrame.Add(KissFraming.Fesc);
                        break;
                    default:
                        // Per spec: receivers should be lenient with malformed
                        // escape sequences. Drop the byte and continue.
                        break;
                }
                DropIfOversize();
                continue;
            }

            switch (b)
            {
                case KissFraming.Fend:
                    if (currentFrame.Count > 0)
                    {
                        if (TryFinish(out var frame))
                        {
                            frames.Add(frame);
                        }
                        currentFrame.Clear();
                    }
                    // else: empty inter-frame FEND, ignore
                    break;
                case KissFraming.Fesc:
                    inEscape = true;
                    break;
                default:
                    currentFrame.Add(b);
                    DropIfOversize();
                    break;
            }
        }
        return frames;
    }

    /// <summary>Discard any partially-decoded frame state.</summary>
    public void Reset()
    {
        currentFrame.Clear();
        ReleaseBuffer();
        inEscape = false;
        resynchronising = false;
    }

    // Over the bound the partial frame is unusable: drop it, hand the memory back,
    // count it, and skip everything up to the next FEND.
    private void DropIfOversize()
    {
        if (currentFrame.Count <= MaxFrameLength)
        {
            return;
        }

        oversizeFramesDropped++;
        currentFrame.Clear();
        ReleaseBuffer();
        inEscape = false;
        resynchronising = true;
    }

    // List.Clear keeps the grown capacity, so a single oversize burst would retain
    // its memory for the life of the decoder. Give it back.
    private void ReleaseBuffer()
    {
        if (currentFrame.Capacity > InitialCapacity)
        {
            currentFrame.Capacity = InitialCapacity;
        }
    }

    private bool TryFinish(out KissFrame frame)
    {
        // Spec requires at least a command byte. Anything shorter is
        // framing garbage — drop it.
        if (currentFrame.Count < 1)
        {
            frame = default;
            return false;
        }

        byte commandByte = currentFrame[0];
        byte port = (byte)((commandByte >> 4) & 0x0F);
        var command = (KissCommand)(commandByte & 0x0F);
        var payload = currentFrame.Skip(1).ToArray();
        frame = new KissFrame(port, command, payload);
        return true;
    }
}
