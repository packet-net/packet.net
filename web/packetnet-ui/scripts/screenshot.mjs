// Screenshot every route from the built `dist/` (served with SPA fallback at
// :4173) using Playwright. Runs INSIDE the Docker container that screenshot.sh
// spins up: the dev LXC denies a host browser's network sockets, so a debian
// container (working sockets + a Playwright-supported OS) is how we screenshot
// here. Output → /shots (a mounted host dir).
//
// Env knobs (all optional):
//   BASE               origin to shoot (default http://127.0.0.1:4173 - what screenshot.sh serves).
//   OUT                output dir (default /shots - the dir screenshot.sh mounts).
//   PDN_TOKEN          access JWT to enter a LIVE build with (see "Session priming" below).
//   PDN_REFRESH_TOKEN  }
//   PDN_USERNAME       } optional companions to PDN_TOKEN; scope defaults to admin.
//   PDN_SCOPE          }
//   PDN_USER/PDN_PASS  instead of PDN_TOKEN: log in for real against BASE and use the
//                      issued pair. Needs BASE to actually serve /api/v1.
//   PDN_PORT           port id for the tuner/waterfall shots. NO default on purpose: the
//                      mock fixture ids (`vhf-1` and friends) were purged from the screens
//                      (#691) and mean nothing on a real node. Unset, those two shots are
//                      taken with no ?port= and the screens fall back to the first port in
//                      /config.
//   PDN_APP            app id for the /apps/:id in-panel frame shot; that shot is skipped
//                      when unset (the route needs a real installed app).
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

const BASE = process.env.BASE || "http://127.0.0.1:4173";
const OUT = process.env.OUT || "/shots";
mkdirSync(OUT, { recursive: true });

const portId = process.env.PDN_PORT || "";
const appId = process.env.PDN_APP || "";
const portQuery = portId ? `?port=${encodeURIComponent(portId)}` : "";

// Every addressable route in src/app/router.tsx (the catch-all just redirects to "/").
// [filename, route (null -> skip), requires-a-session]
const routes = [
  ["01-login", "/login", false],
  ["02-setup", "/setup", false],
  ["03-dashboard", "/", true],
  ["04-monitor", "/monitor", true],
  ["05-sessions", "/sessions", true],
  ["06-console", "/console", true],
  ["07-apps", "/apps", true],
  ["08-app-frame", appId ? `/apps/${encodeURIComponent(appId)}` : null, true],
  ["09-routes", "/routes", true],
  ["10-capabilities", "/capabilities", true],
  ["11-ports", "/ports", true],
  ["12-headends", "/headends", true],
  ["13-config", "/config", true],
  ["14-users", "/users", true],
  ["15-links", "/links", true],
  ["16-tuner", `/tools/tuner${portQuery}`, true],
  ["17-waterfall", `/tools/waterfall${portQuery}`, true],
];

// ---- Session priming ----------------------------------------------------
// The app keeps its session in localStorage under "pdn.session" (KEY in app/auth.tsx,
// mirrored as SESSION_KEY in lib/api.ts) as {token, refreshToken, username, scope}.
// (This script used to set sessionStorage "pdn.authed" - a key that exists nowhere in
// the app, so every "authed" shot was really a boot splash or a bounce to /login.)
//
// A MOCK build needs none of this: the gate in router.tsx short-circuits on
// apiMode === "mock" and enters a synthetic admin session itself. Priming only matters
// when BASE is a live build / a real node, where the gate probes /status with whatever
// token is stored.
const SESSION_KEY = "pdn.session";

async function buildSession(page) {
  if (process.env.PDN_TOKEN) {
    return {
      token: process.env.PDN_TOKEN,
      refreshToken: process.env.PDN_REFRESH_TOKEN || null,
      username: process.env.PDN_USERNAME || null,
      scope: process.env.PDN_SCOPE || "admin",
    };
  }
  const user = process.env.PDN_USER;
  const pass = process.env.PDN_PASS;
  if (!user || !pass) return null;
  // Real POST /api/v1/auth/login from the page origin, then persist exactly what
  // screens/login.tsx persists: the LoginResult {token, expiresAt, scopes, refreshToken,
  // username} mapped onto the Session {token, refreshToken, username, scope}.
  return await page.evaluate(async ([u, p]) => {
    const res = await fetch("/api/v1/auth/login", {
      method: "POST",
      headers: { "content-type": "application/json", accept: "application/json" },
      body: JSON.stringify({ username: u, password: p }),
    });
    if (!res.ok) throw new Error(`login failed (${res.status})`);
    const r = await res.json();
    return {
      token: r.token,
      refreshToken: r.refreshToken ?? null,
      username: r.username ?? u,
      scope: r.scopes ?? null,
    };
  }, [user, pass]);
}

const browser = await chromium.launch({ args: ["--no-sandbox", "--disable-dev-shm-usage"] });
const ctx = await browser.newContext({ viewport: { width: 1320, height: 900 } });
const page = await ctx.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(String(e)));

// Land on the origin first so localStorage is addressable.
await page.goto(`${BASE}/login`, { waitUntil: "domcontentloaded" });
const session = await buildSession(page);
console.log(
  session
    ? `session primed for ${session.username ?? "(unnamed)"} scope=${session.scope ?? "?"}`
    : "no PDN_TOKEN / PDN_USER+PDN_PASS: relying on a mock build's synthetic admin session",
);
if (!portId) console.log("PDN_PORT unset: tuner + waterfall fall back to the first port in /config");

for (const [name, route, authed] of routes) {
  if (route === null) {
    console.log(`skip ${name} (set PDN_APP to an installed app id)`);
    continue;
  }
  await page.evaluate(
    ([key, s]) => (s ? localStorage.setItem(key, JSON.stringify(s)) : localStorage.removeItem(key)),
    [SESSION_KEY, authed ? session : null],
  );
  // domcontentloaded, not networkidle: the monitor/console/tuner/waterfall screens hold an
  // EventSource open, so the network never goes idle and the navigation would time out.
  await page.goto(`${BASE}${route}`, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(1200);
  await page.screenshot({ path: `${OUT}/${name}.png` });
  console.log(`shot ${name}`);
}

await browser.close();
if (errors.length) {
  console.log("PAGE ERRORS:\n" + [...new Set(errors)].join("\n"));
  process.exitCode = 3;
} else {
  console.log("no page errors");
}
