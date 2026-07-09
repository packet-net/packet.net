using System.Globalization;

namespace Packet.Tune.Core;

/// <summary>
/// Supplies this station's live <see cref="StationStatus"/> when a hail arrives. The live
/// implementation (<see cref="NinoTncStationStatusSource"/>) reads the NinoTNC's running mode
/// and the radio's channel/RSSI; tests supply fakes. Called <em>promptly</em> on hail receipt
/// so a sampled RSSI-of-the-hail reflects the moment the hail was heard.
/// </summary>
public interface IStationStatusProvider
{
    /// <summary>Build this station's status in reply to <paramref name="hail"/>.</summary>
    Task<StationStatus> GetStatusAsync(StationHail hail, CancellationToken cancellationToken = default);
}

/// <summary>
/// The answering end of the station-hail protocol: listens for <see cref="StationHail"/>
/// telegrams on an <see cref="ITuningLink"/> and auto-replies with this station's
/// <see cref="StationStatus"/>. Opt-in by construction — nothing answers until
/// <see cref="RunAsync"/> is invoked (mirroring the TARPN-arming pattern; a station never
/// auto-advertises its state without an explicit enable).
/// </summary>
/// <remarks>
/// The reply is sent through the link's own <see cref="ITuningLink.SendAsync"/>, which — for
/// <see cref="SdmTuningLink"/> — already waits out the TM8110 post-receive auto-ack guard before
/// keying, so replying to a hail can never wedge the radio's ack engine. The responder itself
/// keeps no per-peer state: each hail is answered independently.
/// </remarks>
public sealed class StationHailResponder
{
    private readonly ITuningLink link;
    private readonly IStationStatusProvider provider;
    private int sequence = TuningTelegram.NewSessionSequenceBase();

    /// <summary>Create over a link + status provider. Both lifetimes stay the caller's.</summary>
    public StationHailResponder(ITuningLink link, IStationStatusProvider provider)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(provider);
        this.link = link;
        this.provider = provider;
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Listen and auto-reply until cancelled (or the link closes). Each inbound
    /// <c>HAIL</c> is answered with a <c>STAT</c> carrying this station's current status.
    /// Non-hail telegrams (mode-coordination, deviation-tuning, replies to our own hails)
    /// are ignored, so the responder can share a link with other side-channel traffic.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Log?.Invoke("responder: armed — listening for hails");
        try
        {
            await foreach (var telegram in link.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!StationHail.TryFromTelegram(telegram, out var hail) || hail is null)
                {
                    continue; // not a hail (mode-coord, tuning verbs, our own STAT echoes…)
                }
                await AnswerAsync(hail, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        Log?.Invoke("responder: stopped");
    }

    private async Task AnswerAsync(StationHail hail, CancellationToken cancellationToken)
    {
        Log?.Invoke($"responder: hail from {hail.RequesterCallsign ?? "(anonymous)"} — building status");
        StationStatus status;
        try
        {
            // Prompt read so a sampled RSSI-of-the-hail is fresh.
            status = await provider.GetStatusAsync(hail, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"responder: could not build status ({ex.Message}) — not replying");
            return;
        }

        try
        {
            await link.SendAsync(status.ToTelegram(NextSequence()), cancellationToken).ConfigureAwait(false);
            Log?.Invoke($"responder: replied with mode {status.Mode?.ToString(CultureInfo.InvariantCulture) ?? "?"} to {hail.RequesterCallsign ?? "(anonymous)"}");
        }
        catch (TuningLinkException ex)
        {
            Log?.Invoke($"responder: STAT undelivered ({ex.Message}) — the hailer's retry covers it");
        }
    }

    private int NextSequence() => Interlocked.Increment(ref sequence);
}
