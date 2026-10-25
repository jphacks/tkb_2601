// Robomori core dashboard: log in with the one-time password, wait for live data, save README screenshots.
// Called by capture.sh (reads BASE_URL, ROBOMORI_OTP, SHOTS, LOGS, PY, CORE_DIR, VIEWER_HTML, CAPTURE_TIMEOUT,
// EXPECT_EXPORT, RUN_DOOR2, NO_VIEWER, TZ from the environment). Every request that is not 127.0.0.1 or file:// is aborted.
"use strict";
const { chromium } = require("playwright");
const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const BASE = process.env.BASE_URL;
const OTP = process.env.ROBOMORI_OTP || "";
delete process.env.ROBOMORI_OTP;   // the dry-run door2 child must not inherit it
const SHOTS = process.env.SHOTS;
const LOGS = process.env.LOGS || SHOTS;
const TIMEOUT = Number(process.env.CAPTURE_TIMEOUT || 480) * 1000;
const EXPECT_EXPORT = process.env.EXPECT_EXPORT !== "0";
const RUN_DOOR2 = process.env.RUN_DOOR2 !== "0";
const TZ = process.env.TZ || "Asia/Tokyo";

const hide = (text) => (OTP ? String(text).split(OTP).join("****-****").split(OTP.replace("-", "")).join("********") : String(text));
const log = (...parts) => console.log(new Date().toTimeString().slice(0, 8), ...parts.map(hide));
const shot = (name) => path.join(SHOTS, name);

function runDoor2() {
  // the same command the PC-side key runs on SW2, in dry-run: GeminiClient / BlueskyClient are never created
  return new Promise((resolve) => {
    const out = fs.openSync(path.join(LOGS, "door2.log"), "a");
    const child = spawn(process.env.PY, ["manage.py", "door2", "--dry-run", "--by", "PSoC 6 の SW2 の代わり（撮影用の試し運転）"],
      { cwd: process.env.CORE_DIR, env: { ...process.env, DOOR2_DRY_RUN: "1" }, stdio: ["ignore", out, out] });
    child.on("exit", (code) => { fs.closeSync(out); resolve(code); });
  });
}

