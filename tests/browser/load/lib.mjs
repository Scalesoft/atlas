// Shared protocol clients of the manual collaboration load test (tests/browser/README.md, „Zátěž více uživatelů“): one cookie session per simulated person, the
// web's board batch API and /hubs/live view session, and the editor's V1 Yjs relay on /hubs/markdown. Everything here speaks
// exactly what the web client sends, so the load reaches the real server paths.
import { randomUUID } from "node:crypto";
import * as signalR from "@microsoft/signalr";
import * as Y from "yjs";

export const BASE = process.env.ATLAS_URL ?? "http://127.0.0.1:5141";
export const now = () => performance.timeOrigin + performance.now();
export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
export const pick = list => list[Math.floor(Math.random() * list.length)];
export const between = (min, max) => min + Math.random() * (max - min);

export function percentiles(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const at = p => (sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))] : NaN);
  return { n: sorted.length, p50: at(50), p95: at(95), p99: at(99), max: sorted.at(-1) ?? NaN };
}
export const fmt = s => `n=${s.n} p50=${s.p50?.toFixed(0)} p95=${s.p95?.toFixed(0)} p99=${s.p99?.toFixed(0)} max=${s.max?.toFixed(0)} ms`;

/** One signed-in person: cookies, CSRF and the two hubs. */
export class Session {
  constructor(name) { this.name = name; this.cookies = new Map(); this.csrf = undefined; }
  cookieHeader() { return [...this.cookies].map(([k, v]) => `${k}=${v}`).join("; "); }
  remember(response) {
    for (const line of response.headers.getSetCookie?.() ?? []) {
      const [pair] = line.split(";"); const eq = pair.indexOf("=");
      const key = pair.slice(0, eq).trim(), value = pair.slice(eq + 1).trim();
      if (/expires=Thu, 01 Jan 1970/i.test(line) || value === "") this.cookies.delete(key); else this.cookies.set(key, value);
    }
  }
  async raw(method, path, body, extra = {}) {
    if (method !== "GET" && !this.csrf) await this.refreshCsrf();
    const headers = { cookie: this.cookieHeader(), ...extra };
    if (body !== undefined) headers["content-type"] = "application/json";
    if (method !== "GET") headers["X-CSRF-TOKEN"] = this.csrf;
    const response = await fetch(BASE + path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), redirect: "manual" });
    this.remember(response);
    return response;
  }
  async refreshCsrf() {
    const response = await fetch(BASE + "/api/auth/csrf", { headers: { cookie: this.cookieHeader() } });
    this.remember(response);
    this.csrf = (await response.json()).token;
    return this.csrf;
  }
  async json(method, path, body) {
    const response = await this.raw(method, path, body);
    const text = await response.text();
    if (!response.ok) { const error = new Error(`${method} ${path} → ${response.status} ${text.slice(0, 300)}`); error.status = response.status; error.body = text; throw error; }
    return text ? JSON.parse(text) : undefined;
  }
  async developer() { await this.refreshCsrf(); this.me = await this.json("POST", "/api/auth/developer"); await this.refreshCsrf(); return this; }
  async login(username, password) {
    await this.refreshCsrf();
    this.me = await this.json("POST", "/api/auth/login", { username, password });
    await this.refreshCsrf();
    return this;
  }
  hub(path, query = "") {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(BASE + path + query, { headers: { cookie: this.cookieHeader(), origin: BASE }, transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true })
      .configureLogging(signalR.LogLevel.None)
      .build();
    connection.serverTimeoutInMilliseconds = 60_000;
    return connection;
  }
}

