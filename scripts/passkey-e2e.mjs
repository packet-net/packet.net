// ============================================================
// Passkey end-to-end proof (the headline verification for node-passkeys).
//
// Runs INSIDE the Docker container that scripts/passkey-e2e.sh stands up. By that
// point the self-contained pdn node is already running on http://127.0.0.1:8080 with
// auth ENABLED and an admin user bootstrapped (the .sh did the /setup + flipped auth on
// over the API before auth was enforced). This script drives a REAL WebAuthn ceremony
// with a Chrome DevTools-Protocol virtual authenticator on localhost (a secure context
// over plain HTTP — no cert needed), proving the localhost-first flow actually works:
//
//   1. password-login as the admin (to get a session that can ENROL a passkey),
//   2. add a CDP virtual authenticator (ctap2, internal, resident-key, UV),
//   3. REGISTER a passkey via the Users screen "Add passkey" affordance,
//   4. LOG OUT, then SIGN IN PASSWORDLESSLY with that passkey via the login screen,
//   5. assert the authenticated control panel renders.
//
// The virtual authenticator makes the navigator.credentials.create/.get calls return
// real, signed attestation/assertion responses that the node's Fido2 verifier accepts —
// this is a genuine ceremony end-to-end, not a stub.
// ============================================================
import { chromium } from "playwright";

const BASE = process.env.PDN_BASE ?? "http://localhost:8080";
const ADMIN = process.env.PDN_ADMIN_USER ?? "sysop";
const PASSWORD = process.env.PDN_ADMIN_PASS ?? "hunter2hunter2";

function fail(msg) {
  console.error(`❌ ${msg}`);
  process.exit(1);
}
function ok(msg) {
  console.log(`✅ ${msg}`);
}

const browser = await chromium.launch({ args: ["--no-sandbox"] });
const context = await browser.newContext({ baseURL: BASE });
const page = await context.newPage();

// A CDP virtual authenticator on this context — the core of the proof. ctap2 + internal
// transport + resident key + user-verified, with automatic presence so the ceremony
// completes without a real fingerprint/PIN prompt.
const cdp = await context.newCDPSession(page);
await cdp.send("WebAuthn.enable");
const { authenticatorId } = await cdp.send("WebAuthn.addVirtualAuthenticator", {
  options: {
    protocol: "ctap2",
    transport: "internal",
    hasResidentKey: true,
    hasUserVerification: true,
    isUserVerified: true,
    automaticPresenceSimulation: true,
  },
});
ok(`virtual authenticator added: ${authenticatorId}`);

try {
  // --- 1) password-login as the admin -------------------------------------------
  await page.goto("/login");
  await page.waitForLoadState("networkidle");

  // The login screen renders a "Continue with passkey" button (enabled — localhost is a
  // secure context) plus the username/password form. First sign in with the password so
  // we have a session that can enrol a passkey.
  await page.getByRole("button", { name: /Continue with passkey/i })
    .waitFor({ state: "visible", timeout: 15000 });
  ok("login screen shows the passkey button (secure context detected)");

  await page.locator('input[autocomplete="username"]').fill(ADMIN);
  await page.locator('input[type="password"]').fill(PASSWORD);
  await page.getByRole("button", { name: /^Sign in$/ }).click();

  // Landed in the app (the login route is gone).
  await page.waitForFunction(() => !location.pathname.startsWith("/login"), null, { timeout: 15000 });
  ok("password login succeeded; in the control panel");

  // --- 2/3) enrol a passkey via the Users screen --------------------------------
  await page.goto("/users");
  await page.waitForLoadState("networkidle");

  const addPasskey = page.getByRole("button", { name: /Add passkey/i }).first();
  await addPasskey.waitFor({ state: "visible", timeout: 15000 });
  await addPasskey.click();

  // After a successful enrolment the row re-renders with "1 passkey" and a Remove link.
  await page.getByText(/1 passkey/i).waitFor({ state: "visible", timeout: 20000 });
  ok("passkey REGISTERED (real attestation verified by the node)");

  // Sanity: the credential is actually held by the virtual authenticator.
  const { credentials } = await cdp.send("WebAuthn.getCredentials", { authenticatorId });
  if (!credentials || credentials.length < 1) fail("virtual authenticator holds no credential after registration");
  ok(`virtual authenticator now holds ${credentials.length} credential(s)`);

  // --- 4) log out, then sign in PASSWORDLESSLY with the passkey ------------------
  // Clear the session the only sure way in a headless run: drop storage + reload to the
  // login gate (the app persists the session in sessionStorage).
  await context.clearCookies();
  await page.evaluate(() => { sessionStorage.clear(); localStorage.clear(); });
  await page.goto("/login");
  await page.waitForLoadState("networkidle");
  await page.getByRole("button", { name: /Continue with passkey/i })
    .waitFor({ state: "visible", timeout: 15000 });

  // The headline: a passwordless assertion. No username typed → discoverable credential.
  await page.getByRole("button", { name: /Continue with passkey/i }).click();

  // We're back in the app, authenticated by the passkey alone.
  await page.waitForFunction(() => !location.pathname.startsWith("/login"), null, { timeout: 20000 });
  ok("PASSWORDLESS sign-in succeeded — authenticated by the passkey alone");

  // The authenticated panel renders (a /status fetch behind the gate would 401 otherwise).
  await page.waitForFunction(
    () => document.body && document.body.innerText.length > 50 && !location.pathname.startsWith("/login"),
    null, { timeout: 15000 });
  ok("authenticated control panel rendered after passwordless sign-in");

  console.log("\n✅ PASSKEY E2E PASSED: register → passwordless sign-in on localhost.");
} catch (err) {
  // Dump a screenshot + the last console for triage, then fail.
  try { await page.screenshot({ path: "/shots/passkey-e2e-failure.png", fullPage: true }); } catch { /* best effort */ }
  fail(`passkey E2E failed: ${err && err.stack ? err.stack : err}`);
} finally {
  await browser.close();
}
