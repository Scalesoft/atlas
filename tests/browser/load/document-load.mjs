// Manual load test (tests/browser/README.md, „Zátěž více uživatelů“): a few real editors and N protocol bots on ONE page for a few minutes. The bots speak the
// editor's V1 relay (JoinDocumentWithSchema, SendUpdate, ReceiveUpdate on /hubs/markdown) with real Yjs: they type 3–5
// characters a second into their own and into shared paragraphs and table cells, bold a few letters and undo. Every word
// typed is a unique token, so the end can tell lost and duplicated text apart. Midway OFFLINE bots and one browser drop for
// OFFLINE_SECONDS and come back. Reports sync latency, server CPU/memory, the update log and its compaction, browser frame
// times and key-to-screen latency, and compares every client with a fresh join (the server's state).
//
//   ATLAS_URL=… ATLAS_LOAD_PASSWORD=… ATLAS_LOAD_PAGE=<page id> node document-load.mjs
//   optional: BOTS=26, BROWSERS=4, SECONDS=300, OFFLINE=5, OFFLINE_SECONDS=20, UNDO_BOTS=4, RESTART_AT=<s> with RESTART_CMD, OUT
import { execSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";
import { chromium } from "@playwright/test";
import * as Y from "yjs";
import { BASE, DocPeer, Session, between, fmt, now, percentiles, pick, psql, sampleServer, dbLoad, sleep, yBlocks } from "./lib.mjs";

const BOTS = Number(process.env.BOTS ?? 26), BROWSERS = Number(process.env.BROWSERS ?? 4), SECONDS = Number(process.env.SECONDS ?? 300);
const OFFLINE = Number(process.env.OFFLINE ?? 5), OFFLINE_SECONDS = Number(process.env.OFFLINE_SECONDS ?? 20), UNDO_BOTS = Number(process.env.UNDO_BOTS ?? 4);
const RESTART_AT = process.env.RESTART_AT ? Number(process.env.RESTART_AT) : undefined;
const password = process.env.ATLAS_LOAD_PASSWORD, prefix = process.env.ATLAS_LOAD_PREFIX ?? "c2-load-", pageId = process.env.ATLAS_LOAD_PAGE;
// The pid may change when RESTART_CMD restarts the server: ATLAS_SERVER_PID_FILE names a file that holds the current one.
const serverPid = () => process.env.ATLAS_SERVER_PID_FILE ? readFileSync(process.env.ATLAS_SERVER_PID_FILE, "utf8").trim() : process.env.ATLAS_SERVER_PID;
if (!password || !pageId) throw new Error("Set ATLAS_LOAD_PASSWORD and ATLAS_LOAD_PAGE (run setup-users.mjs first).");
const user = i => `${prefix}${String(i).padStart(2, "0")}`;
const log = (...args) => console.log(new Date().toISOString().slice(11, 19), ...args);
const run = Date.now().toString(36).slice(-4);

// ---- browsers first: the earliest writer is the snapshotter, and a bot renders no projection ----
const EDITOR = '.tiptap:not(.comment-composer *)';
const browser = BROWSERS ? await chromium.launch({ headless: true }) : undefined;
const pages = [];
const pageErrors = {};
for (let b = 0; b < BROWSERS; b++) {
  const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 } });
  const name = user(BOTS + 1 + b);
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const login = await context.request.post("/api/auth/login", { data: { username: name, password }, headers: { "X-CSRF-TOKEN": csrf.token } });
  if (!login.ok()) throw new Error(`browser login ${name}: ${login.status()}`);
  const page = await context.newPage();
  page.on("pageerror", error => (pageErrors[name] ??= []).push(String(error).slice(0, 200)));
  const opened = now();
  await page.goto(`/#/pages/${pageId}`);
  await page.locator(EDITOR).first().waitFor({ timeout: 60_000 });
  await page.getByRole("button", { name: "Upravit", exact: true }).click();
  await page.locator(`${EDITOR}[contenteditable="true"]`).first().waitFor({ timeout: 90_000 });
  const editable = now() - opened;
  // Samplers: frames, long tasks, contenteditable flips, and when a marker token first shows in the text.
  await page.evaluate(selector => {
    const w = window; w.__frames = []; w.__long = []; w.__flips = []; w.__marks = new Map(); w.__shift = [];
    let last = performance.now();
    const tick = t => { w.__frames.push(t - last); last = t; requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
    new PerformanceObserver(list => { for (const e of list.getEntries()) w.__long.push(e.duration); }).observe({ type: "longtask", buffered: false });
    new PerformanceObserver(list => { for (const e of list.getEntries()) if (!e.hadRecentInput) w.__shift.push(e.value); }).observe({ type: "layout-shift", buffered: false });
    const root = document.querySelector(selector);
    new MutationObserver(records => {
      const at = performance.timeOrigin + performance.now();
      for (const r of records) {
        if (r.type === "attributes") { w.__flips.push([at, root.getAttribute("contenteditable")]); continue; }
        const text = r.type === "characterData" ? r.target.data : [...r.addedNodes].map(n => n.textContent).join("");
        for (const m of text.matchAll(/mk(\d+)x/g)) if (!w.__marks.has(m[1])) w.__marks.set(m[1], at);
      }
    }).observe(root, { subtree: true, characterData: true, childList: true, attributes: true, attributeFilter: ["contenteditable"] });
  }, EDITOR);
  pages.push({ page, context, name, editable });
  log(`browser ${name} editing after ${editable.toFixed(0)} ms`);
}

