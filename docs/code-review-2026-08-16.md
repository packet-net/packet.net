# In-depth code review - 2026-08-16

A whole-tree code quality, correctness and consistency review of `packet-net/packet.net`
at `e60d059` (the `node-v0.40.0` tree), with the web control panel
(`web/packetnet-ui`) and its test suite as the named focus. Nothing was modified
during the review; the remediation landed afterwards as fifteen work packages under
umbrella issue [#703](https://github.com/packet-net/packet.net/issues/703), and
`docs/plan.md` §17 carries the per-package ledger.

Two questions were asked, and both answered yes.

**Has the web UI drifted from the backend?** Yes, and not cosmetically. On a default
(auth-on) install the Ports "Add port" happy path returned 422 because the editor sent
a mock catalogue id as the server `profile`; the connect-out dialog defaulted its via
port to a fixture id; the tuner and waterfall SSE feeds 401'd because the
`?access_token=` allow-list had never been extended to them; the NET/ROM routing role
and the INP3 timers could not be edited at all because the server serialised enums as
integers and durations as `"hh:mm:ss"` while the client typed them as strings and
seconds; and any save from the port editor rewrote fields the operator never touched.

**Could the UI test suite see any of it?** No. Every screen test ran against
`lib/mock.ts`, which was simultaneously the fake backend and a source of runtime
constants that live screens imported; nothing compared the client types against what
the server serialises; and `npm run lint` could only fail, because there was no ESLint
config file at all.

## Scope and method

| Phase | What happened |
|---|---|
| 1. Find | 14 reviewers, one dimension each: UI contract, UI mock and tests, UI screens (x2), a live boot-and-drive probe, API endpoints, auth and security, node core hosting, node core services, AX.25 and KISS, NET/ROM and adjacent libraries, RHPv2/MCP/rig/radio, cross-cutting consistency, .NET test quality. 167 raw findings, each with `file:line` evidence. |
| 2. Cluster | Deduplicated to 129 distinct issues (`C001`-`C115` plus `RM-1`-`RM-14`, the re-run RHPv2/MCP/rig/radio set). |
| 3. Verify | Every one of the 129 handed to an independent agent told to refute it. Verdicts: 114 confirmed, 15 partially confirmed (a real defect, but the claim overstated or misattributed something), 0 refuted, 0 unverifiable. Severity was re-rated by the verifier: 8 lowered, 3 raised, leaving 15 high / 49 medium / 65 low. |

Baseline before the review: `dotnet build` clean, .NET suite 4,687 tests with one
load-sensitive flake that passes alone, UI `tsc` clean and vitest 141/141. So none of
this was visible to any gate the project runs.

The live probe booted the node from the Debug build against a scratch YAML with auth
on, did first-run setup over the API, curled every GET the SPA uses and diffed the
responses against a compiler-extracted `types.ts` schema, exercised the writes with
the exact bodies the screens build, opened every SSE feed, then served the live-mode
SPA from the node and drove it with Playwright through every route plus add/edit port,
config review, connect, console, ping and users.

Not covered, and named as candidates for a second pass: passkey and TOTP ceremonies
(no authenticator), app install and gateway end to end, head-end adopt and the
rig/radio hardware paths, a real soundmodem spectrum feed, MCP and OAuth live,
Tailscale, HTTPS, the auth-off posture, mobile viewport, the interop suites, the Go
code in `headend/` and `sidecar/` beyond build and vet, `examples/`, the NinoTNC
firmware flasher, and the Mic-E dual-encoding maths.

## Root causes

The 129 issues collapse into a much smaller set of causes, and the remediation was
grouped to fix each once rather than fix each symptom.

- **Mock data was load-bearing in live mode.** `lib/mock.ts` was both the fake backend
  and a source of runtime constants (`PORTS_LIST`, `NODE_CONFIG`, `RADIO_PROFILES`,
  AX.25/KISS defaults) that screens imported directly, so fixture ids reached the wire
  and vitest stayed green because the mock matched itself.
- **The client types were a hand-mirror of the server with no enforcement.**
  `types.ts`, `mock.ts`, `ports.tsx` and `docs/node-api.yaml` each carried their own
  copy of the transport kinds, `PortConfig` fields, enums and profile names. The server
  added kinds and fields; the copies did not follow, and `portDraftToConfig` rebuilt
  the body field by field, so every server-only field was dropped on save.
- **The server JSON dialect had no enum or duration policy.** Enums rode as integers
  and `TimeSpan`s as `"hh:mm:ss"` while every consumer assumed strings and seconds.
- **Hand-maintained lists that new work forgot to extend.** The SSE `?access_token=`
  allow-list, the `ci.yml` test matrix, the KISS kind-label maps, the six copied SSE
  loops. Each had a cheap structural fix: derive it, or add a guard test that diffs the
  list against reality.
- **Principal-name derivation was copy-pasted and half the copies were wrong.**
  JwtBearer maps `sub` to `NameIdentifier` before any lookup, so the audit log recorded
  `owner` for every authenticated write.
- **Working agreements §2.7 (`TimeProvider`) and §2.8 (AwesomeAssertions) were
  unenforced**, and adoption had holes exactly where timing is measured.
- **Docs decayed after two big shifts.** The 2026-05-17 repo split and the config-in-DB
  move (#473) updated code and the plan's amendment log but not `SECURITY.md`,
  `node-api.yaml`, the packaging template header, the UI README, `observability.md` or
  plan sections 4.1/7/10.
- **The library layer is strong but the seams are not.** The AX.25 and NET/ROM engines
  are well tested against the figures; the defects found sat at seams the tests do not
  reach (window `k` never clamped to modulus-1, mod-128 supervisory frames parsed at
  mod-8 under Strict, the NET/ROM circuit demux, the missing accepted-window octet).

## Work packages

| WP | Issue | Covers |
|---|---|---|
| WP1 | [#688](https://github.com/packet-net/packet.net/issues/688) | Server JSON enum/`TimeSpan` policy and client type alignment |
| WP2 | [#689](https://github.com/packet-net/packet.net/issues/689) | SSE `?access_token` by endpoint metadata, client stream robustness |
| WP3 | [#690](https://github.com/packet-net/packet.net/issues/690) | Ports editor round-trip fidelity |
| WP4 | [#691](https://github.com/packet-net/packet.net/issues/691) | Mock fixtures leaking into live screens; ESLint restored and in CI |
| WP5 | [#692](https://github.com/packet-net/packet.net/issues/692) | Client/server contract fixtures, `api.ts` live-branch tests, live smoke job |
| WP6 | [#693](https://github.com/packet-net/packet.net/issues/693) | Auth and security fixes |
| WP7 | [#694](https://github.com/packet-net/packet.net/issues/694) | Control API correctness |
| WP8 | [#695](https://github.com/packet-net/packet.net/issues/695) | Node core hosting and services |
| WP9 | [#696](https://github.com/packet-net/packet.net/issues/696) | AX.25 and KISS library fixes |
| WP10 | [#697](https://github.com/packet-net/packet.net/issues/697) | NET/ROM fixes |
| WP11 | [#698](https://github.com/packet-net/packet.net/issues/698) | RHPv2, rig, radio, MCP and tuning fixes |
| WP12 | [#699](https://github.com/packet-net/packet.net/issues/699) | Packaging, CI and repo hygiene |
| WP13 | [#700](https://github.com/packet-net/packet.net/issues/700) | .NET test-suite quality |
| WP14 | [#701](https://github.com/packet-net/packet.net/issues/701) | Docs drift (this record) |
| WP15 | [#702](https://github.com/packet-net/packet.net/issues/702) | Web UI miscellany |

Each work-package issue quotes, per item, the verifier's verdict, the corrected
severity, the verified facts and a fix sketch. The umbrella [#703] carries the live
checklist and the severity ledger; `docs/plan.md` §17 records what each merged package
actually changed.

The rules for the arc: fix at the root rather than suppress, add the test that would
have caught it, keep `docs/plan.md` §17 current in the same PR, and merge on a green
local run (CI is main-only by design).

## Where the evidence lives

The full report (findings, reproductions, live-probe screenshots) is a private
artifact. The working set that produced it is on the dev box under
`/home/tf/pdn-review-state/`: `RESUME.md` indexes it, `phase1.json` holds the raw
findings, `clusters.json` the dedupe, `verdicts-final.json` every verdict keyed by
cluster id, and the live probe's 39 screenshots plus raw API responses sit alongside.
Line numbers quoted anywhere in the review set are as of `e60d059`.

[#703]: https://github.com/packet-net/packet.net/issues/703
