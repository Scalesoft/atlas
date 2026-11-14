// Shared helpers of the upgrade test (seed-old.mjs on the OLD server, verify-new.mjs on the NEW one).
// Runs inside the official Playwright image; playwright comes from tests/browser/node_modules.
import { createRequire } from "node:module";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";

const require = createRequire(new URL("../browser/package.json", import.meta.url));
export const { chromium, request } = require("playwright");

export const BASE = process.env.ATLAS_URL ?? "http://localhost:8080";
export const OUT = process.env.UPGRADE_OUT ?? "/tmp/upgrade";
export const ADMIN = process.env.ATLAS_E2E_USER ?? "browser-admin";
export const PASSWORD = process.env.ATLAS_E2E_PASSWORD ?? "";
if (PASSWORD.length < 12) throw new Error("ATLAS_E2E_PASSWORD is required.");
// Seeded accounts derive their password from the run secret; the harness scrubs it from logs.
// browser-reader is activated by first-run.spec.ts with `${ATLAS_E2E_PASSWORD}-reader`.
export const passwordOf = (username) => (username === ADMIN ? PASSWORD : username === "browser-reader" ? `${PASSWORD}-reader` : `${PASSWORD}-${username}`);

export const sha256 = (value) => createHash("sha256").update(value).digest("hex");
export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export class HttpError extends Error {
  constructor(method, path, status, body) {
    super(`${method} ${path} -> ${status}: ${String(body).slice(0, 400)}`);
    this.status = status;
  }
}

/** One cookie session. Every mutation fetches a fresh CSRF token (it is bound to the signed-in user). */
export class Session {
  constructor(context, username) { this.context = context; this.username = username; }

  static async anonymous() { return new Session(await request.newContext({ baseURL: BASE }), null); }

  static async login(username, password = passwordOf(username)) {
    const session = await Session.anonymous();
    const response = await session.raw("POST", "/api/auth/login", { username, password });
    if (response.status() !== 200) {
      const status = response.status();
      await session.dispose();
      throw new HttpError("POST", `/api/auth/login (${username})`, status, await response.text());
    }
    session.username = username;
    session.user = await response.json();
    return session;
  }

  async csrf() { return (await (await this.context.get("/api/auth/csrf", { timeout: 180_000 })).json()).token; }

  async raw(method, path, data, { multipart } = {}) {
    // Generous timeout: the Podman machine is shared with other pods and may be swapping.
    const options = { failOnStatusCode: false, maxRedirects: 0, timeout: 180_000 };
    if (!["GET", "HEAD"].includes(method)) options.headers = { "X-CSRF-TOKEN": await this.csrf() };
    if (multipart) options.multipart = multipart;
    else if (data !== undefined) options.data = data;
    try {
      return await this.context.fetch(path, { method, ...options });
    } catch (error) {
      // Playwright's call log carries the session cookie and CSRF token; keep them out of reports.
      throw new Error(`${method} ${path}: ${error.name}: ${String(error.message).split("\n")[0]}`);
    }
  }

  /**
   * Opens the ten-minute re-authentication window of this session with the account's password. Since 1.3.0 a security change
   * (a new wiki administrator, the Spravuje level) answers 403 reauth_required without it; older servers never ask.
   */
  async reauth() {
    const challenge = await this.post("/api/security/reauth", { operation: "security" });
    await this.post(`/api/security/reauth/${challenge.id}/password`, { password: passwordOf(this.username) });
  }

  async call(method, path, data, { expect = [200, 201, 204], multipart, reauthenticated = false } = {}) {
    const response = await this.raw(method, path, data, { multipart });
    const status = response.status();
    const text = await response.text();
    if (status === 403 && !reauthenticated && this.username && !expect.includes(403) && text.includes('"reauth_required"')) {
      await this.reauth();
      return this.call(method, path, data, { expect, multipart, reauthenticated: true });
    }
    if (!expect.includes(status)) throw new HttpError(method, path, status, text);
    if (!text) return null;
    try { return JSON.parse(text); } catch { return text; }
  }

  get(path, options) { return this.call("GET", path, undefined, options); }
  post(path, data, options) { return this.call("POST", path, data, options); }
  put(path, data, options) { return this.call("PUT", path, data, options); }
  delete(path, options) { return this.call("DELETE", path, undefined, options); }
  async status(path) { return (await this.raw("GET", path)).status(); }
  async bytes(path) {
    const response = await this.raw("GET", path);
    if (response.status() !== 200) throw new HttpError("GET", path, response.status(), await response.text());
    return response.body();
  }
  async dispose() { await this.context.dispose(); }
}

/** Rich editor document with the Markdown projection the web client would store next to it. */
export function richPage(title, paragraphs, extra = []) {
  const content = [
    { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: title }] },
    ...paragraphs.map((text) => ({ type: "paragraph", content: [{ type: "text", text }] })),
    ...extra.map((node) => node.node),
  ];
  const markdown = [`## ${title}`, ...paragraphs, ...extra.map((node) => node.markdown)].join("\n\n") + "\n";
  // The OLD server stored the format id "scalesoft-rich"; the upgrade test proves it is still read.
  return { contentJson: JSON.stringify({ format: "scalesoft-rich", version: 1, document: { type: "doc", content } }), markdown };
}