// ---- bots ----
const admin = await new Session("admin").developer();
const schemaJson = await psql(`select "SchemaJson" from "PageCollaboration" where "PageId"='${pageId}'`);
if (!schemaJson) throw new Error("The page has no bound schema yet: open it in an editor first (BROWSERS ≥ 1).");
const schema = JSON.parse(schemaJson);
const stats = { ackMs: [], sentUpdates: 0, received: 0 };
const sentAt = new Map(); // b64 of an update → when it was sent
const latencies = [];
const peers = [];
for (let i = 1; i <= BOTS; i++) {
  const session = await new Session(user(i)).login(user(i), password);
  const lease = await session.json("POST", `/api/pages/${pageId}/collaboration`);
  const peer = new DocPeer(session, lease.documentId, schema, stats);
  peer.onReceive = update => { const at = sentAt.get(update); if (at) latencies.push(now() - at); };
  peer.onSend = payload => sentAt.set(payload, now());
  await peer.open();
  peers.push(peer);
}
const documentId = peers[0]?.documentId;
log(`${peers.length} bots joined ${documentId}`);
const logBefore = (await psql(`select count(*), coalesce(sum(length("Payload")),0) from "CollaborationUpdate" where "PageId"='${pageId}'`)).split("|").map(Number);
const collabBefore = await psql(`select "Sequence", "SnapshotSequence", length("State") from "PageCollaboration" where "PageId"='${pageId}'`);

// ---- the workload ----
const tokens = new Map(); // token → who typed it (accounting bots and browsers only)
const inCell = new Map(); // token → typed into a table cell
let running = true, markers = 0;
const markerSent = new Map();
const blocks = peer => peer.textblocks();
const isCell = block => { let p = block.parent; while (p && !(p instanceof Y.XmlFragment && !(p instanceof Y.XmlElement))) { if (p.nodeName === "tableCell" || p.nodeName === "tableHeader") return true; p = p.parent; } return false; };

