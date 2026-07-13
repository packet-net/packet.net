# Packet.Rig.Hamlib

[`IRigControl`](https://www.nuget.org/packages/Packet.Rig) over **hamlib's NET rigctl
protocol** — the TCP text protocol served by `rigctld` (default port 4532). Pure managed
sockets; **no native libhamlib dependency**, so no ABI churn, no per-RID native packaging, and
one client reaches every rig hamlib supports plus the ecosystem of rigctld-protocol emulators
(wfview, SDR++, GQRX, SparkSDR, skycatd, nCAT, …; only real rigctld is tested today).

```csharp
await using var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions
{
    Host = "127.0.0.1",
    Port = 4532,
});

Console.WriteLine($"{rig.Info.Manufacturer} {rig.Info.Model}");   // from \dump_caps
await rig.SetFrequencyAsync(14_074_000);
await rig.SetModeAsync(RigMode.PktUsb, passbandHz: 3000);
var swr = await rig.ReadSwrAsync();
var watts = await rig.ReadRfPowerWattsAsync();                    // hamlib ≥ 4.4 rigs

// Escape hatches below the common subset:
var strength = await rig.ReadLevelAsync("STRENGTH");
var vfo = await rig.TransactRawAsync("v");
```

## Behaviour notes

- Every command uses hamlib's **Extended Response Protocol** (deterministic `RPRT n`
  terminators). `\chk_vfo` is probed at connect, so daemons running `--vfo` work transparently
  (`currVFO` is injected).
- Capabilities and identity come from `\dump_caps` at connect. Advertised capabilities are the
  backend's statement of intent — a rig can still reject at runtime, surfacing as
  `RigCommandException` with the hamlib error name (`RIG_ENAVAIL (-11)` …).
- One TCP connection, commands serialised in arrival order. On any transport fault, timeout, or
  cancellation mid-command the connection is dropped and the **next command re-dials** —
  rigctld holds all rig state, so redial is free.
- If this client keyed the transmitter, disposal sends a best-effort unkey (`T 0`) first.
- rigctld has **no authentication** — the default host is loopback on purpose.

## Testing your consumer

The dummy rig is the ecosystem's standard harness: `rigctld -m 1 --set-conf=static_data=1`
serves a stateful fake Kenwood-ish rig (fresh state 145 MHz / FM; deterministic meters:
RFPOWER_METER 0.5, RFPOWER_METER_WATTS 50.0). This package's own integration tests do exactly
that and skip when `rigctld` is not installed (`apt install libhamlib-utils`).

Part of [Packet.NET](https://github.com/packet-net/packet.net).