// Tiny deterministic ZIP/store writer (same as tests/browser/wiki-import.spec.ts).
export function zip(files) {
  const local = [], central = []; let offset = 0;
  for (const [path, bytes] of Object.entries(files)) {
    const name = Buffer.from(path), header = Buffer.alloc(30), directory = Buffer.alloc(46);
    let crc = 0xffffffff;
    for (const byte of bytes) { crc ^= byte; for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0); }
    crc = (crc ^ 0xffffffff) >>> 0;
    header.writeUInt32LE(0x04034b50, 0); header.writeUInt16LE(20, 4); header.writeUInt16LE(0x800, 6); header.writeUInt16LE(33, 12);
    header.writeUInt32LE(crc, 14); header.writeUInt32LE(bytes.length, 18); header.writeUInt32LE(bytes.length, 22); header.writeUInt16LE(name.length, 26);
    local.push(header, name, bytes);
    directory.writeUInt32LE(0x02014b50, 0); directory.writeUInt16LE(20, 4); directory.writeUInt16LE(20, 6); directory.writeUInt16LE(0x800, 8); directory.writeUInt16LE(33, 14);
    directory.writeUInt32LE(crc, 16); directory.writeUInt32LE(bytes.length, 20); directory.writeUInt32LE(bytes.length, 24); directory.writeUInt16LE(name.length, 28); directory.writeUInt32LE(offset, 42);
    central.push(directory, name); offset += header.length + name.length + bytes.length;
  }
  const directory = Buffer.concat(central), end = Buffer.alloc(22), count = Object.keys(files).length;
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(count, 8); end.writeUInt16LE(count, 10); end.writeUInt32LE(directory.length, 12); end.writeUInt32LE(offset, 16);
  return Buffer.concat([...local, directory, end]);
}

export function zipDirectory(root) {
  const files = {};
  const walk = (dir) => {
    for (const entry of readdirSync(dir).sort()) {
      const path = join(dir, entry);
      if (statSync(path).isDirectory()) walk(path);
      else files[relative(root, path).split("\\").join("/")] = readFileSync(path);
    }
  };
  walk(root);
  return zip(files);
}

/** Wiki.js fixture ZIP from tests/fixtures (the repo is mounted at /repo). */
export const wikiFixture = () => zipDirectory(new URL("../fixtures/wiki-import/wikijs/", import.meta.url).pathname);

export const PIXEL_PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jO1sAAAAASUVORK5CYII=", "base64");

/** Browser login through the real UI (same steps as tests/browser/collaboration.spec.ts). */
export async function browserLogin(browser, username) {
  const context = await browser.newContext({ baseURL: BASE, locale: "cs-CZ", viewport: { width: 1280, height: 900 } });
  const page = await context.newPage();
  page.on("dialog", (dialog) => dialog.accept());
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await page.getByLabel("Heslo", { exact: true }).evaluate((element, value) => {
    element.value = value;
    element.dispatchEvent(new Event("input", { bubbles: true }));
    element.dispatchEvent(new Event("change", { bubbles: true }));
  }, passwordOf(username));
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await page.getByRole("button", { name: /^Hledat ve wiki/ }).waitFor({ timeout: 30_000 });
  return { context, page };
}

/** Opens a page and enters collaborative editing; returns the collaboration lease. */
/** The live editor taking input: not the one still hydrating hidden behind the rendered page (3.5+). */
export const LIVE_EDITOR = '.document-body .ProseMirror[contenteditable="true"]:not(.editor-hydrating *)';

/**
 * Opens a page for editing in either build: Upravit before 3.6; from 3.6 (shipped as 4.0) a writer's page joins by itself and a click
 * into the text edits. The lease is the `POST /api/pages/{id}/collaboration` answer; from 4.0 the page bundle (`?bundle=true`) names the
 * session instead and the editor joins without that request, unless the bundled state is missing or of another generation
 * (tests/browser/editing.ts sessionLease).
 */
export async function startEditing(page, id) {
  let joined = null, bundle = null;
  const seen = (response) => {
    const url = new URL(response.url());
    if (url.pathname === `/api/pages/${id}/collaboration` && response.request().method() === "POST") joined = response;
    else if (url.pathname === `/api/pages/${id}` && url.searchParams.get("bundle") === "true") bundle = response;
  };
  page.on("response", seen);
  try { await openForEditing(page, id); } finally { page.off("response", seen); }
  // A join answers before the editor it opens takes input, so it is seen by now if there was one; it wins over the bundle it replaced.
  if (joined) {
    if (joined.status() !== 200) throw new Error(`collaboration lease ${joined.status()}: ${await joined.text()}`);
    return joined.json();
  }
  const bundled = bundle?.status() === 200 ? (await bundle.json()).bundle?.collaboration : null;
  if (!bundled) throw new Error(`page ${id}: editable without a collaboration lease (no POST .../collaboration, no bundled session)`);
  return { documentId: bundled.documentId, generation: bundled.generation, currentVersion: bundled.currentVersion };
}