async function botLoop(peer, index) {
  const undoer = index < UNDO_BOTS;
  const all = blocks(peer);
  const hotBlock = () => blocks(peer)[Math.min(3, blocks(peer).length - 1)];
  const cells = all.filter(isCell);
  const mine = all[(7 + index * 5) % all.length];
  const paragraphs = all.filter(block => !isCell(block));
  const ownParagraph = paragraphs[(7 + index * 5) % paragraphs.length];
  let word = 0;
  while (running) {
    const roll = Math.random();
    // TARGET=cells or TARGET=paragraphs narrows the bots to table cells or to text outside tables (to tell where text goes missing).
    const target = process.env.TARGET;
    const block = target === "cells" && cells.length ? pick(cells) : target === "paragraphs" ? (roll < 0.3 ? paragraphs[Math.min(3, paragraphs.length - 1)] : ownParagraph)
      : roll < 0.3 ? hotBlock() : roll < 0.45 && cells.length ? pick(cells) : mine;
    if (!block?.doc) { await sleep(500); continue; }
    if (index === 0 && Math.random() < 0.3) {
      // A marker goes in as one insertion (a paste), so its time on the other screens is the sync latency alone.
      const k = ++markers; markerSent.set(String(k), now()); await peer.typeAt(block, ` mk${k}x`, 1e6, () => running); await sleep(1000); continue;
    }
    const token = `${undoer ? "u" : "b"}${String(index + 1).padStart(2, "0")}w${++word}q${run}`;
    const typedAll = await peer.typeAt(block, ` ${token}`, between(3, 5), () => running);
    // Only a word typed to its end counts: the one cut off when the run stops is not a token.
    if (!undoer && typedAll) { tokens.set(token, peer.session.name); inCell.set(token, isCell(block)); }
    if (Math.random() < 0.1) peer.bold(block);
    if (undoer && Math.random() < 0.3) { peer.undoLast(); stats.undos = (stats.undos ?? 0) + 1; }
    await sleep(between(200, 1200));
  }
}

// Browsers type too: their own paragraph near the top, unique tokens, real keys.
async function browserLoop({ page, name }, b) {
  const keyMs = [];
  let word = 0;
  await page.evaluate(([selector, index]) => {
    const root = document.querySelector(selector);
    const blocks = [...root.querySelectorAll("p")].filter(p => p.textContent.trim().length > 0 && !p.closest("table"));
    const target = blocks[Math.min(blocks.length - 1, 2 + index * 2)];
    const range = document.createRange(); range.selectNodeContents(target); range.collapse(false);
    const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
    root.focus();
  }, [EDITOR, b]);
  // tiptap focuses its view in a rAF: a key typed before that frame is lost (a tool artifact, no person types that fast).
  await page.waitForFunction(selector => document.activeElement === document.querySelector(selector), EDITOR).catch(() => {});
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
  while (running) {
    if (await page.evaluate(selector => document.querySelector(selector).getAttribute("contenteditable"), EDITOR) !== "true") { await page.waitForTimeout(500); continue; }
    const token = `p${b + 1}w${++word}q${run}`;
    tokens.set(token, name);
    for (const ch of ` ${token}`) {
      const start = await page.evaluate(() => performance.now());
      await page.keyboard.type(ch);
      keyMs.push((await page.evaluate(() => new Promise(done => requestAnimationFrame(() => done(performance.now()))))) - start);
      await page.waitForTimeout(between(150, 300));
    }
    await page.waitForTimeout(between(300, 1500));
  }
  return keyMs;
}

const samples = [];
let samplerOn = true;
const sampler = (async () => { while (samplerOn) { samples.push(await sampleServer(serverPid(), dbLoad).catch(error => ({ error: String(error) }))); await sleep(5000); } })();
const started = now();
log(`running ${SECONDS}s: ${BOTS} bots, ${BROWSERS} browsers`);
const loops = peers.map((peer, index) => botLoop(peer, index));
const typing = pages.map((p, b) => browserLoop(p, b));

