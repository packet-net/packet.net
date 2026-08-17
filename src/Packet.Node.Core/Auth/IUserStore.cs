namespace Packet.Node.Core.Auth;

/// <summary>
/// The persistence seam for web control-API users and the JWT signing key, kept
/// web-free so it lives in <c>Packet.Node.Core</c> (the host wires the JWT
/// middleware + endpoints around it).
/// </summary>
/// <remarks>
/// Resilient like the NET/ROM routing store: a backing-store fault logs and
/// degrades (a query returns null/empty, a write returns false) — it never throws
/// out to crash the node. Implementations open a fresh pooled connection per call.
/// </remarks>
public interface IUserStore
{
    /// <summary>
    /// The number of users, or <c>null</c> when the store could not be read. <c>0</c>
    /// means first-run setup is still required.
    /// </summary>
    /// <remarks>
    /// The fault is deliberately <b>distinguishable</b> from "zero users": zero is the
    /// open gate for the unauthenticated <c>POST /setup</c> bootstrap, so a store fault
    /// that read as zero re-opened node-claiming on a node that already has an owner
    /// (review item C026). Callers treat <c>null</c> as "setup is over" (fail closed)
    /// and, where they can, surface it as a 503.
    /// </remarks>
    int? TryCount();

    /// <summary>Look up a user by exact username, or null if absent / on fault.</summary>
    UserRecord? FindByUsername(string username);

    /// <summary>
    /// Look up a user by the callsign bound to their over-RF TOTP credential
    /// (case-insensitive). Returns null if no user has that callsign or on a store
    /// fault. This is the lookup the over-RF authorization gate uses to resolve a
    /// connecting station's callsign to the account whose TOTP secret it must verify.
    /// </summary>
    UserRecord? FindByCallsign(string callsign);

    /// <summary>
    /// Bind a TOTP secret (base32) + a callsign to a user, resetting the replay
    /// high-water mark (<see cref="UserRecord.LastTotpCounter"/>) to "none accepted"
    /// so the very first presented code is eligible. Enforces callsign uniqueness:
    /// returns <c>false</c> if the callsign is already bound to a DIFFERENT user (so
    /// the caller can surface a 409), if the user does not exist, or on a store fault;
    /// <c>true</c> on success.
    /// </summary>
    bool SetTotpSecret(string username, string secret, string callsign);

    /// <summary>
    /// Clear a user's TOTP enrolment — null out the secret, callsign, and replay
    /// counter. Returns <c>true</c> if a row changed, <c>false</c> if the user was
    /// absent / already had none / on a store fault.
    /// </summary>
    bool ClearTotp(string username);

    /// <summary>
    /// Persist the new TOTP replay high-water mark for a user (the counter
    /// <see cref="TotpService.TryVerify"/> just accepted). Best-effort in intent, but
    /// returns <c>false</c> on a store fault so the over-RF gate KNOWS the counter
    /// could not be persisted (and can refuse to treat the code as consumed rather
    /// than risk a replay window). <c>true</c> on success.
    /// </summary>
    bool UpdateTotpCounter(string username, long counter);

    /// <summary>All users (hash included — callers project to <see cref="UserSummary"/>
    /// before returning to a client). Empty on fault.</summary>
    IReadOnlyList<UserRecord> List();

    /// <summary>
    /// Create a user. Returns <c>false</c> if the username already exists
    /// (UNIQUE violation) or on a store fault; <c>true</c> on success.
    /// </summary>
    bool Create(UserRecord user);

    /// <summary>
    /// Create a user <b>only while no user exists</b> - the first-run bootstrap's
    /// one-shot, enforced in SQL (<c>INSERT ... WHERE NOT EXISTS</c>) rather than by a
    /// read-then-write check, so two concurrent <c>POST /setup</c> calls (or one racing a
    /// store fault) cannot both claim the node. Returns <c>false</c> if any user already
    /// exists, on a UNIQUE violation, or on a store fault.
    /// </summary>
    bool CreateFirst(UserRecord user);

    /// <summary>Delete a user by username. Returns <c>true</c> if a row was
    /// removed, <c>false</c> if absent or on fault.</summary>
    bool Delete(string username);

    /// <summary>Stamp a user's last-login time. Best-effort: a fault is swallowed
    /// (a failed last-login update must never fail an otherwise-good login).</summary>
    void UpdateLastLogin(string username, DateTimeOffset whenUtc);

    /// <summary>
    /// The persisted 256-bit JWT signing key, generating + storing it on first
    /// call so tokens survive a restart. Returns null only if the store is so
    /// broken it can neither read nor persist a key (auth then cannot be enabled
    /// safely — the host treats a null key as "auth unavailable"). Never logged.
    /// </summary>
    byte[]? GetOrCreateSigningKey();

    /// <summary>
    /// Replace the persisted signing key with a fresh 256-bit one, returning it (null on
    /// a store fault). <b>Every JWT this node ever issued stops validating</b> - panel
    /// access tokens and the long-lived MCP bearer tokens alike - which is the only
    /// revocation a stateless token has. The running process keeps validating with the
    /// key it read at startup, so a rotation takes effect on restart; the
    /// <c>pdn auth rotate-signing-key</c> CLI is therefore the offline verb. Never logged.
    /// </summary>
    byte[]? RotateSigningKey();
}
