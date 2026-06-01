namespace Packet.Ax25.Session;

/// <summary>
/// Per-session toggles for deliberate, documented deviations from the AX.25
/// SDL figures, used where a figure is a confirmed upstream spec defect.
/// </summary>
/// <remarks>
/// <para>
/// This is the session-layer analogue of <see cref="Packet.Core.Ax25ParseOptions"/>
/// (which covers wire-parse pragmatism). The SDL tables themselves
/// (<c>Packet.Ax25.Sdl</c>, from <c>m0lte/ax25sdl</c>) stay faithful to the
/// published figures — including their defects — so the canonical transcription
/// tracks the in-progress draft. Where a figure is provably wrong, the runtime
/// corrects it here, behind a named flag, rather than diverging the tables.
/// </para>
/// <para>
/// Philosophy mirrors <c>Ax25ParseOptions</c>: the <see cref="Default"/> preset
/// does the spec-<i>correct</i> thing so the stack works out of the box;
/// <see cref="StrictlyFaithful"/> turns every quirk off, reproducing the figures
/// exactly as drawn (defects and all) for strict conformance testing.
/// </para>
/// <para>
/// <b>Pattern for adding a quirk</b> (replicable): name the flag
/// <c>Ax25Spec&lt;issue&gt;…</c> after the <c>packethacking/ax25spec</c> issue it
/// works around — so it is greppable and removable once the spec is fixed —
/// default it to the corrected behaviour, document the spec prose + the de-facto
/// implementation evidence, and open a packet.net tracking issue to delete it
/// when ax25sdl ships a figure carrying the upstream resolution.
/// </para>
/// </remarks>
public sealed record Ax25SessionQuirks
{
    /// <summary>
    /// Work around <c>packethacking/ax25spec#38</c>: figc4.5 (Timer Recovery)
    /// draws the SREJ-received retransmit path as the generic fresh-DL-DATA
    /// "Push frame onto queue" verb followed by "Invoke Retransmission"
    /// (go-back-N). That contradicts §4.3.2.4 / §6.4.8 ("retransmission of the
    /// <i>single</i> I frame numbered N(R) … frames transmitted following … are
    /// not retransmitted"), figc4.4's correct SREJ handler, and every surveyed
    /// implementation (direwolf and linbpq do single-frame selective; linux and
    /// rax25 don't implement SREJ-driven go-back-N at all). direwolf's author
    /// independently flagged the exact box as a "2006 revision … cut-n-paste
    /// from the REJ flow chart" and disabled it.
    /// </summary>
    /// <remarks>
    /// When <c>true</c> (default), an SREJ-received transition does single-frame
    /// selective retransmit — it redirects the figure's "Push frame onto queue"
    /// to the figc4.4 "Push Old I Frame N(r) on Queue" behaviour and skips the
    /// go-back-N "Invoke Retransmission". When <c>false</c>, the figc4.5 figure
    /// runs as drawn (which also throws on the payload-less push — strict
    /// conformance only). Delete this quirk once ax25sdl ships a corrected
    /// figc4.5. Removal tracked at m0lte/packet.net#227 ← packethacking/ax25spec#38.
    /// </remarks>
    public bool Ax25Spec38SrejSelectiveRetransmit { get; init; } = true;

    /// <summary>
    /// Default preset — spec-<i>correct</i> behaviour (all quirks on). This is
    /// what a session uses unless explicitly configured otherwise.
    /// </summary>
    public static Ax25SessionQuirks Default { get; } = new();

    /// <summary>
    /// Every quirk off — execute the SDL figures exactly as drawn, including
    /// known defects. For strict conformance testing against the published
    /// figures, not for on-air use.
    /// </summary>
    public static Ax25SessionQuirks StrictlyFaithful { get; } = new()
    {
        Ax25Spec38SrejSelectiveRetransmit = false,
    };
}