/** Creates (once) and signs in the load accounts c2-load-01 … on a copy; activation through the admin API. */
export async function loadAccounts(admin, count, password, prefix = process.env.ATLAS_LOAD_PREFIX ?? "c2-load-") {
  const users = await admin.json("GET", "/api/admin/users");
  const accounts = [];
  for (let i = 1; i <= count; i++) {
    const username = `${prefix}${String(i).padStart(2, "0")}`;
    let user = users.find(u => u.username === username);
    let token;
    if (!user) {
      const created = await admin.json("POST", "/api/admin/users", { username, displayName: `Zátěž ${String(i).padStart(2, "0")}`, isWikiAdmin: false });
      user = created.user; token = created.token;
    } else if (!(await new Session(username).login(username, password).then(() => true, () => false))) {
      // A password reset needs a fresh sign-in (reauth), which a script cannot give: use another ATLAS_LOAD_PREFIX.
      throw new Error(`${username} exists but does not sign in with ATLAS_LOAD_PASSWORD; set another ATLAS_LOAD_PREFIX`);
    }
    for (; token;) {
      const response = await new Session("activate").raw("POST", "/api/auth/activate", { token, newPassword: password });
      if (response.status === 429) { await sleep(5000); continue; }
      if (!response.ok) throw new Error(`activate ${username}: ${response.status} ${await response.text()}`);
      break;
    }
    accounts.push({ username, id: user.id });
  }
  return accounts;
}

export async function grant(admin, spaceId, userId, role = "Writer") {
  const access = await admin.json("GET", `/api/spaces/${spaceId}/access`);
  const current = (access.grants ?? access.assignments ?? []).find(g => (g.userId ?? g.subjectId) === userId);
  if (current && (current.role === role)) return;
  await admin.json("PUT", `/api/spaces/${spaceId}/access`, { userId, role, expectedVersion: current?.version ?? null });
}

// ---- boards ----

const geometryOf = ({ x, y, width, height }) => ({ x, y, width, height });
/** The web's confirmed board model (boardModel.ts applyChange) on the server's batches, for comparing with GET /api/boards/{id}. */
export class BoardMirror {
  constructor(dto) {
    this.sequence = dto.sequence;
    this.items = new Map(dto.items.map(item => [item.id, { id: item.id, kind: item.kind, ...geometryOf(item), z: item.z, text: item.text ?? undefined,
      color: item.color ?? undefined, parent: item.parent ?? undefined, from: item.from ?? undefined, to: item.to ?? undefined, props: item.props ?? undefined }]));
    this.unknown = 0;
  }
  apply(changes) {
    for (const change of changes) {
      if (change.type === "create") { const i = change.item; this.items.set(i.id, { id: i.id, kind: i.kind, ...geometryOf(i), z: i.z, text: i.text, color: i.color, parent: undefined, from: i.from, to: i.to, props: i.props }); continue; }
      if (change.type === "rename" || change.type === "settings") continue;
      const item = this.items.get(change.itemId);
      if (!item || (item.deleted && change.type !== "restore")) { this.unknown++; continue; }
      switch (change.type) {
        case "move": Object.assign(item, geometryOf(change)); break;
        case "set": if (change.text !== undefined) item.text = change.text; if (change.color) item.color = change.color; else if (change.color === "") item.color = undefined;
          if (change.props) item.props = { ...item.props, ...change.props }; break;
        case "reorder": item.z = change.z; break;
        case "parent": item.parent = change.parentId ?? undefined; break;
        case "delete": item.deleted = true; break;
        case "restore": item.deleted = false; break;
      }
    }
  }
  /** What the server's read shows: live items, connectors only with both ends alive. */
  visible() {
    const alive = id => { const item = id && this.items.get(id); return item && !item.deleted; };
    return [...this.items.values()].filter(i => !i.deleted && (i.kind !== "connector" || (alive(i.from) && alive(i.to))));
  }
}
const round = v => Math.round(v * 100) / 100;
export const itemKey = i => `${i.id}|${i.kind}|${round(i.x)}|${round(i.y)}|${round(i.width)}|${round(i.height)}|${i.z}|${i.text ?? ""}|${i.color ?? ""}|${i.parent ?? ""}`;
export function diffBoards(expected, actual) {
  const a = new Map(expected.map(i => [i.id, itemKey(i)])), b = new Map(actual.map(i => [i.id, itemKey(i)]));
  const missing = [...a.keys()].filter(id => !b.has(id)), extra = [...b.keys()].filter(id => !a.has(id));
  const differ = [...a.keys()].filter(id => b.has(id) && a.get(id) !== b.get(id));
  return { missing, extra, differ, same: !missing.length && !extra.length && !differ.length, sample: differ.slice(0, 3).map(id => [a.get(id), b.get(id)]) };
}

