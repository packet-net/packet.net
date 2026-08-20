namespace Packet.Node.Core.Api;

/// <summary>
/// The result of a local modem scan (<c>GET /api/v1/setup/devices</c>): every serial device on
/// this machine that could carry a KISS TNC, with the NinoTNCs among them positively identified
/// by their own firmware reply. This is what the first-run wizard's device picker offers, so the
/// operator chooses a modem from a list instead of typing <c>/dev/ttyACM0</c> and hoping.
/// System.Text.Json's web defaults camel-case the members.
/// </summary>
/// <param name="Devices">One row per candidate serial device, best-first (identified NinoTNCs
/// ahead of unidentified serial ports).</param>
/// <param name="PermissionDenied">True when at least one candidate could not be opened because
/// the node's user lacks permission on the device. The wizard turns this into the
/// "add packetnet to the dialout group" hint rather than an unexplained empty list; a correctly
/// packaged install never sets it (the postinst puts the service user in <c>dialout</c>).</param>
public sealed record ModemScan(
    IReadOnlyList<ModemScanDevice> Devices,
    bool PermissionDenied);

/// <summary>
/// One local serial device seen by a modem scan.
/// </summary>
/// <param name="DevicePath">The stable path to bind a port to: the <c>/dev/serial/by-id/*</c>
/// symlink when udev provides an unambiguous one, otherwise the kernel path. This is the value
/// that goes into a transport's <c>device:</c>.</param>
/// <param name="KernelPath">The kernel device path right now (e.g. <c>/dev/ttyACM0</c>). It can
/// renumber across replug/reboot, which is why <see cref="DevicePath"/> prefers by-id.</param>
/// <param name="Descriptor">The by-id basename (the udev USB descriptor string), or null when
/// there is no unambiguous by-id link.</param>
/// <param name="Kind">What the device is, as far as the scan could tell:
/// <c>nino-tnc</c> (it answered GETVER) or <c>serial</c> (a serial port that was not identified -
/// it may still be a KISS TNC, which has no identify handshake to answer).</param>
/// <param name="FirmwareVersion">The NinoTNC firmware string it replied with (e.g. <c>3.44</c>),
/// or null when the device is not an identified NinoTNC.</param>
/// <param name="ClaimedBy">What already claims this device in the current config - a human
/// description like <c>port 'vhf-1' transport (nino-tnc)</c> - or null when it is free.</param>
/// <param name="ProbeError">Why the NinoTNC identify did not succeed, in one operator-readable
/// phrase (<c>permission denied</c>, <c>no reply</c>, <c>device is busy</c>), or null when the
/// device was identified or was never probed.</param>
public sealed record ModemScanDevice(
    string DevicePath,
    string KernelPath,
    string? Descriptor,
    string Kind,
    string? FirmwareVersion,
    string? ClaimedBy,
    string? ProbeError);
