namespace Packet.Ax25.Session;

/// <summary>
/// Classifies a parsed <see cref="Ax25Frame"/> into the matching
/// <see cref="Ax25Event"/> subtype. Inverse of
/// <see cref="FrameSpecExtensions"/> — that goes spec → frame → bytes
/// for outbound; this goes bytes (already parsed to a frame) → event
/// for inbound, ready to feed into <see cref="Ax25Session.PostEvent"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure function over the control byte and frame-level properties — no
/// session state needed. The classifier looks only at the frame's
/// bit-level shape; it doesn't know whether the frame is destined for
/// us or some other station. The link layer is expected to address-
/// filter before calling this.
/// </para>
/// <para>
/// Mod-8 only. Mod-128 (extended) frames use a 2-byte control field
/// which <see cref="Ax25Frame"/> doesn't model yet; until that lands
/// the classifier assumes 1-byte control.
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
        byte ctrl = frame.Control;

        // I-frame: bit 0 = 0. Lowest bit is the discriminator between
        // I and S/U.
        if ((ctrl & 0x01) == 0)
        {
            return new IFrameReceived(frame);
        }

        // S-frame: bits 1-0 = 01. SS bits at positions 3-2 pick the
        // subtype. P/F bit at 4 is preserved on the frame, doesn't
        // affect classification. N(R) bits 7-5 likewise.
        if ((ctrl & 0x03) == 0x01)
        {
            // S frames carry no information field (§3.5). One present — accepted
            // only under a lenient parse; Ax25ParseOptions.Strict rejects it at
            // decode — is the data-link "information not permitted in frame" error
            // (DL-ERROR M), surfaced here so the figc4.x error-input transition
            // fires rather than the frame being silently processed as a plain RR.
            if (!frame.Info.IsEmpty)
            {
                return new InfoNotPermittedInFrame();
            }

            return (ctrl & 0x0C) switch
            {
                0x00 => new RrReceived(frame),      // 0001
                0x04 => new RnrReceived(frame),     // 0101
                0x08 => new RejReceived(frame),     // 1001
                0x0C => new SrejReceived(frame),    // 1101
                _    => new ControlFieldError(),    // unreachable given mask 0x03==0x01
            };
        }

        // U-frame: bits 1-0 = 11. MMM at bits 7-5 and MM at bits 3-2
        // identify the subtype (P/F bit at 4 is ignored here).
        // Mask out P/F: ctrl & ~0x10 = base control octet.
        byte uBase = (byte)(ctrl & 0xEF);
        bool hasInfo = !frame.Info.IsEmpty;
        return uBase switch
        {
            // SABM/SABME/DISC/UA/DM carry no information field (§3.5; e.g. "an
            // information field is not permitted in a DISC command frame"). One
            // present — accepted only under a lenient parse — is the data-link
            // "information not permitted in frame" error (DL-ERROR M), so the
            // figc4.x error-input transition fires instead of the frame being
            // silently processed as a plain SABM/UA/DM/etc.
            0x2F => hasInfo ? new InfoNotPermittedInFrame() : new SabmReceived(frame),   // SABM
            0x6F => hasInfo ? new InfoNotPermittedInFrame() : new SabmeReceived(frame),  // SABME
            0x43 => hasInfo ? new InfoNotPermittedInFrame() : new DiscReceived(frame),   // DISC
            0x63 => hasInfo ? new InfoNotPermittedInFrame() : new UaReceived(frame),     // UA
            0x0F => hasInfo ? new InfoNotPermittedInFrame() : new DmReceived(frame),     // DM
            // FRMR/XID/TEST/UI legitimately carry an information field.
            0x87 => new FrmrReceived(frame),    // FRMR
            0xAF => new XidReceived(frame),     // XID
            0xE3 => new TestReceived(frame),    // TEST
            0x03 => ClassifyUi(frame),          // UI — special handling
            _    => new ControlFieldError(),    // unknown U-frame control byte
        };
    }

    /// <summary>
    /// UI frames don't have a single dedicated event — they always
    /// arrive as <see cref="UiReceived"/>. Kept as its own helper for
    /// symmetry with the other shapes (and a future home for any
    /// info-field validation that needs to happen before routing).
    /// </summary>
    private static UiReceived ClassifyUi(Ax25Frame frame) => new(frame);
}
