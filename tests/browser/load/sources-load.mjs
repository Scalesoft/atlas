// Manual load test (tests/browser/README.md, „Zátěž více uživatelů“), „Zdroje pod tabulí“: N bots work on ONE board while an editor changes what its cards
// show: directory contacts (rename, trash, restore, purge), pages (text and publish, rename, move, trash, restore), a page with a
// contact component (the entity catalog, search, mention notifications), the same page edited from its editor while the bots
// drag its card, and a bot losing its space grant mid-session. Measures how fast every client hears of it (bots: ObjectChanged;
// browsers: the card's text on screen), that unavailable cards stay clean, that derived data follows, and server health.
//
//   ATLAS_URL=… ATLAS_LOAD_PASSWORD=… ATLAS_LOAD_SPACE=<space id> node sources-load.mjs   (BOTS=30, BROWSERS=2, ROUNDS=3, OUT)
import { randomUUID } from "node:crypto";
import { writeFileSync } from "node:fs";
import { chromium } from "@playwright/test";
import { BASE, BoardPeer, DocPeer, Session, between, dbLoad, diffBoards, now, percentiles, pick, psql, sampleServer, sleep, yBlocks } from "./lib.mjs";

const BOTS = Number(process.env.BOTS ?? 30), BROWSERS = Number(process.env.BROWSERS ?? 2), ROUNDS = Number(process.env.ROUNDS ?? 3);
const password = process.env.ATLAS_LOAD_PASSWORD, prefix = process.env.ATLAS_LOAD_PREFIX ?? "c2-load-", spaceId = process.env.ATLAS_LOAD_SPACE;
if (!password || !spaceId) throw new Error("Set ATLAS_LOAD_PASSWORD and ATLAS_LOAD_SPACE.");
const user = i => `${prefix}${String(i).padStart(2, "0")}`;
const run = Date.now().toString(36).slice(-5);
const log = (...args) => console.log(new Date().toISOString().slice(11, 19), ...args);
const admin = await new Session("admin").developer();

