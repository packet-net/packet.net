using Microsoft.Extensions.Logging;

namespace Packet.Node.Api;

/// <summary>
/// Structured audit-log lines for the auth endpoints, emitted via the
/// <see cref="LoggerMessage"/> source generator (allocation-free, repo logging rule).
/// </summary>
/// <remarks>
/// <b>No secrets ever cross this surface.</b> Every parameter is a username, a source
/// IP, a scope, or an outcome enum name — never a password, an access/refresh token,
/// or a token hash. The username is not secret (it appears in the user list), but a
/// missing one is redacted to a placeholder by the caller so a line never logs a blank.
/// </remarks>
internal static partial class AuthLog
{
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "auth: login OK user={User} ip={Ip} scope={Scope}")]
    public static partial void LoginSucceeded(ILogger logger, string user, string ip, string scope);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "auth: login FAIL user={User} ip={Ip}")]
    public static partial void LoginFailed(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning,
        Message = "auth: login LOCKED-OUT (rate limit) user={User} ip={Ip}")]
    public static partial void LoginLockedOut(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information,
        Message = "auth: refresh OK user={User} ip={Ip}")]
    public static partial void RefreshSucceeded(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning,
        Message = "auth: refresh REJECTED reason={Reason} ip={Ip}")]
    public static partial void RefreshRejected(ILogger logger, string reason, string ip);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning,
        Message = "auth: refresh REUSE-DETECTED — revoking token family user={User} ip={Ip}")]
    public static partial void RefreshReuseDetected(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Information,
        Message = "auth: logout user={User} ip={Ip}")]
    public static partial void Logout(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Information,
        Message = "auth: passkey REGISTERED user={User} ip={Ip}")]
    public static partial void PasskeyRegistered(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4009, Level = LogLevel.Warning,
        Message = "auth: passkey registration FAILED user={User} ip={Ip} reason={Reason}")]
    public static partial void PasskeyRegistrationFailed(ILogger logger, string user, string ip, string reason);

    [LoggerMessage(EventId = 4010, Level = LogLevel.Information,
        Message = "auth: passkey assertion OK (passwordless login) user={User} ip={Ip} scope={Scope}")]
    public static partial void PasskeyAssertionSucceeded(ILogger logger, string user, string ip, string scope);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Warning,
        Message = "auth: passkey assertion FAILED ip={Ip} reason={Reason}")]
    public static partial void PasskeyAssertionFailed(ILogger logger, string ip, string reason);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Warning,
        Message = "auth: passkey CLONE DETECTED — signature counter regressed user={User} ip={Ip}")]
    public static partial void PasskeyCloneDetected(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4013, Level = LogLevel.Information,
        Message = "auth: passkey DELETED user={User} ip={Ip}")]
    public static partial void PasskeyDeleted(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 4014, Level = LogLevel.Information,
        Message = "auth: TOTP enrolled (over-RF sysop code) user={User} ip={Ip} callsign={Callsign}")]
    public static partial void TotpEnrolled(ILogger logger, string user, string ip, string callsign);

    [LoggerMessage(EventId = 4015, Level = LogLevel.Warning,
        Message = "auth: TOTP enrolment FAILED user={User} ip={Ip} reason={Reason}")]
    public static partial void TotpEnrollFailed(ILogger logger, string user, string ip, string reason);

    [LoggerMessage(EventId = 4016, Level = LogLevel.Information,
        Message = "auth: TOTP cleared (over-RF sysop code) user={User} ip={Ip}")]
    public static partial void TotpCleared(ILogger logger, string user, string ip);
}
