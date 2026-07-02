// Tait TM8100/TM8200 CCDI hardware spike.
//
// Bench rig: 2× NinoTNC (USB, /dev/ttyACM*) audio-wired to 2× Tait TM8100-series mobiles whose
// CCDI serial ports (mic socket, 28800 8N1) are on USB dongles (/dev/ttyUSB*). The radios talk
// to each other through ~100 dB of attenuation on one frequency.
//
// Subcommands:
//   inventory <ccdiPort>...                    identity + telemetry dump per radio, JSON to
//                                              artifacts/radio-capabilities/<serial>.json
//   dcd <ccdiPort>                             stream carrier-sense/PTT/RSSI events live
//   rf-rssi <txTnc> <rxTnc> <rxRadioCcdi> [frames] [mode]
//                                              the headline PoC: AX.25 frames over RF, each
//                                              received frame stamped with RSSI + SNR from the
//                                              receiving radio, plus DCD lead-time stats

if (args.Length >= 2 && args[0] == "inventory")
{
    return await Packet.Tait.Spike.Inventory.Run(args[1..]);
}
if (args.Length >= 2 && args[0] == "dcd")
{
    return await Packet.Tait.Spike.DcdMonitor.Run(args[1]);
}
if (args.Length >= 4 && args[0] == "rf-rssi")
{
    int frames = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 8;
    byte mode = args.Length > 5 ? byte.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : (byte)6;
    return await Packet.Tait.Spike.RfRssiLoop.Run(args[1], args[2], args[3], frames, mode);
}

Console.WriteLine("usage:");
Console.WriteLine("  inventory <ccdiPort>...");
Console.WriteLine("  dcd <ccdiPort>");
Console.WriteLine("  rf-rssi <txTnc> <rxTnc> <rxRadioCcdi> [frames=8] [mode=6]");
return 2;