// Offline: OFFLINE bots and the last browser drop out midway and come back.
const offlineAt = SECONDS * 0.4;
const offline = (async () => {
  await sleep(offlineAt * 1000);
  if (!running) return;
  const dropped = peers.slice(BOTS - OFFLINE);
  log(`offline: ${dropped.length} bots and ${pages.length > 1 ? 1 : 0} browser for ${OFFLINE_SECONDS}s`);
  await Promise.all(dropped.map(peer => peer.disconnect()));
  if (pages.length > 1) await pages.at(-1).context.setOffline(true);
  await sleep(OFFLINE_SECONDS * 1000);
  const back = now();
  await Promise.all(dropped.map(peer => peer.reconnect().catch(error => { stats.reconnectErrors = (stats.reconnectErrors ?? 0) + 1; stats.lastReconnectError = String(error).slice(0, 200); })));
  stats.botReconnectMs = now() - back;
  if (pages.length > 1) {
    await pages.at(-1).context.setOffline(false);
    const t = now();
    await pages.at(-1).page.locator(`${EDITOR}[contenteditable="true"]`).first().waitFor({ timeout: 60_000 }).catch(() => {});
    stats.browserReconnectMs = now() - t;
  }
  log("offline peers back");
})();
const restart = RESTART_AT !== undefined ? (async () => {
  await sleep(RESTART_AT * 1000);
  log("restarting the server");
  const t = now();
  execSync(process.env.RESTART_CMD, { stdio: "inherit", shell: process.env.RESTART_SHELL });
  stats.restartMs = now() - t;
  // The bots reconnect as the web's provider does: a fresh join with what is still unsent.
  await sleep(1000);
  await Promise.all(peers.map(async peer => { for (let a = 0; a < 20; a++) { try { if (peer.hub.state !== "Connected") await peer.reconnect(); return; } catch { await sleep(1000); } } }));
  log("bots rejoined after restart");
})() : Promise.resolve();

await sleep(SECONDS * 1000);
running = false;
await Promise.all(loops);
const keyMs = (await Promise.all(typing)).flat();
await offline; await restart;
log("workload done; letting everything settle");
// Settle: bots flush their outboxes, browsers report no pending changes.
for (let i = 0; i < 60 && peers.some(p => p.outbox.length || p.sending); i++) await sleep(500);
for (const { page } of pages) await page.waitForFunction(selector => !document.querySelector(".collaboration-status [data-pending]"), EDITOR, { timeout: 20_000 }).catch(() => {});
await sleep(8000);
samplerOn = false; await sampler;
const elapsed = (now() - started) / 1000;

