// The live-smoke driver: a real chromium against a real pdn node serving the real
// live-mode SPA. Run by scripts/live-smoke/run.sh inside the prebuilt Playwright image
// on --network host; see that script for why it runs in docker at all.
//
// This is NOT a "did the page render" check. Every step asserts an outcome the review
// found the SPA getting WRONG against a live node while every unit test stayed green:
// the dashboard showing the mock's callsign, the Ports editor's defaults 422ing, the
// editor dropping fields it had loaded, the Connect modal naming a port that does not
// exist here. Each of those is a regression the moment it comes back.
//
// Failure is collected, not thrown: the run drives every step and reports every
// problem together at the end, because "the first thing that broke" is much less
// useful than "these six things broke" when you are triaging a UI contract.
import { chromium } from "playwright-core";
import { mkdirSync, writeFileSync } from "node:fs";

const BASE = process.env.BASE ?? "http://127.0.0.1:8080";
const OUT = process.env.OUT ?? "/out";
const USER = process.env.SMOKE_USER ?? "smokeop";
const PASS = process.env.SMOKE_PASS ?? "";
const CALLSIGN = process.env.SMOKE_CALLSIGN ?? "";
const TOKEN = process.env.SMOKE_TOKEN ?? "";
const BOOT_PORT_ID = process.env.BOOT_PORT_ID ?? "smk-a";
const NEW_PORT_ID = process.env.NEW_PORT_ID ?? "smk-b";
const NEW_PORT_HOST = process.env.NEW_PORT_HOST ?? "127.0.0.1";
const NEW_PORT_TCP = process.env.NEW_PORT_TCP ?? "8001";

// web/packetnet-ui/src/lib/mock.ts's identity. If this string reaches a live screen,
// the panel is rendering fixture data at a real operator (review item C041 / WP4).
const MOCK_CALLSIGN = "GB7RDG";

// Text that should never be visible to an operator: a formatting/serialisation bug
// leaking through the render. `null` is deliberately NOT here - the SPA prints a
// literal "null" in a few legitimate places (a config value that IS null).
const SUSPECT = /\bundefined\b|\bNaN\b|\[object Object\]/;

mkdirSync(OUT, { recursive: true });

// ---------------------------------------------------------------- report + failures
const failures = [];
const steps = [];
let cur = null;

// Write the report on ANY exit, including a locator timeout that escapes a step: a
// half-finished run's evidence is exactly when you most want to see which step it died
// in. writeFileSync is synchronous, so it is safe in an exit handler.
process.on("exit", () => {
  try {
    writeFileSync(`${OUT}/live-smoke-report.json`, JSON.stringify({ base: BASE, callsign: CALLSIGN, steps, failures }, null, 1));
  } catch { /* nothing useful to do while exiting */ }
});

function begin(name) {
  cur = { name, notes: [], problems: [] };
  steps.push(cur);
  console.log(`\n== ${name}`);
  return cur;
}
function note(msg) {
  const s = String(msg).slice(0, 500);
  (cur ?? { notes: [] }).notes.push(s);
  console.log(`   note: ${s}`);
}
function fail(kind, detail) {
  const entry = { step: cur ? cur.name : "(startup)", kind, detail: String(detail).slice(0, 700) };
  failures.push(entry);
  if (cur) cur.problems.push(`${kind}: ${entry.detail}`);
  console.log(`   FAIL ${kind}: ${entry.detail}`);
}

// ---------------------------------------------------------------- expected 4xx/5xx
// A response >= 400 fails the run UNLESS it is one of these, each of which is a status
// the SPA deliberately provokes and handles. Anything not listed here is a real defect:
// the SPA asked the node for something the node does not serve.
const EXPECTED_BAD = [
  {
    // The Sessions step dials a station that is not on the air. The node answers 502
    // (dial refused/failed) or 504 (dial ceiling); the assertion is that the SPA sends
    // the right portId and surfaces the failure as a notice instead of crashing.
    match: (r) => r.request().method() === "POST" && /\/api\/v1\/sessions$/.test(r.url()),
    codes: [502, 504],
    why: "the deliberate connect-out to a station that is not there",
  },
];
function isExpectedBad(res) {
  return EXPECTED_BAD.some((e) => e.match(res) && e.codes.includes(res.status()));
}

