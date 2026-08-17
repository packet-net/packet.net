using Microsoft.AspNetCore.Http;
using Packet.Node.Core.Audit;

namespace Packet.Node.Api;

/// <summary>
/// Mirrors the security-relevant authentication events into the persisted
/// <see cref="IAuditLog"/> (<c>pdn.db</c>, surfaced by the admin-gated
/// <c>GET /api/v1/audit</c>) alongside the structured <see cref="AuthLog"/> journal lines.
/// </summary>
/// <remarks>
/// <para>
/// Node-claiming (<c>POST /setup</c>) emitted nothing at all - not even a journal line - and
/// the login/refresh/passkey/TOTP events reached the journal only, so the one surface an owner
/// actually reads showed no failed logins, no lockouts, no token-theft response and no
/// credential changes (review item C058). The events mirrored here are the ones that answer
/// "did someone try to get in, and did anything about my credentials change?":
/// <c>setup</c>, <c>login_failed</c>, <c>login_lockout</c>, <c>refresh_reuse_detected</c>,
/// <c>passkey_enrolled</c>, <c>passkey_deleted</c>, <c>totp_enrolled</c>, <c>totp_cleared</c>.
/// Successful logins stay journal-only: they are the high-volume, low-signal case, and every
/// privileged thing the session then does is audited on its own.
/// </para>
/// <para>
/// Same no-secrets rule as <see cref="AuditHttpExtensions"/>: usernames and outcomes, never a
/// password, token, token hash or TOTP secret. <see cref="IAuditLog.Record"/> never throws, so
/// this can be called from any auth path without a try/catch.
/// </para>
/// </remarks>
internal static class AuthAudit
{
    /// <summary>The audit source for auth events. Distinct from <c>rest</c> so an owner can
    /// tell an authentication event from an audited API write at a glance.</summary>
    internal const string Source = "auth";

    /// <summary>Record one auth event. <paramref name="outcome"/> follows
    /// <see cref="AuditEntry"/>: <c>ok</c> | <c>denied</c> | <c>error</c>.</summary>
    internal static void Record(
        IAuditLog audit, HttpContext http, TimeProvider clock,
        string action, string actor, string outcome, string detail = "")
    {
        audit.Record(AuditEntry.New(
            clock.GetUtcNow(), actor, Source, action, actor, outcome, detail,
            http.Connection.RemoteIpAddress?.ToString()));
    }
}
