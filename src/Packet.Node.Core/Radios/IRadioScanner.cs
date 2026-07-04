using Packet.Node.Core.Api;

namespace Packet.Node.Core.Radios;

/// <summary>
/// Scans the machine's serial ports for attached radios — the node-host seam behind
/// <c>GET /api/v1/radios/scan</c>. Read-scope, but it opens candidate serial ports transiently to
/// probe them, so implementations keep it bounded (a timeout) and single-flight (no two concurrent
/// scans hammering the same bus). A test double substitutes scripted results without touching
/// hardware.
/// </summary>
public interface IRadioScanner
{
    /// <summary>
    /// Probe the candidate ports and return each radio found, with its stable CCDI serial and
    /// (on Linux) its <c>/dev/serial/by-id</c> symlink. <paramref name="baudRates"/> defaults to the
    /// CCDI factory rate (28800) only; pass more to sweep radios programmed at other rates. Bounded
    /// and safe: on timeout it returns what it found so far rather than hanging.
    /// </summary>
    Task<IReadOnlyList<RadioScanResult>> ScanAsync(
        IReadOnlyList<int>? baudRates = null, CancellationToken cancellationToken = default);
}
