namespace Packet.Kiss.NinoTnc;

/// <summary>Behavioural knobs for <see cref="NinoTncSerialPort"/>.</summary>
public sealed record NinoTncSerialPortOptions
{
    /// <summary>
    /// Keep-alive poll (#580): when the TNC has produced no inbound KISS frame for this long, the
    /// driver issues a GETVER — the reply's bytes prove the link alive, so a TCP-backed pipe's
    /// read-idle liveness budget (5 min — the half-open-link detector) never trips on a channel
    /// that is merely RF-quiet, while a genuinely dead link (no reply bytes) still faults on that
    /// budget and reconnects fast. Mirrors the Tait driver's watchdog
    /// (<c>TaitCcdiRadioOptions.KeepAliveInterval</c>). <c>null</c> disables. Default 2 min —
    /// comfortably inside the 5-min idle budget; <see cref="NinoTncSerialPort.OpenTcp"/> applies
    /// it by default (a local serial port has no idle budget and takes no options).
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; init; } = TimeSpan.FromMinutes(2);
}
