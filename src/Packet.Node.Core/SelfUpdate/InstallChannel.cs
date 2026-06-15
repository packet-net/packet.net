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

    /// <summary>A dpkg-managed <c>.deb</c> whose binary is upgradable from a configured
    /// apt repo (the maintainer's OARC reprepro repo). dpkg owns the files; an update
    /// applies a <em>targeted</em> <c>apt</c> upgrade through the privileged helper — the
    /// node never touches the filesystem itself. Resolved at runtime: stamp <c>deb</c>,
    /// the running binary is dpkg-owned, and <c>apt-cache policy</c> shows a real repo
    /// origin (not just <c>/var/lib/dpkg/status</c>).</summary>
    Apt,

    /// <summary>A dpkg-managed <c>.deb</c> that was <c>dpkg -i</c>'d from a GitHub Release
    /// — dpkg owns the files, but there is no apt repo to upgrade from, so the update must
    /// still go <em>through dpkg</em> (download the next release <c>.deb</c> → sha-verify →
    /// <c>dpkg -i</c>), never the self-contained symlink swap. Resolved at runtime: stamp
    /// <c>deb</c>, the binary is dpkg-owned, but <c>apt-cache policy</c> shows no repo origin
    /// (or <c>apt-cache</c> is absent — the conservative fall, since a Releases self-update is
    /// the safe worst case, never an unwanted apt upgrade).</summary>
    Github,

    /// <summary>Installed from the self-contained tarball / <c>install.sh</c>. Nothing
    /// else manages it, so the node owns the in-place atomic self-update (versioned
    /// dirs + a <c>current</c> symlink). Resolved on the build stamp alone (<c>selfcontained</c>)
    /// — or when the stamp says <c>deb</c>/absent but the running binary is <em>not</em>
    /// dpkg-owned (no <c>dpkg-query</c>, or it doesn't claim the binary). No dpkg/apt probe
    /// is ever made when the stamp already says <c>selfcontained</c>.</summary>
    SelfContained,
}
