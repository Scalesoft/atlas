// Manual load test (tests/browser/README.md, „Zátěž více uživatelů“): N protocol bots and a few real browsers on ONE board for a few minutes. Each bot moves,
// resizes, creates, deletes and undoes at a human rate (one gesture every 1–3 s) with contention on a hot set of items, and sends
// its drag as presence first, as the canvas does. Reports accept/reject rates, action → appearance latency on the other clients,
// server CPU/memory, DB connections, the outbox backlog, BoardOperation growth, frame times and remote-move motion in the
// browsers, and at the end compares every client with GET /api/boards/{id}.
//
//   ATLAS_URL=http://127.0.0.1:5141 ATLAS_LOAD_PASSWORD=… ATLAS_LOAD_SPACE=<space id> node board-load.mjs
//   optional: ATLAS_LOAD_BOARD=<board id> (else a seeded one), BOTS=30, BROWSERS=4, SECONDS=300, ATLAS_SERVER_PID, OUT=<json>
import { randomUUID } from "node:crypto";
import { writeFileSync } from "node:fs";
import { chromium } from "@playwright/test";
import { BASE, BoardMirror, BoardPeer, Session, between, dbLoad, diffBoards, fmt, now, percentiles, pick, psql, sampleServer, sleep } from "./lib.mjs";

const BOTS = Number(process.env.BOTS ?? 30), BROWSERS = Number(process.env.BROWSERS ?? 4), SECONDS = Number(process.env.SECONDS ?? 300);
const password = process.env.ATLAS_LOAD_PASSWORD, prefix = process.env.ATLAS_LOAD_PREFIX ?? "c2-load-";
const spaceId = process.env.ATLAS_LOAD_SPACE;
const serverPid = process.env.ATLAS_SERVER_PID;
if (!password || !spaceId) throw new Error("Set ATLAS_LOAD_PASSWORD and ATLAS_LOAD_SPACE (run setup-users.mjs first).");
const user = i => `${prefix}${String(i).padStart(2, "0")}`;
const log = (...args) => console.log(new Date().toISOString().slice(11, 19), ...args);

