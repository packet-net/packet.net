namespace Packet.Node.Core.Api;

/// <summary>
/// The result of a connectionless AX.25 TEST ping run (the "axping" analogue) - the
/// shape <c>POST /api/v1/ping</c> returns. Field names match
/// <c>docs/node-api.md</c>'s <c>PingResult</c> schema + the web client's
/// <c>src/lib/types.ts</c>; System.Text.Json's web defaults camel-case the PascalCase
/// properties (<c>LossPct</c> → <c>lossPct</c>).
/// </summary>
/// <remarks>
/// A peer that never answers (no TEST responder, or out of range) → every reply
/// <see cref="PingReply.Timeout"/> with a null <see cref="PingReply.RttMs"/> → all
/// timeouts → <see cref="LossPct"/> 100. That is a normal result, not an error: not
/// every node implements TEST. <see cref="MinMs"/> / <see cref="AvgMs"/> /
/// <see cref="MaxMs"/> are computed over the successful replies only, and are 0 when
/// none succeeded.
/// </remarks>
public sealed record PingResult(
    IReadOnlyList<PingReply> Replies,
    int MinMs,
    int AvgMs,
    int MaxMs,
    double LossPct);

/// <summary>One probe in a <see cref="PingResult"/>: its sequence number, its round-trip
/// time in milliseconds (null on timeout), and whether it timed out.</summary>
public sealed record PingReply(int Seq, int? RttMs, bool Timeout);