// ---------------------------------------------------------------- browser
const browser = await chromium.launch({ args: ["--no-sandbox", "--disable-dev-shm-usage"] });
const ctx = await browser.newContext({ viewport: { width: 1360, height: 940 } });
const page = await ctx.newPage();

page.on("console", (m) => {
  if (m.type() !== "error") return;
  // "Failed to load resource: ..." is chromium echoing a network status we already
  // judge (with an allowlist) in the response handler below. Counting it here too
  // would double-report the deliberate connect-out failure with no way to exempt it.
  if (m.text().startsWith("Failed to load resource")) return;
  fail("console-error", m.text());
});
page.on("pageerror", (e) => fail("pageerror", e && e.stack ? e.stack.split("\n")[0] : e));
page.on("requestfailed", (r) => {
  // Not a hard failure: navigating away aborts in-flight SSE and fetches, which is
  // normal. Recorded so a genuinely unreachable endpoint is visible in the report.
  const err = r.failure() ? r.failure().errorText : "";
  if (err === "net::ERR_ABORTED") return;
  note(`request failed: ${r.method()} ${r.url().replace(BASE, "")} ${err}`);
});
page.on("response", async (res) => {
  if (res.status() < 400) return;
  if (isExpectedBad(res)) {
    note(`expected ${res.status()} ${res.request().method()} ${res.url().replace(BASE, "")}`);
    return;
  }
  let body = "";
  try { body = (await res.text()).slice(0, 250); } catch { /* body already consumed/streamed */ }
  fail("http", `${res.status()} ${res.request().method()} ${res.url().replace(BASE, "")} ${body}`);
});

// The PUT the Ports editor sends, captured on the wire so we can compare it against
// what the SPA loaded. Reading the request body is the only honest way to see whether
// a field was dropped between load and save.
let lastPortPut = null;
let lastSessionPost = null;
page.on("request", (req) => {
  const url = req.url();
  if (req.method() === "PUT" && /\/api\/v1\/ports\/[^/?]+(\?|$)/.test(url)) {
    try { lastPortPut = { url, body: JSON.parse(req.postData() ?? "null") }; } catch (e) { fail("bad-put", `unparseable PUT body: ${e}`); }
  }
  if (req.method() === "POST" && /\/api\/v1\/sessions(\?|$)/.test(url)) {
    try { lastSessionPost = JSON.parse(req.postData() ?? "null"); } catch (e) { fail("bad-post", `unparseable POST body: ${e}`); }
  }
});

// ---------------------------------------------------------------- helpers
let shotSeq = 0;
async function shot(name) {
  const n = String(shotSeq++).padStart(2, "0");
  try { await page.screenshot({ path: `${OUT}/${n}-${name}.png` }); } catch (e) { note(`screenshot failed: ${e}`); }
}
async function settle(ms = 900) {
  try { await page.waitForLoadState("networkidle", { timeout: 8000 }); } catch { /* SSE keeps the page busy forever; the fixed wait below covers it */ }
  await page.waitForTimeout(ms);
}
async function bodyText() {
  return page.evaluate(() => document.body.innerText);
}
/** Fail on any visible undefined/NaN/[object Object] on the screen as it stands. */
async function scanVisible(label) {
  const text = await bodyText();
  for (const line of text.split("\n")) {
    if (SUSPECT.test(line)) fail("suspect-text", `${label}: ${line.trim().slice(0, 200)}`);
  }
  return text;
}
async function api(path, init = {}) {
  const res = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { authorization: `Bearer ${TOKEN}`, "content-type": "application/json", ...(init.headers ?? {}) },
  });
  const text = await res.text();
  let json = null;
  try { json = text ? JSON.parse(text) : null; } catch { /* not json */ }
  return { status: res.status, json, text };
}

/**
 * Every path in `loaded` that holds a real value but did NOT survive into `sent`.
 * Key order is irrelevant (we walk `loaded`), and an absent optional is treated the
 * same as an explicit null - but a field that HAD a value and came back missing,
 * null, or changed is a dropped field, which is the bug this comparison exists for.
 */