/** A person on a board: the view session board:{id} as the web joins it, the mirror, and what arrived when. */
export class BoardPeer {
  constructor(session, boardId, stats) { this.session = session; this.boardId = boardId; this.stats = stats; this.key = `board:${boardId}`; this.seen = new Map(); this.gaps = 0; this.resyncs = 0; }
  async open() {
    this.mirror = new BoardMirror(await this.session.json("GET", `/api/boards/${this.boardId}`));
    this.hub = this.session.hub("/hubs/live");
    this.hub.on("ObjectChanged", message => this.onChanged(message));
    this.hub.on("ObjectPresence", () => { this.stats.presenceIn = (this.stats.presenceIn ?? 0) + 1; });
    this.hub.onclose(() => { this.closed = true; });
    await this.hub.start();
    await this.join();
  }
  async join() {
    await this.hub.invoke("JoinView", this.key);
    await this.hub.invoke("SetViewport", this.key, [this.boardId]);
    await this.catchUp();
  }
  async catchUp() {
    for (;;) {
      const response = await this.session.raw("GET", `/api/boards/${this.boardId}/operations?after=${this.mirror.sequence}`);
      if (response.status === 409) { this.resyncs++; this.mirror = new BoardMirror(await this.session.json("GET", `/api/boards/${this.boardId}`)); return; }
      if (!response.ok) throw new Error(`operations ${response.status}`);
      const log = await response.json();
      for (const batch of log.batches ?? log.operations ?? []) if (batch.sequence === this.mirror.sequence + 1) { this.mirror.apply(batch.changes); this.mirror.sequence = batch.sequence; }
      if (this.mirror.sequence >= log.sequence) return;
    }
  }
  onChanged(message) {
    if (message.objectId?.toLowerCase() !== this.boardId || !message.change) return;
    const batch = message.change;
    const at = now();
    if (!this.seen.has(batch.operationId)) this.seen.set(batch.operationId, at);
    this.stats.fanout = (this.stats.fanout ?? 0) + 1;
    if (batch.sequence <= this.mirror.sequence) return;
    if (batch.sequence !== this.mirror.sequence + 1) {
      this.gaps++;
      this.pendingCatchUp ??= this.catchUp().catch(() => {}).finally(() => { this.pendingCatchUp = undefined; });
      return;
    }
    this.mirror.apply(batch.changes);
    this.mirror.sequence = batch.sequence;
  }
  /** POSTs one gesture; returns { ok, status, ms }. */
  async apply(changes, operationId = randomUUID()) {
    const start = now();
    this.stats.sent?.set(operationId, { at: start, author: this.session.name });
    const response = await this.session.raw("POST", `/api/boards/${this.boardId}/operations`, { operationId, changes });
    const ms = now() - start;
    const body = await response.text();
    return { ok: response.ok, status: response.status, ms, operationId, body };
  }
  /** PRESENCE=0 leaves the drag previews out (to tell their cost from the batches' one). */
  presence(payload) { if (process.env.PRESENCE !== "0" && this.hub?.state === "Connected") void this.hub.send("SendObjectPresence", this.boardId, payload).catch(() => {}); }
  async close() { try { await this.hub?.stop(); } catch { /* gone */ } }
}

// ---- documents (V1 relay, what createSignalRMarkdownAdapter speaks) ----

const b64 = bytes => Buffer.from(bytes).toString("base64");
const unb64 = text => new Uint8Array(Buffer.from(text, "base64"));
export const REMOTE = Symbol("remote");

/** Block texts of a y-prosemirror fragment, in the same shape as textblocksOf() of a ProseMirror document. */
export function yBlocks(fragment) {
  const out = [];
  const walk = node => {
    const texts = node.toArray().filter(child => child instanceof Y.XmlText);
    if (texts.length || (node instanceof Y.XmlElement && node.length === 0 && /paragraph|heading/.test(node.nodeName))) {
      out.push(node.toArray().map(child => child instanceof Y.XmlText ? child.toDelta().map(d => typeof d.insert === "string" ? d.insert : "").join("") : "").join(""));
      return;
    }
    for (const child of node.toArray()) if (child instanceof Y.XmlElement) walk(child);
  };
  walk(fragment);
  return out;
}

