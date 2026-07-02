// Packet.Tune — bench CLI for tuning a NinoTNC + radio pair using the firmware's
// remote-diagnostics primitives (GETALL/GETVER/GETRSSI/STOPTX, [TARPNstat arming,
// CQBEEP-N tone requests).
//
// Bench rig: 2× NinoTNC (USB, /dev/ttyACM*) audio-wired to 2× Tait TM8100-series
// mobiles whose CCDI serial ports are on USB dongles (/dev/ttyUSB*), radios linked
// through ~100 dB of attenuation on one frequency.
//
// Subcommands:
//   verify-control <tncPort> [callsign]        confirm the TNC is under software control:
//                                              DIP positions + config mode from GETALL, then a
//                                              commanded-vs-measured TXDELAY check via the
//                                              preamble-word-count register delta
//   measure <tncPort> [ccdiPort] [callsign]    GETVER + GETRSSI baseline (n=5); with a CCDI
//                                              port also the radio's RSSI / noise floor
//   deviation <localTnc> <remoteTnc> [callsign]
//                                              TX-deviation tuning loop (local bench flavour:
//                                              both TNCs on this host). Arms the REMOTE TNC's
//                                              CQBEEP responder via its own serial, triggers
//                                              CQBEEP-8 from the LOCAL TNC, meters the tone
//                                              with GETRSSI on the local TNC, and prompts the
//                                              operator between runs to adjust the remote's
//                                              TX-DEV pot. The internet/SDM-coordinated remote
//                                              flavour (each end runs half this loop) is a
//                                              documented follow-up.

if (args.Length >= 2 && args[0] == "verify-control")
{
    return await Packet.Tune.VerifyControl.Run(args[1], args.Length > 2 ? args[2] : "N0CALL");
}
if (args.Length >= 2 && args[0] == "measure")
{
    return await Packet.Tune.Measure.Run(args[1], args.Length > 2 ? args[2] : null);
}
if (args.Length >= 3 && args[0] == "deviation")
{
    return await Packet.Tune.Deviation.Run(args[1], args[2], args.Length > 3 ? args[3] : "N0CALL");
}

Console.WriteLine("usage:");
Console.WriteLine("  verify-control <tncPort> [callsign=N0CALL]");
Console.WriteLine("  measure <tncPort> [ccdiPort]");
Console.WriteLine("  deviation <localTnc> <remoteTnc> [callsign=N0CALL]");
Console.WriteLine();
Console.WriteLine("deviation tunes the REMOTE TNC's TX-DEV pot: the remote end is armed and");
Console.WriteLine("emits the 440 Hz tone; the local end triggers it and meters the received");
Console.WriteLine("audio level. Run it with the pot you are adjusting on the <remoteTnc> side.");
return 2;