function droppedFields(loaded, sent, path = "") {
  const at = path || "(root)";
  if (loaded === null || loaded === undefined) return [];
  if (Array.isArray(loaded)) {
    if (!Array.isArray(sent) || sent.length !== loaded.length) {
      return [`${at}: loaded ${JSON.stringify(loaded)} but the SPA sent ${JSON.stringify(sent)}`];
    }
    return loaded.flatMap((v, i) => droppedFields(v, sent[i], `${path}[${i}]`));
  }
  if (typeof loaded === "object") {
    if (sent === null || sent === undefined || typeof sent !== "object" || Array.isArray(sent)) {
      return [`${at}: loaded an object but the SPA sent ${JSON.stringify(sent)}`];
    }
    return Object.keys(loaded).flatMap((k) => droppedFields(loaded[k], sent[k], path ? `${path}.${k}` : k));
  }
  return loaded === sent ? [] : [`${at}: loaded ${JSON.stringify(loaded)} but the SPA sent ${JSON.stringify(sent)}`];
}

// ================================================================ 1. log in
begin("01-login");
await page.goto(`${BASE}/login`, { waitUntil: "domcontentloaded" });
await settle();
await shot("login");
await scanVisible("login");
{
  const text = await bodyText();
  if (text.includes(MOCK_CALLSIGN)) fail("fixture-data", `the sign-in page shows the mock callsign ${MOCK_CALLSIGN}`);
}
// The form is username (first text input) + password + submit; see screens/login.tsx.
await page.locator('form input:not([type="password"])').first().fill(USER);
await page.locator('form input[type="password"]').fill(PASS);
await page.locator('button[type="submit"]').click();
try {
  await page.waitForURL((u) => !u.pathname.startsWith("/login"), { timeout: 20000 });
  note(`landed on ${page.url()}`);
} catch (e) {
  fail("login", `the login form did not navigate off /login: ${e}`);
}
await settle();
await shot("after-login");

// ================================================================ 2. dashboard identity
begin("02-dashboard-identity");
await page.goto(`${BASE}/`, { waitUntil: "domcontentloaded" });
await settle(1400);
await shot("dashboard");
{
  const text = await scanVisible("dashboard");
  if (!text.includes(CALLSIGN)) fail("identity", `the dashboard does not show this node's callsign ${CALLSIGN}`);
  if (text.includes(MOCK_CALLSIGN)) fail("fixture-data", `the dashboard shows the mock callsign ${MOCK_CALLSIGN}`);
  else note(`dashboard shows ${CALLSIGN} and not ${MOCK_CALLSIGN}`);
}

// ================================================================ 3a. ports: add with the panel defaults
// THE regression this leg exists for: the "Add port" panel used to seed its draft from
// the mock catalogue (a RADIO_PROFILES id in `profile`), which the live validator
// rejected 422 - so a stock add could never succeed against a real node (#690 C002).
// Everything below the id and the TCP endpoint is left exactly as the panel offers it.
begin("03-ports-add-defaults");
await page.goto(`${BASE}/ports`, { waitUntil: "domcontentloaded" });
await settle();
await page.getByRole("button", { name: /add port/i }).click();
await page.waitForTimeout(400);
await page.getByPlaceholder("vhf-1").fill(NEW_PORT_ID);
{
  // kiss-tcp is the panel's default transport, so Host + TCP port are the only other
  // boxes to touch. Field (components/ui) renders <div><div><span><label>NAME</label>
  // ...</span></div><input></div> with no htmlFor, so getByLabel cannot reach these -
  // address them structurally instead of by index, so a re-ordered panel fails loudly
  // rather than filling the wrong box.
  const sheet = page.locator('[role="dialog"]').last();   // only the editor is open here
  const field = (label) => sheet.locator(`div:has(> div > span > label:text-is("${label}")) > input`);
  await field("Host").fill(NEW_PORT_HOST);
  await field("TCP port").fill(String(NEW_PORT_TCP));
}
await shot("ports-add-panel");
await page.getByRole("button", { name: /save changes/i }).click();
await page.waitForTimeout(600);
await shot("ports-add-confirm");
await page.getByRole("button", { name: /^apply/i }).click();
await settle(1800);
await shot("ports-add-result");
{
  // A rejection renders INSIDE the drawer (data-testid="port-save-error", #690 C040).
  const rejected = await page.locator('[data-testid="port-save-error"]').count();
  if (rejected > 0) {
    const why = await page.locator('[data-testid="port-save-error"]').innerText();
    fail("ports-add", `the node rejected the panel's OWN defaults: ${why.replace(/\n/g, " | ")}`);
  }
  const text = await scanVisible("ports-after-add");
  if (!text.includes(NEW_PORT_ID)) fail("ports-add", `port '${NEW_PORT_ID}' is not on the Ports screen after a successful add`);
  else note(`port '${NEW_PORT_ID}' added from the panel defaults and is listed`);
  // The node is the arbiter, not the screen: it must be in the persisted config too,
  // carrying the endpoint we typed. Checking the endpoint also keeps the step honest -
  // a silently-unfilled Host/TCP box would otherwise still "pass" on the panel default.
  const cfg = await api("/api/v1/config");
  const added = (cfg.json && cfg.json.ports ? cfg.json.ports : []).find((p) => p.id === NEW_PORT_ID);
  if (!added) {
    const ids = (cfg.json && cfg.json.ports ? cfg.json.ports : []).map((p) => p.id);
    fail("ports-add", `the node's config has no port '${NEW_PORT_ID}' (ports: ${ids.join(", ")})`);
  } else if (added.transport.host !== NEW_PORT_HOST || String(added.transport.port) !== String(NEW_PORT_TCP)) {
    fail("ports-add", `the added port persisted as ${added.transport.host}:${added.transport.port}, not ${NEW_PORT_HOST}:${NEW_PORT_TCP}`);
  }
}

