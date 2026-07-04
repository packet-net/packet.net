namespace Packet.Node.Core.Api;

/// <summary>
/// The capability-doctor report for one port — the read model behind
/// <c>GET /api/v1/ports/{id}/doctor</c> (safe, non-transmitting) and
/// <c>POST /api/v1/ports/{id}/doctor?interrupt=true</c> (runs the transmitting probes too).
/// A plain-language pass/fail/unknown checklist an operator reads to answer "is this port's
/// radio setup healthy, and if not, what do I do about it?". System.Text.Json's web defaults
/// camel-case the properties.
/// </summary>
/// <param name="PortId">The port this report is for.</param>
/// <param name="Probes">The probe checklist, in run order.</param>
/// <param name="RanAt">When the run completed (UTC).</param>
public sealed record PortDoctorReport(
    string PortId,
    IReadOnlyList<PortDoctorProbe> Probes,
    DateTimeOffset RanAt);

/// <summary>
/// One capability probe in a <see cref="PortDoctorReport"/>.
/// </summary>
/// <param name="Name">Short probe identifier (e.g. <c>tnc-present</c>, <c>radio-present</c>,
/// <c>sdm</c>).</param>
/// <param name="Status">The verdict: <c>pass</c> (working), <c>fail</c> (broken — see
/// <see cref="Remedy"/>), or <c>unknown</c> (not determined — e.g. a transmitting probe skipped
/// on the safe form, or "not a NinoTNC").</param>
/// <param name="Detail">What was observed, in operator-facing prose.</param>
/// <param name="Remedy">One-line remedial action, or <c>null</c> when none applies.</param>
public sealed record PortDoctorProbe(
    string Name,
    string Status,
    string Detail,
    string? Remedy);