// ---- the board ----
const admin = await new Session("admin").developer();
async function seedBoard() {
  const board = await admin.json("POST", `/api/spaces/${spaceId}/boards`, { title: `C2 zátěž ${new Date().toISOString().slice(0, 16)}` });
  const tree = await admin.json("GET", `/api/spaces/${spaceId}/tree`);
  const pageIds = tree.items.filter(p => p.type === "page" && p.readable).map(p => p.id).slice(0, 30);
  const contacts = (await admin.json("GET", "/api/objects?type=atlas.contact").catch(() => ({ items: [] })));
  const contactIds = (Array.isArray(contacts) ? contacts : contacts.items ?? contacts.objects ?? []).map(o => o.id ?? o.objectId).slice(0, 5);
  const changes = []; let z = 1;
  for (let f = 0; f < 6; f++) {
    const fx = (f % 3) * 1500, fy = Math.floor(f / 3) * 1300, frame = randomUUID();
    changes.push({ type: "create", item: { id: frame, kind: "frame", x: fx, y: fy, width: 1400, height: 1200, z: z++, text: `Oddíl ${f + 1}`, color: "blue", props: {} } });
    let previous = "";
    for (let i = 0; i < 40; i++) {
      const id = randomUUID(), at = { id, x: fx + 40 + (i % 8) * 165, y: fy + 70 + Math.floor(i / 8) * 215, z: z++ };
      changes.push(i % 3 === 0 ? { type: "create", item: { ...at, kind: "note", width: 150, height: 150, text: `Poznámka ${f + 1}.${i}`, color: "yellow", props: { fontSize: "auto" } } }
        : i % 3 === 1 ? { type: "create", item: { ...at, kind: "shape", width: 150, height: 90, text: `Krok ${i}`, props: { shape: "rounded" } } }
        : { type: "create", item: { ...at, kind: "text", width: 150, height: 50, text: `Text ${f + 1}-${i}`, props: { fontSize: "m", align: "left" } } });
      changes.push({ type: "parent", itemId: id, parentId: frame });
      if (previous && i % 8 !== 0) changes.push({ type: "create", item: { id: randomUUID(), kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: previous, to: id, props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" } } });
      previous = id;
    }
  }
  pageIds.forEach((pageId, i) => changes.push({ type: "create", item: { id: randomUUID(), kind: "page", x: 4600 + (i % 5) * 300, y: (Math.floor(i / 5)) * 420, width: 260, height: i % 3 === 0 ? 360 : 150, z: z++, targetResourceId: pageId, props: { display: i % 3 === 0 ? "preview" : "card" } } }));
  contactIds.forEach((objectId, i) => changes.push({ type: "create", item: { id: randomUUID(), kind: "object", x: 4600 + i * 300, y: 2700, width: 280, height: 160, z: z++, targetResourceId: objectId, props: { objectId, typeId: "atlas.contact", view: "card" } } }));
  for (let from = 0; from < changes.length; from += 180) await admin.json("POST", `/api/boards/${board.id}/operations`, { operationId: randomUUID(), changes: changes.slice(from, from + 180) });
  log(`seeded board ${board.id}: ${changes.length} changes, ${pageIds.length} page cards, ${contactIds.length} contact cards`);
  return board.id;
}
const boardId = (process.env.ATLAS_LOAD_BOARD ?? (await seedBoard())).toLowerCase();
const operationsBefore = Number(await psql(`select count(*) from "BoardOperation" where "BoardId"='${boardId}'`));
const operationsAll = Number(await psql(`select count(*) from "BoardOperation"`));

// ---- bots ----
const stats = { sent: new Map(), status: {}, postMs: [], fanout: 0, presenceIn: 0 };
const peers = [];
for (let i = 1; i <= BOTS; i++) {
  const session = await new Session(user(i)).login(user(i), password);
  const peer = new BoardPeer(session, boardId, stats);
  await peer.open();
  peers.push(peer);
}
log(`${peers.length} bots on board:${boardId}`);

// ---- browsers: observers and one real dragger ----
const browser = BROWSERS ? await chromium.launch({ headless: true }) : undefined;
const pages = [];
for (let b = 0; b < BROWSERS; b++) {
  const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 } });
  const name = user(BOTS + 1 + b);
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const login = await context.request.post("/api/auth/login", { data: { username: name, password }, headers: { "X-CSRF-TOKEN": csrf.token } });
  if (!login.ok()) throw new Error(`browser login ${name}: ${login.status()}`);
  const page = await context.newPage();
  page.on("pageerror", error => (stats.pageErrors ??= []).push(String(error).slice(0, 200)));
  await page.goto(`/#/boards/${boardId}`);
  await page.locator("[data-board-surface]").waitFor({ timeout: 30_000 });
  await page.locator(".vue-flow__node").first().waitFor({ timeout: 30_000 });
  // rAF sampler: frame deltas, and every frame the screen box of watched nodes (a glide shows as many in-between boxes).
  await page.evaluate(() => {
    const w = window; w.__frames = []; w.__watch = new Map(); w.__ghosts = [];
    let last = performance.now();
    const tick = t => {
      w.__frames.push(t - last); last = t;
      for (const [id, samples] of w.__watch) {
        const el = document.querySelector(`.vue-flow__node[data-id="${id}"]`);
        if (!el) continue;
        const r = el.getBoundingClientRect(), key = `${Math.round(r.x)},${Math.round(r.y)}`;
        if (samples.at(-1)?.key !== key) samples.push({ at: performance.timeOrigin + t, key });
      }
      const ghosts = document.querySelectorAll("[data-board-ghost]").length;
      if (ghosts !== (w.__ghosts.at(-1)?.n ?? 0)) w.__ghosts.push({ at: performance.timeOrigin + t, n: ghosts });
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  pages.push({ page, context, name });
}
log(`${pages.length} browsers open`);

// ---- the workload ----
const initial = (peers[0]?.mirror ?? new BoardMirror(await admin.json("GET", `/api/boards/${boardId}`))).visible().filter(i => ["note", "shape", "text"].includes(i.kind));
const hot = initial.slice(0, 5).map(i => i.id);
const watched = initial[6]?.id; // moved by bot 1 every few seconds, watched in the browsers
for (const { page } of pages) await page.evaluate(id => window.__watch.set(id, []), watched);
const remoteMoves = []; // { at, x, y }
let running = true;

async function botLoop(peer, index) {
  const own = []; const history = [];
  while (running) {
    await sleep(between(1000, 3000));
    if (!running) break;
    if (peer.closed) continue; // a restart: the bot is opened again meanwhile
    const alive = peer.mirror.visible().filter(i => ["note", "shape", "text"].includes(i.kind));
    if (!alive.length) continue;
    const roll = Math.random();
    let changes, undo;
    if (index === 0 && watched && roll < 0.5) {
      const item = peer.mirror.items.get(watched); if (!item || item.deleted) continue;
      const x = item.x + (Math.random() < 0.5 ? -120 : 120), y = item.y;
      for (let f = 1; f <= 5; f++) { peer.presence({ dragging: [{ itemId: watched, x: item.x + ((x - item.x) * f) / 5, y, width: item.width, height: item.height }] }); await sleep(50); }
      remoteMoves.push({ at: now(), x, y });
      changes = [{ type: "move", itemId: watched, x, y, width: item.width, height: item.height }];
    } else if (roll < 0.5) {
      const item = Math.random() < 0.25 ? peer.mirror.items.get(pick(hot)) : pick(alive);
      if (!item || item.deleted) continue;
      const x = Math.round(item.x + between(-60, 60)), y = Math.round(item.y + between(-60, 60));
      for (let f = 1; f <= 4; f++) { peer.presence({ cursor: { x, y }, dragging: [{ itemId: item.id, x: item.x + ((x - item.x) * f) / 4, y: item.y + ((y - item.y) * f) / 4, width: item.width, height: item.height }] }); await sleep(50); }
      changes = [{ type: "move", itemId: item.id, x, y, width: item.width, height: item.height }];
      undo = { type: "move", itemId: item.id, x: item.x, y: item.y, width: item.width, height: item.height, expect: { x, y } };
    } else if (roll < 0.62) {
      const item = pick(alive);
      const width = Math.max(60, Math.round(item.width + between(-30, 30))), height = Math.max(40, Math.round(item.height + between(-30, 30)));
      changes = [{ type: "move", itemId: item.id, x: item.x, y: item.y, width, height }];
    } else if (roll < 0.77) {
      const id = randomUUID();
      changes = [{ type: "create", item: { id, kind: "note", x: Math.round(between(0, 4000)), y: Math.round(between(0, 2400)), width: 150, height: 150, z: 100000 + Math.floor(Math.random() * 100000), text: `Bot ${index + 1}`, color: "green", props: { fontSize: "auto" } } }];
      own.push(id);
      undo = { type: "delete", itemId: id };
    } else if (roll < 0.85) {
      const target = own.length && Math.random() < 0.8 ? own.splice(Math.floor(Math.random() * own.length), 1)[0] : pick(alive).id;
      const item = peer.mirror.items.get(target); if (!item || item.deleted) continue;
      changes = [{ type: "delete", itemId: target }];
      undo = { type: "restore", itemId: target };
    } else if (roll < 0.9 && alive.length > 1) {
      const a = pick(alive), b = pick(alive); if (a.id === b.id) continue;
      changes = [{ type: "create", item: { id: randomUUID(), kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: a.id, to: b.id, props: { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" } } }];
    } else if (roll < 0.95) {
      const item = pick(alive);
      changes = [{ type: "set", itemId: item.id, text: `${item.kind === "note" ? "Poznámka" : "Text"} ${index + 1}·${Math.floor(Math.random() * 1000)}` }];
    } else if (history.length) {
      // Undo: write the previous value back only where it still holds (boardModel.ts), else skip as the web does.
      const last = history.pop(); const item = peer.mirror.items.get(last.itemId);
      if (last.type === "move" && (!item || item.deleted || item.x !== last.expect.x || item.y !== last.expect.y)) { stats.undoSkipped = (stats.undoSkipped ?? 0) + 1; continue; }
      const { expect: _expect, ...change } = last; changes = [change];
      stats.undos = (stats.undos ?? 0) + 1;
    } else continue;
    peer.presence({ dragging: [] });
    const result = await peer.apply(changes);
    stats.status[result.status] = (stats.status[result.status] ?? 0) + 1;
    stats.postMs.push(result.ms);
    if (!result.ok) (stats.rejections ??= []).push(`${changes[0].type}:${result.status}:${result.body.slice(0, 120)}`);
    else if (undo) history.push(undo);
  }
}

// One browser drags a real card with the mouse every few seconds (frame times while dragging on a big, busy board).
async function dragLoop({ page }) {
  const dragFrames = [];
  while (running) {
    await sleep(4000);
    if (!running) break;
    const node = page.locator(".vue-flow__node[data-id]", { has: page.locator(".board-note, .board-shape-text") }).nth(Math.floor(Math.random() * 40));
    const box = await node.boundingBox().catch(() => null);
    if (!box || box.y < 80 || box.y > 800 || box.x < 80 || box.x > 1300) continue;
    const start = await page.evaluate(() => window.__frames.length);
    await page.mouse.move(box.x + box.width / 2, box.y + 10);
    await page.mouse.down();
    for (let s = 1; s <= 30; s++) await page.mouse.move(box.x + box.width / 2 + s * 4, box.y + 10 + s * 2);
    await page.mouse.up();
    dragFrames.push(...(await page.evaluate(from => window.__frames.slice(from), start)));
  }
  return dragFrames;
}

const samples = [];
const sampler = (async () => { while (running) { samples.push(await sampleServer(serverPid, dbLoad).catch(error => ({ error: String(error) }))); await sleep(5000); } })();
log(`running ${SECONDS}s: ${BOTS} bots, ${BROWSERS} browsers`);
const started = now();
const loops = peers.map((peer, index) => botLoop(peer, index));
const dragging = pages.length ? dragLoop(pages[0]) : Promise.resolve([]);
// RESTART_AT=<s> with RESTART_CMD: the server goes down hard mid-run; the bots open the board again as the web does (a fresh read
// and a new view session), the browsers reconnect on their own.
const restart = process.env.RESTART_AT ? (async () => {
  await sleep(Number(process.env.RESTART_AT) * 1000);
  log("restarting the server");
  const { execSync } = await import("node:child_process");
  const t = now();
  execSync(process.env.RESTART_CMD, { stdio: "inherit", shell: process.env.RESTART_SHELL });
  stats.restartMs = now() - t;
  await Promise.all(peers.map(async peer => { for (let a = 0; a < 30; a++) { try { await peer.close(); peer.closed = false; await peer.open(); return; } catch { await sleep(1000); } } }));
  log("bots back after restart");
})() : Promise.resolve();
await sleep(SECONDS * 1000);
running = false;
await Promise.all(loops);
await restart;
const dragFrames = await dragging;
await sampler;
const elapsed = (now() - started) / 1000;
log("workload done; settling 8 s");
await sleep(8000);

// ---- results ----
const serverBoard = await admin.json("GET", `/api/boards/${boardId}`);
const live = peers.map(peer => diffBoards(serverBoard.items, peer.mirror.visible()));
await Promise.all(peers.map(peer => peer.catchUp().catch(() => {})));
const caught = peers.map(peer => diffBoards(serverBoard.items, peer.mirror.visible()));
const fanout = [];
for (const [operationId, sent] of stats.sent) for (const peer of peers) { if (peer.session.name === sent.author) continue; const at = peer.seen.get(operationId); if (at) fanout.push(at - sent.at); }
const missedDeliveries = [...stats.sent.keys()].reduce((n, op) => n + peers.filter(p => !p.seen.has(op)).length, 0);
const browserResults = [];
for (const { page, name } of pages) {
  const shown = await page.evaluate(() => [...document.querySelectorAll(".vue-flow__node[data-id]")].map(el => {
    const m = /translate\(([-\d.]+)px, ?([-\d.]+)px\)/.exec(el.style.transform); return { id: el.dataset.id, x: m ? Number(m[1]) : NaN, y: m ? Number(m[2]) : NaN };
  }));
  const serverNodes = new Map(serverBoard.items.filter(i => i.kind !== "connector").map(i => [i.id, i]));
  const wrong = shown.filter(n => { const s = serverNodes.get(n.id); return !s || Math.abs(s.x - n.x) > 0.5 || Math.abs(s.y - n.y) > 0.5; });
  const missing = [...serverNodes.keys()].filter(id => !shown.some(n => n.id === id));
  const frames = await page.evaluate(() => window.__frames.slice(30));
  const watch = await page.evaluate(id => window.__watch.get(id) ?? [], watched);
  const ghosts = await page.evaluate(() => window.__ghosts);
  // Per remote move of the watched item: first box change after the commit was sent, and how many boxes until it settled.
  const moves = remoteMoves.map(move => {
    const after = watch.filter(s => s.at >= move.at - 300 && s.at <= move.at + 2500);
    const ghost = ghosts.find(g => g.at >= move.at - 400 && g.n > 0);
    return { firstMs: after.length ? after[0].at - move.at : NaN, boxes: after.length, spanMs: after.length ? after.at(-1).at - after[0].at : 0, ghostMs: ghost ? ghost.at - (move.at - 250) : NaN };
  }).filter(m => Number.isFinite(m.firstMs));
  browserResults.push({ name, nodes: shown.length, wrong: wrong.length, missing: missing.length, wrongSample: wrong.slice(0, 3),
    frame: percentiles(frames), longFrames: frames.filter(f => f > 50).length, dropped: frames.reduce((n, f) => n + Math.max(0, Math.round(f / 16.7) - 1), 0), frames: frames.length,
    remote: { moves: moves.length, first: percentiles(moves.map(m => m.firstMs)), boxes: percentiles(moves.map(m => m.boxes)), animated: moves.filter(m => m.boxes >= 4).length, ghost: percentiles(moves.map(m => m.ghostMs).filter(Number.isFinite)) } });
}
// A screen behind the server at the end may only be late: it is read again after LATE seconds of quiet (default 45).
const serverNodesLate = new Map(serverBoard.items.filter(i => i.kind !== "connector").map(i => [i.id, i]));
if (browserResults.some(b => b.wrong || b.missing)) {
  const late = Number(process.env.LATE ?? 45);
  await sleep(late * 1000);
  for (const [index, { page }] of pages.entries()) {
    const shown = await page.evaluate(() => [...document.querySelectorAll(".vue-flow__node[data-id]")].map(el => {
      const m = /translate\(([-\d.]+)px, ?([-\d.]+)px\)/.exec(el.style.transform); return { id: el.dataset.id, x: m ? Number(m[1]) : NaN, y: m ? Number(m[2]) : NaN };
    }));
    const wrong = shown.filter(n => { const s = serverNodesLate.get(n.id); return !s || Math.abs(s.x - n.x) > 0.5 || Math.abs(s.y - n.y) > 0.5; }).length;
    const missing = [...serverNodesLate.keys()].filter(id => !shown.some(n => n.id === id)).length;
    browserResults[index].late = { afterSeconds: late, wrong, missing };
  }
}
const operationsAfter = Number(await psql(`select count(*) from "BoardOperation" where "BoardId"='${boardId}'`));
const operationsBytes = await psql(`select pg_size_pretty(pg_total_relation_size('"BoardOperation"'))`);
const result = {
  boardId, bots: BOTS, browsers: BROWSERS, seconds: elapsed, restartMs: stats.restartMs, status: stats.status, posted: stats.postMs.length, perSecond: stats.postMs.length / elapsed,
  postMs: percentiles(stats.postMs), fanoutMs: percentiles(fanout), fanoutMessages: stats.fanout, missedDeliveries, presenceIn: stats.presenceIn,
  rejections: [...new Set(stats.rejections ?? [])].slice(0, 10), undos: stats.undos ?? 0, undoSkipped: stats.undoSkipped ?? 0,
  gaps: peers.reduce((n, p) => n + p.gaps, 0), resyncs: peers.reduce((n, p) => n + p.resyncs, 0), unknownChanges: peers.reduce((n, p) => n + p.mirror.unknown, 0),
  liveDiverged: live.filter(d => !d.same).length, liveSample: live.find(d => !d.same), caughtDiverged: caught.filter(d => !d.same).length, caughtSample: caught.find(d => !d.same),
  serverItems: serverBoard.items.length, serverSequence: serverBoard.sequence,
  boardOperations: { before: operationsBefore, after: operationsAfter, allBefore: operationsAll, tableSize: operationsBytes },
  dragFrame: percentiles(dragFrames), dragLong: dragFrames.filter(f => f > 50).length, dragFrames: dragFrames.length,
  browsers: browserResults, pageErrors: [...new Set(stats.pageErrors ?? [])].slice(0, 5),
  server: { samples: samples.length, cpuSeconds: samples.length > 1 ? samples.at(-1).cpu - samples[0].cpu : NaN, cpuPercentOfOneCore: samples.length > 1 ? (100 * (samples.at(-1).cpu - samples[0].cpu)) / ((samples.at(-1).at - samples[0].at) / 1000) : NaN,
    wsMaxMB: Math.max(...samples.map(s => s.ws ?? 0)) / 2 ** 20, wsStartMB: (samples[0]?.ws ?? 0) / 2 ** 20, wsEndMB: (samples.at(-1)?.ws ?? 0) / 2 ** 20,
    dbConnsMax: Math.max(...samples.map(s => s.conns ?? 0)), dbActiveMax: Math.max(...samples.map(s => s.active ?? 0)), lockWaitsMax: Math.max(...samples.map(s => s.lockWaits ?? 0)), outboxMax: Math.max(...samples.map(s => s.outbox ?? 0)), outboxEnd: samples.at(-1)?.outbox },
};
console.log(JSON.stringify(result, null, 2));
log(`POST ${fmt(result.postMs)} | fan-out ${fmt(result.fanoutMs)} | live diverged ${result.liveDiverged}/${BOTS}, after catch-up ${result.caughtDiverged}/${BOTS}`);
if (process.env.OUT) writeFileSync(process.env.OUT, JSON.stringify(result, null, 2));
await Promise.all(peers.map(peer => peer.close()));
for (const { context } of pages) await context.close();
await browser?.close();
process.exit(0);
