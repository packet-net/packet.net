using FsCheck.Xunit;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Phase A1 — connection setup / teardown under loss. Independent of the SREJ
/// data-recovery space (ax25spec#40), so it fuzzes a different part of the
/// stack: the SABM/UA and DISC/UA handshakes must always reach a terminal
/// state (Connected or Disconnected) under any finite handshake loss — never
/// hang forever in an Awaiting* / AwaitingRelease state.
/// </summary>
public class ConnectionLifecycleProperties
{
    [Property(MaxTest = 300)]
    public bool Connect_under_finite_handshake_loss_reaches_terminal(int seedBudget, int seedPattern)
    {
        int budget = Mod(seedBudget, 8);   // 0..7 dropped handshake frames
        var rng = new Random(seedPattern);
        var h = TwoStationHarness.Build(n2: 10);

        int dropsLeft = budget;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.6) { dropsLeft--; return true; } return false; };

        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();
        for (int r = 0; r < 30 && IsAwaiting(h.A.State); r++) h.AdvanceT1();

        // A must not be stuck mid-handshake: a finite loss burst resolves to
        // either an established link or a clean give-up.
        return h.A.State is "Connected" or "Disconnected";
    }

    [Property(MaxTest = 300)]
    public bool Disconnect_under_finite_loss_reaches_disconnected(int seedBudget, int seedPattern)
    {
        int budget = Mod(seedBudget, 8);
        var rng = new Random(seedPattern);
        var h = TwoStationHarness.Build(n2: 10);
        h.Connect();   // clean establish first

        int dropsLeft = budget;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.6) { dropsLeft--; return true; } return false; };

        h.A.Session.PostEvent(new DlDisconnectRequest());
        h.Settle();
        for (int r = 0; r < 30 && h.A.State != "Disconnected"; r++) h.AdvanceT1();

        // A initiated release; under finite loss it must reach Disconnected.
        return h.A.State == "Disconnected";
    }

    private static bool IsAwaiting(string s) =>
        s is "AwaitingConnection" or "AwaitingV22Connection" or "AwaitingRelease";

    private static int Mod(int v, int m) => (int)(((long)v % m + m) % m);
}