// ---- sources ----
const paragraph = text => ({ type: "paragraph", content: [{ type: "text", text }] });
const PLUGINS = [{ id: "contacts", version: 2 }];
const rich = (...blocks) => JSON.stringify({ format: "atlas-rich", version: 1, plugins: PLUGINS, document: { type: "doc", content: blocks } });
const contactBlock = (name, mention) => ({ type: "contactCard", attrs: { data: { version: 1, contacts: [{ name, ...(mention && { internalUser: { id: mention.id, name: mention.name } }) }] } } });
async function createPage(title, ...blocks) {
  return admin.json("POST", "/api/pages", { spaceId, title, parentId: null, contentJson: rich(...blocks), markdown: null });
}
const mentioned = (await admin.json("GET", "/api/admin/users")).find(u => u.username === user(BOTS));
const pageWithContact = await createPage(`C2 zdroj kontakty ${run}`, paragraph("Stránka s kontaktem pro tabuli."), contactBlock(`C2 Osoba ${run} původní`), paragraph("Konec."));
const pageCard = await createPage(`C2 zdroj karta ${run}`, paragraph("Karta stránky: první verze."));
const pagePreview = await createPage(`C2 zdroj náhled ${run}`, paragraph("Náhled stránky: první verze."), paragraph("Druhý odstavec."));
const pageMoved = await createPage(`C2 zdroj přesun ${run}`, paragraph("Stránka, která se přesouvá a jde do koše."));
const parentPage = await createPage(`C2 zdroj rodič ${run}`, paragraph("Nový rodič."));
// The entity catalog, search and mentions follow a page's publication; this one is not open in an editor, so it can be saved directly.
const pageCatalog = await createPage(`C2 zdroj katalog ${run}`, paragraph("Stránka s kontaktem pro katalog."), contactBlock(`C2 Osoba ${run} původní`));
for (const page of [pageWithContact, pageCard, pagePreview, pageMoved, parentPage, pageCatalog]) await admin.json("POST", `/api/pages/${page.id}/publish`, { expectedVersion: page.version });
const contactA = await admin.json("POST", "/api/objects", { typeId: "atlas.contact", scope: "wiki", data: { name: `C2 Kontakt A ${run}` } });
const contactB = await admin.json("POST", "/api/objects", { typeId: "atlas.contact", scope: "wiki", data: { name: `C2 Kontakt B ${run}` } });
const board = await admin.json("POST", `/api/spaces/${spaceId}/boards`, { title: `C2 zdroje ${run}` });
const card = {}; const changes = []; let z = 1;
const place = (key, item) => { const id = randomUUID(); card[key] = id; changes.push({ type: "create", item: { id, z: z++, ...item } }); };
place("contactPage", { kind: "page", x: 0, y: 0, width: 320, height: 420, targetResourceId: pageWithContact.id, props: { display: "preview" } });
place("pageCard", { kind: "page", x: 360, y: 0, width: 260, height: 150, targetResourceId: pageCard.id, props: { display: "card" } });
place("pagePreview", { kind: "page", x: 660, y: 0, width: 320, height: 420, targetResourceId: pagePreview.id, props: { display: "preview" } });
place("pageMoved", { kind: "page", x: 1020, y: 0, width: 260, height: 150, targetResourceId: pageMoved.id, props: { display: "card" } });
place("catalogPage", { kind: "page", x: 1320, y: 0, width: 320, height: 420, targetResourceId: pageCatalog.id, props: { display: "preview" } });
place("contactA", { kind: "object", x: 360, y: 200, width: 280, height: 160, targetResourceId: contactA.id, props: { objectId: contactA.id, typeId: "atlas.contact", view: "card" } });
place("contactB", { kind: "object", x: 1020, y: 200, width: 280, height: 160, targetResourceId: contactB.id, props: { objectId: contactB.id, typeId: "atlas.contact", view: "card" } });
for (let i = 0; i < 60; i++) place(`note${i}`, { kind: "note", x: (i % 10) * 170, y: 500 + Math.floor(i / 10) * 170, width: 150, height: 150, text: `Poznámka ${i}`, color: "yellow", props: { fontSize: "auto" } });
await admin.json("POST", `/api/boards/${board.id}/operations`, { operationId: randomUUID(), changes });
const objectIds = [pageWithContact.id, pageCard.id, pagePreview.id, pageMoved.id, pageCatalog.id, contactA.id, contactB.id];
log(`board ${board.id} with ${changes.length} items over 4 pages and 2 contacts`);

// ---- people ----
const stats = { sent: new Map(), status: {} };
const heard = new Map(); // objectId → [{ at, who }]
const peers = [];
for (let i = 1; i <= BOTS; i++) {
  const peer = new BoardPeer(await new Session(user(i)).login(user(i), password), board.id, stats);
  await peer.open();
  await peer.hub.invoke("SetViewport", peer.key, [board.id, ...objectIds]);
  peer.hub.on("ObjectChanged", message => { const id = message.objectId?.toLowerCase(); if (id !== board.id) (heard.get(id) ?? heard.set(id, []).get(id)).push({ at: now(), who: peer.session.name, detail: !!message.change }); });
  peer.hub.on("ObjectRevoked", message => { (stats.revoked ??= []).push({ at: now(), who: peer.session.name, objectId: message.objectId }); });
  peers.push(peer);
}
const browser = BROWSERS ? await chromium.launch({ headless: true }) : undefined;
const screens = [];
for (let b = 0; b < BROWSERS; b++) {
  const name = user(BOTS + 1 + b);
  const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 } });
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  await context.request.post("/api/auth/login", { data: { username: name, password }, headers: { "X-CSRF-TOKEN": csrf.token } });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", error => errors.push(String(error).slice(0, 200)));
  page.on("console", message => { if (message.type() === "error" && !/Failed to load resource/.test(message.text())) errors.push(message.text().slice(0, 200)); });
  await page.goto(`/#/boards/${board.id}`);
  await page.locator(".vue-flow__node").first().waitFor({ timeout: 30_000 });
  screens.push({ page, context, name, errors });
}
// The page with the contact is also open in its editor (the last browser), edited while the bots drag its card.
let editorPage;
if (BROWSERS) {
  const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1280, height: 900 } });
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  await context.request.post("/api/auth/login", { data: { username: user(BOTS + 1 + BROWSERS), password }, headers: { "X-CSRF-TOKEN": csrf.token } });
  editorPage = await context.newPage();
  await editorPage.goto(`/#/pages/${pageWithContact.id}`);
  await editorPage.getByRole("button", { name: "Upravit", exact: true }).click();
  await editorPage.locator('.tiptap:not(.comment-composer *)[contenteditable="true"]').first().waitFor({ timeout: 60_000 });
  screens.push({ page: editorPage, context, name: "editor", errors: [], editor: true });
}
log(`${peers.length} bots, ${screens.length} browsers`);

