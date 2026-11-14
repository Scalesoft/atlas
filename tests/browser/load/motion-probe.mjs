// Motion probe (tests/browser/README.md, „Zátěž více uživatelů“): how a board feels in one real browser while someone else works on it. rAF sampling of
// frame times while dragging a card, panning and zooming, and of a watched card that another person (a protocol bot) drags and
// drops: when its drag preview (ghost) appears, when the card itself moves, and whether it glides (many in-between boxes over
// --atlas-motion-base) or jumps (one box). LOAD=<n> adds n bots moving other cards meanwhile.
//
//   node motion-probe.mjs  (ATLAS_URL, ATLAS_LOAD_PASSWORD, ATLAS_LOAD_PREFIX, ATLAS_LOAD_BOARD; MOVES=10, LOAD=0, OUT)
import { chromium } from "@playwright/test";
import { writeFileSync } from "node:fs";
import { BASE, BoardPeer, Session, between, now, percentiles, pick, sleep } from "./lib.mjs";

const password = process.env.ATLAS_LOAD_PASSWORD, prefix = process.env.ATLAS_LOAD_PREFIX ?? "c2-load-", boardId = process.env.ATLAS_LOAD_BOARD;
const MOVES = Number(process.env.MOVES ?? 10), LOAD = Number(process.env.LOAD ?? 0);
if (!password || !boardId) throw new Error("Set ATLAS_LOAD_PASSWORD and ATLAS_LOAD_BOARD.");
const user = i => `${prefix}${String(i).padStart(2, "0")}`;

const browser = await chromium.launch({ headless: true });
const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 } });
const csrf = await (await context.request.get("/api/auth/csrf")).json();
await context.request.post("/api/auth/login", { data: { username: user(34), password }, headers: { "X-CSRF-TOKEN": csrf.token } });
const page = await context.newPage();
await page.goto(`/#/boards/${boardId}`);
await page.locator("[data-board-surface]").waitFor();
await page.locator(".vue-flow__node").first().waitFor();
await page.bringToFront();
await sleep(2000);
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
const framesOf = async fn => { const from = await page.evaluate(() => window.__frames.length); await fn(); return page.evaluate(from => window.__frames.slice(from + 1), from); };
const summary = frames => ({ ...percentiles(frames), long: frames.filter(f => f > 50).length, dropped: frames.reduce((n, f) => n + Math.max(0, Math.round(f / 16.7) - 1), 0) });

// The mover and the optional crowd.
const stats = { sent: new Map() };
const mover = new BoardPeer(await new Session(user(33)).login(user(33), password), boardId, stats);
await mover.open();
const crowd = [];
for (let i = 1; i <= LOAD; i++) { const peer = new BoardPeer(await new Session(user(i)).login(user(i), password), boardId, stats); await peer.open(); crowd.push(peer); }
let crowding = LOAD > 0;
const crowdLoops = crowd.map(async peer => {
  while (crowding) {
    await sleep(between(1000, 3000));
    const item = pick(peer.mirror.visible().filter(i => i.kind === "note" || i.kind === "shape"));
    if (!item) continue;
    const x = Math.round(item.x + between(-40, 40)), y = Math.round(item.y + between(-40, 40));
    for (let f = 1; f <= 4; f++) { peer.presence({ dragging: [{ itemId: item.id, x: item.x + ((x - item.x) * f) / 4, y: item.y + ((y - item.y) * f) / 4, width: item.width, height: item.height }] }); await sleep(50); }
    peer.presence({ dragging: [] });
    await peer.apply([{ type: "move", itemId: item.id, x, y, width: item.width, height: item.height }]);
  }
});

// A watched card in view: the one nearest the middle of the screen.
const watched = await page.evaluate(() => {
  const nodes = [...document.querySelectorAll(".vue-flow__node[data-id]")].filter(el => el.querySelector(".board-note, .board-shape-text"));
  const mid = (el) => { const r = el.getBoundingClientRect(); return Math.hypot(r.x + r.width / 2 - 720, r.y + r.height / 2 - 450); };
  return nodes.sort((a, b) => mid(a) - mid(b))[0]?.dataset.id;
});
await page.evaluate(id => window.__watch.set(id, []), watched);

