// Screenshot every route from the built `dist/` (served with SPA fallback at
// :4173) using Playwright. Runs INSIDE the Docker container that screenshot.sh
// spins up — the dev LXC denies a host browser's network sockets, so a debian
// container (working sockets + a Playwright-supported OS) is how we screenshot
// here. Output → /shots (a mounted host dir).
import { chromium } from "playwright";
import { mkdirSync } from "node:fs";

const BASE = process.env.BASE || "http://127.0.0.1:4173";
const OUT = process.env.OUT || "/shots";
mkdirSync(OUT, { recursive: true });

// [filename, route, requires-auth]
const routes = [
  ["01-login", "/login", false],
  ["02-dashboard", "/", true],
  ["03-monitor", "/monitor", true],
  ["04-sessions", "/sessions", true],
  ["05-routes", "/routes", true],
  ["06-ports", "/ports", true],
  ["07-config", "/config", true],
  ["08-users", "/users", true],
  ["09-tuner", "/tools/tuner?port=vhf-1", true],
];

const browser = await chromium.launch({ args: ["--no-sandbox", "--disable-dev-shm-usage"] });
const ctx = await browser.newContext({ viewport: { width: 1320, height: 900 } });
const page = await ctx.newPage();
const errors = [];
page.on("pageerror", (e) => errors.push(String(e)));

// prime the auth gate's sessionStorage flag on the origin
await page.goto(`${BASE}/login`, { waitUntil: "domcontentloaded" });

for (const [name, route, authed] of routes) {
  await page.evaluate(
    (a) => (a ? sessionStorage.setItem("pdn.authed", "1") : sessionStorage.removeItem("pdn.authed")),
    authed,
  );
  await page.goto(`${BASE}${route}`, { waitUntil: "networkidle" });
  await page.waitForTimeout(900);
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
