using System.Text;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A bare AX.25 station that accepts an inbound connect and, on the first data it
/// receives, sends a fixed reply back - the "third station" the node connects OUT
/// to, used to prove the console's connect-OUT relays both ways.
/// </summary>
public sealed class EchoStation : IAsyncDisposable
{
    private readonly Ax25Listener listener;
    private readonly string reply;
    private volatile bool sawConnect;

    public EchoStation(IAx25Transport transport, Callsign myCall, string reply)
    {
        this.reply = reply;
        listener = new Ax25Listener(transport, new Ax25ListenerOptions
        {
            MyCall = myCall,
            ConfigureSession = WireSession,
            // Small N2 bounds the connect backstop at 30 s, T1V stays spec default -
            // see RemoteStation / TestAx25Timing / Wait.cs (#47 flake).
            N2 = TestAx25Timing.StationN2,
        }, TimeProvider.System);
        listener.SessionAccepted += (_, e) => sawConnect = true;
    }

    /// <summary>True once a peer has connected to this station.</summary>
    public bool SawConnect => sawConnect;

    /// <summary>True when every session this station holds is back in <c>Disconnected</c>: the
    /// settle point a test must wait for before re-dialling, so the peer's own release traffic
    /// (its DISC/UA/DM tail) cannot land on the next SABM and read as a refusal.</summary>
    public bool IsIdle => listener.ActiveSessions.All(s => s.CurrentState == "Disconnected");

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
