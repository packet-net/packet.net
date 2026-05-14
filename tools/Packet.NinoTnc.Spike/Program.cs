using System.Diagnostics;
using System.IO.Ports;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

// NinoTNC hardware-loop spike.
//
// Two NinoTNCs are USB-attached on COM6 / COM8 at 57600 8N1, MODE DIPs set to
// 1111 ("software control") with TXDELAY pots at minimum. Their audio paths
// are wired to each other.
//
// Goal: prove we can SETHW both into a compatible mode (mode 6, 1200 AFSK
// AX.25 — the most lenient over an audio link) and pass an AX.25 UI frame
// from one to the other, in both directions. SETHW uses the +16 "RAM only"
// offset to avoid hammering flash during dev.

const int BaudRate = 57600;
const byte Mode = 6;                 // 1200 AFSK AX.25 — robust on a plain audio link.
const bool PersistToFlash = false;   // RAM-only, do not burn the flash.

string portA = args.Length > 0 ? args[0] : "COM6";
string portB = args.Length > 1 ? args[1] : "COM8";

if (args.Length > 0 && args[0] == "driver")
{
    return await DriverSpike.Run(args.Length > 1 ? args[1] : "COM6", args.Length > 2 ? args[2] : "COM8");
}
if (args.Length > 0 && args[0] == "test-shape")
{
    return await TestShape.Run(args.Length > 1 ? args[1] : "COM6", args.Length > 2 ? args[2] : "COM8");
}

Console.WriteLine($"NinoTNC spike — A={portA}, B={portB}, baud={BaudRate}, mode={Mode} (+16 non-persist)");

using var a = OpenPort(portA);
using var b = OpenPort(portB);

// SETHW on both — instruct the TNC to switch operating mode without writing
// it to flash. Drain any boot chatter first.
Drain(a);
Drain(b);

var setHwFrame = NinoTncSetHardware.BuildKissFrame(Mode, PersistToFlash);
Console.WriteLine($"SETHW frame: {BitConverter.ToString(setHwFrame)}");
a.Write(setHwFrame, 0, setHwFrame.Length);
b.Write(setHwFrame, 0, setHwFrame.Length);

// Brief settle. The firmware needs a moment after a mode switch.
Thread.Sleep(500);
Drain(a);
Drain(b);

// Construct a UI frame: SRC=M0LTE-1  DST=TEST-2  info="HELLO FROM A".
var ax25FromA = Ax25Frame.Ui(
    destination: new Callsign("TEST", 2),
    source: new Callsign("M0LTE", 1),
    info: "HELLO FROM A"u8);

var ax25FromB = Ax25Frame.Ui(
    destination: new Callsign("TEST", 2),
    source: new Callsign("M0LTE", 2),
    info: "HELLO FROM B"u8);

bool aToB = RunOneDirection(a, b, ax25FromA, label: "A → B");
bool bToA = RunOneDirection(b, a, ax25FromB, label: "B → A");

Console.WriteLine();
Console.WriteLine($"RESULT: A→B {(aToB ? "OK" : "FAIL")}, B→A {(bToA ? "OK" : "FAIL")}");
return aToB && bToA ? 0 : 1;

static SerialPort OpenPort(string name)
{
    var port = new SerialPort(name, BaudRate, Parity.None, 8, StopBits.One)
    {
        ReadTimeout = 100,
        WriteTimeout = 1000,
        Handshake = Handshake.None,
        DtrEnable = true,
        RtsEnable = true,
    };
    port.Open();
    Console.WriteLine($"opened {name}");
    return port;
}

static void Drain(SerialPort port)
{
    var buf = new byte[1024];
    while (port.BytesToRead > 0)
    {
        port.Read(buf, 0, Math.Min(buf.Length, port.BytesToRead));
    }
}

static bool RunOneDirection(SerialPort tx, SerialPort rx, Ax25Frame frame, string label)
{
    Console.WriteLine();
    Console.WriteLine($"--- {label} ---");

    byte[] payload = frame.ToBytes();
    byte[] kiss = KissEncoder.Encode(port: 0, command: KissCommand.Data, payload: payload);
    Console.WriteLine($"sent  ({payload.Length}B AX.25 / {kiss.Length}B wire): {BitConverter.ToString(payload)}");

    Drain(rx);
    tx.Write(kiss, 0, kiss.Length);

    var decoder = new KissDecoder();
    var sw = Stopwatch.StartNew();
    var deadline = TimeSpan.FromSeconds(8);   // 1200 AFSK + TX delay → expect ~1-2s.
    var buf = new byte[256];
    while (sw.Elapsed < deadline)
    {
        if (rx.BytesToRead == 0)
        {
            Thread.Sleep(20);
            continue;
        }
        int n = rx.Read(buf, 0, Math.Min(buf.Length, rx.BytesToRead));
        var frames = decoder.Push(buf.AsSpan(0, n));
        foreach (var f in frames)
        {
            Console.WriteLine($"recv  port={f.Port} cmd={f.Command} payloadLen={f.Payload.Length}: {BitConverter.ToString(f.Payload)}");
            if (f.Command != KissCommand.Data)
            {
                continue;
            }
            if (!Ax25Frame.TryParse(f.Payload, out var parsed))
            {
                Console.WriteLine("  (received KISS Data did not parse as AX.25)");
                continue;
            }
            string info = System.Text.Encoding.ASCII.GetString(parsed.Info.Span);
            Console.WriteLine($"  AX.25 src={parsed.Source.Callsign} dst={parsed.Destination.Callsign} info=\"{info}\"");
            if (parsed.Source.Callsign == frame.Source.Callsign &&
                parsed.Destination.Callsign == frame.Destination.Callsign &&
                info == System.Text.Encoding.ASCII.GetString(frame.Info.Span))
            {
                Console.WriteLine($"  MATCH ({sw.ElapsedMilliseconds} ms)");
                return true;
            }
        }
    }

    Console.WriteLine($"  TIMEOUT after {sw.ElapsedMilliseconds} ms");
    return false;
}