export class DocPeer {
  constructor(session, documentId, schema, stats) {
    this.session = session; this.documentId = documentId; this.schema = schema; this.stats = stats;
    this.ydoc = new Y.Doc(); this.fragment = this.ydoc.getXmlFragment("default");
    this.origin = { peer: session.name };
    this.undo = new Y.UndoManager(this.fragment, { trackedOrigins: new Set([this.origin]), captureTimeout: 500 });
    this.seq = 0; this.outbox = []; this.sending = false; this.online = false; this.snapshotterTimes = 0;
    this.ydoc.on("update", (update, origin) => { if (origin !== REMOTE) { this.outbox.push(update); void this.flush(); } });
  }
  async open() {
    this.hub = this.session.hub("/hubs/markdown");
    this.hub.on("ReceiveUpdate", (docId, update, seq) => {
      if (docId !== this.documentId) return;
      Y.applyUpdate(this.ydoc, unb64(update), REMOTE);
      this.seq = Math.max(this.seq, seq);
      this.stats.received = (this.stats.received ?? 0) + 1;
      this.onReceive?.(update, seq);
    });
    this.hub.on("ReceiveAwareness", () => {});
    this.hub.on("RequestSnapshot", () => { this.stats.snapshotRequests = (this.stats.snapshotRequests ?? 0) + 1; });
    this.hub.on("SnapshotterChanged", (docId, mine) => { if (mine) void this.handOff(); });
    this.hub.on("FlushDegraded", (docId, degraded) => { if (degraded) this.stats.flushDegraded = (this.stats.flushDegraded ?? 0) + 1; });
    this.hub.on("DocumentAccessChanged", (docId, canRead, canWrite) => { this.canWrite = canWrite; this.canRead = canRead; });
    this.hub.on("SnapshotStored", () => { this.stats.snapshotStored = (this.stats.snapshotStored ?? 0) + 1; });
    this.hub.on("ExternalEditArchived", () => { this.stats.externalArchived = (this.stats.externalArchived ?? 0) + 1; });
    this.hub.onclose(() => { this.online = false; });
    await this.hub.start();
    await this.join();
  }
  async join() {
    const result = await this.hub.invoke("JoinDocumentWithSchema", this.documentId, this.schema);
    this.ydoc.transact(() => {
      if (result.snapshot) Y.applyUpdate(this.ydoc, unb64(result.snapshot), REMOTE);
      for (const update of result.updates ?? []) Y.applyUpdate(this.ydoc, unb64(update), REMOTE);
    }, REMOTE);
    this.seq = result.seq; this.canWrite = result.canWrite; this.online = true;
    if (result.isSnapshotter) await this.handOff();
    void this.flush();
    return result;
  }
  /** A bot renders no projection: when elected snapshotter it rejoins, which puts it last and gives the role to an editor. */
  async handOff() {
    this.snapshotterTimes++;
    if (this.handingOff) return; this.handingOff = true;
    try { await this.hub.invoke("LeaveDocument", this.documentId); this.online = false; await sleep(200); await this.join(); } finally { this.handingOff = false; }
  }
  async flush() {
    if (this.sending || !this.online) return;
    this.sending = true;
    try {
      while (this.outbox.length && this.online) {
        const update = this.outbox.length > 1 ? Y.mergeUpdates(this.outbox.splice(0)) : this.outbox.shift();
        const start = now();
        try {
          const payload = b64(update);
          this.onSend?.(payload);
          await this.hub.invoke("SendUpdate", this.documentId, randomUUID(), payload);
          this.stats.ackMs?.push(now() - start);
          this.stats.sentUpdates = (this.stats.sentUpdates ?? 0) + 1;
        } catch (error) {
          this.stats.sendErrors = (this.stats.sendErrors ?? 0) + 1;
          this.stats.lastError = String(error?.message ?? error).slice(0, 200);
          this.outbox.unshift(update);
          if (!this.online || this.hub.state !== "Connected") break;
          await sleep(500);
        }
      }
    } finally { this.sending = false; }
  }
  /** Goes offline: drops the connection; edits keep collecting in the outbox (a browser locks instead; a bot keeps them). */
  async disconnect() { this.online = false; await this.hub.stop().catch(() => {}); }
  async reconnect() { await this.open(); }
  textblocks() {
    const blocks = [];
    const walk = node => { for (const child of node.toArray()) if (child instanceof Y.XmlElement) { const texts = child.toArray().filter(c => c instanceof Y.XmlText); if (texts.length) blocks.push(child); else walk(child); } };
    walk(this.fragment);
    return blocks;
  }
  /** Types the text at a word boundary of a textblock at cps characters a second, one keystroke per transaction. The caret is a
   * relative position, as an editor keeps it: it follows the others' edits around it, so a word stays in one piece. */
  async typeAt(block, text, cps = 4, keepGoing = () => true) {
    const xmlText = block.toArray().find(c => c instanceof Y.XmlText); if (!xmlText) return false;
    const plain = xmlText.toDelta().map(d => (typeof d.insert === "string" ? d.insert : " ")).join("");
    let at = Math.floor(Math.random() * (xmlText.length + 1));
    while (at < xmlText.length && plain[at] !== " ") at++;
    // Associated with the character before the caret (assoc -1), so text someone inserts right there goes after my word, not into it.
    let rel = Y.createRelativePositionFromTypeIndex(xmlText, at, -1);
    for (const ch of text) {
      if (!keepGoing()) return false;
      const pos = Y.createAbsolutePositionFromRelativePosition(rel, this.ydoc);
      if (!pos || !xmlText.doc) return false;
      this.ydoc.transact(() => xmlText.insert(pos.index, ch), this.origin);
      rel = Y.createRelativePositionFromTypeIndex(xmlText, pos.index + 1, -1);
      this.stats.typed = (this.stats.typed ?? 0) + 1;
      await sleep(1000 / cps);
    }
    return true;
  }
  bold(block) {
    const xmlText = block.toArray().find(c => c instanceof Y.XmlText);
    if (!xmlText || xmlText.length < 4) return;
    const at = Math.floor(Math.random() * (xmlText.length - 3));
    this.ydoc.transact(() => xmlText.format(at, 3, { bold: {} }), this.origin);
  }
  undoLast() { this.undo.stopCapturing(); this.undo.undo(); }
  async close() { try { await this.hub?.invoke("LeaveDocument", this.documentId); } catch { /* gone */ } try { await this.hub?.stop(); } catch { /* gone */ } }
}

