using Packet.Node.Core.Api;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The production <see cref="IRadioScanner"/>: probes the machine's candidate serial ports via
/// <see cref="TaitRadioPortDiscovery"/> and, on Linux, annotates each hit with its
/// <c>/dev/serial/by-id</c> symlink (<see cref="SerialByIdResolver"/>). Kept bounded (a wall-clock
/// timeout) and single-flight (a semaphore) because a scan transiently opens serial ports — two at
/// once, or one that never returns, would be a bus hazard.
/// </summary>
public sealed class TaitRadioScanner : IRadioScanner, IDisposable
{
    /// <summary>Default hard ceiling on a scan — probing is serial (one port at a time) and each
    /// probe self-times-out sub-second, so a machine with a handful of ttyUSB devices finishes well
    /// inside this; the cap just bounds a pathological hang.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly SerialByIdResolver byId;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim single = new(1, 1);

    /// <summary>Build the scanner. <paramref name="byId"/> defaults to the standard udev resolver;
    /// <paramref name="timeout"/> defaults to <see cref="DefaultTimeout"/>.</summary>
    public TaitRadioScanner(SerialByIdResolver? byId = null, TimeSpan? timeout = null)
    {
        this.byId = byId ?? new SerialByIdResolver();
        this.timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RadioScanResult>> ScanAsync(
        IReadOnlyList<int>? baudRates = null, CancellationToken cancellationToken = default)
    {
        // Single-flight: never let two scans race onto the same serial bus.
        await single.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            var results = new List<RadioScanResult>();
            try
            {
                await foreach (var found in TaitRadioPortDiscovery
                                   .DiscoverAsync(baudRates, cts.Token).ConfigureAwait(false))
                {
                    results.Add(ToResult(found, byId.Resolve(found.Port)));
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own timeout tripped — return whatever we found rather than hanging or throwing.
            }
            return results;
        }
        finally
        {
            single.Release();
        }
    }

    /// <summary>Map one discovered radio to its scan row, carrying the band split
    /// (<see cref="TaitRadioIdentity.Band"/>) the remote head-end scan already surfaces — parity so a
    /// local-attach port can be band-named too (#586). Internal so the mapping is testable with a
    /// synthetic identity (the discovery itself needs real serial hardware).</summary>
    internal static RadioScanResult ToResult(TaitDiscoveredRadio found, string? byIdPath)
        => new(
            Serial: found.Identity.SerialNumber,
            Model: found.Identity.ProductName,
            CcdiVersion: found.Identity.CcdiVersion,
            Baud: found.BaudRate,
            DevicePath: found.Port,
            ByIdPath: byIdPath,
            BandCode: found.Identity.Band?.Code,
            AmateurBand: found.Identity.Band?.AmateurBand);

    /// <inheritdoc/>
    public void Dispose() => single.Dispose();
}
