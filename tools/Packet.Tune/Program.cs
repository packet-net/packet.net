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
//   transparent-doctor <ccdiPort> [peerCcdiPort] [--interrupt] [--json]
//                                              readiness of a TNC-less Tait FFSK Transparent
//                                              link: Transparent-mode enabled / +++ escape
//                                              recovers / baud-clean round-trip. Behavioral +
//                                              field-specific remedies. --interrupt is required
//                                              for the enter/escape/loopback probes (they enter
//                                              Transparent mode + transmit)
//   deviation-sdm --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId>
//                                              remote deviation session coordinated over Tait
//                                              SDMs (radio-native FFSK; no internet)
//   deviation-remote --role tuned|meter --tnc <port> [--radio <ccdi>] --rendezvous <ws url> [--pin N]
//                                              remote deviation session coordinated over a
//                                              WebSocket rendezvous relay (spoken 6-digit PIN)
//   rendezvous --listen <port>                 run the PIN-rendezvous relay
//   mode-survey <tncA> <tncB> <ccdiA> <ccdiB>  per-channel × per-IL2P+CRC-mode RF survey
//                                              (decode rate, latency, RX RSSI, IL2P counters);
//                                              always leaves the rig on channel 0 / mode 6
//   mode-coord --role coordinator|responder --tnc <port> --radio <ccdi> --peer <8charId>
//                                              renegotiate TNC mode (and radio channel) over
//                                              the radios' SDM side channel: propose/confirm/
//                                              commit, probe-verify both ways, revert-to-home
//                                              on any failure (--sequence m[@ch],… | --sweep)
//   txdelay-min --role coordinator|meter --tnc <port> --radio <ccdi> --peer <8charId>
//                                              minimise the coordinator's own TXDELAY over the
//                                              SDM side channel: step DOWN from --start, K
//                                              separate keyings per step, meter counts decodes
//                                              (+ as-heard pre-data via DCD); knee + margin
//                                              recommendation; --apply verifies + keeps it;
//                                              every exit path restores the original
//   set-mode <tncPort> <mode>                  SETHW one TNC's mode (+16 RAM-only default)
//                                              with settle frame + GETALL verify
//   radio-channel <ccdiPort> [channel]         report — or switch and verify — a Tait
//                                              radio's conventional channel
//   radio-health <ccdiPort> [--interval 10] [--duration 60] [--key-once [s]]
//                                              periodic radio-health sampling (averaged RSSI,
//                                              PA temperature, fwd/rev power-detector trend)
//                                              as a live table + min/median/max summary
//   flash-tnc <tncPort> <hexFile> [--yes]      flash NinoTNC firmware (native C# bootloader
//                                              protocol): chip-classifies the hex, refuses a
//                                              held port, GETVERs before/after, confirms
//                                              interactively unless --yes

using Packet.Tune;

