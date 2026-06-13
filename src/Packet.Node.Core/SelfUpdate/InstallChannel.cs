namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// How this node was installed — which decides how (or whether) it self-updates.
/// The hard rule is "one owner per file": a node installed by a system package
/// manager must never overwrite the files that manager owns. See
/// <c>docs/node-self-update-design.md</c>.
/// </summary>
public enum InstallChannel
{
    /// <summary>Provenance could not be determined (no channel marker, no dpkg
    /// record). The node refuses to apply an update — it doesn't know what owns it.</summary>
    Unknown = 0,

    /// <summary>Installed from a system package manager (the maintainer's apt repo).
    /// dpkg owns the files; an update applies a <em>targeted</em> <c>apt</c> upgrade
    /// through the privileged helper — the node never touches the filesystem itself.</summary>
    Apt,

    /// <summary>Installed from the self-contained tarball / <c>install.sh</c>. Nothing
    /// else manages it, so the node owns the in-place atomic self-update (versioned
    /// dirs + a <c>current</c> symlink). (Self-update mechanics land in a later slice.)</summary>
    SelfContained,
}