// ================================================================ 3b. ports: edit round-trip
// Save an EXISTING port with nothing changed and prove the PUT carries back everything
// the SPA loaded. The editor used to spread UI defaults over the loaded port and lose
// the operator's tuned ax25/kiss blocks (#690, WP3) - a silent config edit on a save
// the operator believed was a no-op.
begin("04-ports-edit-round-trip");
await page.goto(`${BASE}/ports`, { waitUntil: "domcontentloaded" });
await settle();
lastPortPut = null;
{
  // Find the Edit button belonging to BOOT_PORT_ID by opening each and reading the
  // drawer title ("Edit port - {id}"); card order is not a contract.
  const edits = page.getByRole("button", { name: "Edit" });
  const count = await edits.count();
  let opened = false;
  for (let i = 0; i < count; i++) {
    await edits.nth(i).click();
    await page.waitForTimeout(400);
    const title = await page.locator("h2, h3").filter({ hasText: /Edit port/ }).first().innerText().catch(() => "");
    if (title.includes(BOOT_PORT_ID)) { opened = true; note(`editing: ${title}`); break; }
    await page.getByRole("button", { name: /^cancel$/i }).first().click().catch(() => {});
    await page.waitForTimeout(250);
  }
  if (!opened) fail("ports-edit", `no Edit drawer for port '${BOOT_PORT_ID}' among ${count} Edit buttons`);
}
await shot("ports-edit-panel");
await page.getByRole("button", { name: /save changes/i }).click();
await page.waitForTimeout(600);
await page.getByRole("button", { name: /^apply/i }).click();
await settle(1600);
await shot("ports-edit-result");
{
  const rejected = await page.locator('[data-testid="port-save-error"]').count();
  if (rejected > 0) {
    const why = await page.locator('[data-testid="port-save-error"]').innerText();
    fail("ports-edit", `an unchanged save was rejected: ${why.replace(/\n/g, " | ")}`);
  }
  if (!lastPortPut) {
    fail("ports-edit", "the SPA sent no PUT /api/v1/ports/{id} for an unchanged save");
  } else {
    // The dry-run preview PUTs first (?dryRun=true) and the apply PUTs again; either
    // body is the same serialisation, so comparing the last one is enough.
    const cfg = await api("/api/v1/config");
    const loaded = (cfg.json && cfg.json.ports ? cfg.json.ports : []).find((p) => p.id === BOOT_PORT_ID);
    if (!loaded) {
      fail("ports-edit", `GET /api/v1/config has no port '${BOOT_PORT_ID}' to compare against`);
    } else {
      // Keep the comparison honest: a port with every optional block null would
      // round-trip trivially and prove nothing. run.sh boots this port with tuned
      // ax25 + kiss blocks precisely so there is something to drop.
      if (!loaded.ax25 || !loaded.kiss) {
        fail("ports-edit", `port '${BOOT_PORT_ID}' has no tuned ax25/kiss block, so the round-trip comparison is vacuous (check the smoke's config)`);
      }
      const dropped = droppedFields(loaded, lastPortPut.body);
      if (dropped.length > 0) {
        for (const d of dropped) fail("ports-edit-dropped", d);
      } else {
        note(`the unchanged save round-tripped every field of '${BOOT_PORT_ID}' (${Object.keys(loaded).length} top-level keys)`);
      }
    }
  }
}

