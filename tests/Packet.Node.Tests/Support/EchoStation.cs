using System.Text;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A bare AX.25 station that accepts an inbound connect and, on the first data it
/// receives, sends a fixed reply back — the "third station" the node connects OUT
/// to, used to prove the console's connect-OUT relays both ways.
/// </summary>
public sealed class EchoStation : IAsyncDisposable
{
    private readonly Ax25Listener listener;
    private readonly string reply;
    private volatile bool sawConnect;

    public EchoStation(IKissModem modem, Callsign myCall, string reply)
    {
        this.reply = reply;
        listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = myCall,
            ConfigureSession = WireSession,
        }, TimeProvider.System);
        listener.SessionAccepted += (_, e) => sawConnect = true;
    }

    /// <summary>True once a peer has connected to this station.</summary>
    public bool SawConnect => sawConnect;

    public async Task StartAsync()
    {
        await listener.StartAsync().ConfigureAwait(false);
        listener.AcceptIncoming = true;
    }

    private void WireSession(Ax25Session session)
    {
        session.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is DataLinkConnectIndication)
            {
                sawConnect = true;
            }
            else if (sig is DataLinkDataIndication)
            {
                // Echo a fixed reply on the first inbound data.
                listener.SendData(session, Encoding.UTF8.GetBytes(reply));
            }
        };
    }

    public async ValueTask DisposeAsync() => await listener.DisposeAsync().ConfigureAwait(false);
}
