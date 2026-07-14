namespace Packet.Node.Core.Api;

/// <summary>
/// The result of a local rig scan (<c>GET /api/v1/rigs/scan</c>): every candidate CAT-capable
/// serial device on this machine, marked with what already claims it and — where the USB by-id
/// descriptor is genuinely model-distinctive — a suggested hamlib model. This is the "plug in and
/// go" preview the rig-setup wizard shows; adoption happens through the normal port-write path
/// (attaching a <c>rig:</c> block), never here. Passive identification only: the scan never
/// writes to a serial device. System.Text.Json's web defaults camel-case the members.
/// </summary>
/// <param name="Devices">One row per candidate serial device (<c>/dev/ttyUSB*</c> +
/// <c>/dev/ttyACM*</c>, or the <c>PACKETNET_RIG_PORTS</c> override).</param>
/// <param name="CatalogueAvailable">Whether the hamlib model catalogue (<c>rigctl -l</c>) was
/// available to resolve suggestion model numbers — false on a host without rigctl installed
/// (suggestions then carry a null <see cref="RigSuggestion.ModelNumber"/>).</param>
public sealed record RigScan(
    IReadOnlyList<RigScanDevice> Devices,
    bool CatalogueAvailable);

/// <summary>
/// One local serial device seen by a rig scan.
/// </summary>
/// <param name="DevicePath">The device path right now (e.g. <c>/dev/ttyUSB0</c>) — may renumber
/// across replug/reboot; prefer <see cref="ByIdPath"/> for a <c>rig.device:</c> binding.</param>
/// <param name="ByIdPath">The <c>/dev/serial/by-id/*</c> symlink that canonicalises to
/// <see cref="DevicePath"/>, or null when there is none, when two symlinks collide (shared USB
/// serial — ambiguous), or off Linux.</param>
/// <param name="Descriptor">The by-id basename (the udev USB descriptor string, e.g.
/// <c>usb-Icom_Inc._IC-705_IC-705_12345678-if00</c>), or null when <see cref="ByIdPath"/> is.</param>
/// <param name="ClaimedBy">What already claims this device in the current config — a human
/// description like <c>port 'hf' rig</c> or <c>port 'vhf' transport (serial-kiss)</c> — or null
/// when the device is free to adopt.</param>
/// <param name="Suggestion">A hamlib model suggestion derived from the descriptor, or null when
/// the descriptor is absent or not model-distinctive (a bare FTDI/CP210x/CH340 cable says
/// nothing about the rig behind it).</param>
public sealed record RigScanDevice(
    string DevicePath,
    string? ByIdPath,
    string? Descriptor,
    string? ClaimedBy,
    RigSuggestion? Suggestion);

/// <summary>
/// A suggested hamlib rig model for a scanned device.
/// </summary>
/// <param name="Manufacturer">Manufacturer as the hamlib catalogue spells it (e.g. <c>Icom</c>).</param>
/// <param name="Model">Model as the hamlib catalogue spells it (e.g. <c>IC-7300</c>).</param>
/// <param name="ModelNumber">The hamlib model number (<c>rigctld -m</c>), resolved at scan time
/// against the installed rigctl's catalogue by name — null when rigctl is absent or the installed
/// hamlib doesn't know the model. Never hardcoded: numbers drift across hamlib versions.</param>
/// <param name="Source">How the suggestion was derived — <c>by-id</c> (the USB descriptor) is the
/// only source in this slice (passive identification; no ID;/CI-V probing).</param>
public sealed record RigSuggestion(
    string Manufacturer,
    string Model,
    int? ModelNumber,
    string Source);

/// <summary>
/// One row of the hamlib model catalogue (<c>GET /api/v1/rigs/models</c>) — a parsed
/// <c>rigctl -l</c> line.
/// </summary>
/// <param name="Number">The hamlib model number (<c>rigctld -m</c>).</param>
/// <param name="Manufacturer">Manufacturer column (e.g. <c>Icom</c>).</param>
/// <param name="Model">Model column (e.g. <c>IC-7300</c>; may contain spaces —
/// <c>MARK-V FT-1000MP</c>).</param>
/// <param name="Status">Backend status column (<c>Stable</c> / <c>Beta</c> / <c>Alpha</c> / …),
/// or null when the line carried none.</param>
public sealed record RigCatalogueModel(
    int Number,
    string Manufacturer,
    string Model,
    string? Status);