async function openForEditing(page, id) {
  await page.goto(`/#/pages/${id}`);
  await page.locator(".ProseMirror").first().waitFor({ timeout: 30_000 });
  const upravit = page.getByRole("button", { name: "Upravit", exact: true });
  await upravit.or(page.locator("[data-edit-lock]")).first().waitFor({ timeout: 30_000 });
  if (await upravit.count()) await upravit.click();
  else await page.locator(".document-body .ProseMirror:not(.editor-hydrating *)").first().locator("p, h1, h2, h3, li").first().click({ position: { x: 3, y: 6 } });
  await page.locator(LIVE_EDITOR).waitFor({ timeout: 30_000 });
}

/**
 * The Markdown of a page as this session reads it. Since 1.1 the web answer of GET /api/pages/{id} (PageView) omits the Markdown
 * projection (the personal API /api/v1 keeps PageDto), so it comes from the history revision of the current version, which is
 * read through the same secret-reference filter. Works against both the old and the new build.
 */
export async function pageMarkdown(session, page) {
  if (typeof page.markdown === "string") return page.markdown;
  const history = await session.get(`/api/pages/${page.id}/history?limit=50`);
  const current = history.items.find((item) => item.version === page.version);
  if (!current) throw new Error(`page ${page.id}: no history revision for version ${page.version}`);
  return (await session.get(`/api/pages/${page.id}/history/${current.id}`)).markdown;
}

/** Stable, comparable projection of what an API caller can observe about a page. */
export async function pageSnapshot(session, id) {
  const status = await session.status(`/api/pages/${id}`);
  if (status !== 200) return { status };
  const page = await session.get(`/api/pages/${id}`);
  const history = await session.get(`/api/pages/${id}/history?limit=50`);
  const revisions = [];
  let markdown = page.markdown; // see pageMarkdown
  for (const item of history.items) {
    const revision = await session.get(`/api/pages/${id}/history/${item.id}`);
    if (markdown === undefined && item.version === page.version) markdown = revision.markdown;
    revisions.push({ id: item.id, version: item.version, title: item.title, isPublished: item.isPublished, authorId: item.authorId,
      contentSha: sha256(revision.contentJson), markdownSha: sha256(revision.markdown) });
  }
  if (typeof markdown !== "string") throw new Error(`page ${id}: no Markdown for version ${page.version}`);
  const tags = (await session.get(`/api/pages/${id}/tags`)).tags.map((tag) => ({ id: tag.id, name: tag.name })).sort((a, b) => a.name.localeCompare(b.name));
  return {
    status, id: page.id, spaceId: page.spaceId, parentId: page.parentId, title: page.title, version: page.version,
    isPublished: page.isPublished, publishedVersion: page.publishedVersion, contentSha: sha256(page.contentJson), markdownSha: sha256(markdown),
    decoration: page.decoration ? { icon: page.decoration.icon, color: page.decoration.color, revisionId: page.decoration.revisionId } : null,
    revisions: revisions.sort((a, b) => a.version - b.version), tags,
  };
}

/**
 * Access decisions of every person who reaches a page, as the page access dialog lists them. 1.0 listed only people who could
 * read the page; since 1.1 (WP3 S5) the manager's list shows everyone with a role source, so a space Writer/Admin of someone
 * else's private draft appears with every read/edit flag false ("Koncept zatím nevidí"). Those rows are left out so both builds
 * compare the same set; what each account can actually open is compared separately (userProbe).
 */
export async function pageAccess(session, id) {
  const result = {};
  for (let offset = 0; ; offset += 50) {
    const state = await session.get(`/api/pages/${id}/access?offset=${offset}&limit=50`);
    for (const person of state.items) {
      if (!person.canReadPublished && !person.canReadDraft && !person.canEdit) continue;
      result[person.userId] = { role: person.role, isWikiAdmin: person.isWikiAdmin, canReadPublished: person.canReadPublished, canReadDraft: person.canReadDraft, canEdit: person.canEdit };
    }
    if (!state.hasMore) return result;
  }
}

/** What one signed-in user can reach: spaces with roles, and read/edit status of every seeded page. */
export async function userProbe(session, pages) {
  const spaces = (await session.get("/api/spaces")).map((space) => ({ id: space.id, role: space.role })).sort((a, b) => a.id.localeCompare(b.id));
  const reach = {};
  for (const [key, id] of Object.entries(pages)) {
    const response = await session.raw("GET", `/api/pages/${id}`);
    const status = response.status();
    let canEdit = null, version = null;
    if (status === 200) { const body = await response.json(); canEdit = body.canEdit; version = body.version; }
    reach[key] = { status, canEdit, version };
  }
  return { spaces, pages: reach };
}
