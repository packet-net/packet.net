namespace Packet.Ax25.Xid;

/// <summary>
/// Leniency knobs for <see cref="XidInfoField.TryParse(System.ReadOnlySpan{byte},XidParseOptions,out XidParameters?)"/>.
/// Mirrors the repo's spec-compliant-by-default philosophy (see
/// <c>docs/strict-vs-pragmatic-audit.md</c> and <c>CLAUDE.md</c>): the
/// <see cref="Strict"/> default rejects any malformed XID information field;
/// each accommodation for a non-conformant real-world peer is a named flag,
/// defaulted off.
/// </summary>
/// <remarks>
/// The outbound construction path (<see cref="XidInfoField.Encode"/>) has no
/// equivalent - it is unconditionally strict and never emits a malformed field.
/// </remarks>
public sealed record XidParseOptions
{
    /// <summary>
    /// Accept a Group Length that claims more parameter-field bytes than the
    /// buffer actually contains, by clamping to the available bytes. Strict
    /// spec (§4.3.3.7 ¶1021: GL is the exact parameter-field length) rejects
    /// this. Default <c>false</c>.
    /// </summary>
    public bool AllowGroupLengthOverrun { get; init; }

    /// <summary>
    /// Accept a PI/PL whose PV runs past the end of the parameter field (a
    /// trailing PI with no PL octet, or a PL larger than the remaining bytes),
    /// by taking only the bytes that remain. Strict spec rejects this - a
    /// well-formed parameter field is an exact run of complete PI/PL/PV
    /// triples. Default <c>false</c>.
    /// </summary>
    public bool AllowTruncatedParameter { get; init; }

    /// <summary>Spec-strict: reject any malformed XID information field. The default.</summary>
    public static XidParseOptions Strict { get; } = new();

    /// <summary>
    /// Lenient: tolerate a short/over-claimed Group Length and a truncated
    /// trailing parameter. Use for ingesting frames from peers that mis-size
    /// the XID info field; never for outbound construction.
    /// </summary>
    public static XidParseOptions Lenient { get; } = new()
    {
        AllowGroupLengthOverrun = true,
        AllowTruncatedParameter = true,
    };
}
