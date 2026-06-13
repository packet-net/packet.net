# WebAuthn / passkey ceremony review — 2026-06-13

A focused security review of the WebAuthn / passkey registration and authentication
ceremonies, picking up the thread left open by `docs/security-review-2026-06-13.md`
§"Residual / not done this pass" ("WebAuthn / passkey ceremony was not deep-reviewed
beyond the challenge-cache bounds"). The ceremonies are built on `Fido2.AspNet`
(`Fido2NetLib` 4.0.0-beta.16).

This is the written findings report. **No source behaviour was changed** — the surface
is sound; the items below are observations and deliberate-by-design notes, none of which
is a clear exploitable bug. See the "Considered and rejected" section for what was looked
at and why it is not a finding.

## Scope

| File | What was checked |
|---|---|
| `src/Packet.Node/Api/PdnWebAuthnApi.cs` | register + assert begin/complete endpoints, credential list/delete, principal binding, error handling |
| `src/Packet.Node/Api/WebAuthnFido2Builder.cs` | RP id pinning, accepted-origin computation (Host-spoof resistance) |
| `src/Packet.Node.Core/Auth/WebAuthnChallengeCache.cs` | challenge single-use / expiry / key-binding / bounded memory |
| `src/Packet.Node.Core/Auth/WebAuthnCredentialRecord.cs` | record shape, sign-count semantics |
| `src/Packet.Node.Core/Auth/IWebAuthnCredentialStore.cs` + `SqliteWebAuthnCredentialStore.cs` | credential persistence, ownership guard, counter update, duplicate handling |
| `src/Packet.Node/Program.cs` | DI wiring + the `read`-gate on the register/management group |
| `tests/Packet.Node.Tests` (WebAuthn*) | existing coverage / harness conventions |

## Verdict

**No exploitable findings.** Each of the high-value WebAuthn failure modes was checked
and is correctly defended:

1. **Challenge lifecycle — sound.** `WebAuthnChallengeCache.Take<T>` removes-and-returns
   via `ConcurrentDictionary.TryRemove` (atomic single-use); a replayed `complete` finds
   nothing (`PdnWebAuthnApi.cs:153`, `:344`). Each entry carries an absolute expiry off
   the injected `TimeProvider` (`WebAuthnChallengeCache.cs:105`, `:120`) — no wall-clock,
   5-minute default TTL. The verifier always compares the authenticator's signed challenge
   against the server-stashed options' challenge, never a client-echoed value
   (`PdnWebAuthnApi.cs:200`, `:369`). Registration is keyed to the enrolling **user**
   (`reg:<username>`); assertion is keyed to a fresh 256-bit CSPRNG session handle
   (`assert:<sessionId>`, `PdnWebAuthnApi.cs:540-541`) — one user's pending ceremony cannot
   be completed as another. `PruneExpired` (`:131`) bounds memory against abandoned
   ceremonies; it is called opportunistically on every begin.

2. **Origin & RP ID validation — sound.** RP id is fixed from config and never derived
   from the request (`WebAuthnFido2Builder.cs:48`). When the operator pins
   `AllowedOrigins`, **only** those are trusted — the request's own (spoofable) origin is
   deliberately not added (`WebAuthnFido2Builder.cs:71-78`), so a forged `Host` header
   cannot widen the accepted set. The zero-config path trusts the actual serving origin +
   loopback only, which is the intended localhost-first behaviour. `WebAuthnConfigValidator`
   rejects an IP-literal RP id (`NodeConfigValidator.cs:603`). Pinned by
   `WebAuthnFido2BuilderTests` including the explicit spoofed-Host case.

3. **Signature counter (clone detection) — sound.** The stored counter is passed as
   `MakeAssertionParams.StoredSignatureCounter` (`PdnWebAuthnApi.cs:470`); Fido2NetLib
   throws `Fido2VerificationException` on a regression, which is caught and rejected
   (`:202-216`). A second, belt-and-braces monotonic check runs in our own code
   (`:220-224`) for authenticators that use counters (`> 0`). On success the advanced
   counter is persisted via `UpdateSignCount` (`:228`, `SqliteWebAuthnCredentialStore.cs:185`).
   The `0 → 0` case (platform/passkey authenticators that never increment) is correctly
   treated as "no counter, no regression".

4. **User verification / presence — spec-compliant.** UP is always enforced by the
   library. UV is requested as `Preferred` for both registration and assertion
   (`PdnWebAuthnApi.cs:300`, `:112`). `Preferred` means UV is *not* mandatory — this is a
   deliberate, spec-valid policy choice for a localhost-first node, not a defect. (See
   Observation O-1.)

5. **Credential ↔ user binding — sound.**
   - *Assertion:* the credential is resolved by its raw id (`GetByCredentialId`,
     `:183`); the `IsUserHandleOwnerOfCredentialIdCallback` (`:473-478`) ties the
     authenticator's returned user handle to the stored username with a constant-time
     compare (`CryptographicOperations.FixedTimeEquals`). Registration forces
     `ResidentKey=Required` (`:299`), so every credential is discoverable and returns a
     user handle, meaning the callback always runs. The minted token is for the
     credential's *stored* owner — no cross-user acceptance.
   - *Registration:* `IsCredentialIdUniqueToUserCallback` (`:372-373`) plus the
     `credential_id` PRIMARY KEY (`SqliteWebAuthnCredentialStore.cs:39`) make a credential
     id globally unique — a duplicate `Add` returns `false` on SQLITE_CONSTRAINT (`:111`)
     and the enrolment fails closed (`PdnWebAuthnApi.cs:392-396`). No overwrite of another
     user's credential is possible.
   - *Delete:* the `username` predicate in the SQL is the ownership guard
     (`SqliteWebAuthnCredentialStore.cs:213-214`) — a caller can only remove their own
     passkey, regardless of the id supplied.

6. **Error handling — fails closed.** Every failure path (`WebAuthnUnavailable`,
   malformed request, unparseable response, no pending challenge, unknown credential,
   owner-gone, verification exception, counter regression) returns a rejection; there is no
   fall-through to an authenticated state. `Fido2VerificationException` is the only caught
   exception around the verify and it always rejects (`:202`, `:376`). The assert failures
   all return a single generic 401 (`AssertionRejected`) so no step is a behavioural oracle.

7. **Secrets / PII in logs — clean.** Audit logs carry username + client IP + a short
   reason string (`AuthLog.*`), never the challenge, the attestation/assertion blob, the
   public key, or the credential id. The store's `WebAuthnCredentialSummary` projection
   omits the public key entirely (`WebAuthnCredentialRecord.cs:64`). The principal username
   always comes from the authenticated principal, never the request body
   (`PdnWebAuthnApi.cs:527-538`).

## Observations (not bugs — flagged for the owner, not changed)

These are deliberate design positions or low-value polish items. Per the review's
"report-don't-change for anything ambiguous or architecturally significant" rule, none was
modified.

- **O-1 (informational): UV is `Preferred`, not `Required`.** Both ceremonies request
  `UserVerification = Preferred` (`PdnWebAuthnApi.cs:112`, `:300`). This is spec-valid and
  appropriate for the localhost-first threat model, but it means a passkey can authenticate
  with user-presence only (no PIN/biometric) if the authenticator chooses. If a real-domain
  operator wants to *require* UV (e.g. the `pdn.m0lte.uk`-from-a-phone tier), that would be a
  config knob on `WebAuthnConfig` plumbed into both `GetAssertionOptionsParams.UserVerification`
  and the `AuthenticatorSelection`. **Flagged, not changed** — tightening the default would be
  a behaviour change and is a policy decision for the operator, not a bug.

- **O-2 (low / by-design): username-scoped `assert/begin` is a passkey-existence oracle.**
  `assert/begin` with a `username` returns that user's credential descriptors (ids +
  transports) in `allowCredentials`; an unknown user — or one with no passkeys — returns an
  empty list (`PdnWebAuthnApi.cs:99-106`). The inline comment claims this does not leak
  whether a username exists; that is true only for users *without* passkeys. A user **with**
  enrolled passkeys is distinguishable (non-empty `allowCredentials`, and the credential ids
  are exposed pre-auth). This is **inherent to the standard non-discoverable `allowCredentials`
  assertion flow** — it is how username-scoped WebAuthn login works, and the endpoint must be
  pre-auth. The mitigation already in place is that the username-less / discoverable path
  (empty `allowCredentials`) is the default and leaks nothing. **Flagged, not changed:**
  removing the username-scoped allow-list would break non-discoverable / cross-device
  authenticators, an architecturally significant behaviour change. Worth a one-line doc note
  that the username field is an enrolment-existence oracle by design, and worth tightening the
  code comment at `:96-98` (which overstates the indistinguishability).