/** Process CPU/memory of the server and PostgreSQL load, sampled while a scenario runs. */
export async function sampleServer(pid, query) {
  const sample = { at: now() };
  if (pid) {
    const { execFileSync } = await import("node:child_process");
    try {
      const out = execFileSync("powershell", ["-NoProfile", "-Command", `$p=Get-Process -Id ${pid}; "$($p.CPU)|$($p.WorkingSet64)|$($p.PrivateMemorySize64)|$($p.Threads.Count)"`], { encoding: "utf8" }).trim();
      const [cpu, ws, priv, threads] = out.split("|").map(Number);
      Object.assign(sample, { cpu, ws, priv, threads });
    } catch { /* gone */ }
  }
  if (query) Object.assign(sample, await query());
  return sample;
}

export function psql(sql) {
  return import("node:child_process").then(({ execFileSync }) => execFileSync(process.env.PSQL ?? "psql", ["-h", process.env.PGHOST ?? "127.0.0.1", "-p", process.env.PGPORT ?? "5432", "-U", process.env.PGUSER ?? "postgres",
    "-d", process.env.PGDATABASE ?? "atlas", "-tA", "-F", "|", "-c", sql], { encoding: "utf8", env: process.env }).trim());
}
export async function dbLoad() {
  const [conns, active, waiting] = (await psql(`select count(*), count(*) filter (where state='active'), count(*) filter (where wait_event_type='Lock') from pg_stat_activity where datname=current_database()`)).split("|").map(Number);
  const outbox = Number(await psql(`select count(*) from "OutboxMessage"`).catch(() => "NaN"));
  return { conns, active, lockWaits: waiting, outbox };
}