/** Waits until a card on every board screen shows `text` (or none does, with `absent`); returns ms per screen. */
async function cardShows(key, text, { absent = false, timeout = 15_000 } = {}) {
  return Promise.all(screens.filter(s => !s.editor).map(async ({ page }) => {
    const start = now();
    try {
      await page.waitForFunction(([id, wanted, gone]) => { const el = document.querySelector(`.vue-flow__node[data-id="${id}"]`); const shown = el?.textContent ?? ""; return gone ? !shown.includes(wanted) : shown.includes(wanted); }, [card[key], text, absent], { timeout, polling: 50 });
      return now() - start;
    } catch { return NaN; }
  }));
}
const cardState = (key) => Promise.all(screens.filter(s => !s.editor).map(({ page }) => page.evaluate(id => {
  const el = document.querySelector(`.vue-flow__node[data-id="${id}"]`); if (!el) return { present: false };
  const node = el.querySelector(".board-node");
  return { present: true, missing: node?.classList.contains("is-missing") ?? false, text: (el.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 80) };
}, card[key])));
const botsHeard = (objectId, since) => { const list = (heard.get(objectId) ?? []).filter(h => h.at >= since); const who = new Set(list.map(h => h.who)); return { bots: who.size, ms: list.length ? percentiles(list.map(h => h.at - since)) : null }; };

// ---- background work: bots drag notes and the source cards, the doc bot types into the page from its editor side ----
let running = true;
const moveLoops = peers.map(async (peer, index) => {
  while (running) {
    await sleep(between(1000, 3000));
    if (!running || peer.revoked) break;
    const items = peer.mirror.visible().filter(i => i.kind !== "connector");
    const item = Math.random() < 0.3 ? peer.mirror.items.get(pick([card.contactPage, card.pageCard, card.contactA])) : pick(items);
    if (!item || item.deleted) continue;
    const r = await peer.apply([{ type: "move", itemId: item.id, x: Math.round(item.x + between(-30, 30)), y: Math.round(item.y + between(-30, 30)), width: item.width, height: item.height }]);
    stats.status[r.status] = (stats.status[r.status] ?? 0) + 1;
  }
});
const docSchema = JSON.parse(await psql(`select "SchemaJson" from "PageCollaboration" where "PageId"='${pageWithContact.id}'`) || "null");
let docBot;
const docTokens = [];
if (docSchema) {
  const session = await new Session(user(1)).login(user(1), password);
  const lease = await session.json("POST", `/api/pages/${pageWithContact.id}/collaboration`);
  docBot = new DocPeer(session, lease.documentId, docSchema, {});
  await docBot.open();
}
const docLoop = (async () => {
  let w = 0;
  while (running && docBot) {
    const blocks = docBot.textblocks(); const token = ` d${++w}q${run}`;
    if (await docBot.typeAt(blocks[0], token, 4, () => running)) docTokens.push(token.trim());
    await sleep(500);
  }
})();
const editorLoop = (async () => {
  let w = 0; const typed = [];
  if (!editorPage) return typed;
  await editorPage.locator('.tiptap:not(.comment-composer *) p').last().click();
  while (running) { const token = ` e${++w}q${run}`; typed.push(token.trim()); await editorPage.keyboard.type(token, { delay: 220 }); await editorPage.waitForTimeout(600); }
  return typed;
})();
const samples = [];
const sampler = (async () => { while (running) { samples.push(await sampleServer(process.env.ATLAS_SERVER_PID, dbLoad).catch(() => ({}))); await sleep(5000); } })();