- **O-3 (nit): `IWebAuthnCredentialStore.GetAllCredentialIds` is unused by the endpoints.**
  The XML doc says it is for building the discoverable-assertion allow-list and enforcing
  global uniqueness, but the discoverable path deliberately uses an empty allow-list (correct —
  enumerating every user's credentials into a pre-auth response would be a privacy leak), and
  uniqueness is enforced via `GetByCredentialId` in the `IsCredentialIdUniqueToUserCallback`.
  The method is only exercised by its own unit tests. Harmless dead-ish surface; **not a
  security issue.** Could be removed or its doc-comment corrected in a future tidy.

- **O-4 (informational): `CounterRegressed` classifies by exception message substring.**
  The audit-log branch that distinguishes a clone from a generic verification failure matches
  `ex.Message.Contains("counter")` (`PdnWebAuthnApi.cs:483-484`). This is **log-classification
  only** — both branches reject — so a library message change would at worst mislabel an audit
  line, never weaken the rejection. The independent belt-and-braces check at `:220` is the
  durable counter guard. **Not a finding;** noted because it is the one place coupled to a
  library string.

## Considered and rejected (not findings)

- *Could a non-discoverable credential bypass the user-handle binding (callback only runs
  when a user handle is present)?* No — registration forces `ResidentKey=Required`, so every
  credential is discoverable and returns a user handle; the callback always runs in this flow.
- *Counter belt-and-braces is weaker than the library (only fires when `stored > 0`).* By
  design — the library is the primary check and covers the `stored==0, new>0` first-increment
  and `new<=stored` regression cases; our check is the redundant guard, not the sole one.
- *Challenge TTL too long?* 5 minutes is generous head-room for a few-seconds ceremony but is
  a tightly bounded, single-use, key-bound window — not a meaningful replay surface.
- *Sign-count `uint` ↔ SQLite signed-`long` round-trip.* Lossless (a uint always fits a signed
  64-bit), `unchecked((uint)row.SignCount)` reverses it; no truncation reachable.
- *Store faults opening an auth bypass.* Faults degrade to null/empty/false (lookups fail
  closed → "unknown credential" → 401; writes fail closed). A best-effort `UpdateSignCount`
  fault cannot fail an otherwise-good assertion, but the security-relevant counter check has
  already run in the verify path before that write, so a missed counter *persist* (not check)
  is the documented, acceptable degradation, not a clone bypass.

## Outcome

No source changed, no new tests required: there is no clear unambiguous bug to pin. The
existing test coverage (`WebAuthnChallengeCacheTests`, `WebAuthnFido2BuilderTests`,
`SqliteWebAuthnCredentialStoreTests`, `WebAuthnApiTests`, plus the CDP-virtual-authenticator
E2E `scripts/passkey-e2e.mjs`) already pins the security-critical properties — single-use,
expiry, key-binding, Host-spoof resistance, ownership-scoped delete, and the no-oracle
generic rejection. The owner may wish to act on O-1 (a `requireUserVerification` knob) and
O-2 (correct the over-strong comment at `PdnWebAuthnApi.cs:96-98`); both are policy/clarity,
not defects.
