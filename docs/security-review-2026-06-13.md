# Security / hardness review — 2026-06-13

A focused security and robustness pass over packet.net, prompted by "time to do
some hardness testing." This is the written findings report; the code changes it
describes landed on the same branch (see `docs/plan.md` §17 for the per-change
ledger).

## Scope

| Area | What was looked at | Outcome |
|---|---|---|
| RHPv2 TCP server | `RhpServer` connection/handle/frame lifecycle, auth gate | **Hardened** — 4 changes |
| Wire parsers | APRS, AGW, NET/ROM (the un-fuzzed untrusted-byte parsers) | **Fuzzed** — 1 real bug found + fixed |
| Web auth core | JWT, refresh tokens, TOTP, password hashing, scope handler, login flow | **Reviewed — no exploitable findings** |
| CI | supply-chain / dependency scanning | **Added** a vulnerability-scan gate |

The AX.25 / KISS / XID / segment / node-command parsers were already well-fuzzed
(`tools/Packet.Fuzz`, ~hundreds of thousands of clean inputs) and were not the
focus; the new work extends that coverage outward and hardens the network-facing
host.

## 1. RHPv2 server hardening (fixed)

The RHPv2 TCP front-end (`src/Packet.Rhp2.Server/RhpServer.cs`) is the node's most
exposed live surface. It had no resource bounds, so a hostile or buggy peer could
wedge it. Closed four vectors (all configurable in the `rhp:` block, on by default):

1. **Connection exhaustion** — unbounded `Task.Run` per accept → `MaxConnections`
   (64), overflow closed on accept.
2. **Per-client memory growth** (`socket`/`open` in a loop) — unbounded handle
   allocation → `MaxHandlesPerClient` (256), atomic reservation freed on teardown,
   refused with errCode 4.
3. **Slowloris** — `ReadFrameAsync` awaited a frame forever → `InFrameTimeout`
   (30s) bounds the rest of a frame once its first byte arrives, leaving
   idle-between-frames unbounded (a legitimately idle multiplexed connection is
   never dropped). Implemented at the framing layer.
4. **Auth brute-force** — the cleartext `auth` message could be guessed without
   limit (unlike the web panel) → reuse the web `LoginThrottle` per source IP; a
   locked IP is refused before the password verify.

Tested in `RhpServerHardeningTests` + `FramingTests`.

## 2. Wire-parser fuzzing (1 finding, fixed)

Extended `tools/Packet.Fuzz` with three new targets — APRS info-field decoders,
AGW framing, NET/ROM network-layer parsers — driven by the existing smoke +
AFL/libfuzzer harness.

**Finding (fixed): `AgwFrame.TryReadDataLength` threw instead of returning false.**
A `Try*` method documented to return `false` on bad input instead threw
`InvalidDataException` when the advertised little-endian data-length field
overflowed Int32. Wire-reachable: `AgwFrameStream`'s read loop calls it un-guarded
on every header off the socket, so a peer sending a length field ≥ `0x7FFFFFDC`
tore down the entire AGW frame stream (default loopback; `--listen-public`
exposes it). Fixed to return `false` (consistent with its too-short branch);
`AgwFrame.Parse` keeps its one-shot throwing contract. Regression pinned in
`AgwFrameTests`; details in `tools/Packet.Fuzz/FINDINGS.md` (2026-06-13).

APRS and NET/ROM were clean across a 50k-iteration sweep.

## 3. Web auth core review (no findings)

Read the security-critical auth components end to end. **No exploitable issues
found** — the surface is well-built and defensively coded:

- **JWT (`JwtTokenService`)** — HS256 with `ValidAlgorithms` pinned to HS256 only
  (blocks the `alg:none` / algorithm-confusion class), issuer + audience pinned,
  `ClockSkew = 0`, lifetime validated against the injected clock. Sound.
- **Refresh tokens (`RefreshTokenService`)** — 256-bit opaque CSPRNG tokens,
  SHA-256 at rest, strict one-time-use rotation within a family, reuse-detection
  theft response (burn the family) with a bounded self-race leeway window. The
  unsalted hash is correct here (the input is already 256 bits of entropy). Sound.
- **TOTP (`TotpService`)** — RFC 6238, the load-bearing replay guard accepts only a
  counter *strictly greater* than the last accepted (a code is single-use), drift
  window never re-opens a consumed counter, constant-time code compare. Sound.
- **Password hashing (`PasswordHasher`)** — Argon2id at OWASP parameters, per-user
  CSPRNG salt, PHC-encoded with parameters for graceful cost migration, fixed-time
  digest compare. Sound.
- **Authorization (`ScopeRequirementHandler` / `AuthScopes.Satisfies`)** — the rank
  comparison correctly enforces admin ⊃ operate ⊃ read, an unknown/absent granted
  scope satisfies nothing, and an unknown *required* scope is never satisfied (no
  fail-open). Default-off pass-through is explicit and read live from config.
- **Login flow (`PdnAuthApi`)** — throttle checked *before* the Argon2 verify (429,
  and a locked account doesn't burn CPU); an unknown username still pays the full
  Argon2 cost via a decoy hash (no user-enumeration timing oracle); generic 401 on
  any failure; throttle reset on success; audit logging without secrets. Textbook.

### Observations (not bugs, for future consideration)

- **TOTP high-water-mark persistence is the caller's responsibility.** `TryVerify`
  returns the accepted counter and documents that the caller *must* persist it. The
  replay guard is only as strong as that persistence — worth a test that asserts the
  on-air elevation path actually stores and re-reads the high-water mark across a
  restart (the §10.1 "future direction" work, when it lands).
- **RHP auth is cleartext by protocol.** The new per-IP throttle bounds online
  guessing, but RHP has no transport encryption; the threat model's "never expose
  the port beyond a trusted network" guidance remains the real control. No change
  recommended — noting it so the throttle isn't mistaken for sufficient exposure
  protection.

## 4. CI: dependency vulnerability scan (added)

Added a self-hosted `dependency-scan` job to `ci.yml`:
`dotnet list package --vulnerable --include-transitive` over the solution, failing
the build on any direct or transitive package with a known advisory. Currently
clean. This is the supply-chain tripwire the project lacked.

## Residual / not done this pass

- **CodeQL static analysis** — considered but not added; the dependency scan +
  fuzzing + targeted review covered the high-value ground for this pass, and a
  CodeQL job is a larger, separately-scoped addition.
- **AXUDP** was assessed and deliberately not given a separate fuzz target: its
  decode delegates to `Ax25Frame.TryParse`, which is already fuzzed.
- **WebAuthn / passkey ceremony** (`Fido2.AspNet`) was not deep-reviewed beyond the
  challenge-cache bounds; the heavy lifting is in the vendored library.
