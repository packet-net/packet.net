namespace Packet.Node.Core.Console;

/// <summary>
/// A bidirectional byte stream to a connected user, plus the metadata the node
/// console needs — independent of how the user reached us. The command service
/// runs over this interface and over this interface <b>only</b>, so the prompt
/// logic never depends on AX.25.
/// </summary>
/// <remarks>
/// Two implementations exist in slice 1: an AX.25 adapter wrapping an
/// <c>Ax25Session</c> (the over-the-air service path) and a telnet adapter
/// wrapping a TCP socket (the local dial-in). Both surface received user bytes
/// via <see cref="ReadAsync"/> and accept outbound bytes via
/// <see cref="WriteAsync"/>; <see cref="Completion"/> signals the far end going
/// away, regardless of transport.
/// </remarks>
public interface INodeConnection : IAsyncDisposable
{
    /// <summary>An identifier for the peer — a callsign for AX.25, a remote
    /// endpoint string for telnet. For logging and the <c>Info</c> command.</summary>
    string PeerId { get; }

    /// <summary>Which transport this connection arrived on.</summary>
    NodeTransportKind TransportKind { get; }

    /// <summary>
    /// Read the next chunk of inbound bytes from the peer. Returns an empty
    /// buffer when the connection has closed (EOF) — the command loop treats
    /// that as "the user is gone". Never throws on a normal close.
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Send bytes to the peer. Segmentation / framing is the
    /// implementation's concern (the AX.25 adapter routes through the listener's
    /// segmentation-aware send).</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// A task that completes when the peer disconnects (or the connection is
    /// otherwise torn down). The command loop races its read against this so a
    /// peer-initiated drop unblocks it promptly.
    /// </summary>
    Task Completion { get; }
}

/// <summary>The transport a <see cref="INodeConnection"/> arrived on.</summary>
public enum NodeTransportKind
{
    /// <summary>An over-the-air AX.25 connected-mode session.</summary>
    Ax25,

    /// <summary>A local telnet dial-in over TCP.</summary>
    Telnet,

    /// <summary>An end-to-end NET/ROM L4 virtual circuit (across the network).</summary>
    NetRom,
}