// ---- the source changes, ROUNDS times ----
const steps = [];
const step = async (name, action, check) => {
  const at = now(); let error;
  try { await action(); } catch (e) { error = String(e.message ?? e).slice(0, 200); }
  const shown = check ? await check(at) : undefined;
  steps.push({ name, error, ...shown });
  log(name, error ?? "", JSON.stringify(shown ?? {}).slice(0, 220));
};
const version = async id => (await admin.json("GET", `/api/pages/${id}`)).version;
const treeVersion = async () => (await admin.json("GET", `/api/spaces/${spaceId}/tree`)).version;
const objectVersion = async id => (await admin.json("GET", `/api/objects/${id}`)).version;
await sleep(5000);
for (let round = 1; round <= ROUNDS; round++) {
  const tag = `${run}-${round}`;
  await step(`r${round} contact rename`, async () => admin.json("POST", `/api/objects/${contactA.id}/changes`, { changeId: randomUUID(), expectedVersion: await objectVersion(contactA.id), payload: { fields: { name: `C2 Kontakt A ${tag}` } } }),
    async at => ({ screensMs: await cardShows("contactA", `C2 Kontakt A ${tag}`), heard: botsHeard(contactA.id, at) }));
  await step(`r${round} contact trash`, async () => admin.json("DELETE", `/api/objects/${contactB.id}`),
    async at => ({ screensMs: await cardShows("contactB", `C2 Kontakt B ${run}`, { absent: true }), state: await cardState("contactB"), heard: botsHeard(contactB.id, at) }));
  await step(`r${round} contact restore`, async () => admin.json("POST", `/api/objects/${contactB.id}/restore`),
    async at => ({ screensMs: await cardShows("contactB", `C2 Kontakt B ${run}`), state: await cardState("contactB"), heard: botsHeard(contactB.id, at) }));
  await step(`r${round} page rename`, async () => admin.json("PUT", `/api/pages/${pageCard.id}/title`, { title: `C2 zdroj karta ${tag}`, expectedVersion: await version(pageCard.id) }),
    async at => ({ screensMs: await cardShows("pageCard", `C2 zdroj karta ${tag}`), heard: botsHeard(pageCard.id, at) }));
  await step(`r${round} page edit+publish`, async () => {
    const page = await admin.json("GET", `/api/pages/${pagePreview.id}`);
    const saved = await admin.json("PUT", `/api/pages/${pagePreview.id}`, { title: page.title, contentJson: rich(paragraph(`Náhled stránky: verze ${tag}.`), paragraph("Druhý odstavec.")), markdown: null, expectedVersion: page.version });
    await admin.json("POST", `/api/pages/${pagePreview.id}/publish`, { expectedVersion: saved.version });
  }, async at => ({ screensMs: await cardShows("pagePreview", `verze ${tag}`), heard: botsHeard(pagePreview.id, at) }));
  await step(`r${round} page move`, async () => admin.json("POST", `/api/pages/${pageMoved.id}/move`, { parentId: round % 2 ? parentPage.id : null, beforePageId: null, expectedTreeVersion: await treeVersion() }),
    async at => ({ state: await cardState("pageMoved"), heard: botsHeard(pageMoved.id, at) }));
  await step(`r${round} page trash`, async () => admin.json("POST", `/api/pages/${pageMoved.id}/trash`, { includeSubtree: false, expectedTreeVersion: await treeVersion(), expectedVersion: await version(pageMoved.id) }),
    async at => { await sleep(1500); return { state: await cardState("pageMoved"), heard: botsHeard(pageMoved.id, at) }; });
  await step(`r${round} page restore`, async () => admin.json("POST", `/api/pages/${pageMoved.id}/untrash`, { parentId: null, includeSubtree: false, expectedTreeVersion: await treeVersion() }),
    async at => ({ screensMs: await cardShows("pageMoved", "C2 zdroj přesun"), state: await cardState("pageMoved"), heard: botsHeard(pageMoved.id, at) }));
  // A contact component inside a page: the entity catalog and search follow the published page; a mention tells the account.
  await step(`r${round} page contact + mention`, async () => {
    const page = await admin.json("GET", `/api/pages/${pageCatalog.id}`);
    const doc = JSON.parse(page.contentJson);
    const blocks = doc.document.content.map(block => block.type === "contactCard" ? contactBlock(`C2 Osoba ${tag}`, round === ROUNDS ? { id: mentioned.id, name: mentioned.displayName } : undefined) : block);
    const saved = await admin.json("PUT", `/api/pages/${pageCatalog.id}`, { title: page.title, contentJson: JSON.stringify({ ...doc, plugins: doc.plugins ?? PLUGINS, document: { ...doc.document, content: blocks } }), markdown: null, expectedVersion: page.version });
    await admin.json("POST", `/api/pages/${pageCatalog.id}/publish`, { expectedVersion: saved.version });
  }, async at => {
    const deadline = at + 20_000; let catalogMs = NaN, searchMs = NaN, oldGone = false, noticeMs = NaN;
    while (now() < deadline && (Number.isNaN(catalogMs) || Number.isNaN(searchMs) || (round === ROUNDS && Number.isNaN(noticeMs)))) {
      if (Number.isNaN(catalogMs)) { const list = await admin.json("GET", `/api/entities/contact?q=${encodeURIComponent(`C2 Osoba ${tag}`)}`).catch(() => null); if (JSON.stringify(list ?? {}).includes(`C2 Osoba ${tag}`)) { catalogMs = now() - at; const old = await admin.json("GET", `/api/entities/contact?q=${encodeURIComponent(`C2 Osoba ${run}-${round - 1}`)}`).catch(() => null); oldGone = round === 1 || !JSON.stringify(old ?? {}).includes(`C2 Osoba ${run}-${round - 1}`); } }
      if (Number.isNaN(searchMs)) { const found = await admin.json("GET", `/api/search?q=${encodeURIComponent(`C2 Osoba ${tag}`)}`).catch(() => null); if (JSON.stringify(found ?? {}).includes(pageCatalog.id)) searchMs = now() - at; }
      if (round === ROUNDS && Number.isNaN(noticeMs)) { const n = Number(await psql(`select count(*) from "Notification" where "RecipientId"='${mentioned.id}' and "CreatedAt" > now() - interval '2 minutes'`).catch(() => "0")); if (n > 0) noticeMs = now() - at; }
      await sleep(250);
    }
    return { catalogMs, oldGone, searchMs, ...(round === ROUNDS && { noticeMs }), screensMs: await cardShows("catalogPage", `C2 Osoba ${tag}`), heard: botsHeard(pageCatalog.id, at) };
  });
}
// Losing access mid-session: bot N-1's grant goes; it must stop hearing the board and its cards, and HTTP must refuse it.
const revokedPeer = peers.at(-2);
let afterRevoke = 0;
await step("revoke a space grant", async () => {
  const access = await admin.json("GET", `/api/spaces/${spaceId}/access`);
  const grant = access.grants.find(g => g.userId === revokedPeer.session.me.id);
  await admin.json("PUT", `/api/spaces/${spaceId}/access`, { userId: revokedPeer.session.me.id, role: null, expectedVersion: grant.version });
}, async at => {
  await sleep(4000);
  revokedPeer.revoked = true;
  const seenAt = revokedPeer.seen.size;
  await sleep(6000);
  afterRevoke = revokedPeer.seen.size - seenAt;
  const read = await revokedPeer.session.raw("GET", `/api/boards/${board.id}`);
  const body = await read.text();
  const revokedMessages = (stats.revoked ?? []).filter(r => r.who === revokedPeer.session.name && r.at >= at);
  const write = await revokedPeer.apply([{ type: "move", itemId: card.note0, x: 5, y: 500, width: 150, height: 150 }]);
  return { boardStatus: read.status, leaksTitle: body.includes("C2 zdroj"), revokedMessages: revokedMessages.length, revokedMs: revokedMessages[0] ? revokedMessages[0].at - at : NaN, batchesHeardAfterRevoke: afterRevoke, writeStatus: write.status };
});
// Purge: trash, then purge the contact; its card shows a clean unavailable state.
await step("contact purge", async () => { await admin.json("DELETE", `/api/objects/${contactA.id}`); await admin.json("POST", `/api/objects/${contactA.id}/purge`); },
  async at => { await sleep(3000); return { state: await cardState("contactA"), heard: botsHeard(contactA.id, at), boardStatus: (await admin.raw("GET", `/api/boards/${board.id}`)).status }; });