// 1. Remote moves of the watched card: 5 preview frames at 20 Hz (as the canvas sends them), then the batch.
const moves = [];
const remoteFrom = await page.evaluate(() => window.__frames.length);
for (let m = 0; m < MOVES; m++) {
  await sleep(2500);
  const item = mover.mirror.items.get(watched);
  const dx = m % 2 ? -160 : 160;
  const dragStart = now();
  for (let f = 1; f <= 5; f++) { mover.presence({ dragging: [{ itemId: watched, x: item.x + (dx * f) / 5, y: item.y, width: item.width, height: item.height }] }); await sleep(50); }
  mover.presence({ dragging: [] });
  const commit = now();
  const result = await mover.apply([{ type: "move", itemId: watched, x: item.x + dx, y: item.y, width: item.width, height: item.height }]);
  moves.push({ dragStart, commit, acked: commit + result.ms });
}
await sleep(1500);
const remoteFrames = await page.evaluate(from => window.__frames.slice(from + 1), remoteFrom);
const watch = await page.evaluate(id => window.__watch.get(id), watched);
const ghosts = await page.evaluate(() => window.__ghosts);
const remote = moves.map(move => {
  const boxes = watch.filter(s => s.at >= move.commit && s.at <= move.commit + 2000);
  const ghost = ghosts.find(g => g.at >= move.dragStart && g.n > 0);
  return { ghostMs: ghost ? ghost.at - move.dragStart : NaN, firstMs: boxes.length ? boxes[0].at - move.commit : NaN, boxes: boxes.length, spanMs: boxes.length > 1 ? boxes.at(-1).at - boxes[0].at : 0, ackMs: move.acked - move.commit };
});

// 2. Dragging a card myself, 3. panning (Space + drag) and 4. zooming (wheel).
const box = await page.locator(`.vue-flow__node[data-id="${watched}"]`).boundingBox();
const drag = await framesOf(async () => {
  for (let r = 0; r < 3; r++) {
    await page.mouse.move(box.x + box.width / 2, box.y + 12); await page.mouse.down();
    for (let s = 1; s <= 40; s++) { await page.mouse.move(box.x + box.width / 2 + s * 3, box.y + 12 + s * 2); await page.waitForTimeout(16); }
    await page.mouse.up(); await page.waitForTimeout(400);
  }
});
await page.keyboard.press("Control+z").catch(() => {});
const surface = await page.locator("[data-board-surface]").boundingBox();
const cx = surface.x + surface.width / 2, cy = surface.y + surface.height / 2;
const pan = await framesOf(async () => {
  await page.locator("[data-board-surface]").focus();
  await page.mouse.move(cx, cy - 300);
  await page.keyboard.down("Space"); await page.mouse.down();
  for (let s = 1; s <= 60; s++) { await page.mouse.move(cx - s * 6, cy - 300 - s * 2); await page.waitForTimeout(16); }
  await page.mouse.up(); await page.keyboard.up("Space");
});
const zoom = await framesOf(async () => { for (let s = 0; s < 16; s++) { await page.mouse.wheel(0, s < 8 ? -100 : 100); await page.waitForTimeout(40); } await page.waitForTimeout(300); });
crowding = false;
await Promise.all(crowdLoops);
const idle = await framesOf(() => page.waitForTimeout(3000));

const result = {
  boardId, load: LOAD, nodes: await page.locator(".vue-flow__node").count(), watched,
  remote: { moves: remote.length, ghostMs: percentiles(remote.map(r => r.ghostMs).filter(Number.isFinite)), firstMs: percentiles(remote.map(r => r.firstMs).filter(Number.isFinite)),
    boxes: percentiles(remote.map(r => r.boxes)), spanMs: percentiles(remote.map(r => r.spanMs)), ackMs: percentiles(remote.map(r => r.ackMs)), glided: remote.filter(r => r.boxes >= 4).length, raw: remote,
    timeline: { t0: moves[0]?.dragStart, commits: moves.map(m => Math.round(m.commit - moves[0].dragStart)), boxes: watch.map(s => [Math.round(s.at - moves[0].dragStart), s.key]), ghosts: ghosts.filter(g => g.at >= moves[0].dragStart).map(g => [Math.round(g.at - moves[0].dragStart), g.n]) } },
  remoteFrames: summary(remoteFrames), drag: summary(drag), pan: summary(pan), zoom: summary(zoom), idle: summary(idle),
};
console.log(JSON.stringify(result, null, 1));
if (process.env.OUT) writeFileSync(process.env.OUT, JSON.stringify(result, null, 1));
await mover.close(); await Promise.all(crowd.map(p => p.close()));
await browser.close();
process.exit(0);