// ================================================================ 4. config dry run
// "Review & apply" is disabled in Forms mode until something is dirty, so the
// nothing-changed dry run goes through the Raw YAML tab's "Validate & preview" - the
// same openReview -> PUT /config/raw?dryRun=true the apply path uses.
begin("05-config-dry-run");
await page.goto(`${BASE}/config`, { waitUntil: "domcontentloaded" });
await settle(1200);
await scanVisible("config");
await page.getByRole("button", { name: /raw yaml/i }).click();
await settle(700);
await shot("config-raw");
await page.getByRole("button", { name: /validate & preview/i }).click();
await settle(1500);
await shot("config-review");
{
  const modal = await page.locator("text=Review & apply").count();
  if (modal === 0) fail("config-review", "the reconcile preview did not open");
  const text = await bodyText();
  if (/problems? - not applied/i.test(text)) {
    const detail = text.match(/[^\n]*problems? - not applied[\s\S]{0,300}/i);
    fail("config-review", `the node rejected its OWN current config on a dry run: ${(detail ? detail[0] : "").replace(/\n/g, " | ")}`);
  } else {
    note(`dry run valid; preview says: ${(text.match(/No effective changes\.|[0-9]+ apply live/) || ["(no summary line)"])[0]}`);
  }
  await scanVisible("config-review");
}
await page.getByRole("button", { name: /^cancel$/i }).last().click().catch(() => note("no Cancel to click"));
await page.waitForTimeout(400);

// ================================================================ 5. sessions connect
// The Connect modal's via-port select used to be populated from the mock's ports, so it
// offered a port id this node has never heard of (#691 C021). Assert the select names a
// port that EXISTS here, and that the POST body carries exactly that id.
begin("06-sessions-connect");
await page.goto(`${BASE}/sessions`, { waitUntil: "domcontentloaded" });
await settle(1000);
await scanVisible("sessions");
await page.getByRole("button", { name: /^connect$/i }).first().click();
await page.waitForTimeout(600);
await page.getByPlaceholder(/GB7CIP/i).fill("M0SMK-9");
let selectedPort = null;
{
  const sel = page.locator("select").last();
  const shown = await sel.evaluate((s) => ({
    value: s.value,
    label: s.options[s.selectedIndex] ? s.options[s.selectedIndex].text : null,
    options: Array.from(s.options).map((o) => o.value),
  }));
  selectedPort = shown.value;
  note(`via-port select: value=${shown.value} label=${shown.label} options=${JSON.stringify(shown.options)}`);
  const cfg = await api("/api/v1/config");
  const ids = (cfg.json && cfg.json.ports ? cfg.json.ports : []).map((p) => p.id);
  for (const o of shown.options) {
    if (o && !ids.includes(o)) fail("sessions-connect", `the via-port select offers '${o}', which is not a port on this node (${ids.join(", ")})`);
  }
  if (!shown.value) fail("sessions-connect", "the via-port select has no selection");
}
await shot("connect-modal");
lastSessionPost = null;
await page.getByRole("button", { name: /^connect$/i }).last().click();
// Wait for the outcome rather than a fixed sleep: the dial takes ~2 s with this port's
// tuned T1/N2, but the API's own ceiling is 30 s, so a loaded CI box must not race it.
await page
  .waitForFunction(() => /failed|timed out|refused|could not|no route/i.test(document.body.innerText), null, { timeout: 40000 })
  .catch(() => note("no connect outcome within 40s"));
