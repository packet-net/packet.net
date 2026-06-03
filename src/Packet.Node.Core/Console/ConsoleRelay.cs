namespace Packet.Node.Core.Console;

/// <summary>
/// Pumps bytes both ways between two <see cref="INodeConnection"/>s until either
/// end drops, then returns. Used by the console's <c>Connect</c> command to
/// bridge an inbound user to an outbound session, with no AX.25 knowledge — both
/// sides are just byte streams.
/// </summary>
public static class ConsoleRelay
{
    /// <summary>
    /// Relay <paramref name="a"/> ↔ <paramref name="b"/> until one side closes
    /// (read returns empty) or its <see cref="INodeConnection.Completion"/>
    /// fires, or <paramref name="cancellationToken"/> trips. Returns once the
    /// relay has wound down; does not dispose either connection.
    /// </summary>
    public static async Task PipeAsync(INodeConnection a, INodeConnection b, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = linked.Token;

        // One pump per direction. The first to finish (a drop on either side)
        // cancels the linked token so the other pump unblocks promptly.
        var aToB = PumpAsync(a, b, linked, ct);
        var bToA = PumpAsync(b, a, linked, ct);
        await Task.WhenAll(aToB, bToA).ConfigureAwait(false);
    }

    private static async Task PumpAsync(INodeConnection from, INodeConnection to, CancellationTokenSource linked, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadOnlyMemory<byte> chunk;
                try
                {
                    var readTask = from.ReadAsync(ct).AsTask();
                    var done = await Task.WhenAny(readTask, from.Completion, to.Completion).ConfigureAwait(false);
                    if (done != readTask)
                    {
                        // A connection completed — wind down this direction.
                        if (readTask.IsCompletedSuccessfully)
                        {
                            chunk = readTask.Result;   // a read also landed; deliver it then stop
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        chunk = await readTask.ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (chunk.IsEmpty)
                {
                    break;   // EOF on the read side
                }

                try
                {
                    await to.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    break;   // write side gone
                }
            }
        }
        finally
        {
            // Tear the other direction down too — the bridge is over.
            if (!linked.IsCancellationRequested)
            {
                await linked.CancelAsync().ConfigureAwait(false);
            }
        }
    }
}
