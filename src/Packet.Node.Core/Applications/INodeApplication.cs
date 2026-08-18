using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// A node application: given a connected user's <see cref="INodeConnection"/> session and
/// the <see cref="NodeAppContext"/> describing how they arrived, run an interactive (or
/// one-shot) exchange over the byte stream, returning when the app is done or the peer
/// drops. The built-in node console (<see cref="NodeCommandService"/>) is application #0;
/// external apps are spawned out-of-process and bridged in over stdio (see
/// <see cref="ExternalProcessApplication"/>). The node never links an app's code - this is
/// the single seam every app routes through, generalising what the console already does.
/// </summary>
public interface INodeApplication
{
    /// <summary>
    /// Run over <paramref name="session"/> until the app finishes or the peer drops. The
    /// caller owns <paramref name="session"/> (and disposes it) - the app must not. An
    /// implementation must be total: a fault is logged and the session returns to the node
    /// prompt, never crashing the node.
    /// </summary>
    Task RunAsync(INodeConnection session, NodeAppContext context, CancellationToken cancellationToken = default);
}