await settle(600);
await shot("connect-result");
{
  if (!lastSessionPost) {
    fail("sessions-connect", "the SPA sent no POST /api/v1/sessions");
  } else {
    note(`POST /api/v1/sessions body: ${JSON.stringify(lastSessionPost)}`);
    if (lastSessionPost.portId !== selectedPort) {
      fail("sessions-connect", `the select displayed '${selectedPort}' but the POST body carried portId='${lastSessionPost.portId}'`);
    }
  }
  // The dial to a station that is not there must surface, not crash the screen.
  const text = await scanVisible("sessions-connect");
  const notice = text.match(/[^\n]*(failed|timed out|refused|Could not|no route)[^\n]*/i);
  if (!notice) fail("sessions-connect", "the failed connect produced no visible notice");
  else note(`notice: ${notice[0].trim().slice(0, 160)}`);
}

// ================================================================ 6. console
begin("07-console");
await page.goto(`${BASE}/console`, { waitUntil: "domcontentloaded" });
await settle(2500);
await page.locator(".xterm").click().catch(() => note("no .xterm to click"));
// Typed at human speed on purpose. The SPA fires ONE independent fetch per keystroke
// (screens/console.tsx term.onData -> api.consoleInput, POST .../input, 202 "queued"),
// and nothing sequences them: two POSTs in flight at once can be applied in either
// order, so a zero-delay type() delivers "PORTS" to the node as "OPRTS". The ordering
// defect is asserted explicitly below rather than papered over; the delay only keeps
// the "does a command round-trip at all" question separable from it.
const COMMAND = "PORTS";
await page.keyboard.type(COMMAND, { delay: 150 });
await page.keyboard.press("Enter");
// Wait for the node to answer rather than sleeping a fixed amount; either the port
// listing or a rejection ends the wait, and both are judged below.
await page
  .waitForFunction((id) => document.body.innerText.includes(id) || /Unknown command:/.test(document.body.innerText), BOOT_PORT_ID, { timeout: 15000 })
  .catch(() => note("no console answer within 15s"));
await page.waitForTimeout(500);
await shot("console");
{
  const text = await bodyText();
  // Did the node see the characters in the order they were typed? Its "Unknown command"
  // reply quotes back exactly what arrived, so a scramble is directly visible.
  const unknown = text.match(/Unknown command:\s*(\S+)/);
  if (unknown) {
    const got = unknown[1];
    const scrambled = got.length === COMMAND.length
      && got.split("").sort().join("") === COMMAND.split("").sort().join("")
      && got !== COMMAND;
    fail(
      scrambled ? "console-input-order" : "console",
      scrambled
        ? `typed '${COMMAND}' but the node received '${got}' - the per-keystroke POSTs are not sequenced`
        : `the node rejected the typed command as '${got}'`,
    );
  }
  // The node's own command processor answers a PORTS listing naming this node's ports.
  if (!text.includes(BOOT_PORT_ID)) {
    fail("console", `the console produced no output naming '${BOOT_PORT_ID}' after ${COMMAND} (terminal text: ${text.slice(0, 300).replace(/\n/g, " | ")})`);
  } else {
    note(`${COMMAND} answered with this node's port list`);
  }
  await scanVisible("console");
}

// ================================================================ 7. monitor
begin("08-monitor");
await page.goto(`${BASE}/monitor`, { waitUntil: "domcontentloaded" });
await settle(2000);
await shot("monitor");
{
  const text = await scanVisible("monitor");
  if (!/Live monitor/i.test(text)) fail("monitor", "the monitor screen did not render its frame table");
  else note("monitor rendered");
}

// ================================================================ 8. users
begin("09-users");
await page.goto(`${BASE}/users`, { waitUntil: "domcontentloaded" });
await settle(1200);
await shot("users");
{
  const text = await scanVisible("users");
  if (!text.includes(USER)) fail("users", `the admin account '${USER}' created at setup is not listed`);
  else note(`'${USER}' is listed`);
}

