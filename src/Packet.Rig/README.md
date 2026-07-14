# Packet.Rig

Station-rig (CAT) control abstraction for amateur-radio transceivers: get/set **frequency** and
**mode**, **PTT**, **SWR / RF-power metering**, and receive-side **DCD / signal-strength**
reads, all behind capability probes.

```csharp
IRigControl rig = await RigctldRig.ConnectAsync();   // Packet.Rig.Hamlib
// IRigControl rig = await FlrigRig.ConnectAsync();  // Packet.Rig.Flrig

await rig.SetFrequencyAsync(14_074_000);
await rig.SetModeAsync(RigMode.PktUsb);

if (rig.Capabilities.HasFlag(RigCapabilities.SwrMeter))
{
    var swr = await rig.ReadSwrAsync();              // dimensionless ratio, 1.0 = perfect
}

if (rig.Capabilities.HasFlag(RigCapabilities.DcdRead))
{
    var busy = await rig.ReadDcdAsync();             // true = carrier present / channel busy
}
```

## Design

- **`IRigControl`** is the cross-backend common subset. Everything a backend might lack is
  gated by `RigCapabilities` flags discovered at connect time; calling an unadvertised member
  throws `NotSupportedException`.
- **`RigMode`** wraps a canonical token (hamlib vocabulary: `USB`, `LSB`, `CW`, `PKTUSB`, …)
  with pass-through for backend-native names (`RigMode.From("DATA-U")`) — mode vocabularies
  genuinely diverge across backends, so this is not a closed enum.
- **Receive-side reads** — `ReadDcdAsync` (true = carrier present / channel busy) and
  `ReadSignalStrengthDbmAsync` (dBm) are what the packet stack's carrier-sense seam needs;
  the `IRadioControl` bridge adapter that consumes them is separate work in `Packet.Radio`.
- **Errors** are typed: `RigConnectionException` (link down — retry is sane),
  `RigTimeoutException`, `RigCommandException` (the backend said no; carries its native code),
  `RigProtocolException` (unparseable reply).
- **Poll-only.** Current backends (rigctld, flrig) have no push channel; callers own their
  polling cadence.

This package is deliberately **dependency-free** — it does not pull in the rest of the
Packet.NET AX.25 stack. Backends:

- [`Packet.Rig.Hamlib`](https://www.nuget.org/packages/Packet.Rig.Hamlib) — hamlib's `rigctld`
  network protocol (any of hamlib's 200+ rigs, plus the many rigctld-protocol emulators).
- [`Packet.Rig.Flrig`](https://www.nuget.org/packages/Packet.Rig.Flrig) — flrig's XML-RPC server.
- [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait) — `TaitRigControl`, a
  partial view (PTT + relative RF-power meter) of a Tait TM8100/TM8200 over CCDI, demonstrating
  a backend that honestly advertises only a slice of the surface.

Part of [Packet.NET](https://github.com/packet-net/packet.net). Design and research notes:
`docs/research/rig-control-spike.md` in the repo.
