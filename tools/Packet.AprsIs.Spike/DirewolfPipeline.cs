using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Reusable decode_aprs subprocess pipeline. Both
/// <see cref="DirewolfMode"/> (decode raw corpus lines) and
/// <see cref="DirewolfRewriteMode"/> (decode envelope-rewritten lines)
/// share this code path.
/// </summary>
public static class DirewolfPipeline
{
    /// <summary>Structured per-frame result of one decode_aprs invocation.</summary>
    public sealed class DirewolfRow
    {
        public string? DecodedType { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? AltitudeM { get; set; }
        public string? Comment { get; set; }
        public bool HasError { get; set; }
        public string? FirstError { get; set; }
        public string RawOutput { get; set; } = "";
    }

    /// <summary>
    /// Feed <paramref name="lines"/> through <c>decode_aprs</c> in one
    /// subprocess. Returns parsed per-frame rows. The returned list is
    /// 1:1 with the input (with empty rows in slots where decode_aprs
    /// emitted no recognisable frame block, which shouldn't happen for
    /// well-formed TNC2 input but is logged as a warning).
    /// </summary>
    /// <remarks>
    /// Lines are sanitised: APRS-IS Q-construct paths (<c>,qA*,...</c>)
    /// are stripped per direwolf's documentation. CR/LF and the
    /// terminal's colour escapes are also stripped from the output so
    /// downstream parsing is robust to direwolf's ANSI usage.
    /// </remarks>
    public static async Task<IReadOnlyList<DirewolfRow>> DecodeLinesAsync(
        IReadOnlyList<string> lines, string direwolfBin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = direwolfBin,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment = { ["TERM"] = "dumb" },
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("decode_aprs failed to start");

        var writer = Task.Run(async () =>
        {
            foreach (var raw in lines)
            {
                await proc.StandardInput.WriteLineAsync(SanitiseForDirewolf(raw));
            }
            proc.StandardInput.Close();
        });

        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                output.AppendLine(line);
            }
        });

        await writer;
        await reader;
        await proc.WaitForExitAsync();

        var frames = SplitOutputByFrame(output.ToString());
        var rows = new List<DirewolfRow>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            if (i < frames.Count)
            {
                var row = ParseFrameOutput(frames[i]);
                row.RawOutput = frames[i];
                rows.Add(row);
            }
            else
            {
                rows.Add(new DirewolfRow { HasError = true, FirstError = "no decode_aprs output for this line" });
            }
        }
        return rows;
    }

    // ─── Internal: sanitisation, splitting, parsing ────────────────────

    private static readonly Regex QConstructStripper =
        new(@",q[A-Z][A-Z]?(,[^:]+)?:", RegexOptions.Compiled);

    private static readonly Regex AnsiStripper =
        new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    private static readonly Regex Tnc2Header =
        new(@"^[A-Za-z0-9\-]+>[A-Za-z0-9\-,*]+:", RegexOptions.Compiled);

    private static readonly Regex LatLonAlt = new(
        @"([NS])\s+(\d+)\s+(\d+\.\d+),\s+([EW])\s+(\d+)\s+(\d+\.\d+)(?:,\s+alt\s+(-?\d+)\s*m)?",
        RegexOptions.Compiled);

    public static string SanitiseForDirewolf(string rawLine)
        => QConstructStripper.Replace(rawLine, ":");

    public static List<string> SplitOutputByFrame(string output)
    {
        // Frame-boundary detection rule (FIXED 2026-05-14): a line that
        // matches the TNC2 header regex only counts as a NEW frame
        // boundary when it's preceded by a blank line. Direwolf's output
        // contains an ANSI colour change (which becomes a blank line
        // after AnsiStripper) between consecutive frame analyses, so
        // every real header is preceded by a blank.
        //
        // The previous version split on ANY header-matching line, which
        // misfired on concatenated frames like
        //   DB0OA-1>APDG03,TCPIP*,qAC,DB0OA-1:!pos1DB0OA-R>APDG03,...:!pos2
        // Direwolf takes the WHOLE input as one frame, decodes its
        // position, then emits the trailing portion as the "comment".
        // That comment line happens to start with `B0OA-R>APDG03,...:`,
        // which matched our header regex and made the splitter think
        // one input produced two frames — shifting all subsequent
        // batch-position alignments by one. ~2.4% of corpus rows were
        // affected before the fix.
        var stripped = AnsiStripper.Replace(output, "");
        var lines = stripped.Split('\n');
        var frames = new List<string>();
        var current = new StringBuilder();
        bool currentHasHeader = false;
        bool previousBlank = true;  // treat start-of-output as if preceded by blank
        foreach (var line in lines)
        {
            bool isBlank = string.IsNullOrWhiteSpace(line);
            bool isHeader = !isBlank && Tnc2Header.IsMatch(line);
            bool isBoundary = isHeader && previousBlank;
            if (isBoundary && currentHasHeader)
            {
                frames.Add(current.ToString().TrimEnd());
                current.Clear();
                currentHasHeader = false;
            }
            if (isBoundary) currentHasHeader = true;
            current.AppendLine(line);
            previousBlank = isBlank;
        }
        if (current.Length > 0 && currentHasHeader) frames.Add(current.ToString().TrimEnd());
        return frames;
    }

    public static DirewolfRow ParseFrameOutput(string frame)
    {
        var result = new DirewolfRow();
        var lines = frame.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return result;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("Error", StringComparison.Ordinal) ||
                line.StartsWith("ERROR", StringComparison.Ordinal) ||
                line.StartsWith("Warning", StringComparison.Ordinal) ||
                line.StartsWith("Invalid", StringComparison.Ordinal) ||
                line.StartsWith("Address ", StringComparison.Ordinal) ||
                line.StartsWith("Failed to ", StringComparison.Ordinal) ||
                (line.StartsWith("Digi", StringComparison.Ordinal) && line.Contains("Address")))
            {
                result.HasError = true;
                result.FirstError ??= line;
                continue;
            }

            var m = LatLonAlt.Match(line);
            if (m.Success)
            {
                double lat = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                           + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 60.0;
                if (m.Groups[1].Value == "S") lat = -lat;
                double lon = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)
                           + double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) / 60.0;
                if (m.Groups[4].Value == "W") lon = -lon;
                result.Latitude = lat;
                result.Longitude = lon;
                if (m.Groups[7].Success)
                {
                    result.AltitudeM = double.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
                }
                continue;
            }

            if (result.DecodedType is null && !line.Contains("0x", StringComparison.Ordinal) && line.Length < 120)
            {
                result.DecodedType = line;
                continue;
            }

            if (result.Comment is null && !line.StartsWith("Use of ", StringComparison.Ordinal)
                && !line.StartsWith("Tell the sender", StringComparison.Ordinal)
                && !line.StartsWith("For most ", StringComparison.Ordinal)
                && !line.StartsWith("\"", StringComparison.Ordinal))
            {
                result.Comment = line;
            }
        }

        return result;
    }
}
