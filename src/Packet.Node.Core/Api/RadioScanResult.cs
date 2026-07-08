namespace Packet.Node.Core.Api;

/// <summary>
/// One radio found by a bus scan — the row shape of <c>GET /api/v1/radios/scan</c>. The
/// <see cref="Serial"/> (CCDI serial number) is the <b>primary stable key</b>: device paths renumber
/// across replug/reboot, and the CP2102 CCDI dongles share a USB serial so <c>/dev/serial/by-id</c>
/// can collide — the CCDI serial is the only reliable identity, and the one to bind a port to
/// (<c>radio.serial:</c>). System.Text.Json's web defaults camel-case the properties.
/// </summary>
/// <param name="Serial">The radio's CCDI serial number — the stable key to bind a port by.</param>
/// <param name="Model">Friendly product name (e.g. <c>Tait TM8110</c>).</param>
/// <param name="CcdiVersion">The CCDI protocol version the radio reports.</param>
/// <param name="Baud">The baud rate it answered at.</param>
/// <param name="DevicePath">The device it is on right now (e.g. <c>/dev/ttyUSB0</c>) — may differ
/// from one scan to the next as USB devices re-enumerate.</param>
/// <param name="ByIdPath">The <c>/dev/serial/by-id/*</c> symlink that canonicalises to
/// <see cref="DevicePath"/>, or <c>null</c> when there is none, when two dongles' symlinks collide
/// (shared USB serial — ambiguous), or off Linux. Informational only; bind by <see cref="Serial"/>.</param>
/// <param name="BandCode">The Tait band designator (e.g. <c>B1</c>) read from the radio's product
/// code, or null when the radio reported no parseable product code. Parity with the head-end scan's
/// device rows.</param>
/// <param name="AmateurBand">The UK amateur band the radio's split covers (<c>2m</c> / <c>70cm</c> /
/// <c>4m</c>), or null for a split with no amateur allocation — so a local-attach port can be
/// band-named exactly like a head-end-adopted one.</param>
public sealed record RadioScanResult(
    string Serial,
    string Model,
    string CcdiVersion,
    int Baud,
    string DevicePath,
    string? ByIdPath,
    string? BandCode = null,
    string? AmateurBand = null);
