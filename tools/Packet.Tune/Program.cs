// Packet.Tune — bench CLI for tuning a NinoTNC + radio pair using the firmware's
// remote-diagnostics primitives (GETALL/GETVER/STOPTX, [TARPNstat arming,
// CQBEEP-N tone requests) and the Packet.Tune.Core remote-tuning assistant
// (SDM- or WebSocket-coordinated deviation sessions, capability doctor).
//
// Bench rig: 2× NinoTNC (USB, /dev/ttyACM*) audio-wired to 2× Tait TM8100-series
// mobiles whose CCDI serial ports are on USB dongles (/dev/ttyUSB*), radios linked
// through ~100 dB of attenuation on one frequency.
//
// Subcommands:
//   verify-control <tncPort> [callsign]        confirm the TNC is under software control:
//                                              DIP positions + config mode from GETALL, then a
//                                              commanded-vs-measured TXDELAY check. Pins a
//                                              known-good mode first (--mode N, default 6;
//                                              --keep-mode skips) — a stale post-flash mode 0
//                                              yields a false "pot override" verdict.
//   measure <tncPort> [ccdiPort]               GETVER (+ GETRSSI baseline where the firmware
//                                              still has it — removed in 3.44); with a CCDI
//                                              port also the radio's RSSI / noise floor
//   deviation <localTnc> <remoteTnc> [callsign]
//                                              TX-deviation tuning loop (local bench flavour,
//                                              GETRSSI-based — firmware 3.41 only)
//   doctor <tncPort> [ccdiPort] [--json]       capability probes for the whole stack, each
//                                              with a one-line remedy
//   deviation-sdm --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId>
//                                              remote deviation session coordinated over Tait
//                                              SDMs (radio-native FFSK; no internet)
//   deviation-remote --role tuned|meter --tnc <port> [--radio <ccdi>] --rendezvous <ws url> [--pin N]
//                                              remote deviation session coordinated over a
//                                              WebSocket rendezvous relay (spoken 6-digit PIN)
//   rendezvous --listen <port>                 run the PIN-rendezvous relay

using Packet.Tune;

return args switch
{
    ["verify-control", var tnc, .. var rest] => await VerifyControl.Run(tnc, rest),
    ["measure", var tnc, .. var rest] => await Measure.Run(tnc, rest.Length > 0 ? rest[0] : null),
    ["deviation", var local, var remote, .. var rest] =>
        await Deviation.Run(local, remote, rest.Length > 0 ? rest[0] : "N0CALL"),
    ["doctor", var tnc, .. var rest] => await DoctorCommand.Run(tnc, rest),
    ["deviation-sdm", .. var rest] => await DeviationAssist.RunSdm(rest),
    ["deviation-remote", .. var rest] => await DeviationAssist.RunRemote(rest),
    ["rendezvous", .. var rest] => await RendezvousCommand.Run(rest),
    ["radio-reset", var ccdi] => await RadioResetCommand.Run(ccdi),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage:");
    Console.WriteLine("  verify-control <tncPort> [callsign=N0CALL] [--mode N=6] [--keep-mode]");
    Console.WriteLine("  measure <tncPort> [ccdiPort]");
    Console.WriteLine("  deviation <localTnc> <remoteTnc> [callsign=N0CALL]   (GETRSSI-based; firmware 3.41 only)");
    Console.WriteLine("  doctor <tncPort> [ccdiPort] [--json] [--callsign X] [--mode N=6]");
    Console.WriteLine("  deviation-sdm --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId>");
    Console.WriteLine("                [--callsign X] [--burst N=5] [--verbose]");
    Console.WriteLine("  deviation-remote --role tuned|meter --tnc <port> [--radio <ccdi>] --rendezvous <ws url>");
    Console.WriteLine("                [--pin NNNNNN] [--callsign X] [--burst N=5]");
    Console.WriteLine("  rendezvous --listen <port>");
    Console.WriteLine("  radio-reset <ccdiPort>       (CCR enter+exit soft reset — un-wedges SDM auto-ack)");
    Console.WriteLine();
    Console.WriteLine("deviation-* tune the TX-DEV pot at the TUNED end: the meter end requests");
    Console.WriteLine("frame bursts, measures decode rate / IL2P FEC deltas / ADC clipping / CCDI");
    Console.WriteLine("RSSI, and advises UP/DN/OK. Run both TNCs in mode 7 (IL2P) so the FEC");
    Console.WriteLine("signal is meaningful. The coordination channel (SDM or internet) does not");
    Console.WriteLine("depend on the pot under tune.");
    return 2;
}
