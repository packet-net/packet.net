namespace Packet.Ax25.Monitor;

/// <summary>
/// What a frame means for its link beyond its type: the things a display should draw attention
/// to. Set by <see cref="Ax25LinkObserver"/> on each <see cref="Ax25LinkEvent"/>; most frames
/// carry <see cref="None"/>.
/// </summary>
[Flags]
public enum Ax25LinkFlags
{
    /// <summary>An ordinary frame in an ordinary place.</summary>
    None = 0,

    /// <summary>An I frame whose N(S) this side has already sent: a retransmission, whether
    /// after a REJ or after a timeout.</summary>
    Resend = 1 << 0,

    /// <summary>A command with the P bit set: the sender wants an answer now. On an RR or RNR
    /// this is a station that has stopped hearing acknowledgements and is asking what got
    /// through, which is the retry that matters on a poor path.</summary>
    Poll = 1 << 1,

    /// <summary>A response with the F bit set: the answer to a poll.</summary>
    Final = 1 << 2,

    /// <summary>A REJ or SREJ: the receiver is asking for something to be sent again.</summary>
    Reject = 1 << 3,

    /// <summary>An RNR: the receiver has asked the sender to hold off.</summary>
    Busy = 1 << 4,

    /// <summary>The same call, hang-up or poll sent again because nothing answered the last
    /// one; <see cref="Ax25LinkEvent.Count"/> says how many so far.</summary>
    Repeat = 1 << 5,

    /// <summary>A jump in N(S): I frames went by that this observer did not hear.</summary>
    Missed = 1 << 6,

    /// <summary>A DM answering a call: the called station will not connect.</summary>
    Refused = 1 << 7,

    /// <summary>An FRMR, or a control octet no frame type owns: something on the link is
    /// malformed.</summary>
    ProtocolError = 1 << 8,

    /// <summary>This frame brought the link up.</summary>
    LinkUp = 1 << 9,

    /// <summary>This frame took the link down.</summary>
    LinkDown = 1 << 10,

    /// <summary>A digipeater's copy of the previous frame from this side: the same frame, with
    /// one more H bit set. Nothing on the link changed; it went round again.</summary>
    Digipeated = 1 << 11,

    /// <summary>A frame that makes no sense where the link stands: numbered traffic on a link
    /// that is down, an acknowledgement nothing asked for.</summary>
    Unexpected = 1 << 12,

    /// <summary>A call or a hang-up that nothing answered within the observer's
    /// <see cref="Ax25LinkObserverOptions.CallTimeout"/>, given up on. Not a frame: this is the
    /// observer's own conclusion, raised by <see cref="Ax25LinkObserver.Expire"/>, and the
    /// event's <see cref="Ax25LinkEvent.FrameType"/> is null.</summary>
    Timeout = 1 << 13,
}