(async () => {
  if (!/^[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(OTP)) throw new Error("ROBOMORI_OTP missing");
  const browser = await chromium.launch();
  try {
    const context = await browser.newContext({
      viewport: { width: 1440, height: 900 }, deviceScaleFactor: 2, locale: "ja-JP", timezoneId: TZ,
      colorScheme: "light", reducedMotion: "reduce",
    });
    await context.route("**/*", (route) => {
      const url = route.request().url();
      if (url.startsWith(BASE + "/") || url.startsWith("file:")) return route.continue();
      log("blocked request to", url.split(/[?#]/)[0]);
      return route.abort();
    });
    const page = await context.newPage();
    page.on("console", (m) => { if (m.type() === "error") log("console error:", m.text()); });
    page.on("pageerror", (e) => log("page error:", e.message));

    // ---- log in the way start.sh does: /login#code=XXXX-XXXX, login.js submits it and wipes the hash ----
    try {
      await page.goto(`${BASE}/login#code=${OTP}`, { waitUntil: "domcontentloaded" });
      await page.waitForURL((u) => new URL(u).pathname === "/", { timeout: 20000 });
    } catch (error) {
      throw new Error("login failed: " + hide(error.message).split("\n")[0]);
    }
    await page.waitForSelector("#map");
    log("logged in");

    const api = (p) => page.evaluate(async (p) => {
      const r = await fetch(p, { cache: "no-store", credentials: "same-origin" });
      return r.ok ? r.json() : { status: r.status };
    }, p);

    // ---- wait for live values (and run the dry-run door2 once the timetable allows it) ----
    const deadline = Date.now() + TIMEOUT;
    let door2Done = !RUN_DOOR2;
    let last = "";
    for (;;) {
      const s = await api("/api/state");
      const d = await api("/api/drive");
      const items = d.items || [];
      const ready = {
        "stand-in connected": s.unity && s.unity.connected,
        "3 robots": (s.robots || []).length >= 3,
        "AI slowdown": (s.slowdowns || []).length > 0,
        "scheduled export": !EXPECT_EXPORT || items.some((i) => i.kind === "scheduled"),
        "mode share": !EXPECT_EXPORT || (s.schedule && s.schedule.mode === "share"),
        "door2 closed": s.door2 && s.door2.state === "store",
        "dry-run door2": door2Done,
        "recommendation": (s.dispatch || []).length > 0,
      };
      if (!door2Done && ready["stand-in connected"] && ready["AI slowdown"] && ready["scheduled export"] && ready["mode share"] && ready["door2 closed"]) {
        log("running door2 --dry-run");
        log("door2 exit code", await runDoor2());
        door2Done = true;
        continue;
      }
      const missing = Object.keys(ready).filter((k) => !ready[k]);
      const line = missing.length ? "waiting for: " + missing.join(", ") : "all live values present";
      if (line !== last) { log(line); last = line; }
      if (!missing.length) break;
      if (Date.now() > deadline) { log("TIMEOUT - capturing anyway"); break; }
      await page.waitForTimeout(2000);
    }

    // ---- ask the rule AI two of its quick questions so the chat is not empty ----
    for (const label of ["いま危ない", "どうすれば"]) {   // button labels (data-q differs, e.g. "どうすればいい？")
      await page.locator(".ruleai .quick button", { hasText: label }).first().click();
      await page.waitForTimeout(1200);
    }
    await page.waitForTimeout(3000);   // one more poll for charts / share preview
    await page.evaluate(() => window.scrollTo(0, 0));

    await page.screenshot({ path: shot("01-dashboard-full.png"), fullPage: true });
    await page.screenshot({ path: shot("02-dashboard-top.png") });
    // element shots: un-stick the header so it cannot overlap a section that is taller than the viewport
    // (set through the CSSOM; the page's CSP forbids injected <style>)
    await page.evaluate(() => { document.querySelector("header.top").style.position = "static"; });
    await page.locator("section.center").screenshot({ path: shot("03-store-map.png") });
    // share section with the record preview ("記録の中身") unfolded
    await page.evaluate(() => { const box = document.getElementById("share-record-box"); if (box && !box.hidden) box.open = true; });
    await page.locator("section.panel.share").screenshot({ path: shot("04-share-atproto.png") });
    await page.evaluate(() => {
      document.getElementById("share-record-box").open = false;
      document.querySelector("header.top").style.position = "";
      window.scrollTo(0, 0);
    });
    await page.waitForTimeout(500);
    log("saved dashboard shots");

    // ---- Google Drive inbox (right drawer), first item opened ----
    await page.click("#drive-open");
    await page.waitForSelector("#drive-list li button");
    await page.click("#drive-list li button");
    await page.waitForSelector("#drive-read .doc");
    await page.waitForTimeout(800);
    await page.evaluate(() => document.activeElement && document.activeElement.blur());
    await page.screenshot({ path: shot("05-drive-inbox.png") });
    await page.keyboard.press("Escape");
    log("saved drive inbox shot");

    // ---- optional: the read-only viewer as a local file (no PDS: only the key part works offline) ----
    if (process.env.NO_VIEWER !== "1" && process.env.VIEWER_HTML && fs.existsSync(process.env.VIEWER_HTML)) {
      const viewer = await context.newPage();
      viewer.on("pageerror", (e) => log("viewer page error:", e.message));
      await viewer.goto("file://" + process.env.VIEWER_HTML);
      try {
        await viewer.click("#make-key");
        await viewer.waitForSelector("#pub-box:not([hidden])", { timeout: 5000 });
      } catch (error) {
        log("viewer: key generation did not show a public key (" + error.message.split("\n")[0] + ")");
      }
      await viewer.screenshot({ path: shot("06-viewer-offline.png"), fullPage: true });
      log("saved viewer shot");
    }
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(hide(error && error.stack ? error.stack : error));
  process.exit(1);
});
