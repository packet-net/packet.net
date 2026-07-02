using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// NinoTNC overlay over <see cref="KissFrameClassifier"/>. Runs the
/// generic classification first and then upgrades the result when the
/// frame matches a NinoTNC-firmware-specific shape — specifically the
/// synthetic TX-Test diagnostic frame the firmware emits when the
/// front-panel button is pressed.
/// </summary>
public static class NinoTncFrameClassifier
{
    /// <summary>
    /// Classify <paramref name="frame"/> with NinoTNC firmware awareness.
    /// Returns one of: <see cref="NinoTncTxTestFrameReceivedEvent"/>,
    /// <see cref="NinoTncStatusFrameReceivedEvent"/>,
    /// <see cref="NinoTncRssiReadingReceivedEvent"/>,
    /// <see cref="NinoTncAirTestFrameReceivedEvent"/>,
    /// <see cref="Ax25FrameReceivedEvent"/>,
    /// <see cref="AckModeDataReceivedEvent"/>, or
    /// <see cref="UnknownInboundEvent"/>. Never null.
    /// </summary>
    public static KissInboundEvent Classify(KissFrame frame)
    {
        var generic = KissFrameClassifier.Classify(frame);

        // 1) Labelled host-side diagnostic — the frame the firmware sends to
        //    its own host on a TX-Test button press (and, on firmware 3.41,
        //    as the GETALL reply). The "=FirmwareVr:" ASCII marker is the
        //    authoritative signal.
        if (generic is Ax25FrameReceivedEvent or UnknownInboundEvent &&
            frame.Command == KissCommand.Data &&
            NinoTncTxTestFrame.TryParse(frame, out var diag) && diag is not null)
        {
            return new NinoTncTxTestFrameReceivedEvent(frame, diag);
        }

        // 2) Numeric =II: register report — the periodic status frame (fake
        //    UI header, KISS Data), or the GETALL reply on firmware that
        //    answers numerically.
        if (generic is Ax25FrameReceivedEvent or UnknownInboundEvent &&
            NinoTncStatusFrame.TryParse(frame, out var status) && status is not null)
        {
            return new NinoTncStatusFrameReceivedEvent(frame, status);
        }

        // 3) GETRSSI reply — "RSSI:" ASCII on the 0xE0 reply command byte.
        if (generic is UnknownInboundEvent &&
            NinoTncRssiReading.TryParse(frame, out var rssi) && rssi is not null)
        {
            return new NinoTncRssiReadingReceivedEvent(frame, rssi);
        }

        // 4) Over-air TX-Test / CQBEEP UI frame — the AX.25 frame another
        //    NinoTNC's modulator put on the air (button press, or a host-
        //    built CQBEEP-N beep request). We receive this through our own
        //    modem as a normal KISS Data frame; the generic classifier
        //    already gave us the parsed Ax25Frame.
        if (generic is Ax25FrameReceivedEvent ax25Evt &&
            NinoTncAirTestFrame.TryRecognise(ax25Evt.Ax25, out var air) && air is not null)
        {
            return new NinoTncAirTestFrameReceivedEvent(frame, air);
        }

        return generic;
    }
}
