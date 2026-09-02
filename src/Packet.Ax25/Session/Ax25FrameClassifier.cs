namespace Packet.Ax25.Session;

/// <summary>
/// Classifies a parsed <see cref="Ax25Frame"/> into the matching
/// <see cref="Ax25Event"/> subtype. Inverse of
/// <see cref="FrameSpecExtensions"/> - that goes spec → frame → bytes
/// for outbound; this goes bytes (already parsed to a frame) → event
/// for inbound, ready to feed into <see cref="Ax25Session.PostEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure function over the control byte and frame-level properties - no
/// session state needed. The classifier looks only at the frame's
/// bit-level shape; it doesn't know whether the frame is destined for
/// us or some other station. The link layer is expected to address-
/// filter before calling this.
/// </para>
/// <para>
/// Modulo-independent. The I/S/U frame-type discriminator (bit 0; bits
/// 1-0) and the S-frame subtype (low nibble) / U-frame subtype all live in
/// the first control octet, which is identical under modulo-8 and extended
/// modulo-128 (Fig 4.1a/4.1b). The classifier reads only that octet, so it
/// classifies an extended frame correctly without knowing the modulo; the
/// 7-bit N(R)/N(S) (which <em>do</em> differ by modulo) are decoded later,
/// mode-aware, via <see cref="Ax25Frame.Nr"/> / <see cref="Ax25Frame.Ns"/>.
/// </para>
/// </remarks>
public static class Ax25FrameClassifier
{
    /// <summary>
    /// Map an inbound <see cref="Ax25Frame"/> to the
    /// <see cref="Ax25Event"/> the dispatcher should receive.
    /// </summary>
    /// <returns>
    /// A typed frame-receipt event (e.g. <see cref="SabmReceived"/>,
    /// <see cref="IFrameReceived"/>, <see cref="RrReceived"/>) when the
    /// control byte matches a known frame type. Falls back to
    /// <see cref="ControlFieldError"/> for control bytes that don't
    /// match any valid mod-8 frame pattern.
    /// </returns>
    public static Ax25Event Classify(Ax25Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Ax25FrameType type = frame.FrameType;

        if (type == Ax25FrameType.I)
        {
            return new IFrameReceived(frame);
        }

        // P/F bit and N(R) are preserved on the frame; neither affects classification.
        if (type.IsSupervisory())
        {
            // S frames carry no information field (§3.5). One present - accepted
            // only under a lenient parse; Ax25ParseOptions.Strict rejects it at
            // decode - is the data-link "information not permitted in frame" error
            // (DL-ERROR M), surfaced here so the figc4.x error-input transition
            // fires rather than the frame being silently processed as a plain RR.
            if (!frame.Info.IsEmpty)
            {
                return new InfoNotPermittedInFrame();
            }

            return type switch
            {
                Ax25FrameType.Rr => new RrReceived(frame),
                Ax25FrameType.Rnr => new RnrReceived(frame),
                Ax25FrameType.Rej => new RejReceived(frame),
                _ => new SrejReceived(frame),
            };
        }

        bool hasInfo = !frame.Info.IsEmpty;
        return type switch
        {
            // SABM/SABME/DISC/UA/DM carry no information field (§3.5; e.g. "an
            // information field is not permitted in a DISC command frame"). One
            // present - accepted only under a lenient parse - is the data-link
            // "information not permitted in frame" error (DL-ERROR M), so the
            // figc4.x error-input transition fires instead of the frame being
            // silently processed as a plain SABM/UA/DM/etc.
            Ax25FrameType.Sabm => hasInfo ? new InfoNotPermittedInFrame() : new SabmReceived(frame),
            Ax25FrameType.Sabme => hasInfo ? new InfoNotPermittedInFrame() : new SabmeReceived(frame),
            Ax25FrameType.Disc => hasInfo ? new InfoNotPermittedInFrame() : new DiscReceived(frame),
            Ax25FrameType.Ua => hasInfo ? new InfoNotPermittedInFrame() : new UaReceived(frame),
            Ax25FrameType.Dm => hasInfo ? new InfoNotPermittedInFrame() : new DmReceived(frame),
            // FRMR/XID/TEST/UI legitimately carry an information field.
            Ax25FrameType.Frmr => new FrmrReceived(frame),
            Ax25FrameType.Xid => new XidReceived(frame),
            Ax25FrameType.Test => new TestReceived(frame),
            Ax25FrameType.Ui => ClassifyUi(frame),
            _ => new ControlFieldError(),    // unknown U-frame control byte
        };
    }

    /// <summary>
    /// UI frames don't have a single dedicated event - they always
    /// arrive as <see cref="UiReceived"/>. Kept as its own helper for
    /// symmetry with the other shapes (and a future home for any
    /// info-field validation that needs to happen before routing).
    /// </summary>
    private static UiReceived ClassifyUi(Ax25Frame frame) => new(frame);
}
