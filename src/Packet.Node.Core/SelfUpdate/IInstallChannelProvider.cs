namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Resolves the node's <see cref="InstallChannel"/> — the seam the update endpoint
/// branches on. Resolved once and cached; the channel doesn't change at runtime.
/// </summary>
public interface IInstallChannelProvider
{
    /// <summary>The install channel this node was provisioned through.</summary>
    InstallChannel Channel { get; }
}