// ---- convergence ----
const fresh = new DocPeer(admin, documentId, schema, {});
await fresh.open();
await sleep(1000);
const serverBlocks = yBlocks(fresh.fragment);
const serverText = serverBlocks.join("\n");
const botBlocks = peers.map(peer => yBlocks(peer.fragment).join("\n"));
const botDiverged = botBlocks.filter(text => text !== serverText).length;
const browserState = [];
for (const { page, name } of pages) {
  const blocksOf = await page.evaluate(selector => {
    const view = document.querySelector(selector); const editor = view.editor;
    if (!editor) return null;
    const out = []; editor.state.doc.descendants(node => { if (node.isTextblock) { out.push(node.textContent); return false; } return true; });
    return out;
  }, EDITOR);
  const frames = await page.evaluate(() => window.__frames.slice(60));
  const longTasks = await page.evaluate(() => window.__long);
  const flips = await page.evaluate(() => window.__flips);
  const shift = await page.evaluate(() => window.__shift.reduce((a, b) => a + b, 0));
  const marks = await page.evaluate(() => [...window.__marks]);
  const markLatency = marks.map(([k, at]) => at - (markerSent.get(k) ?? at)).filter(ms => ms > 0);
  browserState.push({ name, same: blocksOf ? blocksOf.join("\n") === serverText : null, blocks: blocksOf?.length,
    firstDiff: blocksOf ? blocksOf.findIndex((t, i) => t !== serverBlocks[i]) : null,
    frame: percentiles(frames), longFrames: frames.filter(f => f > 50).length, frames: frames.length, longTasks: percentiles(longTasks), longTaskMs: longTasks.reduce((a, b) => a + b, 0),
    readonlyFlips: flips.filter(f => f[1] === "false").length, layoutShift: shift, markerLatency: percentiles(markLatency) });
}
// Accounting: every token typed (and not undone) exactly once in the server's state.
const counts = new Map();
for (const m of serverText.matchAll(/[bp]\d+w\d+q[a-z0-9]{4}/g)) counts.set(m[0], (counts.get(m[0]) ?? 0) + 1);
const typedTokens = [...tokens.keys()];
const lost = typedTokens.filter(t => !counts.has(t));
const duplicated = [...counts].filter(([, n]) => n > 1).map(([t]) => t);
const offlineBrowserLost = lost.filter(t => pages.length > 1 && tokens.get(t) === pages.at(-1).name);
const logAfter = (await psql(`select count(*), coalesce(sum(length("Payload")),0) from "CollaborationUpdate" where "PageId"='${pageId}'`)).split("|").map(Number);
const collabAfter = await psql(`select "Sequence", "SnapshotSequence", length("State") from "PageCollaboration" where "PageId"='${pageId}'`);
const result = {
  pageId, documentId, bots: BOTS, browsers: BROWSERS, seconds: elapsed, typedChars: stats.typed, sentUpdates: stats.sentUpdates, sendErrors: stats.sendErrors ?? 0, lastError: stats.lastError,
  ackMs: percentiles(stats.ackMs), syncMs: percentiles(latencies), received: stats.received, snapshotRequests: stats.snapshotRequests ?? 0, snapshotStored: stats.snapshotStored ?? 0, flushDegraded: stats.flushDegraded ?? 0,
  botSnapshotterHandOffs: peers.reduce((n, p) => n + p.snapshotterTimes, 0), undos: stats.undos ?? 0,
  offline: { bots: OFFLINE, seconds: OFFLINE_SECONDS, botReconnectMs: stats.botReconnectMs, browserReconnectMs: stats.browserReconnectMs, reconnectErrors: stats.reconnectErrors ?? 0, lastReconnectError: stats.lastReconnectError },
  restartMs: stats.restartMs,
  convergence: { serverBlocks: serverBlocks.length, botDiverged, browsers: browserState.map(b => ({ name: b.name, same: b.same, firstDiff: b.firstDiff })) },
  accounting: { tokens: typedTokens.length, lost: lost.length, lostSample: lost.slice(0, 8), lostByWho: Object.entries(lost.reduce((m, t) => ({ ...m, [tokens.get(t)]: (m[tokens.get(t)] ?? 0) + 1 }), {})), offlineBrowserLost: offlineBrowserLost.length, lostInCells: lost.filter(t => inCell.get(t)).length,
    // A word interleaved with another writer's at the same caret is no loss: its head and its tail are both still there.
    lostSplit: lost.filter(t => serverText.includes(t.slice(0, Math.ceil(t.length / 2))) && serverText.includes(t.slice(Math.ceil(t.length / 2)))).length,
    lostContext: lost.slice(0, 4).map(t => { const head = t.slice(0, Math.ceil(t.length / 2)); const i = serverText.indexOf(head); return i < 0 ? null : serverText.slice(i, i + 40); }), typedInCells: [...inCell.values()].filter(Boolean).length, duplicated: duplicated.length, duplicatedSample: duplicated.slice(0, 8) },
  log: { before: logBefore, after: logAfter, collabBefore, collabAfter },
  keyToFrameMs: percentiles(keyMs), browsers: browserState.map(({ same: _s, firstDiff: _f, ...rest }) => rest), editableAfterMs: pages.map(p => Math.round(p.editable)), pageErrors,
  server: { samples: samples.length, cpuPercentOfOneCore: samples.length > 1 ? (100 * (samples.at(-1).cpu - samples[0].cpu)) / ((samples.at(-1).at - samples[0].at) / 1000) : NaN,
    wsStartMB: (samples[0]?.ws ?? 0) / 2 ** 20, wsMaxMB: Math.max(...samples.map(s => s.ws ?? 0)) / 2 ** 20, wsEndMB: (samples.at(-1)?.ws ?? 0) / 2 ** 20,
    dbConnsMax: Math.max(...samples.map(s => s.conns ?? 0)), lockWaitsMax: Math.max(...samples.map(s => s.lockWaits ?? 0)), outboxMax: Math.max(...samples.map(s => s.outbox ?? 0)) },
};
console.log(JSON.stringify(result, null, 1));
log(`sync ${fmt(result.syncMs)} | ack ${fmt(result.ackMs)} | bots diverged ${botDiverged}/${BOTS} | lost ${lost.length}/${typedTokens.length} dup ${duplicated.length}`);
if (process.env.OUT) writeFileSync(process.env.OUT, JSON.stringify(result, null, 1));
await fresh.close();
await Promise.all(peers.map(peer => peer.close()));
for (const { context } of pages) await context.close();
await browser?.close();
process.exit(0);