// ================================================================ 9. SSE feeds
// Every feed the SPA opens through openStream() (web/packetnet-ui/src/lib/api.ts). A
// browser EventSource has no header API, so the JWT can only ride in the query string:
// each feed must honour ?access_token= exactly as it honours the header, and must still
// 401 without one. Probed with fetch, not EventSource, so we can read the status line.
begin("10-sse-feeds");
{
  const probe = async (path, token) => {
    const ac = new AbortController();
    const timer = setTimeout(() => ac.abort(), 10000);
    try {
      const url = `${BASE}${path}${path.includes("?") ? "&" : "?"}access_token=${encodeURIComponent(token)}`;
      const res = await fetch(url, { signal: ac.signal, headers: { accept: "text/event-stream" } });
      const contentType = res.headers.get("content-type") ?? "";
      // A 200 SSE body never ends; drop it as soon as the headers are in.
      try { if (res.body) await res.body.cancel(); } catch { /* already gone */ }
      return { status: res.status, contentType };
    } catch (e) {
      return { status: 0, contentType: "", error: String(e) };
    } finally {
      clearTimeout(timer);
    }
  };

  // Mint a real console so the console feed is asserted at 200, not waved past as a 404.
  const opened = await api("/api/v1/console", { method: "POST", body: "{}" });
  const consoleId = opened.json && opened.json.id ? opened.json.id : "console:no-such";
  if (opened.status !== 200) note(`POST /api/v1/console answered ${opened.status}; falling back to a synthetic id`);

  const feeds = [
    {
      path: "/api/v1/events",
      ok: [200],
      why: "the monitor frame feed is always live on a running node",
    },
    {
      path: "/api/v1/rigs/events",
      ok: [200],
      why: "the rig-status fan-out is always live, with or without an attached rig",
    },
    {
      path: `/api/v1/console/${encodeURIComponent(consoleId)}/stream`,
      ok: [200],
      why: "the console was minted above, so this one must actually stream",
    },
    {
      path: "/api/v1/sessions/no-such-session/stream",
      ok: [200, 404],
      // No AX.25 peer exists in this smoke (the sink never answers a SABM), so there is
      // no session to stream. 404 is the correct "no such session" answer; what matters
      // here is that the query token is honoured, which the 401 probe below proves.
      why: "no live session exists in the smoke, so a documented 404 is accepted",
    },
    {
      path: `/api/v1/ports/${encodeURIComponent(BOOT_PORT_ID)}/tuning/events`,
      ok: [200, 404],
      why: "no deviation-tuning session is armed on this port, so 200 (idle feed) or 404 (no session) are both correct",
    },
    {
      path: `/api/v1/ports/${encodeURIComponent(BOOT_PORT_ID)}/spectrum/events`,
      ok: [200, 404],
      why: "the smoke's ports are kiss-tcp, and the waterfall is soundmodem-only: 404 is the documented answer",
    },
  ];

  for (const feed of feeds) {
    const good = await probe(feed.path, TOKEN);
    if (!feed.ok.includes(good.status)) {
      fail("sse", `${feed.path} answered ${good.status}${good.error ? ` (${good.error})` : ""}; expected ${feed.ok.join("/")} - ${feed.why}`);
    } else if (good.status === 200 && !good.contentType.includes("text/event-stream")) {
      fail("sse", `${feed.path} answered 200 with content-type '${good.contentType}', not text/event-stream`);
    } else {
      note(`${feed.path} -> ${good.status} ${good.contentType || "(no body)"}`);
    }

    // A bogus token must still 401: that is what proves the route reads the query
    // parameter at all, rather than answering the same regardless.
    const bogus = await probe(feed.path, "not-a-real-token");
    if (bogus.status !== 401) fail("sse", `${feed.path} answered ${bogus.status} for an INVALID access_token; expected 401`);
  }
}

// ================================================================ report
await shot("final");
await browser.close();

// (the JSON report is written by the exit handler registered at the top)
console.log("\n================ live smoke summary ================");
for (const s of steps) {
  console.log(`${s.problems.length === 0 ? "ok  " : "FAIL"} ${s.name}`);
}
if (failures.length === 0) {
  console.log(`\nAll ${steps.length} steps passed. Report: ${OUT}/live-smoke-report.json`);
  process.exit(0);
}
console.log(`\n${failures.length} problem(s):`);
for (const f of failures) console.log(`  [${f.step}] ${f.kind}: ${f.detail}`);
console.log(`\nScreenshots + report in ${OUT}`);
process.exit(1);
