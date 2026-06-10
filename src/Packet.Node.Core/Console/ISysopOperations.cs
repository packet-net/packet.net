using Packet.Node.Core.Auth;

namespace Packet.Node.Core.Console;

/// <summary>
/// The privileged node-administration actions an elevated over-RF sysop can drive from a
/// connected console session. Every method routes through the SAME serialized host seams
/// the web control API uses (<c>NodeHostedService.RunExclusiveAsync</c> for lifecycle /
/// session actions, <c>IWritableConfigProvider.TryApply</c> for config), so RF admin can
/// never race a config reconcile or a port bring-up — it is the identical validated path,
/// reached over a different transport.
/// </summary>
/// <remarks>
/// <para>
/// This interface is the AUTHORIZED surface only: the caller
/// (<c>NodeCommandService</c>) gates every call behind a live, unexpired
/// <c>SYSOP</c> elevation AND a sufficient scope. Implementations assume the caller has
/// already authorised; they do not re-check elevation.
/// </para>
/// <para>
/// Null-implementation contract: when no operations are wired (older call sites, tests, a
/// node where the host couldn't supply the seam), <see cref="SysopContext.Operations"/> is
/// null and the console reports the action as unavailable rather than invoking anything.
/// </para>
/// <para>
/// <b>Deliberately scoped (named, not silent).</b> The first tranche covers the
/// operationally meaningful RF-admin actions: list sessions, kick a session, bring a port
/// up/down, reload the conffile. Free-form config <c>SET &lt;path&gt; &lt;value&gt;</c> and
/// a full-node <c>RESTART</c> over RF are deferred by design — editing a deep config tree
/// char-by-char over a lossy 1200-baud line, and a restart that drops the operator's own
/// RF session, each want their own focused design — and this seam is shaped so they slot in
/// later without disturbing the elevation gate.
/// </para>
/// </remarks>
public interface ISysopOperations
{
    /// <summary>List the node's active sessions as human-readable lines
    /// (<c>portId:peer state</c>), under the host's exclusive gate. Empty when none.</summary>
    Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default);

    /// <summary>Disconnect a session identified as <c>portId:peer</c> (the same id
    /// <see cref="ListSessionsAsync"/> renders). Returns whether a matching session was
    /// found and asked to disconnect.</summary>
    Task<SysopActionResult> KickAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Enable or disable a configured port by id — persisted via
    /// <c>TryApply</c> then brought up/down under the exclusive gate (the same path the web
    /// Ports API uses). Returns the outcome + a message for the console.</summary>
    Task<SysopActionResult> SetPortEnabledAsync(string portId, bool enabled, CancellationToken ct = default);

    /// <summary>Re-read the on-disk conffile (the same reload the file-watcher triggers),
    /// applying any change through the reconcile path. Returns the outcome + a message.</summary>
    Task<SysopActionResult> ReloadAsync(CancellationToken ct = default);
}

/// <summary>The outcome of a privileged sysop action: whether it succeeded and a short
/// message the console prints back to the operator.</summary>
public sealed record SysopActionResult(bool Ok, string Message)
{
    public static SysopActionResult Success(string message) => new(true, message);
    public static SysopActionResult Failure(string message) => new(false, message);
}

/// <summary>
/// The dependencies the console needs to authenticate and serve an over-RF <c>SYSOP</c>
/// elevation: the user store (to resolve the connecting callsign / named user and read
/// their TOTP secret + replay high-water mark), the <see cref="TotpService"/> verifier, and
/// the privileged <see cref="ISysopOperations"/>. Bundled into one optional dependency so
/// the per-connection <see cref="NodeConsoleEnvironment"/> stays simple, and so a node with
/// none of it wired (auth off / older call site / test) simply has a null
/// <see cref="NodeConsoleEnvironment.Sysop"/> and the <c>SYSOP</c> command reports
/// "not available" — the default-off contract.
/// </summary>
public sealed class SysopContext
{
    public SysopContext(IUserStore users, TotpService totp, ISysopOperations operations)
    {
        Users = users ?? throw new ArgumentNullException(nameof(users));
        Totp = totp ?? throw new ArgumentNullException(nameof(totp));
        Operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    /// <summary>The user store — resolves a callsign (AX.25) or username (telnet) to the
    /// account whose TOTP secret a presented code is checked against.</summary>
    public IUserStore Users { get; }

    /// <summary>The RFC 6238 verifier (with the single-use replay guard).</summary>
    public TotpService Totp { get; }

    /// <summary>The privileged actions an elevated session may run.</summary>
    public ISysopOperations Operations { get; }
}
