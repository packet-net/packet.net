using System.Text;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A minimal "other end of the radio" for the node integration tests: a bare
/// <see cref="Ax25Listener"/> on one <see cref="InMemoryRadio"/> endpoint that
/// can connect out to the node and exchange console text. Captures received
/// bytes so a test can assert the node's banner / command replies arrive. This
/// is the remote operator's TNC, opposite the node under test.
/// </summary>
public sealed class RemoteStation : IAsyncDisposable
{
    private readonly Ax25Listener listener;
    private readonly StringBuilder received = new();
    private readonly object gate = new();
    private Ax25Session? session;

    public RemoteStation(IAx25Transport transport, Callsign myCall)
    {
        listener = new Ax25Listener(transport, new Ax25ListenerOptions
        {
            MyCall = myCall,
            ConfigureSession = s => s.DataLinkSignalEmitted += OnSignal,
            // Small N2 bounds ConnectAsync's (N2+1)·T1V backstop at 30 s instead of
            // the 66 s spec default, so a starved handshake fails fast instead of
            // hanging the CI job. T1V stays the spec default (see TestAx25Timing /
            // Wait.cs — the #47 flake).
            N2 = TestAx25Timing.StationN2,
        }, TimeProvider.System);
    }

    /// <summary>Everything the node has sent us so far, decoded as text.</summary>
    public string ReceivedText { get { lock (gate) return received.ToString(); } }

    public async Task StartAsync()
    {
        await listener.StartAsync().ConfigureAwait(false);
        listener.AcceptIncoming = false;   // this side only dials out
    }

    /// <summary>Connect out to the node and keep the session for sending lines.</summary>
    public async Task ConnectAsync(Callsign nodeCall, CancellationToken ct = default)
    {
        session = await listener.ConnectAsync(nodeCall, ct).ConfigureAwait(false);
        // ConnectAsync attaches via ConfigureSession only for sessions IT builds;
        // make sure our handler is attached (idempotent — duplicate handlers would
        // double-count, so attach exactly once here for the outbound session).
        session.DataLinkSignalEmitted -= OnSignal;
        session.DataLinkSignalEmitted += OnSignal;
    }

    /// <summary>Send a console line (CR-terminated) to the node.</summary>
    public void SendLine(string line)
    {
        if (session is null) throw new InvalidOperationException("not connected");
        listener.SendData(session, Encoding.UTF8.GetBytes(line + "\r"));
    }

    /// <summary>True once the node has sent us text containing <paramref name="needle"/>.</summary>
    public bool Saw(string needle) => ReceivedText.Contains(needle, StringComparison.Ordinal);

    public string CurrentState => session?.CurrentState ?? "(none)";

    private void OnSignal(object? sender, DataLinkSignal sig)
    {
        if (sig is DataLinkDataIndication di)
        {
            lock (gate) received.Append(Encoding.UTF8.GetString(di.Info.Span));
        }
    }

    public async ValueTask DisposeAsync() => await listener.DisposeAsync().ConfigureAwait(false);
}
