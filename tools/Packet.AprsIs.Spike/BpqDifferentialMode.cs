using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Kiss;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Differential test: pair every <c>kiss</c> MQTT message in the BPQ
/// corpus with its sibling <c>ax25/trace/bpqformat</c> message, parse
/// the KISS bytes through our <see cref="Ax25Frame.TryParse"/> +
/// <see cref="Ax25FrameClassifier"/>, parse the BPQ JSON monitor text,
/// and compare what each side says about the frame.
/// </summary>
/// <remarks>
/// <para>
/// The BPQ MQTT plugin publishes each on-air frame twice: once as
/// <c>kiss</c> (raw KISS bytes - either standard data frame cmd=0x00,
/// or ACKMODE cmd=0x0C with a 2-byte sequence tag prefix) and once as
/// <c>ax25/trace/bpqformat</c> (a JSON envelope around BPQ's own
/// monitor-format line, e.g.
/// <c>09:42:59R GB7RDG-2>EI5IYB-1 Port=3 &lt;XID C P> ...</c>). That gives
/// us a third-party reference decoder we can A/B against for every
/// frame type BPQ sees - including connected-mode I/RR/REJ/SREJ/UA/DM/
/// SABM/DISC/FRMR/XID that the APRS differential never exercised.
/// </para>
/// <para>
/// Comparison surface (in order of strictness):
/// <list type="number">
///   <item>source / destination callsign+SSID and digipeater path</item>
///   <item>frame-type tag (mapping our classifier output to BPQ's
///   short tags: SABM→<c>C</c>, DISC→<c>D</c>, otherwise verbatim)</item>
///   <item>command/response and P/F bits (the two address-field C-bits
///   per AX.25 §6.1.2 and the control-byte P/F bit)</item>
///   <item>N(s)/N(r) for I and S frames</item>
/// </list>
/// Each row is bucketed by the first mismatch found, or
/// <c>Match</c> if everything agrees.
/// </para>
/// </remarks>
public static class BpqDifferentialMode
{
    public static async Task<int> RunAsync(Options opts)
    {
        var dbs = ResolveDatabases(opts);
        if (dbs.Count == 0)
        {
            Console.Error.WriteLine($"# no SQLite files matched {opts.Db} / {opts.DataDir}");
            return 1;
        }

        Directory.CreateDirectory(opts.OutDir);

        var stats = new BucketStats();
        long processed = 0;
        foreach (var dbPath in dbs)
        {
            Console.WriteLine($"# scanning {dbPath} ...");
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            // Pull all messages ordered by id; we'll separate into kiss/bpq
            // streams and pair them by (direction, port) in arrival order.
            // The BPQ MQTT plugin publishes both kiss and bpqformat in lock-
            // step, so position-in-stream is the most reliable pairing key.
            var streams = new Dictionary<(string dir, int port), StreamPair>();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT id, ts_utc_us, format, direction, port, payload
                    FROM messages
                    WHERE format IN ('kiss', 'ax25/trace/bpqformat')
                      AND direction IS NOT NULL
                      AND port IS NOT NULL
                    ORDER BY id
                """;
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    long id = rdr.GetInt64(0);
                    long ts = rdr.GetInt64(1);
                    string fmt = rdr.GetString(2);
                    string dir = rdr.GetString(3);
                    int port = (int)rdr.GetInt64(4);
                    byte[] payload = (byte[])rdr.GetValue(5);

                    var key = (dir, port);
                    if (!streams.TryGetValue(key, out var sp))
                    {
                        sp = new StreamPair();
                        streams[key] = sp;
                    }
                    if (fmt == "kiss")
                    {
                        sp.Kiss.Add((id, ts, payload));
                    }
                    else
                    {
                        sp.Bpq.Add((id, ts, payload));
                    }
                }
            }

            foreach (var ((dir, port), sp) in streams)
            {
                int pairCount = Math.Min(sp.Kiss.Count, sp.Bpq.Count);
                if (sp.Kiss.Count != sp.Bpq.Count)
                {
                    Console.Error.WriteLine($"# warning: ({dir},{port}) stream mismatch: " +
                                            $"kiss={sp.Kiss.Count} bpq={sp.Bpq.Count}");
                }
                for (int i = 0; i < pairCount; i++)
                {
                    var (kid, kts, kpayload) = sp.Kiss[i];
                    var (bid, bts, bpayload) = sp.Bpq[i];

                    // Sanity: paired rows should be within a handful of ms.
                    long dtUs = Math.Abs(bts - kts);
                    if (dtUs > 1_000_000)
                    {
                        stats.Record(Bucket.UnpairedSkew, kid, $"|dt|={dtUs}us — stream drift");
                        continue;
                    }

                    ProcessPair(kid, bid, kpayload, bpayload, port, stats);
                    processed++;
                    if (opts.Limit > 0 && processed >= opts.Limit)
                    {
                        break;
                    }
                }
                if (opts.Limit > 0 && processed >= opts.Limit)
                {
                    break;
                }
            }
        }

        var reportPath = Path.Combine(opts.OutDir, "bpq-differential.md");
        await File.WriteAllTextAsync(reportPath, stats.RenderMarkdown(processed));
        Console.WriteLine();
        Console.WriteLine($"# scanned {processed:N0} pairs");
        stats.WriteShortSummary(Console.Out);
        Console.WriteLine($"# report -> {reportPath}");
        return 0;
    }

    // ─── Per-pair processing ───────────────────────────────────────────

    static void ProcessPair(long kid, long bid, byte[] kissBytes, byte[] bpqJson,
                            int topicPort, BucketStats stats)
    {
        // 1. Extract AX.25 bytes from the MQTT-published kiss payload.
        //    The BPQ MQTT plugin uses different framings for sent vs rcvd:
        //      sent: standard KISS  (C0 cmd [body] C0), cmd 0x00 = Data or 0x0C = AckMode (2-byte tag prefix)
        //      rcvd: BPQ internal   (00 00 00 00 00 LEN 00 [ax25]) - no FEND wrapping, no escapes
        //    Detect via the leading byte.
        if (!TryExtractAx25(kissBytes, out var ax25, out var framingError))
        {
            stats.Record(Bucket.KissDecodeFailed, kid, framingError);
            return;
        }

        // 3. AX.25 parse.
        if (!Ax25Frame.TryParse(ax25.Span, out var frame))
        {
            // Distinguish "BPQ-style frame with a blank dest or source slot"
            // (which our strict Callsign type rejects) from "real malformed
            // AX.25 we should look at". BPQ uses these for its ID beacons
            // and some stations' QRV broadcasts.
            var bucket = HasBlankCallsignField(ax25.Span)
                ? Bucket.BlankCallsignField
                : Bucket.Ax25ParseFailed;
            stats.Record(bucket, kid, $"{ax25.Length}B payload, ctl_offset={FindControlByteOffset(ax25.Span)}");
            return;
        }

        // 4. Classify control byte.
        var ev = Ax25FrameClassifier.Classify(frame);
        string ourTag = TagFor(ev);
        if (ourTag == "?")
        {
            stats.Record(Bucket.UnknownControlByte, kid,
                $"classifier returned {ev.GetType().Name} for control=0x{frame.Control:X2}");
            return;
        }

        // 5. Parse the BPQ JSON envelope.
        BpqLine? bpq;
        try
        {
            var doc = JsonDocument.Parse(bpqJson);
            var payload = doc.RootElement.GetProperty("payload").GetString() ?? "";
            bpq = BpqLine.Parse(payload);
        }
        catch (Exception ex)
        {
            stats.Record(Bucket.BpqParseFailed, kid, $"BPQ JSON parse: {ex.Message}");
            return;
        }
        if (bpq is null)
        {
            stats.Record(Bucket.BpqParseFailed, kid, "BPQ monitor regex didn't match");
            return;
        }

        // 6. Compare. First-mismatch-wins bucket assignment.
        // (a) Source / destination
        string ourSrc = frame.Source.Callsign.ToString();
        string ourDst = frame.Destination.Callsign.ToString();
        if (!CallsignEqual(ourSrc, bpq.Source))
        {
            stats.Record(Bucket.MismatchSource, kid,
                $"ours={ourSrc} bpq={bpq.Source} (kid={kid} bid={bid})");
            return;
        }
        if (!CallsignEqual(ourDst, bpq.Destination))
        {
            stats.Record(Bucket.MismatchDestination, kid,
                $"ours={ourDst} bpq={bpq.Destination}");
            return;
        }

        // (b) Digipeater path
        var ourDigis = frame.Digipeaters.Select(d => d.Callsign.ToString()).ToList();
        if (!DigiListEqual(ourDigis, bpq.Digipeaters))
        {
            stats.Record(Bucket.MismatchDigipeaters, kid,
                $"ours=[{string.Join(",", ourDigis)}] bpq=[{string.Join(",", bpq.Digipeaters)}]");
            return;
        }

        // (c) Frame-type tag
        if (ourTag != bpq.Tag)
        {
            stats.Record(Bucket.MismatchTag, kid,
                $"ours=<{ourTag}> bpq=<{bpq.Tag}> control=0x{frame.Control:X2}");
            return;
        }

        // (d) Command/Response — BPQ omits this on some legacy frame types.
        //     Only check when BPQ asserts one.
        if (bpq.IsCommand.HasValue && bpq.IsCommand.Value != frame.IsCommand)
        {
            stats.Record(Bucket.MismatchCommandResponse, kid,
                $"ours=cmd:{frame.IsCommand}/resp:{frame.IsResponse} " +
                $"bpq=cmd:{bpq.IsCommand}");
            return;
        }

        // (e) P/F bit — likewise BPQ omits on UI frames.
        if (bpq.PollFinal.HasValue && bpq.PollFinal.Value != frame.PollFinal)
        {
            stats.Record(Bucket.MismatchPollFinal, kid,
                $"ours=PF:{frame.PollFinal} bpq=PF:{bpq.PollFinal}");
            return;
        }

        // (f) N(s) / N(r) for mod-8 I and S frames.
        if ((frame.Control & 0x01) == 0)
        {
            // I-frame: bits 3-1 = N(s), bits 7-5 = N(r).
            int ourNs = (frame.Control >> 1) & 0x07;
            int ourNr = (frame.Control >> 5) & 0x07;
            if (bpq.Ns.HasValue && bpq.Ns.Value != ourNs)
            {
                stats.Record(Bucket.MismatchNs, kid, $"ours=N(s)={ourNs} bpq=N(s)={bpq.Ns}");
                return;
            }
            if (bpq.Nr.HasValue && bpq.Nr.Value != ourNr)
            {
                stats.Record(Bucket.MismatchNr, kid, $"ours=N(r)={ourNr} bpq=N(r)={bpq.Nr}");
                return;
            }
        }
        else if ((frame.Control & 0x03) == 0x01)
        {
            // S-frame: only N(r) present (bits 7-5).
            int ourNr = (frame.Control >> 5) & 0x07;
            if (bpq.Nr.HasValue && bpq.Nr.Value != ourNr)
            {
                stats.Record(Bucket.MismatchNr, kid, $"ours=N(r)={ourNr} bpq=N(r)={bpq.Nr}");
                return;
            }
        }

        stats.Record(Bucket.Match, kid, $"<{ourTag}> {ourSrc}>{ourDst}");
    }

    /// <summary>
    /// Pull the bare AX.25 frame bytes out of one MQTT kiss payload. Handles
    /// both BPQ framings the corpus contains: standard KISS for "sent"
    /// frames (C0-wrapped, with FESC unescaping, cmd 0x00 Data or 0x0C
    /// AckMode-with-2-byte-tag), and BPQ-internal for "rcvd" frames
    /// (7-byte prefix `00 00 00 00 00 LEN 00` then raw AX.25, no FEND).
    /// </summary>
    static bool TryExtractAx25(byte[] bytes, out ReadOnlyMemory<byte> ax25, out string error)
    {
        ax25 = default;
        error = "";
        if (bytes.Length == 0) { error = "empty payload"; return false; }

        if (bytes[0] == KissFraming.Fend)
        {
            // KISS-framed.
            var decoder = new KissDecoder();
            var frames = decoder.Push(bytes);
            if (frames.Count != 1)
            {
                error = $"KISS decoder produced {frames.Count} frames (expected 1)";
                return false;
            }
            var kf = frames[0];
            if (kf.Command == KissCommand.Data)
            {
                ax25 = kf.Payload.AsMemory();
                return true;
            }
            if (kf.Command == KissCommand.AckMode)
            {
                if (!KissAckMode.TryParseDataFrame(kf, out _, out ax25))
                {
                    error = $"ACKMODE payload too short ({kf.Payload.Length}B)";
                    return false;
                }
                return true;
            }
            error = $"unexpected KISS command {kf.Command} (0x{(byte)kf.Command:X2})";
            return false;
        }

        // BPQ internal envelope: 7-byte prefix.
        //   byte 0-4: 00 00 00 00 00         (reserved / zero padding)
        //   byte 5-6: little-endian 16-bit total payload length
        //   byte 7+:  AX.25 frame bytes
        // Confirmed empirically: for every rcvd row in the corpus,
        // (bytes[5] | bytes[6] << 8) == bytes.Length.
        const int BpqHeaderLength = 7;
        if (bytes.Length < BpqHeaderLength + 14)  // 14 = min AX.25 (dest+src+control)
        {
            error = $"too short for BPQ internal envelope ({bytes.Length}B)";
            return false;
        }
        for (int i = 0; i < 5; i++)
        {
            if (bytes[i] != 0)
            {
                error = $"unknown framing (leading byte[{i}]=0x{bytes[i]:X2})";
                return false;
            }
        }
        int declaredLen = bytes[5] | (bytes[6] << 8);
        if (declaredLen != bytes.Length)
        {
            error = $"BPQ envelope length mismatch (declared {declaredLen}, actual {bytes.Length})";
            return false;
        }
        ax25 = bytes.AsMemory(BpqHeaderLength);
        return true;
    }

    /// <summary>
    /// Quick scan of an AX.25 address chain for an entirely-space callsign
    /// in either the destination (bytes 0-5) or source (bytes 7-12) slot —
    /// the BPQ corpus contains UI frames with empty dest (PD4R-12 status
    /// broadcasts) and empty source (BPQ's own ID beacons) that our strict
    /// <see cref="Callsign"/> type rejects but BPQ accepts on the wire.
    /// </summary>
    static bool HasBlankCallsignField(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 14)
        {
            return false;
        }

        return AllShiftedSpaces(bytes[..6]) || AllShiftedSpaces(bytes[7..13]);
    }

    static bool AllShiftedSpaces(ReadOnlySpan<byte> slot)
    {
        foreach (var b in slot)
        {
            if (b != 0x40)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walk the address chain looking for an end-extension bit; the next
    /// byte after that is the control byte. Returns -1 if the chain doesn't
    /// terminate within the allowed length. For diagnostics only.
    /// </summary>
    static int FindControlByteOffset(ReadOnlySpan<byte> bytes)
    {
        for (int i = 6; i < Math.Min(bytes.Length, 7 + 9 * 7); i += 7)
        {
            if ((bytes[i] & 0x01) != 0)
            {
                return i + 1;
            }
        }
        return -1;
    }

    // Map our classifier event → BPQ short tag.
    static string TagFor(Ax25Event ev) => ev switch
    {
        IFrameReceived => "I",
        RrReceived => "RR",
        RnrReceived => "RNR",
        RejReceived => "REJ",
        SrejReceived => "SREJ",
        SabmReceived => "C",       // BPQ writes <C> for Connect = SABM
        SabmeReceived => "C",       // mod-128 also rendered as <C> by BPQ
        DiscReceived => "D",
        UaReceived => "UA",
        DmReceived => "DM",
        FrmrReceived => "FRMR",
        XidReceived => "XID",
        TestReceived => "TEST",
        UiReceived => "UI",
        _ => "?",
    };

    static bool CallsignEqual(string ours, string theirs)
    {
        // Strict equality is enough - both sides emit "BASE" or "BASE-N".
        return string.Equals(ours, theirs, StringComparison.Ordinal);
    }

    static bool DigiListEqual(IList<string> a, IList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!CallsignEqual(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    // ─── BPQ monitor-line parser ──────────────────────────────────────

    /// <summary>
    /// Parsed shape of one BPQ monitor line - the text BPQ publishes in
    /// the JSON envelope's <c>payload</c> field.
    /// </summary>
    sealed record BpqLine(
        string Source,
        string Destination,
        IList<string> Digipeaters,
        int Port,
        string Tag,
        bool? IsCommand,
        bool? PollFinal,
        int? Ns,
        int? Nr)
    {
        // 09:42:59R [SRC][-SSID]>[DEST][-SSID][,VIA1[-SSID]][,VIA2...] Port=N <TAG flags...>[: info]
        // Source and destination may each be empty - BPQ emits e.g. ">IS" for
        // its own ID beacon (empty source) and "PD4R-12>,TEST" for that
        // station's QRV broadcast (empty destination).
        static readonly Regex Re = new(
            @"^(?<ts>\d{2}:\d{2}:\d{2}[A-Z]?)\s+" +
            @"(?<src>[A-Za-z0-9\-]*)>(?<dst>[A-Za-z0-9\-]*)" +
            @"(?:,(?<via>[A-Za-z0-9\-,*]+))?" +
            @"\s+Port=(?<port>\d+)\s+" +
            @"<(?<tag>[A-Z]+)(?<flags>[^>]*)>",
            RegexOptions.Compiled);

        public static BpqLine? Parse(string payload)
        {
            var m = Re.Match(payload);
            if (!m.Success)
            {
                return null;
            }

            var digis = new List<string>();
            if (m.Groups["via"].Success)
            {
                foreach (var raw in m.Groups["via"].Value.Split(','))
                {
                    var token = raw.TrimEnd('*');  // strip H-bit marker
                    if (token.Length > 0)
                    {
                        digis.Add(token);
                    }
                }
            }

            string tag = m.Groups["tag"].Value;
            string flagsText = m.Groups["flags"].Value;
            bool? isCommand = null;
            bool? pollFinal = null;
            int? ns = null;
            int? nr = null;
            foreach (var rawTok in flagsText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawTok == "C")
                {
                    isCommand = true;
                }
                else if (rawTok == "R")
                {
                    isCommand = false;
                }
                else if (rawTok == "P")
                {
                    pollFinal = true;
                }
                else if (rawTok == "F")
                {
                    pollFinal = true;   // F is the response-side P/F
                }
                else if (rawTok.Length >= 2 && rawTok[0] == 'S' && char.IsDigit(rawTok[1])
                         && int.TryParse(rawTok.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    ns = s;
                }
                else if (rawTok.Length >= 2 && rawTok[0] == 'R' && char.IsDigit(rawTok[1])
                         && int.TryParse(rawTok.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                {
                    nr = r;
                }
            }

            return new BpqLine(
                Source: m.Groups["src"].Value,
                Destination: m.Groups["dst"].Value,
                Digipeaters: digis,
                Port: int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture),
                Tag: tag,
                IsCommand: isCommand,
                PollFinal: pollFinal,
                Ns: ns,
                Nr: nr);
        }
    }

    static List<string> ResolveDatabases(Options opts)
    {
        if (!string.IsNullOrEmpty(opts.Db) && File.Exists(opts.Db))
        {
            return [opts.Db];
        }

        if (Directory.Exists(opts.DataDir))
        {
            return Directory.EnumerateFiles(opts.DataDir, "*.sqlite")
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        return [];
    }

    sealed class StreamPair
    {
        public List<(long id, long ts, byte[] payload)> Kiss { get; } = new();
        public List<(long id, long ts, byte[] payload)> Bpq { get; } = new();
    }

    enum Bucket
    {
        Match,
        MismatchSource,
        MismatchDestination,
        MismatchDigipeaters,
        MismatchTag,
        MismatchCommandResponse,
        MismatchPollFinal,
        MismatchNs,
        MismatchNr,
        KissDecodeFailed,
        KissNonDataFrame,
        BlankCallsignField,
        Ax25ParseFailed,
        UnknownControlByte,
        BpqParseFailed,
        UnpairedSkew,
    }

    sealed class BucketStats
    {
        const int MaxExamplesPerBucket = 20;
        readonly Dictionary<Bucket, long> counts = new();
        readonly Dictionary<Bucket, List<string>> examples = new();

        public void Record(Bucket bucket, long id, string detail)
        {
            counts.TryGetValue(bucket, out var c);
            counts[bucket] = c + 1;
            if (!examples.TryGetValue(bucket, out var list))
            {
                list = new List<string>();
                examples[bucket] = list;
            }
            if (list.Count < MaxExamplesPerBucket)
            {
                list.Add($"id={id} {detail}");
            }
        }

        public void WriteShortSummary(TextWriter w)
        {
            long total = counts.Values.Sum();
            if (total == 0) { w.WriteLine("#   (no rows)"); return; }
            foreach (var (bucket, count) in counts.OrderByDescending(kv => kv.Value))
            {
                w.WriteLine($"#   {bucket,-26} {count,10:N0}  {100.0 * count / total,5:F1}%");
            }
        }

        public string RenderMarkdown(long total)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BPQ corpus differential — our AX.25 decoder vs BPQ's monitor render");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Scanned **{total:N0}** paired (kiss, bpqformat) messages from the BPQ MQTT corpus.");
            sb.AppendLine();
            sb.AppendLine("## Bucket breakdown");
            sb.AppendLine();
            sb.AppendLine("| Bucket | Count | % |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var (bucket, count) in counts.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{bucket}` | {count:N0} | {100.0 * count / Math.Max(total, 1):F2}% |");
            }
            sb.AppendLine();
            sb.AppendLine("## Examples per bucket");
            sb.AppendLine();
            foreach (var bucket in Enum.GetValues<Bucket>())
            {
                if (!examples.TryGetValue(bucket, out var list) || list.Count == 0)
                {
                    continue;
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"### `{bucket}`");
                foreach (var ex in list)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(ex)}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string Escape(string s) =>
            s.Replace("`", "\\`", StringComparison.Ordinal)
             .Replace("\r", " ", StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal);
    }
}
