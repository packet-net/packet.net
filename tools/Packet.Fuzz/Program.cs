using System.Diagnostics;
using System.Globalization;
using System.Text;
using Packet.Agw;
using Packet.Aprs;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Packet.Core;
using Packet.Kiss;
using Packet.NetRom.Wire;
using Packet.Node.Core.Console;
using SharpFuzz;

namespace Packet.Fuzz;

/// <summary>
/// SP-004 — SharpFuzz harness for the AX.25 and KISS wire-format parsers, plus
/// the AX.25 v2.2 parsing/codec surface (extended/mod-128 frames, the XID
/// information-field codec, and the §6.6 segment reassembler).
/// </summary>
/// <remarks>
/// <para>
/// Parser / codec targets (one fuzz subcommand each, all replayed under
/// <c>--smoke</c>):
/// </para>
/// <list type="bullet">
/// <item><c>Ax25Frame.TryParse(ReadOnlySpan&lt;byte&gt;, out _)</c> — the direct AX.25
/// KISS-form (no flags, no FCS) parser, modulo-8 control field.</item>
/// <item><c>KissDecoder.Push(ReadOnlySpan&lt;byte&gt;)</c> — KISS parser entry. The
/// task brief asked for <c>KissFrame.TryParse</c> but no such method exists; KISS
/// is a stateful framer, not a one-shot parser, so the equivalent harness drives
/// arbitrary byte sequences through <see cref="KissDecoder"/> instead.</item>
/// <item><c>Ax25Frame.TryParse(…, extended: true, out _)</c> — the v2.2
/// EXTENDED/mod-128 parse path. The decoder is mode-aware (the caller tells it
/// the link modulo), so an I/S frame's control field is 2 octets; this target
/// fuzzes that second-octet parse.</item>
/// <item><c>XidInfoField.TryParse(ReadOnlySpan&lt;byte&gt;, options, out _)</c> — the
/// XID (Exchange Identification) info-field TLV codec (§4.3.3.7). Attacker-
/// controlled FI/GI/GL and a run of PI/PL/PV triples — truncation, bad types,
/// length overruns. Fuzzed under both <see cref="XidParseOptions.Strict"/> and
/// <see cref="XidParseOptions.Lenient"/>.</item>
/// <item><c>Reassembler.Push(ReadOnlySpan&lt;byte&gt;)</c> + the on-the-wire
/// <see cref="SegmentationLayer.OnDataIndication"/> seam — the §6.6 segment
/// reassembler, fed hostile/malformed segment sequences (out-of-order,
/// oversized counts, missing-first, inner-PID-quirk edges).</item>
/// </list>
/// <para>Usage:</para>
/// <code>
///   dotnet run --project tools/Packet.Fuzz -- --smoke [N]
///   dotnet run --project tools/Packet.Fuzz -- ax25 [corpus-dir]
///   dotnet run --project tools/Packet.Fuzz -- kiss [corpus-dir]
///   dotnet run --project tools/Packet.Fuzz -- ax25ext [corpus-dir]
///   dotnet run --project tools/Packet.Fuzz -- xid [corpus-dir]
///   dotnet run --project tools/Packet.Fuzz -- segment [corpus-dir]
/// </code>
/// <para>
/// <c>--smoke</c> is the always-works mode: it generates N random / structured
/// inputs in-process and asserts no target escapes an exception. The per-target
/// subcommands invoke <see cref="Fuzzer.OutOfProcess.Run"/> for use under
/// <c>afl-fuzz</c> + <c>libfuzzer-dotnet</c>; they aren't required for the smoke
/// pass.
/// </para>
/// <para>
/// <b>Invariant for every parser/codec target:</b> never throw an unhandled
/// exception — return false / a clean parse error / no state corruption. The
/// reassembler is the one exception to that rule by current design (its
/// <c>Push</c> contract throws on a protocol-violating segment sequence); see
/// <c>FINDINGS.md</c> 2026-06-03 for the on-the-wire reachability of that throw.
/// The segment-target smoke run pins the *current* behaviour (throws are
/// counted, not asserted-clean) rather than masking it.
/// </para>
/// </remarks>
public static class Program
{
    private const int DefaultSmokeIterations = 1000;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "--smoke"        => RunSmoke(args),
            "--seed-corpus"  => RunSeedCorpus(args),
            "ax25"           => RunAx25Fuzzer(args),
            "kiss"           => RunKissFuzzer(args),
            "ax25ext"        => RunAx25ExtendedFuzzer(args),
            "xid"            => RunXidFuzzer(args),
            "segment"        => RunSegmentFuzzer(args),
            "command"        => RunCommandFuzzer(args),
            "aprs"           => RunAprsFuzzer(args),
            "agw"            => RunAgwFuzzer(args),
            "netrom"         => RunNetRomFuzzer(args),
            "--help"
                or "-h"      => RunHelp(),
            _ => UnknownCommand(args[0]),
        };
    }

    private static int RunHelp()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Packet.Fuzz — SP-004 frame-parser fuzzing harness");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Packet.Fuzz --smoke [N] [seed]     Smoke test (default N=1000) covering both parsers; optional RNG seed.");
        Console.WriteLine("  Packet.Fuzz --seed-corpus [dir]    Write the known-valid seed corpus files into <dir>/{ax25,kiss,ax25ext,xid,segment}.");
        Console.WriteLine("  Packet.Fuzz ax25 [corpus-dir]      AFL/libfuzzer harness for Ax25Frame.TryParse (mod-8).");
        Console.WriteLine("  Packet.Fuzz kiss [corpus-dir]      AFL/libfuzzer harness for KissDecoder.Push.");
        Console.WriteLine("  Packet.Fuzz ax25ext [corpus-dir]   AFL/libfuzzer harness for Ax25Frame.TryParse (extended/mod-128).");
        Console.WriteLine("  Packet.Fuzz xid [corpus-dir]       AFL/libfuzzer harness for XidInfoField.TryParse.");
        Console.WriteLine("  Packet.Fuzz segment [corpus-dir]   AFL/libfuzzer harness for Reassembler.Push + SegmentationLayer.");
        Console.WriteLine("  Packet.Fuzz command [corpus-dir]   AFL/libfuzzer harness for the node console command parser.");
        Console.WriteLine("  Packet.Fuzz aprs [corpus-dir]      AFL/libfuzzer harness for the APRS info-field decoders.");
        Console.WriteLine("  Packet.Fuzz agw [corpus-dir]       AFL/libfuzzer harness for AgwFrame.Parse.");
        Console.WriteLine("  Packet.Fuzz netrom [corpus-dir]    AFL/libfuzzer harness for the NET/ROM wire parsers.");
    }

    // ─── seed corpus ──────────────────────────────────────────────────

    private static int RunSeedCorpus(string[] args)
    {
        string root = args.Length >= 2
            ? args[1]
            : Path.Combine(AppContext.BaseDirectory, "corpus");
        Directory.CreateDirectory(Path.Combine(root, "ax25"));
        Directory.CreateDirectory(Path.Combine(root, "kiss"));
        Directory.CreateDirectory(Path.Combine(root, "ax25ext"));
        Directory.CreateDirectory(Path.Combine(root, "xid"));
        Directory.CreateDirectory(Path.Combine(root, "segment"));
        Directory.CreateDirectory(Path.Combine(root, "command"));
        Directory.CreateDirectory(Path.Combine(root, "aprs"));
        Directory.CreateDirectory(Path.Combine(root, "agw"));
        Directory.CreateDirectory(Path.Combine(root, "netrom"));

        Console.WriteLine($"Writing seed corpus under {root}…");

        // ── AX.25 seeds (in KISS form: no flags, no FCS) ─────────────
        var ax25Seeds = new (string Name, byte[] Bytes)[]
        {
            ("sabm.bin",       Ax25Frame.Sabm(Cs("MOLTER", 0), Cs("M0LTE",  7)).ToBytes()),
            ("ua.bin",         Ax25Frame.Ua  (Cs("M0LTE",  7), Cs("MOLTER", 0)).ToBytes()),
            ("disc.bin",       Ax25Frame.Disc(Cs("MOLTER", 0), Cs("M0LTE",  7)).ToBytes()),
            ("ui-aprs.bin",    Ax25Frame.Ui  (Cs("APRS",   0), Cs("M0LTE",  7),
                                              info: System.Text.Encoding.ASCII.GetBytes("!5126.30N/00121.30W>"),
                                              pid:  Ax25Frame.PidNoLayer3).ToBytes()),
            ("i-frame.bin",    Ax25Frame.I   (Cs("MOLTER", 0), Cs("M0LTE",  7),
                                              nr: 3, ns: 5,
                                              info: System.Text.Encoding.ASCII.GetBytes("hello world"),
                                              pid:  Ax25Frame.PidNoLayer3).ToBytes()),
            ("rr.bin",         Ax25Frame.Rr  (Cs("MOLTER", 0), Cs("M0LTE",  7),
                                              nr: 0, isCommand: true).ToBytes()),
        };
        foreach (var (name, bytes) in ax25Seeds)
        {
            string path = Path.Combine(root, "ax25", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  ax25/{name} — {bytes.Length} bytes");
        }

        // ── KISS seeds (full SLIP-framed FEND…FEND) ──────────────────
        // Each is the canonical "data frame on port 0" shape: FEND, command 0x00,
        // payload, FEND. The payload is one of the AX.25 seeds above.
        foreach (var (name, ax25Bytes) in ax25Seeds)
        {
            byte[] kiss = WrapKissDataFrame(port: 0, ax25Bytes);
            string path = Path.Combine(root, "kiss", Path.ChangeExtension(name, ".kiss.bin"));
            File.WriteAllBytes(path, kiss);
            Console.WriteLine($"  kiss/{Path.GetFileName(path)} — {kiss.Length} bytes");
        }

        // ── Extended / mod-128 seeds (KISS form) ─────────────────────
        // Valid mod-128 I and S frames — 2-octet control field (Fig 4.1b),
        // including the 7-bit N(S)/N(R) and the 127 boundary that mod-8 can't
        // reach. The decoder is mode-aware, so these only decode correctly with
        // extended: true (which the ax25ext target / smoke uses).
        foreach (var (name, bytes) in ExtendedSeeds())
        {
            string path = Path.Combine(root, "ax25ext", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  ax25ext/{name} — {bytes.Length} bytes");
        }

        // ── XID info-field seeds ─────────────────────────────────────
        // The Figure 4.6 worked example plus encoder output for a couple of
        // negotiated parameter sets. These are the known-valid TLV payloads the
        // XID parser must accept; the fuzzer mutates around them.
        foreach (var (name, bytes) in XidSeeds())
        {
            string path = Path.Combine(root, "xid", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  xid/{name} — {bytes.Length} bytes");
        }

        // ── Segment-reassembler seeds ────────────────────────────────
        // Single segment-info-field buffers (one I-frame's worth of segment),
        // in both the figure-literal and inner-PID formats, that a Reassembler
        // would accept. The segment fuzzer concatenates / mutates these into
        // multi-segment sequences.
        foreach (var (name, bytes) in SegmentSeeds())
        {
            string path = Path.Combine(root, "segment", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  segment/{name} — {bytes.Length} bytes");
        }

        // ── Node console command seeds ───────────────────────────────
        // The valid command lines (ASCII) the parser must classify; the
        // command fuzzer mutates around them and the structured generator
        // produces near-valid verb+callsign lines.
        foreach (var (name, bytes) in CommandSeeds())
        {
            string path = Path.Combine(root, "command", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  command/{name} — {bytes.Length} bytes");
        }

        // ── APRS info-field seeds (raw info bytes, sans AX.25 header) ─────
        foreach (var (name, bytes) in AprsSeeds())
        {
            string path = Path.Combine(root, "aprs", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  aprs/{name} — {bytes.Length} bytes");
        }

        // ── AGW length-prefixed frame seeds (full 36-byte header + body) ──
        foreach (var (name, bytes) in AgwSeeds())
        {
            string path = Path.Combine(root, "agw", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  agw/{name} — {bytes.Length} bytes");
        }

        // ── NET/ROM network-layer datagram seeds ─────────────────────────
        foreach (var (name, bytes) in NetRomSeeds())
        {
            string path = Path.Combine(root, "netrom", name);
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  netrom/{name} — {bytes.Length} bytes");
        }

        return 0;
    }

    /// <summary>
    /// Known-valid extended (mod-128) frames in KISS form. Each decodes only
    /// with <c>extended: true</c>; the 2-octet control field carries 7-bit
    /// N(S)/N(R) (Fig 4.1b), exercised here at the mod-8-unreachable boundary
    /// (N(S)=127, N(R)=64) plus a mid-range I-frame and an extended RR.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Bytes)> ExtendedSeeds()
    {
        yield return ("i-ext-mid.bin",
            Ax25Frame.I(Cs("MOLTER", 0), Cs("M0LTE", 7),
                nr: 40, ns: 100, info: System.Text.Encoding.ASCII.GetBytes("extended i frame"),
                pid: Ax25Frame.PidNoLayer3, pollBit: true, extended: true).ToBytes());
        yield return ("i-ext-wrap.bin",
            Ax25Frame.I(Cs("MOLTER", 0), Cs("M0LTE", 7),
                nr: 64, ns: 127, info: System.Text.Encoding.ASCII.GetBytes("ns at the wrap"),
                pid: Ax25Frame.PidNoLayer3, pollBit: false, extended: true).ToBytes());
        yield return ("rr-ext.bin",
            Ax25Frame.Rr(Cs("MOLTER", 0), Cs("M0LTE", 7),
                nr: 96, isCommand: true, pollFinal: true, extended: true).ToBytes());
        yield return ("srej-ext.bin",
            Ax25Frame.Srej(Cs("MOLTER", 0), Cs("M0LTE", 7),
                nr: 127, isCommand: false, pollFinal: false, extended: true).ToBytes());
    }

    /// <summary>
    /// Known-valid XID information fields. The Figure 4.6 worked example (the
    /// spec's own bytes) plus the encoder's output for a default and a
    /// full-parameter negotiation set.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Bytes)> XidSeeds()
    {
        // Figure 4.6 (NJ7P → N7LEM) — FI GI GL + the 6 PI/PL/PV triples. The HDLC
        // Optional Functions PV is the MSB-octet-first form (`22 A8 82`) our codec
        // emits/parses; see XidInfoFieldTests.Figure46Info for the octet-order note.
        yield return ("figure-4-6.bin", new byte[]
        {
            0x82, 0x80, 0x00, 0x17,
            0x02, 0x02, 0x22, 0x00,
            0x03, 0x03, 0x22, 0xA8, 0x82,
            0x06, 0x02, 0x04, 0x00,
            0x08, 0x01, 0x02,
            0x09, 0x02, 0x10, 0x00,
            0x0A, 0x01, 0x03,
        });
        yield return ("empty.bin", XidInfoField.Encode(new XidParameters()));
        yield return ("full.bin", XidInfoField.Encode(new XidParameters
        {
            ClassesOfProcedures = ClassesOfProcedures.FullDuplexCapable,
            HdlcOptionalFunctions = new HdlcOptionalFunctions
            {
                Reject = RejectMode.SelectiveReject,
                Modulo128 = true,
                SegmenterReassembler = true,
            },
            IFieldLengthRxBits = XidParameters.OctetsToBits(256),
            WindowSizeRx = 32,
            AckTimerMillis = 3000,
            Retries = 10,
        }));
    }

    /// <summary>
    /// Known-valid single-segment info-field buffers, in both segmentation
    /// formats. A reassembler reads each as one segment of a series; the segment
    /// fuzzer concatenates and mutates these into (often hostile) multi-segment
    /// sequences.
    /// </summary>
    private static IEnumerable<(string Name, byte[] Bytes)> SegmentSeeds()
    {
        // Figure-literal: [F/X][data]. First segment of a 3-segment series, then a
        // middle, then the last.
        yield return ("figlit-first.bin",  new byte[] { Segmenter.FirstBit | 2, 0xA0, 0xA1, 0xA2 });
        yield return ("figlit-mid.bin",    new byte[] { 1, 0xB0, 0xB1, 0xB2 });
        yield return ("figlit-last.bin",   new byte[] { 0, 0xC0, 0xC1 });
        // Inner-PID (Dire Wolf): the first segment carries [F/X][inner-PID][data].
        yield return ("innerpid-first.bin", new byte[] { Segmenter.FirstBit | 1, Ax25Frame.PidNetRom, 0xD0, 0xD1 });
        // A single-segment (First + last) series — remaining count 0 on the First.
        yield return ("single.bin",         new byte[] { Segmenter.FirstBit | 0, 0xEE, 0xEF });
    }

    private static Packet.Core.Callsign Cs(string @base, byte ssid)
        => new(@base, ssid);

    /// <summary>
    /// Wrap an AX.25 frame in a single KISS data frame: <c>FEND, (port&lt;&lt;4)|cmd, payload (escaped), FEND</c>.
    /// </summary>
    private static byte[] WrapKissDataFrame(byte port, byte[] payload)
    {
        const byte Fend  = 0xC0;
        const byte Fesc  = 0xDB;
        const byte Tfend = 0xDC;
        const byte Tfesc = 0xDD;
        var ms = new MemoryStream(payload.Length + 4);
        ms.WriteByte(Fend);
        ms.WriteByte((byte)((port & 0x0F) << 4));   // command 0x0 = Data
        foreach (byte b in payload)
        {
            switch (b)
            {
                case Fend: ms.WriteByte(Fesc); ms.WriteByte(Tfend); break;
                case Fesc: ms.WriteByte(Fesc); ms.WriteByte(Tfesc); break;
                default:   ms.WriteByte(b); break;
            }
        }
        ms.WriteByte(Fend);
        return ms.ToArray();
    }

    // ─── smoke ────────────────────────────────────────────────────────

    private static int RunSmoke(string[] args)
    {
        int iterations = DefaultSmokeIterations;
        int seed = unchecked((int)0xC0DEFEED);
        if (args.Length >= 2 && !int.TryParse(args[1], out iterations))
        {
            Console.Error.WriteLine($"Bad iteration count: {args[1]}");
            return 1;
        }
        if (args.Length >= 3 && !int.TryParse(args[2], out seed))
        {
            Console.Error.WriteLine($"Bad seed: {args[2]}");
            return 1;
        }

        Console.WriteLine($"Packet.Fuzz smoke run: {iterations} iterations per parser, seed=0x{seed:X8}");
        Console.WriteLine();

        // Always replay the on-disk seed corpus first so the smoke run is at
        // least as broad as the AFL seed set — any throw on a known-valid
        // sample is a regression we want to catch in CI.
        var ax25Seeds = LoadCorpus("ax25");
        var kissSeeds = LoadCorpus("kiss");
        var extSeeds  = LoadCorpus("ax25ext");
        var xidSeeds  = LoadCorpus("xid");
        var segSeeds  = LoadCorpus("segment");
        var cmdSeeds  = LoadCorpus("command");
        var aprsSeeds = LoadCorpus("aprs");
        var agwSeeds  = LoadCorpus("agw");
        var nrSeeds   = LoadCorpus("netrom");

        var ax25 = SmokeOne("Ax25Frame.TryParse", iterations, FuzzAx25Bytes, ax25Seeds, seed);
        Console.WriteLine();
        var kiss = SmokeOne("KissDecoder.Push", iterations, FuzzKissBytes, kissSeeds, seed);
        Console.WriteLine();
        var ext = SmokeOne("Ax25Frame.TryParse(extended)", iterations, FuzzAx25ExtendedBytes, extSeeds, seed,
            structuredGenerator: MostlyValidExtendedAx25);
        Console.WriteLine();
        var xid = SmokeOne("XidInfoField.TryParse", iterations, FuzzXidBytes, xidSeeds, seed,
            structuredGenerator: MostlyValidXid);
        Console.WriteLine();
        // The segment target's reassembler throws InvalidOperationException /
        // ArgumentException *by design* on protocol-violating segment sequences
        // (its documented contract; see FINDINGS.md 2026-06-03). FuzzSegmentBytes
        // swallows exactly those two documented types and records anything else as
        // a finding — so the smoke run fails only on a crash-class bug (IOOR,
        // NRE, …), not on the contractual protocol-violation throw.
        var seg = SmokeOne("Reassembler.Push / SegmentationLayer", iterations, FuzzSegmentBytes, segSeeds, seed,
            structuredGenerator: HostileSegmentSequence);
        Console.WriteLine();
        // The node console command parser is total by contract — it must never
        // throw on any byte sequence, must bound its work, and must never produce
        // a spurious Connect/Bye from non-command bytes. FuzzCommandBytes asserts
        // all three; any violation is recorded as a finding (no exception is
        // expected at all here, unlike the segment target).
        var cmd = SmokeOne("NodeCommandParser.Parse", iterations, FuzzCommandBytes, cmdSeeds, seed,
            structuredGenerator: MostlyValidCommand);
        Console.WriteLine();
        // The APRS decoders are total by contract — every public TryDecode must return
        // false on a malformed info field, never throw. The target runs each in turn.
        var aprs = SmokeOne("APRS info-field decoders", iterations, FuzzAprsBytes, aprsSeeds, seed,
            structuredGenerator: MostlyValidAprs);
        Console.WriteLine();
        // AgwFrame.Parse throws InvalidDataException by documented contract on a short /
        // over-claiming frame; the target swallows exactly that and records any other
        // (crash-class) exception as a finding — like the segment target.
        var agw = SmokeOne("AgwFrame.Parse", iterations, FuzzAgwBytes, agwSeeds, seed,
            structuredGenerator: MostlyValidAgw);
        Console.WriteLine();
        // The NET/ROM wire parsers are total by contract — return false, never throw.
        var nr = SmokeOne("NET/ROM wire parsers", iterations, FuzzNetRomBytes, nrSeeds, seed,
            structuredGenerator: MostlyValidNetRom);

        Console.WriteLine();
        Console.WriteLine("════════ Summary ════════");
        Report(ax25);
        Report(kiss);
        Report(ext);
        Report(xid);
        Report(seg);
        Report(cmd);
        Report(aprs);
        Report(agw);
        Report(nr);

        int totalFindings = ax25.Findings.Count + kiss.Findings.Count
                          + ext.Findings.Count + xid.Findings.Count + seg.Findings.Count
                          + cmd.Findings.Count + aprs.Findings.Count + agw.Findings.Count
                          + nr.Findings.Count;
        return totalFindings == 0 ? 0 : 2;
    }

    private static byte[][] LoadCorpus(string subdir)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "corpus", subdir);
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return [.. Directory.EnumerateFiles(dir).Select(File.ReadAllBytes)];
    }

    /// <summary>
    /// Run one target through three phases: seed replay, single-mutation of each
    /// seed, and bulk generated inputs. <paramref name="structuredGenerator"/>,
    /// when non-null, supplies target-aware structured inputs that alternate with
    /// the generic random/structured generator in the bulk phase — so a TLV/XID
    /// or segment target gets deep, format-shaped coverage on top of raw bytes.
    /// </summary>
    private static SmokeResult SmokeOne(
        string label, int iterations, Action<byte[]> target, byte[][] seeds, int rngSeed,
        Func<Random, byte[]>? structuredGenerator = null)
    {
        Console.WriteLine($"── {label} ──");
        var rng = new Random(rngSeed);
        var stopwatch = Stopwatch.StartNew();
        var result = new SmokeResult(label, iterations);

        // 1) Replay the seed corpus verbatim. These are known-valid frames;
        //    parsing them must succeed and not throw.
        foreach (var seed in seeds)
        {
            TryRun(target, seed, result);
        }

        // 2) Bit-flip + byte-replacement mutations of each seed (one mutation
        //    per pass, several passes). Cheap way to probe near-valid inputs
        //    that the structural random generator may not reach.
        const int mutationsPerSeed = 32;
        foreach (var seed in seeds)
        {
            for (int m = 0; m < mutationsPerSeed; m++)
            {
                TryRun(target, MutateOnce(rng, seed), result);
            }
        }

        // 3) Bulk random / structured inputs. When a target-aware structured
        //    generator is supplied, every other input is drawn from it so the
        //    bulk phase mixes raw-byte robustness with format-shaped depth.
        for (int i = 0; i < iterations; i++)
        {
            byte[] input = (structuredGenerator is not null && (i & 1) == 0)
                ? structuredGenerator(rng)
                : GenerateInput(rng, i);
            TryRun(target, input, result);
        }

        stopwatch.Stop();
        int totalInputs = seeds.Length + seeds.Length * mutationsPerSeed + iterations;
        Console.WriteLine($"  {totalInputs} inputs ({seeds.Length} seed + {seeds.Length * mutationsPerSeed} seed-mutations + {iterations} generated) / {stopwatch.ElapsedMilliseconds} ms / {result.Findings.Count} unhandled exceptions");
        return result;
    }

    private static void TryRun(Action<byte[]> target, byte[] input, SmokeResult result)
    {
        try
        {
            target(input);
        }
#pragma warning disable CA1031 // catch general Exception: the whole point of fuzzing is to find escaping exceptions
        catch (Exception ex)
#pragma warning restore CA1031
        {
            result.Findings.Add(new Finding(input, ex));
        }
    }

    private static byte[] MutateOnce(Random rng, byte[] seed)
    {
        if (seed.Length == 0)
        {
            return RandomBuffer(rng, rng.Next(0, 4));
        }
        var copy = (byte[])seed.Clone();
        int pos = rng.Next(copy.Length);
        copy[pos] = rng.Next(4) switch
        {
            0 => (byte)(copy[pos] ^ (1 << rng.Next(8))), // bit flip
            1 => (byte)rng.Next(256),                     // byte replace
            2 => 0,                                       // zero
            _ => 0xFF,                                    // 0xFF
        };
        return copy;
    }

    private static void Report(SmokeResult result)
    {
        if (result.Findings.Count == 0)
        {
            Console.WriteLine($"  {result.Label}: clean — {result.Iterations} inputs, no throws.");
            return;
        }

        Console.WriteLine($"  {result.Label}: {result.Findings.Count} unhandled exception(s):");
        // Group by exception type + outer frame to keep output digestible.
        var groups = result.Findings
            .GroupBy(f => f.Exception.GetType().FullName + "|" + FirstStackFrame(f.Exception))
            .OrderByDescending(g => g.Count());
        foreach (var g in groups)
        {
            var sample = g.First();
            Console.WriteLine($"    × {g.Count()}  {sample.Exception.GetType().Name}: {sample.Exception.Message}");
            Console.WriteLine($"           at {FirstStackFrame(sample.Exception)}");
            Console.WriteLine($"           sample input ({sample.Input.Length} bytes): {ToHex(sample.Input, max: 64)}");
        }
    }

    private static string FirstStackFrame(Exception ex)
    {
        var trace = new StackTrace(ex, fNeedFileInfo: false);
        var frame = trace.GetFrame(0);
        if (frame is null)
        {
            return "(no frame)";
        }
        var method = frame.GetMethod();
        if (method is null)
        {
            return "(no method)";
        }
        return $"{method.DeclaringType?.FullName}.{method.Name}";
    }

    private static string ToHex(byte[] bytes, int max)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        int n = Math.Min(bytes.Length, max);
        for (int i = 0; i < n; i++)
        {
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        if (bytes.Length > max)
        {
            sb.Append('…');
        }
        return sb.ToString();
    }

    // ─── input generation ─────────────────────────────────────────────

    /// <summary>
    /// Generate a random byte buffer biased toward shapes likely to surface
    /// parser edge cases: short buffers, lengths near the minimum frame size,
    /// long payloads, structured-looking frames, and pathological all-same-byte
    /// inputs that probe the framing layer.
    /// </summary>
    private static byte[] GenerateInput(Random rng, int i)
    {
        // Mix seven strategies so the corpus covers small, near-threshold,
        // long, structured, and pathological inputs evenly:
        return (i % 7) switch
        {
            0 => RandomBuffer(rng, rng.Next(0, 16)),                 // truncated
            1 => RandomBuffer(rng, rng.Next(14, 32)),                // around min size
            2 => RandomBuffer(rng, rng.Next(15, 350)),               // typical AX.25 KISS payload
            3 => RandomBuffer(rng, rng.Next(350, 4096)),             // oversized — well beyond paclen
            4 => SameByteBuffer(rng, (byte)rng.Next(256)),           // all-same-byte (FEND, FESC, 0x00, …)
            5 => SlipPathological(rng),                              // KISS-aware: lots of FENDs / FESCs / dangling escapes
            _ => MostlyValidAx25(rng),                               // structured: looks like an AX.25 frame
        };
    }

    private static byte[] SameByteBuffer(Random rng, byte value)
    {
        int length = rng.Next(0, 1024);
        var buf = new byte[length];
        Array.Fill(buf, value);
        return buf;
    }

    private static byte[] SlipPathological(Random rng)
    {
        int length = rng.Next(0, 512);
        var buf = new byte[length];
        for (int i = 0; i < length; i++)
        {
            // Heavy bias toward FEND/FESC/Tfend/Tfesc to exercise the
            // KISS escape state machine. Random fills the gaps.
            buf[i] = rng.Next(4) switch
            {
                0 => 0xC0, // FEND
                1 => 0xDB, // FESC
                2 => (byte)(rng.Next(2) == 0 ? 0xDC : 0xDD), // Tfend / Tfesc
                _ => (byte)rng.Next(256),
            };
        }
        return buf;
    }

    private static byte[] RandomBuffer(Random rng, int length)
    {
        var buf = new byte[length];
        rng.NextBytes(buf);
        return buf;
    }

    /// <summary>
    /// Produce a buffer that looks like an AX.25 frame: two 7-byte address
    /// slots, optional digipeaters, then a control byte and tail. Several
    /// byte positions are deliberately random so the parser exercises its
    /// reject branches.
    /// </summary>
    private static byte[] MostlyValidAx25(Random rng)
    {
        int digiCount = rng.Next(0, 10);                            // 0..9 — one extra to exceed the §6.1 max
        int infoLen   = rng.Next(0, 256);
        int total     = (2 + digiCount) * 7 + 1 + 1 + infoLen;     // dest+src+digis + ctrl + pid + info
        var buf       = new byte[total];

        int off = 0;
        WriteAddrSlot(buf, ref off, rng, isLast: false);            // destination
        WriteAddrSlot(buf, ref off, rng, isLast: digiCount == 0);   // source
        for (int d = 0; d < digiCount; d++)
        {
            WriteAddrSlot(buf, ref off, rng, isLast: d == digiCount - 1);
        }
        buf[off++] = (byte)rng.Next(256);                            // control
        buf[off++] = (byte)rng.Next(256);                            // pid
        rng.NextBytes(buf.AsSpan(off));                              // info
        return buf;
    }

    private static void WriteAddrSlot(byte[] buf, ref int off, Random rng, bool isLast)
    {
        // 6 callsign bytes (high 7 bits = ASCII << 1; low bit must be 0) — sometimes
        // valid-looking, sometimes garbage to exercise the address validator.
        for (int i = 0; i < 6; i++)
        {
            if (rng.Next(4) == 0)
            {
                buf[off + i] = (byte)rng.Next(256);
            }
            else
            {
                // valid-looking: A..Z or 0..9 shifted left.
                char c = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 "[rng.Next(37)];
                buf[off + i] = (byte)(c << 1);
            }
        }
        // SSID octet: bit 0 = E-bit (extension), bit 7 = H-bit/CRH (per slot's role).
        byte ssidByte = (byte)((rng.Next(16) << 1) | 0x60); // reserved bits "11" by spec default
        if (isLast)
        {
            ssidByte |= 0x01;                                   // set E-bit on the last slot
        }
        if (rng.Next(8) == 0)
        {
            ssidByte = (byte)rng.Next(256);                     // 1-in-8 fully random SSID byte
        }
        buf[off + 6] = ssidByte;
        off += 7;
    }

    // ─── v2.2 structured generators ────────────────────────────────────

    /// <summary>
    /// Produce a buffer shaped like an EXTENDED (mod-128) AX.25 I or S frame:
    /// the address pair + a 2-octet control field (Fig 4.1b) + tail. The second
    /// control octet (7-bit N(R) + P/F) is what the mod-8 generator never emits,
    /// so this is what stresses the extended-parse path. Some bytes are random so
    /// the parser still exercises its reject branches.
    /// </summary>
    private static byte[] MostlyValidExtendedAx25(Random rng)
    {
        bool iFrame = rng.Next(2) == 0;
        int infoLen = iFrame ? rng.Next(0, 64) : 0;
        // dest + src + ctrl(2) + [pid + info on I].
        int total = 14 + 2 + (iFrame ? 1 + infoLen : 0);
        var buf = new byte[total];

        int off = 0;
        WriteAddrSlot(buf, ref off, rng, isLast: false);   // destination
        WriteAddrSlot(buf, ref off, rng, isLast: true);    // source (E-bit set → no digis)

        if (iFrame)
        {
            // I-frame mod-128: octet0 = (N(S)<<1)|0; octet1 = (N(R)<<1)|P.
            buf[off++] = (byte)((rng.Next(128) << 1) & 0xFE);
            buf[off++] = (byte)(((rng.Next(128) << 1) & 0xFE) | rng.Next(2));
            buf[off++] = (byte)rng.Next(256);              // pid
            rng.NextBytes(buf.AsSpan(off));                // info
        }
        else
        {
            // S-frame mod-128: octet0 = SS|01 (low nibble), octet1 = (N(R)<<1)|F.
            byte[] sBases = { 0x01, 0x05, 0x09, 0x0D };    // RR / RNR / REJ / SREJ
            buf[off++] = sBases[rng.Next(sBases.Length)];
            buf[off++] = (byte)(((rng.Next(128) << 1) & 0xFE) | rng.Next(2));
        }
        return buf;
    }

    /// <summary>
    /// Produce a buffer shaped like an XID information field: <c>FI GI GL</c>
    /// then a run of PI/PL/PV triples. FI/GI are usually the spec constants
    /// (0x82/0x80) so the parser gets past the header and into the TLV loop; GL
    /// and the PL octets are sometimes deliberately wrong (over-claimed,
    /// truncated) to drive the length-overrun reject branches.
    /// </summary>
    private static byte[] MostlyValidXid(Random rng)
    {
        var pf = new List<byte>(32);
        int triples = rng.Next(0, 8);
        // Real AX.25 PIs plus some unknown/out-of-range ones.
        byte[] pis = { 0x02, 0x03, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x42, 0xFF, 0x00 };
        for (int t = 0; t < triples; t++)
        {
            byte pi = pis[rng.Next(pis.Length)];
            int pl = rng.Next(0, 6);
            pf.Add(pi);
            pf.Add((byte)pl);
            for (int i = 0; i < pl; i++) pf.Add((byte)rng.Next(256));
        }

        // Group length: usually the true parameter-field length, sometimes a
        // deliberately wrong value to exercise the overrun/clamp branch.
        int gl = rng.Next(4) switch
        {
            0 => Math.Min(pf.Count + rng.Next(1, 8), 0xFFFF),   // over-claim
            1 => Math.Max(pf.Count - rng.Next(1, 4), 0),         // under-claim
            _ => pf.Count,                                        // truthful
        };

        var buf = new byte[XidInfoField.HeaderLength + pf.Count];
        // FI/GI: mostly the spec constants, occasionally wrong (header reject).
        buf[0] = rng.Next(8) == 0 ? (byte)rng.Next(256) : XidInfoField.FormatIdentifier;
        buf[1] = rng.Next(8) == 0 ? (byte)rng.Next(256) : XidInfoField.GroupIdentifier;
        buf[2] = (byte)((gl >> 8) & 0xFF);
        buf[3] = (byte)(gl & 0xFF);
        pf.CopyTo(buf, XidInfoField.HeaderLength);
        return buf;
    }

    /// <summary>
    /// Produce a (usually hostile) multi-segment sequence as a single buffer:
    /// a length-prefixed run of segment info-fields, the encoding the segment
    /// fuzz target replays one segment at a time. Mixes valid first/middle/last
    /// orderings with the malformed shapes the reassembler must survive:
    /// missing-first, out-of-sequence counts, duplicate firsts, empty fields,
    /// and over-long remaining counts.
    /// </summary>
    private static byte[] HostileSegmentSequence(Random rng)
    {
        int segments = rng.Next(0, 6);
        var ms = new MemoryStream();
        for (int s = 0; s < segments; s++)
        {
            // Each segment: 1 length byte then that many info-field bytes.
            int len = rng.Next(5) switch
            {
                0 => 0,                       // empty info field (ArgumentException path)
                1 => 1,                       // F/X only, no data / no inner PID
                _ => rng.Next(1, 8),
            };
            var seg = new byte[len];
            if (len > 0)
            {
                // Header byte: random First bit + random remaining count — this is
                // exactly what produces out-of-sequence / missing-first hostility.
                byte first = rng.Next(2) == 0 ? Segmenter.FirstBit : (byte)0;
                seg[0] = (byte)(first | (byte)(rng.Next(128) & Segmenter.CountMask));
                for (int i = 1; i < len; i++) seg[i] = (byte)rng.Next(256);
            }
            ms.WriteByte((byte)len);
            ms.Write(seg, 0, seg.Length);
        }
        return ms.ToArray();
    }

    // ─── targets ─────────────────────────────────────────────────────

    private static void FuzzAx25Bytes(byte[] bytes)
    {
        _ = Ax25Frame.TryParse(bytes, out _);
    }

    /// <summary>
    /// Drive arbitrary bytes through a fresh <see cref="KissDecoder"/>. The
    /// decoder is the byte-stream parser entry point for the KISS framing
    /// layer — equivalent of <c>KissFrame.TryParse</c> for a stream-oriented
    /// protocol.
    /// </summary>
    private static void FuzzKissBytes(byte[] bytes)
    {
        var decoder = new KissDecoder();
        decoder.Push(bytes);
    }

    /// <summary>
    /// Parse arbitrary bytes as an EXTENDED (mod-128) frame. The decoder is
    /// mode-aware, so this drives the 2-octet-control-field branch of
    /// <see cref="Ax25Frame.TryParse(ReadOnlySpan{byte}, Ax25ParseOptions, bool, out Ax25Frame?)"/>.
    /// Invariant: never throw — return false on malformed input.
    /// </summary>
    private static void FuzzAx25ExtendedBytes(byte[] bytes)
    {
        _ = Ax25Frame.TryParse(bytes, Ax25ParseOptions.Lenient, extended: true, out _);
        // Also exercise the strict-options path — different reject branches.
        _ = Ax25Frame.TryParse(bytes, Ax25ParseOptions.Strict, extended: true, out _);
    }

    /// <summary>
    /// Parse arbitrary bytes as an XID information field under both the strict
    /// and lenient option presets. Invariant: never throw — return false on a
    /// malformed buffer (bad FI/GI, truncated header, GL overrun, truncated
    /// PI/PL/PV). Re-encoding a successfully-parsed value must also not throw.
    /// </summary>
    private static void FuzzXidBytes(byte[] bytes)
    {
        if (XidInfoField.TryParse(bytes, XidParseOptions.Strict, out var strict))
        {
            // A value the strict parser accepted must re-encode without throwing.
            _ = XidInfoField.Encode(strict);
        }
        if (XidInfoField.TryParse(bytes, XidParseOptions.Lenient, out var lenient))
        {
            _ = XidInfoField.Encode(lenient);
        }
    }

    /// <summary>
    /// Drive a hostile multi-segment sequence through both a fresh
    /// <see cref="Reassembler"/> (figure-literal and inner-PID) and the
    /// on-the-wire <see cref="SegmentationLayer.OnDataIndication"/> seam.
    /// </summary>
    /// <remarks>
    /// The reassembler throws <see cref="InvalidOperationException"/> /
    /// <see cref="ArgumentException"/> by documented design on a protocol-
    /// violating segment sequence (see <c>FINDINGS.md</c> 2026-06-03). Those two
    /// types are the contract, so this target swallows exactly them and lets any
    /// other exception escape to be recorded as a finding — i.e. it asserts "no
    /// <i>crash-class</i> exception (IndexOutOfRange, NullReference, …) escapes",
    /// which is the invariant that actually matters for this target.
    /// </remarks>
    private static void FuzzSegmentBytes(byte[] bytes)
    {
        foreach (var expectInnerPid in new[] { false, true })
        {
            var reassembler = new Reassembler(expectInnerPid);
            foreach (var seg in SplitSegmentStream(bytes))
            {
                try { _ = reassembler.Push(seg); }
                catch (InvalidOperationException) { /* documented contract */ }
                catch (ArgumentException) { /* documented contract */ }
            }
        }

        // The on-the-wire seam: deliver each segment as a PID-0x08 DL-DATA
        // indication, exactly as Ax25Listener does. Same documented-throw
        // tolerance — this is the reachability path the finding documents.
        foreach (var quirks in new[] { Ax25SessionQuirks.Default, Ax25SessionQuirks.StrictlyFaithful })
        {
            var ctx = new Ax25SessionContext
            {
                Local = new Packet.Core.Callsign("M0LTE", 0),
                Remote = new Packet.Core.Callsign("G7XYZ", 7),
                Quirks = quirks,
            };
            var layer = new SegmentationLayer(ctx);
            foreach (var seg in SplitSegmentStream(bytes))
            {
                var ind = new DataLinkDataIndication(seg, Ax25Frame.PidSegmented);
                try { _ = layer.OnDataIndication(ind); }
                catch (InvalidOperationException) { /* documented contract */ }
                catch (ArgumentException) { /* documented contract */ }
            }
        }
    }

    /// <summary>
    /// Split the length-prefixed segment-stream encoding (see
    /// <see cref="HostileSegmentSequence"/>) into individual segment info-field
    /// buffers. A truncated final length is clamped to what remains so arbitrary
    /// AFL bytes (which won't follow the encoding) still produce some segments.
    /// </summary>
    private static IEnumerable<byte[]> SplitSegmentStream(byte[] bytes)
    {
        int pos = 0;
        while (pos < bytes.Length)
        {
            int len = bytes[pos++];
            int take = Math.Min(len, bytes.Length - pos);
            yield return bytes.AsSpan(pos, take).ToArray();
            pos += take;
        }
    }

    // ─── afl / libfuzzer entry points ─────────────────────────────────

    private static int RunAx25Fuzzer(string[] args)
    {
        // SharpFuzz harness — afl-fuzz feeds stdin or a path under
        // Fuzzer.OutOfProcess.Run; we just call TryParse with whatever we get.
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _ = Ax25Frame.TryParse(ms.ToArray(), out _);
        });
        return 0;
    }

    private static int RunKissFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var decoder = new KissDecoder();
            decoder.Push(ms.ToArray());
        });
        return 0;
    }

    private static int RunAx25ExtendedFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzAx25ExtendedBytes(ms.ToArray());
        });
        return 0;
    }

    private static int RunXidFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzXidBytes(ms.ToArray());
        });
        return 0;
    }

    private static int RunSegmentFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzSegmentBytes(ms.ToArray());
        });
        return 0;
    }

    // ─── Node console command-parser target ──────────────────────────────

    /// <summary>
    /// Drive the node console command parser with arbitrary bytes, asserting its
    /// three contract invariants: (1) it never throws; (2) every result is a
    /// bounded, typed command — an over-long line is truncated to
    /// <see cref="NodeCommandParser.MaxLineLength"/>; (3) a result is never a
    /// spurious <c>Connect</c> carrying an unparseable callsign, nor a <c>Connect</c>
    /// /<c>Bye</c> from a line whose first token can't be that verb. Both the
    /// one-shot <see cref="NodeCommandParser.Parse(ReadOnlySpan{byte})"/> and the
    /// streamed <see cref="LineAssembler"/> → parse path are exercised. A
    /// violation is surfaced as a thrown exception (recorded as a finding by
    /// <c>TryRun</c>); the target itself is expected to be exception-free.
    /// </summary>
    private static void FuzzCommandBytes(byte[] bytes)
    {
        // (1) one-shot parse never throws.
        var direct = NodeCommandParser.Parse(bytes.AsSpan());
        AssertCommandContract(direct);

        // (2) streamed line assembly is bounded + each line parses to a typed
        //     command. Feed the whole buffer as one chunk; the assembler bounds
        //     each line internally.
        var assembler = new LineAssembler();
        foreach (var line in assembler.Push(bytes))
        {
            if (line.Length > NodeCommandParser.MaxLineLength)
            {
                throw new InvalidOperationException(
                    $"LineAssembler emitted a {line.Length}-byte line exceeding the {NodeCommandParser.MaxLineLength} cap.");
            }
            AssertCommandContract(NodeCommandParser.Parse(line));
        }
    }

    private static void AssertCommandContract(NodeCommand command)
    {
        switch (command)
        {
            case ConnectCommand connect:
                if (!Packet.Core.Callsign.TryParse(connect.Target.ToString(), out _))
                {
                    throw new InvalidOperationException(
                        $"parser produced a Connect with an invalid callsign '{connect.Target}'.");
                }
                break;
            case UnknownCommand unknown when unknown.Raw.Length > NodeCommandParser.MaxLineLength:
                throw new InvalidOperationException(
                    $"Unknown command echo exceeded the {NodeCommandParser.MaxLineLength} cap ({unknown.Raw.Length}).");
        }
    }

    /// <summary>
    /// Near-valid command line bytes: a verb (full or abbreviated, random case)
    /// optionally followed by a callsign-ish token, terminated by CR/LF. Mixes in
    /// junk so the parser's classification + the connect-arg path are both
    /// exercised more than raw random bytes would.
    /// </summary>
    private static byte[] MostlyValidCommand(Random rng)
    {
        string[] verbs = ["C", "CONNECT", "Conn", "c", "B", "BYE", "D", "DISC", "N", "NODES", "I", "INFO", "H", "HELP", "?", "BOGUS"];
        var verb = verbs[rng.Next(verbs.Length)];
        var sb = new StringBuilder(verb);
        if (rng.Next(2) == 0)
        {
            sb.Append(' ');
            int len = rng.Next(0, 9);
            for (int i = 0; i < len; i++)
            {
                // Mostly callsign chars, sometimes a stray byte.
                sb.Append(rng.Next(8) == 0
                    ? (char)rng.Next(33, 127)
                    : "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-"[rng.Next(37)]);
            }
        }
        sb.Append(rng.Next(2) == 0 ? "\r" : "\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Known-valid console command lines (the smoke seed corpus).</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> CommandSeeds()
    {
        (string, string)[] lines =
        [
            ("connect.bin",   "C M0LTE-1\r"),
            ("connect-full.bin", "CONNECT G7XYZ\r"),
            ("bye.bin",       "B\r"),
            ("disconnect.bin", "DISCONNECT\r"),
            ("nodes.bin",     "N\r"),
            ("info.bin",      "INFO\r"),
            ("help.bin",      "?\r"),
            ("empty.bin",     "\r"),
            ("unknown.bin",   "frobnicate the gadget\r"),
        ];
        foreach (var (name, text) in lines)
        {
            yield return (name, Encoding.ASCII.GetBytes(text));
        }
    }

    private static int RunCommandFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzCommandBytes(ms.ToArray());
        });
        return 0;
    }

    // ─── APRS / AGW / NET/ROM targets ────────────────────────────────────

    /// <summary>
    /// Drive arbitrary info-field bytes through every public APRS decoder. All are total
    /// by contract (return <c>false</c> on a malformed field, never throw); a single
    /// hostile APRS-IS / RF info field must not crash any of them. The MIC-E decoder also
    /// takes a destination-base string (the encoded latitude in the AX.25 dest callsign),
    /// so it is fed a structured-ish base derived from the same bytes.
    /// </summary>
    private static void FuzzAprsBytes(byte[] bytes)
    {
        var info = bytes.AsSpan();
        foreach (var options in new[] { AprsParseOptions.Strict, AprsParseOptions.Lenient })
        {
            _ = AprsPositionDecoder.TryDecode(info, out _);
            _ = AprsPositionDecoder.TryDecodePayload(info, out _);
            _ = AprsMessageDecoder.TryDecode(info, out _);
            _ = AprsStatusDecoder.TryDecode(info, options, out _);
            _ = AprsObjectDecoder.TryDecode(info, out _);
            _ = AprsItemDecoder.TryDecode(info, out _);
            _ = AprsTelemetryDecoder.TryDecode(info, options, out _);

            // MIC-E needs a 6-char destination base (the lat-encoding callsign); derive a
            // plausible-but-fuzzed one from the leading bytes so the decoder's dest-field
            // math is exercised too.
            string destBase = MicEDestBase(bytes);
            _ = AprsMicEDecoder.TryDecode(destBase, info, options, out _);
        }
    }

    private static string MicEDestBase(byte[] bytes)
    {
        // 6 chars in the MIC-E dest alphabet (0-9, A-L, P-Z and a few specials), drawn
        // deterministically from the input so a given seed maps to a stable base.
        const string alphabet = "0123456789ABCDEFGHIJKLPQRSTUVWXYZ";
        Span<char> chars = stackalloc char[6];
        for (int i = 0; i < 6; i++)
        {
            byte b = bytes.Length == 0 ? (byte)i : bytes[i % bytes.Length];
            chars[i] = alphabet[b % alphabet.Length];
        }
        return new string(chars);
    }

    /// <summary>
    /// Parse arbitrary bytes as an AGW frame off the head of the buffer. <see cref="AgwFrame.Parse"/>
    /// reads an attacker-controlled little-endian data-length and slices the body by it —
    /// a classic over-read / overflow shape — and throws <see cref="InvalidDataException"/>
    /// by documented contract on a short / over-claiming / overflowing frame. This target
    /// swallows exactly that documented type and lets any other (crash-class) exception
    /// escape to be recorded as a finding. <see cref="AgwFrame.TryReadDataLength"/> (a
    /// bool-returning length peek) must never throw at all.
    /// </summary>
    private static void FuzzAgwBytes(byte[] bytes)
    {
        _ = AgwFrame.TryReadDataLength(bytes, out _);   // must not throw on any input
        try
        {
            _ = AgwFrame.Parse(bytes, out _);
        }
        catch (InvalidDataException) { /* documented contract */ }
    }

    /// <summary>
    /// Drive arbitrary bytes through the NET/ROM network-layer wire parsers. All are total
    /// by contract — they return <c>false</c> on a malformed datagram (short header, bad
    /// callsign octets, truncated NODES body), never throw. Covers the full datagram
    /// (<see cref="NetRomPacket"/>), the network/transport headers, and a NODES broadcast
    /// under both option presets.
    /// </summary>
    private static void FuzzNetRomBytes(byte[] bytes)
    {
        var info = bytes.AsSpan();
        _ = NetRomPacket.TryParse(info, out _);
        _ = NetRomNetworkHeader.TryParse(info, out _);
        _ = NetRomTransportHeader.TryParse(info, out _);
        foreach (var options in new[] { NetRomParseOptions.Strict, NetRomParseOptions.Lenient })
        {
            _ = NodesBroadcast.TryParse(info, options, out _);
        }
    }

    // ─── APRS / AGW / NET/ROM structured generators ──────────────────────

    /// <summary>
    /// Produce a buffer shaped like a real APRS info field: a data-type-identifier byte
    /// from the common set, then format-plausible content with some bytes deliberately
    /// random so each decoder's reject branches are exercised.
    /// </summary>
    private static byte[] MostlyValidAprs(Random rng)
    {
        // Lead with a real APRS DTI; the body is a mix of the canonical shape and noise.
        char[] dtis = { '!', '=', '/', '@', ':', '>', ';', ')', 'T', '`', '\'' };
        char dti = dtis[rng.Next(dtis.Length)];
        var sb = new StringBuilder();
        sb.Append(dti);
        string[] fragments =
        {
            "5126.30N/00121.30W>", "4903.50N/07201.75W#", "WB2OSZ   :hello{1",
            "LEADER   *092345z", "T#005,199,000,255,073,123,01101001", "My status",
            "AID!4903.50N", "/A=001234", "000/000", "!!", "    ",
        };
        int frags = rng.Next(1, 4);
        for (int f = 0; f < frags; f++)
        {
            sb.Append(fragments[rng.Next(fragments.Length)]);
            if (rng.Next(3) == 0)
            {
                sb.Append((char)rng.Next(32, 127));   // stray printable
            }
        }
        var buf = Encoding.ASCII.GetBytes(sb.ToString());
        // 1-in-6 flip a byte fully random to probe the binary-ish (MIC-E / compressed) paths.
        if (buf.Length > 0 && rng.Next(6) == 0)
        {
            buf[rng.Next(buf.Length)] = (byte)rng.Next(256);
        }
        return buf;
    }

    /// <summary>
    /// Produce a buffer shaped like an AGW frame: a valid 36-byte header whose
    /// little-endian data-length field is sometimes truthful, sometimes wildly over-
    /// claimed / near Int32.MaxValue (the overflow-guard probe), followed by a body that
    /// usually does NOT match the advertised length.
    /// </summary>
    private static byte[] MostlyValidAgw(Random rng)
    {
        int bodyLen = rng.Next(0, 64);
        var body = new byte[bodyLen];
        rng.NextBytes(body);
        byte[] frame = new AgwFrame(
            (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256),
            From: "M0LTE-1", To: "GB7RDG", Data: body, UserField: (uint)rng.Next()).ToBytes();

        // Now corrupt the advertised data-length (bytes 28..31, little-endian) so the
        // body short / over-claim / overflow branches of Parse are all reached.
        uint claimed = rng.Next(5) switch
        {
            0 => (uint)bodyLen,                         // truthful
            1 => (uint)(bodyLen + rng.Next(1, 4096)),   // over-claim
            2 => uint.MaxValue,                          // overflow guard
            3 => (uint)int.MaxValue,                     // boundary
            _ => (uint)rng.Next(),                       // arbitrary
        };
        frame[28] = (byte)(claimed & 0xFF);
        frame[29] = (byte)((claimed >> 8) & 0xFF);
        frame[30] = (byte)((claimed >> 16) & 0xFF);
        frame[31] = (byte)((claimed >> 24) & 0xFF);
        // Occasionally truncate the whole buffer mid-header to probe the short-header guard.
        if (rng.Next(6) == 0 && frame.Length > 4)
        {
            Array.Resize(ref frame, rng.Next(0, AgwFrame.HeaderSize));
        }
        return frame;
    }

    /// <summary>
    /// Produce a buffer shaped like a NET/ROM datagram: a 20-octet network header (two
    /// AX.25-encoded callsigns + TTL + a 5-octet transport header) then a small payload,
    /// with address and circuit/opcode bytes partly random to drive the reject branches.
    /// </summary>
    private static byte[] MostlyValidNetRom(Random rng)
    {
        // NET/ROM network header = src callsign (7) + dest callsign (7) + TTL (1) +
        // transport header (5) = 20 octets, then payload.
        int payload = rng.Next(0, 48);
        var buf = new byte[20 + payload];
        int off = 0;
        WriteAddrSlot(buf, ref off, rng, isLast: false);    // source (7)
        WriteAddrSlot(buf, ref off, rng, isLast: false);    // dest (7)
        buf[off++] = (byte)rng.Next(8);                      // TTL
        // Transport header: circuit index/id, tx/rx seq, opcode.
        buf[off++] = (byte)rng.Next(256);
        buf[off++] = (byte)rng.Next(256);
        buf[off++] = (byte)rng.Next(256);
        buf[off++] = (byte)rng.Next(256);
        buf[off++] = (byte)rng.Next(256);                    // opcode + flags
        rng.NextBytes(buf.AsSpan(off));                      // payload
        // 1-in-5 NODES broadcast: signature byte 0xFF then mnemonic + entries.
        if (rng.Next(5) == 0 && buf.Length > 0)
        {
            buf[0] = 0xFF;
        }
        return buf;
    }

    // ─── APRS / AGW / NET/ROM seed corpora ───────────────────────────────

    private static IEnumerable<(string Name, byte[] Bytes)> AprsSeeds()
    {
        (string, string)[] seeds =
        {
            ("position.bin",  "!5126.30N/00121.30W>Packet.NET test"),
            ("pos-ts.bin",    "@092345z4903.50N/07201.75W>weather"),
            ("message.bin",   ":WB2OSZ   :hello world{1"),
            ("status.bin",    ">My status text here"),
            ("telemetry.bin", "T#005,199,000,255,073,123,01101001"),
            ("object.bin",    ";LEADER   *092345z4903.50N/07201.75W>"),
            ("item.bin",      ")AID!4903.50N/07201.75W#"),
            ("compressed.bin","!/5L!!<*e7>{?!"),
        };
        foreach (var (name, text) in seeds)
        {
            yield return (name, Encoding.ASCII.GetBytes(text));
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> AgwSeeds()
    {
        yield return ("data.bin",
            new AgwFrame(0, (byte)'D', 0xF0, "M0LTE-1", "GB7RDG",
                Encoding.ASCII.GetBytes("hello over agw"), 0).ToBytes());
        yield return ("empty.bin",
            new AgwFrame(0, (byte)'X', 0x00, "M0LTE", "GB7RDG", ReadOnlyMemory<byte>.Empty, 0).ToBytes());
        yield return ("header-only.bin",
            new AgwFrame(1, (byte)'K', 0xF0, "G7XYZ-2", "APRS", ReadOnlyMemory<byte>.Empty, 42).ToBytes());
    }

    private static IEnumerable<(string Name, byte[] Bytes)> NetRomSeeds()
    {
        // A minimal but well-formed NET/ROM datagram: src/dest callsigns + TTL + a
        // 5-octet transport header (info/connect-request-ish) + a short payload.
        static byte[] AddrSlot(string @base, byte ssid, bool last)
        {
            var slot = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                char c = i < @base.Length ? @base[i] : ' ';
                slot[i] = (byte)(c << 1);
            }
            slot[6] = (byte)((ssid << 1) | 0x60 | (last ? 0x01 : 0x00));
            return slot;
        }
        var info = new List<byte>();
        info.AddRange(AddrSlot("M0LTE", 1, last: false));
        info.AddRange(AddrSlot("GB7RDG", 0, last: false));
        info.Add(7);                                   // TTL
        info.AddRange(new byte[] { 0x10, 0x20, 0x00, 0x00, 0x05 });  // transport header
        info.AddRange(Encoding.ASCII.GetBytes("payload"));
        yield return ("info.bin", info.ToArray());
        yield return ("header-only.bin", info.GetRange(0, 20).ToArray());
    }

    // ─── APRS / AGW / NET/ROM afl entry points ───────────────────────────

    private static int RunAprsFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzAprsBytes(ms.ToArray());
        });
        return 0;
    }

    private static int RunAgwFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzAgwBytes(ms.ToArray());
        });
        return 0;
    }

    private static int RunNetRomFuzzer(string[] args)
    {
        Fuzzer.OutOfProcess.Run(stream =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            FuzzNetRomBytes(ms.ToArray());
        });
        return 0;
    }

    private sealed record Finding(byte[] Input, Exception Exception);

    private sealed class SmokeResult(string label, int iterations)
    {
        public string Label { get; } = label;
        public int Iterations { get; } = iterations;
        public List<Finding> Findings { get; } = [];
    }
}
