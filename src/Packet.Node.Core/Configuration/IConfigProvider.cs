namespace Packet.Node.Core.Configuration;

/// <summary>
/// The single seam between "where config comes from" and "everything that
/// consumes it". A provider exposes the <see cref="Current"/> validated config
/// and raises <see cref="OnChange"/> whenever a new valid config is applied.
/// </summary>
/// <remarks>
/// <para>
/// Slice 1 ships <see cref="FileConfigProvider"/> (load + watch a YAML file).
/// A later slice ships a <c>SqliteConfigProvider</c> whose web-driven edits
/// raise the <b>same</b> <see cref="OnChange"/>, so the
/// <see cref="Hosting.NodeHostedService"/> reconcile logic is identical
/// regardless of where the config lives. Implementations must:
/// </para>
/// <list type="bullet">
/// <item>Fully parse + validate a candidate config <b>before</b> swapping
/// <see cref="Current"/> — an invalid candidate leaves <see cref="Current"/>
/// untouched and raises no <see cref="OnChange"/> (atomic apply / rollback by
/// construction).</item>
/// <item>Only raise <see cref="OnChange"/> after <see cref="Current"/> has been
/// updated, with the new value.</item>
/// </list>
/// </remarks>
public interface IConfigProvider
{
    /// <summary>The current, validated configuration. Never null once the
    /// provider has been constructed (first load happens in construction).</summary>
    NodeConfig Current { get; }

    /// <summary>
    /// Subscribe to config changes. The callback fires (off the watcher /
    /// editing path) with each newly-applied valid config. Dispose the returned
    /// token to unsubscribe.
    /// </summary>
    /// <param name="listener">Invoked with the new config after
    /// <see cref="Current"/> has advanced to it.</param>
    /// <returns>A disposable that removes the subscription.</returns>
    IDisposable OnChange(Action<NodeConfig> listener);
}
