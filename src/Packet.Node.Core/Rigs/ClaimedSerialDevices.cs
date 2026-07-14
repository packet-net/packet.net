using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// Collects every serial device the current config already claims — the "don't offer the operator
/// a device something else is using" set behind the rig scan (<c>GET /api/v1/rigs/scan</c>). Keys
/// are <b>canonical</b> device paths (symlinks resolved to their final target), so a config that
/// says <c>/dev/serial/by-id/usb-…</c> and a scan that enumerates <c>/dev/ttyUSB0</c> collide on
/// the same key; values are human "claimed by" descriptions (e.g. <c>port 'hf' rig</c>).
/// </summary>
/// <remarks>
/// <para>Disabled ports still claim: a device belonging to a port the operator merely toggled off
/// is not free to adopt. Sources per port: the serial-shaped transport device
/// (<c>serial-kiss</c> / <c>nino-tnc</c> / a device-bound <c>tait-transparent</c>), the radio
/// block's control device (<see cref="PortRadioConfig.Port"/>), and the rig block's CAT device
/// (<see cref="PortRigConfig.Device"/>).</para>
/// <para><b>Serial-number bindings are deliberately skipped</b>: a <c>radio.serial:</c> /
/// <c>tait-transparent serial:</c> binding names a CCDI serial number, not a device path — which
/// device it lands on is only knowable by probing, and this helper is passive. A serial-bound
/// radio's device therefore shows as unclaimed here; the operator (who bound it) knows better.</para>
/// <para>Head-end-bound devices live on another machine and never collide with a local scan, so
/// they contribute nothing. When two blocks claim the same device (a misconfiguration validation
/// may not police), the first claim in port order wins — one honest description beats two.</para>
/// </remarks>
public static class ClaimedSerialDevices
{
    /// <summary>
    /// The canonical-device-path → "claimed by" map for <paramref name="config"/>.
    /// <paramref name="canonicalise"/> is injectable for tests that can't create real symlinks;
    /// null uses <see cref="Canonicalise"/> (resolve the symlink chain, fall back to the path
    /// itself when it doesn't exist — a claim on an unplugged device still registers).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Collect(
        NodeConfig config, Func<string, string>? canonicalise = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        canonicalise ??= Canonicalise;

        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var port in config.Ports)
        {
            var transportDevice = port.Transport switch
            {
                SerialKissTransport s => s.Device,
                NinoTncTransport n => n.Device,
                // Only the device-path binding mode names a local device; a serial: binding is
                // resolved by probing at bring-up (see the type remarks), a head-end binding is
                // remote.
                TaitTransparentTransportConfig t when !t.IsHeadEndBound => t.Device,
                _ => null,
            };
            Claim(claimed, canonicalise, transportDevice,
                $"port '{port.Id}' transport ({port.Transport.Kind})");

            // radio.port: is a device path; radio.serial: is a CCDI serial (skipped — see remarks).
            Claim(claimed, canonicalise, port.Radio?.Port, $"port '{port.Id}' radio");

            Claim(claimed, canonicalise, port.Rig?.Device, $"port '{port.Id}' rig");
        }
        return claimed;
    }

    /// <summary>
    /// Canonicalise a device path: follow the symlink chain to its final target
    /// (<c>/dev/serial/by-id/usb-… → /dev/ttyUSB0</c>) and normalise to a full path. Never
    /// throws — a path that doesn't exist (unplugged device) or can't be read canonicalises to
    /// itself, so equal strings still collide. The scanner uses the same function on the paths it
    /// enumerates, so both sides of a lookup agree.
    /// </summary>
    public static string Canonicalise(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var target = path;
        try
        {
            target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Doesn't exist (unplugged) / unreadable — canonicalise the literal path instead.
        }
        try
        {
            return Path.GetFullPath(target);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }

    private static void Claim(
        Dictionary<string, string> claimed, Func<string, string> canonicalise,
        string? device, string description)
    {
        if (!string.IsNullOrWhiteSpace(device))
        {
            claimed.TryAdd(canonicalise(device), description);
        }
    }
}
