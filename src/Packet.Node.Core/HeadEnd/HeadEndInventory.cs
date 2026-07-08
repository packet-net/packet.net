namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// The <c>GET /inventory</c> response from a head-end daemon (<c>headend/</c>): the instance's
/// stable identity plus every serial device it bridges as a raw TCP byte pipe. PDN keys its
/// device→port bindings by <c>(instanceId, port.id)</c> and dials <see cref="HeadEndPortInfo.TcpPort"/>
/// for the transparent pipe. Field names bind to the Go daemon's camelCase JSON (see
/// <c>headend/api.go</c> <c>InventoryResponse</c>).
/// </summary>
public sealed record HeadEndInventory
{
    /// <summary>The head-end's stable instance id (a config value on the daemon, not its IP) — the
    /// same id an operator puts in <see cref="Configuration.HeadEndConfig.Id"/>.</summary>
    public string InstanceId { get; init; } = "";

    /// <summary>Every bridged serial port, in the daemon's stable discovery order.</summary>
    public IReadOnlyList<HeadEndPortInfo> Ports { get; init; } = [];
}

/// <summary>
/// One bridged serial device in a head-end's <see cref="HeadEndInventory"/>: its stable id + USB
/// hints (for reach-through identification in Stage 3b), the TCP port carrying the raw byte pipe,
/// and the current serial line params.
/// </summary>
public sealed record HeadEndPortInfo
{
    /// <summary>Stable device id — the <c>deviceId</c> a port config binds to. Since headend-v0.1.3
    /// (#575) this is the <c>/dev/serial/by-path</c> basename (the physical USB socket — unique by
    /// construction, stable across reboot/same-socket replug; a device moved to a different socket
    /// gets a new id), with the kernel <c>/dev</c> basename as an unstable last resort. It is
    /// never derived from by-id (see <see cref="ById"/>).</summary>
    public string Id { get; init; } = "";

    /// <summary>Which link <see cref="Id"/> was derived from — <c>by-path</c> (the stable
    /// <c>/dev/serial/by-path</c> basename) or <c>dev</c> (the kernel <c>/dev</c> basename, the
    /// unstable last resort). Null when the head-end predates the id-stability fields
    /// (&lt; headend-v0.1.3) and didn't report one — unknown, not assumed.</summary>
    public string? IdSource { get; init; }

    /// <summary>Whether <see cref="Id"/> survives a reboot / same-socket replug. <c>false</c> only
    /// for the <c>dev</c> fallback (the kernel name renumbers) — PDN warns on it, a binding to it
    /// may not survive a replug. Null when the head-end predates the id-stability fields
    /// (&lt; headend-v0.1.3): unknown — deliberately NOT defaulted to stable/true.</summary>
    public bool? IdStable { get; init; }

    /// <summary>The device's <c>/dev</c> path on the head-end (diagnostic only; PDN never opens it).</summary>
    public string DevPath { get; init; } = "";

    /// <summary>USB vendor id hint (e.g. <c>0403</c>), for Stage-3b identification heuristics.</summary>
    public string UsbVid { get; init; } = "";

    /// <summary>USB product id hint, for Stage-3b identification heuristics.</summary>
    public string UsbPid { get; init; } = "";

    /// <summary>The <c>/dev/serial/by-id</c> path, if any. Informational only (a device
    /// serial/model hint for identification) — NOT the id source: a shared, non-unique USB serial
    /// makes by-id collide and flip between sibling devices on replug (#574), which is why
    /// <see cref="Id"/> is by-path-derived instead.</summary>
    public string ById { get; init; } = "";

    /// <summary>The TCP port on the head-end host carrying this device's raw transparent byte pipe.</summary>
    public int TcpPort { get; init; }

    /// <summary>The line rate the head-end currently clocks the physical UART at (for a Tait CCDI port
    /// this is the CCDI rate PDN opens against; a NinoTNC CDC-ACM port's baud is fictional).</summary>
    public int Baud { get; init; }

    /// <summary>Current data bits (typically 8).</summary>
    public int DataBits { get; init; }

    /// <summary>Current parity — one of <c>none</c> / <c>even</c> / <c>odd</c>.</summary>
    public string Parity { get; init; } = "";

    /// <summary>Current stop bits (1 or 2).</summary>
    public int StopBits { get; init; }
}

/// <summary>
/// The effective serial line params a head-end reports back from <c>POST /ports/{id}/line</c> (and
/// carries per-port in the inventory) — the shape the line-control verb echoes so PDN can confirm
/// the clock it asked for was applied.
/// </summary>
public sealed record HeadEndLineParams
{
    /// <summary>The effective baud after the set.</summary>
    public int Baud { get; init; }

    /// <summary>The effective data bits.</summary>
    public int DataBits { get; init; }

    /// <summary>The effective parity (<c>none</c> / <c>even</c> / <c>odd</c>).</summary>
    public string Parity { get; init; } = "";

    /// <summary>The effective stop bits.</summary>
    public int StopBits { get; init; }
}
