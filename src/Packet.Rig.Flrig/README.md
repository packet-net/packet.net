# Packet.Rig.Flrig

[`IRigControl`](https://www.nuget.org/packages/Packet.Rig) over **flrig's XML-RPC server**
(default `127.0.0.1:12345`). The client contract deliberately mirrors hamlib's own flrig
backend (`rigs/dummy/flrig.c`) — the most battle-tested flrig client — including its meter
conversions, so both report identical values.

```csharp
await using var rig = await FlrigRig.ConnectAsync();

Console.WriteLine(rig.Info.Model);          // rig.get_xcvr
Console.WriteLine(rig.FlrigVersion);        // main.get_version

await rig.SetFrequencyAsync(7_074_000);

// flrig mode names are rig-native — enumerate before you set:
Console.WriteLine(string.Join(", ", rig.SupportedModes));   // e.g. LSB, USB, CW, DATA-U
await rig.SetModeAsync(RigMode.From("DATA-U"));

var swr = await rig.ReadSwrAsync();         // rig.get_SWR, falling back to get_swrmeter
var watts = await rig.ReadRfPowerWattsAsync();

// Anything else on flrig's method table:
await rig.CallRawAsync("rig.cat_string", ["FA;"]);   // raw CAT passthrough
```

## flrig dialect notes (handled for you)

- Frequency **gets** return strings of Hz (`rig.get_vfo`); **sets** take XML-RPC doubles
  (`main.set_frequency`).
- Mode names are whatever the attached transceiver calls them. Setting a mode outside
  `SupportedModes` throws instead of being silently ignored (flrig drops unknown mode strings
  without complaint — and the server-side "Ignore xmlrpc mode changes" option drops all of them).
- The get side reports no passband width (`PassbandHz` is `null`), and widths cannot be set.
- SWR uses the newer direct `rig.get_SWR` when present, else interpolates the 0–100
  `rig.get_swrmeter` deflection through hamlib's anchor table (capped at 10:1).
- Power: `rig.get_pwrmeter` (0–100 deflection) scaled by `rig.get_pwrmeter_scale` — relative =
  deflection/100 × scale, watts = deflection × scale (hamlib's exact contract).
- Meters are only meaningful while transmitting; flrig answers 0 when idle or unmetered.
- Poll-only, multiple clients supported by flrig, state can change under you.

Testing: flrig is a GUI app with no headless mode — this package's tests script an in-process
XML-RPC fake (the established technique for flrig clients).

Part of [Packet.NET](https://github.com/packet-net/packet.net).