return args switch
{
    ["verify-control", var tnc, .. var rest] => await VerifyControl.Run(tnc, rest),
    ["measure", var tnc, .. var rest] => await Measure.Run(tnc, rest.Length > 0 ? rest[0] : null),
    ["deviation", var local, var remote, .. var rest] =>
        await Deviation.Run(local, remote, rest.Length > 0 ? rest[0] : "N0CALL"),
    ["doctor", var tnc, .. var rest] => await DoctorCommand.Run(tnc, rest),
    ["transparent-doctor", var ccdi, .. var rest] => await TransparentDoctorCommand.Run(ccdi, rest),
    ["deviation-sdm", .. var rest] => await DeviationAssist.RunSdm(rest),
    ["deviation-remote", .. var rest] => await DeviationAssist.RunRemote(rest),
    ["rendezvous", .. var rest] => await RendezvousCommand.Run(rest),
    ["radio-reset", var ccdi] => await RadioResetCommand.Run(ccdi),
    ["mode-survey", var tncA, var tncB, var ccdiA, var ccdiB, .. var rest] =>
        await ModeSurveyCommand.Run(tncA, tncB, ccdiA, ccdiB, rest),
    ["mode-coord", .. var rest] => await ModeCoordCommand.Run(rest),
    ["txdelay-min", .. var rest] => await TxDelayMinCommand.Run(rest),
    ["hail", .. var rest] => await HailCommand.Run(rest),
    ["set-mode", var tnc, var mode, .. var rest] => await SetModeCommand.Run(tnc, mode, rest),
    ["radio-channel", var ccdi, .. var rest] => await RadioChannelCommand.Run(ccdi, rest),
    ["radio-health", var ccdi, .. var rest] => await RadioHealthCommand.Run(ccdi, rest),
    ["flash-tnc", var tnc, var hex, .. var rest] => await FlashTncCommand.Run(tnc, hex, rest),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage:");
    Console.WriteLine("  verify-control <tncPort> [callsign=N0CALL] [--mode N=6] [--keep-mode]");
    Console.WriteLine("  measure <tncPort> [ccdiPort]");
    Console.WriteLine("  deviation <localTnc> <remoteTnc> [callsign=N0CALL]   (GETRSSI-based; firmware 3.41 only)");
    Console.WriteLine("  doctor <tncPort> [ccdiPort] [--json] [--callsign X] [--mode N=6]");
    Console.WriteLine("  transparent-doctor <ccdiPort> [peerCcdiPort] [--interrupt] [--json] [--callsign X] [--baud N=28800]");
    Console.WriteLine("                               (readiness of a TNC-less Tait FFSK Transparent link:");
    Console.WriteLine("                                Transparent-mode enabled / +++ escape recovers / baud-clean.");
    Console.WriteLine("                                --interrupt ENTERS Transparent + transmits — a radio with");
    Console.WriteLine("                                'Ignore Escape Sequence' ON may need a POWER CYCLE)");
    Console.WriteLine("  deviation-sdm --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId>");
    Console.WriteLine("                [--callsign X] [--burst N=5] [--verbose]");
    Console.WriteLine("  deviation-remote --role tuned|meter --tnc <port> [--radio <ccdi>] --rendezvous <ws url>");
    Console.WriteLine("                [--pin NNNNNN] [--callsign X] [--burst N=5]");
    Console.WriteLine("  rendezvous --listen <port>");
    Console.WriteLine("  radio-reset <ccdiPort>       (CCR enter+exit soft reset — un-wedges SDM auto-ack)");
    Console.WriteLine("  mode-survey <tncA> <tncB> <ccdiA> <ccdiB> [--channels 0,1] [--rounds 5] [--json [path]]");
    Console.WriteLine("                               (IL2P+CRC modes only; always ends on channel 0 / mode 6)");
    Console.WriteLine("  mode-coord --role coordinator|responder --tnc <port> --radio <ccdi> --peer <8charId>");
    Console.WriteLine("             [--sequence m[@ch],…|--sweep] [--strict-bandwidth] [--channel-width narrow|wide]");
    Console.WriteLine("             [--home-mode 6] [--home-channel 0] [--probes 5] [--callsign X] [--verbose]");
    Console.WriteLine("                               (mode/channel renegotiation over the radios' SDM side");
    Console.WriteLine("                                channel; any failure reverts both ends to home)");
    Console.WriteLine("  txdelay-min --role coordinator|meter --tnc <port> --radio <ccdi> --peer <8charId>");
    Console.WriteLine("             [--start 500] [--step 40] [--min 20] [--probes 5] [--callsign X]");
    Console.WriteLine("             [--apply | --apply-at ms] [--verbose]");
    Console.WriteLine("                               (minimise the coordinator's OWN TXDELAY: sweep down,");
    Console.WriteLine("                                K SEPARATE keyings/step, meter counts decodes + as-heard");
    Console.WriteLine("                                pre-data; knee+margin recommendation; abort-safe restore)");
    Console.WriteLine("  hail --tnc <port> --radio <ccdi> --peer <8charId> [--callsign X] [--verbose]");
    Console.WriteLine("       [--respond]");
    Console.WriteLine("                               (query — or, with --respond, answer — a peer's mode/modem +");
    Console.WriteLine("                                capabilities over SDM; works ACROSS a mode mismatch that");
    Console.WriteLine("                                blocks the packet path — the diagnostic use)");
    Console.WriteLine("  set-mode <tncPort> <mode> [--persist] [--callsign X]");
    Console.WriteLine("  radio-channel <ccdiPort> [channel]");
    Console.WriteLine("  radio-health <ccdiPort> [--interval s=10] [--duration s=60] [--key-once [s=2]]");
    Console.WriteLine("                               (averaged RSSI / PA temp / fwd-rev detector TREND;");
    Console.WriteLine("                                --key-once keys ONCE ≤3 s at channel power for a TX sample)");
    Console.WriteLine("  flash-tnc <tncPort> <hexFile> [--yes]");
    Console.WriteLine("                               (flash NinoTNC firmware — 2-4 min, DO NOT interrupt;");
    Console.WriteLine("                                an interrupted flash is recoverable by re-running)");
    Console.WriteLine();
    Console.WriteLine("deviation-* tune the TX-DEV pot at the TUNED end: the meter end requests");
    Console.WriteLine("frame bursts, measures decode rate / IL2P FEC deltas / ADC clipping / CCDI");
    Console.WriteLine("RSSI, and advises UP/DN/OK. Run both TNCs in mode 7 (IL2P) so the FEC");
    Console.WriteLine("signal is meaningful. The coordination channel (SDM or internet) does not");
    Console.WriteLine("depend on the pot under tune.");
    return 2;
}
