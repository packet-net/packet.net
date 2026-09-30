using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Ax25.Monitor;

namespace Packet.Node.Core.Traffic;

/// <summary>
/// Runs a capture back through the AX.25 parser and the link observer (SP-003, #178): the
/// "I saw a strange frame, replay it" tool. Every frame is parsed <c>Strict</c> first and
/// <c>Lenient</c> second, so the report says which frames the spec rejects and which only a
/// preset accepts; every frame that parses is narrated by <see cref="Ax25LinkObserver"/> as a
/// third party would read the link, and the links are summarised at the end.
/// </summary>
public static class TrafficReplay
{
    /// <summary>One frame's outcome.</summary>
    /// <param name="Line">The capture line number (1-based).</param>
    /// <param name="Parse">How the frame parsed.</param>
    /// <param name="Event">The observer's reading, when the frame parsed.</param>
    public sealed record ReplayedFrame(int Line, TrafficCaptureFrame Frame, ParseOutcome Parse, Ax25LinkEvent? Event);

    /// <summary>How a frame parsed: by the spec, only leniently, or not at all.</summary>
    public enum ParseOutcome
    {
        Strict,
        LenientOnly,
        Rejected,
    }

    /// <summary>The whole replay.</summary>
    public sealed record ReplayReport(
        IReadOnlyList<ReplayedFrame> Frames,
        IReadOnlyList<Ax25LinkEvent> Expired,
        IReadOnlyList<Ax25LinkSnapshot> Links)
    {
        public int Total => Frames.Count;
        public int StrictCount => Frames.Count(f => f.Parse == ParseOutcome.Strict);
        public int LenientOnlyCount => Frames.Count(f => f.Parse == ParseOutcome.LenientOnly);
        public int RejectedCount => Frames.Count(f => f.Parse == ParseOutcome.Rejected);
    }

    /// <summary>Replay <paramref name="frames"/> in order. <paramref name="port"/> keeps only
    /// that port's frames when given.</summary>
    public static ReplayReport Run(IEnumerable<TrafficCaptureFrame> frames, string? port = null, Ax25LinkObserverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var observer = options is null ? new Ax25LinkObserver() : new Ax25LinkObserver(options);
        var replayed = new List<ReplayedFrame>();
        DateTimeOffset? last = null;
        int line = 0;
        foreach (var frame in frames)
        {
            line++;
            if (port is not null && !string.Equals(frame.Port, port, StringComparison.Ordinal))
            {
                continue;
            }
            var parse = Ax25Frame.TryParse(frame.Frame, Ax25ParseOptions.Strict, out _) ? ParseOutcome.Strict
                : Ax25Frame.TryParse(frame.Frame, Ax25ParseOptions.Lenient, out _) ? ParseOutcome.LenientOnly
                : ParseOutcome.Rejected;
            Ax25LinkEvent? ev = parse == ParseOutcome.Rejected ? null : observer.Observe(frame.Port, frame.Frame, frame.At, frame.Transmitted);
            replayed.Add(new ReplayedFrame(line, frame, parse, ev));
            last = frame.At;
        }
        // Age out what nothing answered, as a live monitor's timer would, so an unanswered
        // call in the capture shows as such rather than as still calling.
        var expired = last is { } end ? observer.Expire(end + TimeSpan.FromHours(1)) : [];
        return new ReplayReport(replayed, expired, observer.Snapshot());
    }

    /// <summary>Render a report as text: one line per frame, then the links, then the parse
    /// tally. Plain ASCII, for a terminal.</summary>
    public static string Render(ReplayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        foreach (var f in report.Frames)
        {
            var when = f.Frame.At.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var dir = f.Frame.Transmitted ? "tx" : "rx";
            if (f.Event is { } ev)
            {
                var type = ev.FrameType is { } ft ? ft.ToString().ToUpperInvariant() : "?";
                var flags = ev.Flags == Ax25LinkFlags.None ? string.Empty : $" [{ev.Flags}]";
                var lenient = f.Parse == ParseOutcome.LenientOnly ? " (lenient parse)" : string.Empty;
                sb.Append(CultureInfo.InvariantCulture, $"{when} {f.Frame.Port} {dir} {ev.From}>{ev.To} {type} {ev.Narration}{flags}{lenient}");
            }
            else if (f.Parse == ParseOutcome.Rejected)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{when} {f.Frame.Port} {dir} REJECTED by Strict and Lenient: {Convert.ToHexString(f.Frame.Frame).ToLowerInvariant()}");
            }
            else
            {
                sb.Append(CultureInfo.InvariantCulture, $"{when} {f.Frame.Port} {dir} (parsed, not a link frame the observer follows)");
            }
            if (f.Frame.Truncated)
            {
                sb.Append(" (truncated in the capture)");
            }
            sb.AppendLine();
        }
        foreach (var ev in report.Expired)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{ev.At.ToUniversalTime():HH:mm:ss.fff} {ev.Port} -- {ev.From}>{ev.To} {ev.Narration}").AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"links: {report.Links.Count}");
        foreach (var link in report.Links)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {link.Id}: {link.State}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"frames: {report.Total} (strict {report.StrictCount}, lenient only {report.LenientOnlyCount}, rejected {report.RejectedCount})");
        return sb.ToString();
    }
}
