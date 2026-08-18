namespace Packet.Node.Core.Console;

/// <summary>
/// Wraps an interactive, character-at-a-time <see cref="INodeConnection"/> - the
/// telnet dial-in, which is in char-at-a-time mode so the node can echo and
/// line-edit locally - so the connected-mode relay sends one I-frame per
/// <em>line</em> to a packet link instead of one per keystroke. Without this,
/// every character typed during a <c>Connect</c> goes out as its own AX.25
/// I-frame (≈16 bytes of framing + an ack round-trip per payload byte) - useless
/// on a real channel.
/// </summary>
/// <remarks>
/// <see cref="ReadAsync"/> feeds the inner connection's reads through a
/// <see cref="LineAssembler"/> (so backspace/DEL editing stays in step with the
/// connection's server-side echo) and yields one complete line at a time,
/// terminated with a single CR - the AX.25 line ending, which also gives the
/// packet peer a clean terminator with no stray LF (the input half of the
/// telnet↔AX.25 line-discipline split; cf. #51). Every other member delegates to
/// the inner connection, so its per-keystroke echo still gives the user live
/// feedback while typing. This is a non-owning wrapper: the connect handler still
/// owns the inner connection (the local command loop resumes on it after the
/// relay), so <see cref="DisposeAsync"/> is a no-op.
/// </remarks>
internal sealed class LineBufferingNodeConnection : INodeConnection
{
    private const byte Cr = (byte)'\r';
    private readonly INodeConnection inner;
    private readonly LineAssembler assembler = new();
    private readonly Queue<ReadOnlyMemory<byte>> pending = new();

    public LineBufferingNodeConnection(INodeConnection inner)
        => this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc/>
    public string PeerId => inner.PeerId;

    /// <inheritdoc/>
    public NodeTransportKind TransportKind => inner.TransportKind;

    /// <inheritdoc/>
    public Task Completion => inner.Completion;

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (pending.Count == 0)
        {
            var chunk = await inner.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (chunk.IsEmpty)
            {
                return ReadOnlyMemory<byte>.Empty;   // EOF - a half-typed line is dropped
            }
            foreach (var line in assembler.Push(chunk))
            {
                // The assembler yields the edited line without its terminator;
                // forward it with a single CR - one I-frame per line, clean AX.25
                // line ending for the packet peer.
                var framed = new byte[line.Length + 1];
                Array.Copy(line, framed, line.Length);
                framed[line.Length] = Cr;
                pending.Enqueue(framed);
            }
        }
        return pending.Dequeue();
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        => inner.WriteAsync(bytes, cancellationToken);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
