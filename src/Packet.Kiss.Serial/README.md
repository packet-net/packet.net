# Packet.Kiss.Serial

> Generic serial-port KISS modem for any TNC that speaks standard KISS over USB-CDC or a hardware UART.

`KissSerialModem` opens a serial port, runs a background read pump, and surfaces inbound KISS frames as an async stream — implementing the neutral `IAx25Transport` seam so the rest of the stack can move AX.25 frames without caring that the wire is a serial KISS TNC. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Kiss.Serial
```

## Quick start
```csharp
using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Kiss.Serial;

// Open the port and start the background read pump (defaults to 57600 baud).
await using var modem = KissSerialModem.Open("/dev/ttyACM0");

// Apply CSMA channel-access parameters (KISS TXDELAY / PERSIST / SLOTTIME).
await modem.SetTxDelayAsync(30);      // 300 ms keyup delay (units of 10 ms)
await modem.SetPersistenceAsync(63);
await modem.SetSlotTimeAsync(10);     // 100 ms slot

// Send an AX.25 frame body. It's KISS-encoded (escaped + FEND-framed) as a Data frame.
await modem.SendFrameAsync(ax25FrameBytes);

// Pull every inbound KISS frame until the modem is disposed.
await foreach (KissFrame frame in modem.ReadFramesAsync())
{
    if (frame.Command == KissCommand.Data)
        Console.WriteLine($"RX {frame.Payload.Length} bytes on port {frame.Port}");
}
```

Because `KissSerialModem` implements `IAx25Transport`, you can also hand it to anything that takes a transport and consume only AX.25 Data frames (non-Data KISS commands are filtered out, and each frame is stamped with a receive time):

```csharp
IAx25Transport transport = modem;
await transport.SendAsync(ax25FrameBytes);
await foreach (Ax25InboundFrame inbound in transport.ReceiveAsync())
    Console.WriteLine($"AX.25 frame at {inbound.ReceivedAt}");
```

Plain serial KISS has no TX-completion signal, so this transport deliberately does **not** implement `ITxCompletionTransport` — sends are fire-and-forget. For NinoTNC-specific features (ACKMODE TX-completion correlation, SETHW mode switching, TX-Test frame classification) use `Packet.Kiss.NinoTnc` instead.

## Key types
- `KissSerialModem` — the serial KISS transport; `Open(portName, baudRate, timeProvider)` opens the port and starts the read pump. Implements `IAx25Transport`, `ICsmaChannelParams`, `IAsyncDisposable`.
- `KissSerialModem.ReadFramesAsync` / `FrameReceived` — pull (async stream) or push (event) access to inbound KISS frames.
- `KissSerialModem.SendFrameAsync` / `SendKissAsync` — send an AX.25 body as a KISS Data frame, or an arbitrary KISS command.
- `KissSerialModem.SetTxDelayAsync` / `SetPersistenceAsync` / `SetSlotTimeAsync` / `SetTxTailAsync` / `SetFullDuplexAsync` — the KISS CSMA channel-access parameters.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Kiss](https://www.nuget.org/packages/Packet.Kiss) — the KISS framing codec (encoder/decoder, `KissFrame`, `KissCommand`) this builds on.
- [Packet.Kiss.NinoTnc](https://www.nuget.org/packages/Packet.Kiss.NinoTnc) — NinoTNC-specific extensions (ACKMODE, SETHW, frame classification).
- [Packet.Ax25.Transport.Abstractions](https://www.nuget.org/packages/Packet.Ax25.Transport.Abstractions) — the `IAx25Transport` seam this implements.

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