running = false;
await Promise.all(moveLoops); await docLoop; const editorTokens = await editorLoop; await sampler;
await sleep(6000);

// ---- end state ----
const serverBoard = await admin.json("GET", `/api/boards/${board.id}`);
// What a board opened now shows, against the screens that were open all along: tells a missing live update from a broken card.
let fresh = {};
if (browser) {
  const context = await browser.newContext({ baseURL: BASE, viewport: { width: 1440, height: 900 } });
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  await context.request.post("/api/auth/login", { data: { username: user(BOTS + 1), password }, headers: { "X-CSRF-TOKEN": csrf.token } });
  const page = await context.newPage();
  await page.goto(`/#/boards/${board.id}`);
  await page.locator(".vue-flow__node").first().waitFor({ timeout: 30_000 });
  await page.waitForTimeout(3000);
  const text = id => page.evaluate(id => (document.querySelector(`.vue-flow__node[data-id="${id}"]`)?.textContent ?? "").replace(/\s+/g, " ").trim().slice(0, 60), id);
  for (const key of ["contactA", "contactB", "pageCard", "pageMoved", "catalogPage"]) fresh[key] = { fresh: await text(card[key]), live: (await cardState(key))[0]?.text?.slice(0, 60) };
  await context.close();
}
const diverged = peers.filter(p => !p.revoked).map(p => diffBoards(serverBoard.items, p.mirror.visible())).filter(d => !d.same).length;
let doc = {};
if (docBot) {
  const fresh = new DocPeer(admin, docBot.documentId, docSchema, {}); await fresh.open(); await sleep(1000);
  const text = yBlocks(fresh.fragment).join("\n");
  const editorText = await editorPage.evaluate(() => { const ed = document.querySelector(".tiptap:not(.comment-composer *)").editor; return ed ? ed.state.doc.textBetween(0, ed.state.doc.content.size, "\n") : null; });
  doc = { botTokens: docTokens.length, botLost: docTokens.filter(t => !text.includes(t)).length, editorTokens: editorTokens.length, editorLost: editorTokens.filter(t => !text.includes(t)).length,
    botSame: yBlocks(docBot.fragment).join("\n") === text, editorHasAll: editorText ? [...docTokens, ...editorTokens].every(t => editorText.includes(t)) : null,
    contactBlockKept: fresh.textblocks().length > 0 && JSON.stringify(fresh.fragment.toArray().map(n => n.getAttributes?.())).includes("C2 Osoba") };
  await fresh.close();
}
const result = {
  boardId: board.id, bots: BOTS, browsers: BROWSERS, rounds: ROUNDS, steps, batches: stats.status, boardDiverged: diverged, doc, fresh,
  pageErrors: screens.map(s => ({ name: s.name, errors: [...new Set(s.errors)].slice(0, 5) })),
  server: { cpuPercentOfOneCore: samples.length > 1 ? (100 * (samples.at(-1).cpu - samples[0].cpu)) / ((samples.at(-1).at - samples[0].at) / 1000) : NaN, wsMaxMB: Math.max(...samples.map(s => s.ws ?? 0)) / 2 ** 20,
    dbConnsMax: Math.max(...samples.map(s => s.conns ?? 0)), lockWaitsMax: Math.max(...samples.map(s => s.lockWaits ?? 0)), outboxMax: Math.max(...samples.map(s => s.outbox ?? 0)), outboxEnd: samples.at(-1)?.outbox },
};
console.log(JSON.stringify(result, null, 1));
if (process.env.OUT) writeFileSync(process.env.OUT, JSON.stringify(result, null, 1));
// Give the revoked bot its grant back, so the next run starts equal.
await admin.json("PUT", `/api/spaces/${spaceId}/access`, { userId: revokedPeer.session.me.id, role: "Writer", expectedVersion: null }).catch(() => {});
await docBot?.close(); await Promise.all(peers.map(p => p.close()));
for (const s of screens) await s.context.close();
await browser?.close();
process.exit(0);
