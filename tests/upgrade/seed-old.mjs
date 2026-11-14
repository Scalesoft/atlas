// Seeds realistic data through the OLD server's public API and a real browser, then writes manifest.json.
// Assumes the old first-run.spec.ts installed the wiki (browser-admin, space "Začínáme").
import { randomUUID } from "node:crypto";
import { writeFileSync } from "node:fs";
import { ADMIN, LIVE_EDITOR, OUT, PIXEL_PNG, Session, browserLogin, chromium, pageAccess, pageMarkdown, pageSnapshot, passwordOf, richPage, sha256, sleep, startEditing, userProbe, wikiFixture } from "./lib.mjs";

const log = (...args) => console.log(new Date().toISOString().slice(11, 19), ...args);
const manifest = { createdAt: new Date().toISOString(), users: {}, groups: {}, spaces: {}, pages: {}, attachments: [], shareLinks: [], trash: {}, favorites: [], notes: [] };
const admin = await Session.login(ADMIN);
manifest.users.admin = { id: admin.user.id, username: ADMIN, isWikiAdmin: true, isActive: true };
if ((await admin.get("/api/admin/setup")).pending) await admin.post("/api/admin/setup/complete");

// --- accounts and groups ------------------------------------------------------------------------
const accounts = {
  writer: { username: "upgrade-writer", displayName: "Upgrade Pisatel" },
  spaceAdmin: { username: "upgrade-space-admin", displayName: "Upgrade Správce prostoru" },
  groupWriter: { username: "upgrade-group-writer", displayName: "Upgrade Skupinový pisatel" },
  former: { username: "upgrade-former", displayName: "Upgrade Bývalý člen" },
  inactive: { username: "upgrade-inactive", displayName: "Upgrade Neaktivní" },
  outsider: { username: "upgrade-outsider", displayName: "Upgrade Bez přístupu" },
  wikiAdmin2: { username: "upgrade-wiki-admin", displayName: "Upgrade Druhý správce", isWikiAdmin: true },
};
for (const [key, account] of Object.entries(accounts)) {
  const created = await admin.post("/api/admin/users", { username: account.username, displayName: account.displayName, isWikiAdmin: !!account.isWikiAdmin });
  const anonymous = await Session.anonymous();
  await anonymous.post("/api/auth/activate", { token: created.token, newPassword: passwordOf(account.username) });
  await anonymous.dispose();
  manifest.users[key] = { id: created.user.id, username: account.username, isWikiAdmin: !!account.isWikiAdmin, isActive: true };
}
const reader = (await admin.get("/api/admin/users")).find((user) => user.username === "browser-reader");
if (reader) manifest.users.reader = { id: reader.id, username: reader.username, isWikiAdmin: false, isActive: reader.isActive };
log("accounts", Object.keys(manifest.users).join(", "));

const writers = await admin.post("/api/admin/groups", { name: "Upgrade Pisatelé" });
let readers = await admin.post("/api/admin/groups", { name: "Upgrade Čtenáři (návrh)" });
readers = await admin.put(`/api/admin/groups/${readers.id}`, { name: "Upgrade Čtenáři", expectedVersion: readers.version });
manifest.groups = { writers: { id: writers.id, name: writers.name }, readers: { id: readers.id, name: readers.name } };
const member = (group, user, isMember = true, expectedVersion = null) => admin.put(`/api/admin/groups/${group}/members/${user}`, { isMember, expectedVersion });
await member(writers.id, manifest.users.groupWriter.id);
await member(writers.id, manifest.users.former.id);
await member(readers.id, manifest.users.writer.id);
await member(readers.id, manifest.users.inactive.id);
// Removed membership: the former member keeps only history.
const formerMembership = (await admin.get(`/api/admin/groups/${writers.id}/members`)).find((row) => row.id === manifest.users.former.id);
await member(writers.id, manifest.users.former.id, false, formerMembership.version);

// --- spaces and grants --------------------------------------------------------------------------
const spaces = await admin.get("/api/spaces");
const start = spaces.find((space) => space.name === "Začínáme");
manifest.spaces.start = { id: start.id, name: start.name };
const alpha = await admin.post("/api/spaces", { name: "Upgrade Alfa", icon: "code-xml", color: "green" });
const beta = await admin.post("/api/spaces", { name: "Upgrade Beta", icon: "workflow", color: "amber", groups: [{ groupId: writers.id, role: "Writer" }] });
manifest.spaces.alpha = { id: alpha.id, name: alpha.name };
manifest.spaces.beta = { id: beta.id, name: beta.name };
const settings = await admin.get(`/api/spaces/${alpha.id}/settings`);
// pinPolicy exists since 1.2.0 (pinned spaces) and is required there; older servers do not know it.
await admin.put(`/api/spaces/${alpha.id}/settings`, { name: settings.name, icon: settings.icon, color: settings.color, description: "Prostor pro ověření upgradu.",
  ...(settings.pinPolicy ? { pinPolicy: settings.pinPolicy } : {}), expectedVersion: settings.version });

async function grant(spaceId, subject, role) {
  const access = await admin.get(`/api/spaces/${spaceId}/access`);
  const current = access.grants.find((row) => (subject.userId && row.userId === subject.userId) || (subject.groupId && row.groupId === subject.groupId));
  await admin.put(`/api/spaces/${spaceId}/access`, { ...subject, role, expectedVersion: current?.version ?? null });
}
await grant(alpha.id, { userId: manifest.users.writer.id }, "Writer");
await grant(alpha.id, { userId: manifest.users.spaceAdmin.id }, "Admin");
await grant(alpha.id, { userId: manifest.users.inactive.id }, "Writer");
await grant(alpha.id, { userId: manifest.users.former.id }, "Reader");
await grant(alpha.id, { groupId: readers.id }, "Reader");
await grant(beta.id, { groupId: readers.id }, "Reader");
await grant(beta.id, { userId: manifest.users.spaceAdmin.id }, "Reader");
await grant(beta.id, { userId: manifest.users.spaceAdmin.id }, "Writer"); // role change keeps history
// A closed (revoked) grant: the old schema has no bounded validity, only assignment history.
await grant(alpha.id, { userId: manifest.users.former.id }, null);
manifest.notes.push("The deployed schema has no grant validity (StartAt/ExpiresAt arrive in 2026092502); 'expired' grants are seeded as revoked assignments (ActiveTo set) and a removed group membership.");
log("spaces and grants");

// --- pages --------------------------------------------------------------------------------------
async function createPage(session, key, spaceId, title, parentId = null, paragraphs = [`Obsah stránky ${title}.`]) {
  const body = richPage(title, paragraphs);
  const page = await session.post("/api/pages", { spaceId, parentId, title, ...body });
  manifest.pages[key] = { id: page.id };
  return page;
}
async function save(session, page, title, paragraphs, extra) {
  return session.put(`/api/pages/${page.id}`, { title, ...richPage(title, paragraphs, extra), expectedVersion: page.version });
}
const publish = (session, page) => session.post(`/api/pages/${page.id}/publish`, { expectedVersion: page.version });

let root = await createPage(admin, "alphaRoot", alpha.id, "Alfa kořen");
root = await publish(admin, root);
root = await save(admin, root, "Alfa kořen", ["Druhá publikovaná verze."]);
root = await publish(admin, root);
root = await save(admin, root, "Alfa kořen", ["Druhá publikovaná verze.", "Neveřejný rozpracovaný koncept nad publikací."]);

let child = await createPage(admin, "alphaChild", alpha.id, "Alfa přílohy", root.id);
const image = await admin.post(`/api/pages/${child.id}/attachments`, undefined, { multipart: { file: { name: "pixel.png", mimeType: "image/png", buffer: PIXEL_PNG }, kind: "image" } });
const fileBytes = Buffer.from(Array.from({ length: 4096 }, (_, index) => `řádek ${index} upgrade testu\n`).join(""));
const file = await admin.post(`/api/pages/${child.id}/attachments`, undefined, { multipart: { file: { name: "příloha-upgrade.txt", mimeType: "text/plain", buffer: fileBytes }, kind: "file" } });
child = await save(admin, child, "Alfa přílohy", ["Stránka s obrázkem a souborem."], [
  { node: { type: "image", attrs: { src: image.url, alt: "Kontrolní pixel" } }, markdown: `![Kontrolní pixel](${image.url})` },
  { node: { type: "paragraph", content: [{ type: "text", text: "Stáhnout přílohu", marks: [{ type: "link", attrs: { href: file.url } }] }] }, markdown: `[Stáhnout přílohu](${file.url})` },
]);
child = await publish(admin, child);
manifest.attachments.push({ id: image.id, pageKey: "alphaChild", name: image.name, size: image.size, url: image.url, sha256: sha256(PIXEL_PNG) });
manifest.attachments.push({ id: file.id, pageKey: "alphaChild", name: file.name, size: file.size, url: file.url, sha256: sha256(fileBytes) });
await admin.put(`/api/pages/${child.id}/tags`, { names: ["upgrade", "alfa", "přílohy"], expectedTagIds: [] });
await admin.put(`/api/pages/${child.id}/decoration`, { icon: "shield-check", color: "teal", expectedRevisionId: null });

let grandchild = await createPage(admin, "alphaGrandchild", alpha.id, "Alfa vnuk", child.id);
grandchild = await publish(admin, grandchild);

const writer = await Session.login(accounts.writer.username);
await createPage(writer, "alphaPrivateDraft", alpha.id, "Alfa soukromý koncept pisatele", root.id); // never published: owner + wiki admins only
let writerPage = await createPage(writer, "alphaWriterPublished", alpha.id, "Alfa stránka pisatele", root.id);
writerPage = await publish(writer, writerPage);
await writer.put(`/api/pages/${child.id}/favorite`, { isFavorite: true });
await writer.post(`/api/pages/${child.id}/visit`);
manifest.favorites.push({ userKey: "writer", pageKey: "alphaChild" });

let renamed = await createPage(admin, "alphaRenamed", alpha.id, "Alfa původní název", root.id);
renamed = await publish(admin, renamed);
renamed = await admin.put(`/api/pages/${renamed.id}/title`, { title: "Alfa přejmenovaná", expectedVersion: renamed.version });

let restored = await createPage(admin, "alphaRestored", alpha.id, "Alfa obnovená", root.id, ["Verze 1."]);
restored = await publish(admin, restored);
restored = await save(admin, restored, "Alfa obnovená", ["Verze 2, později vrácená."]);
const firstRevision = (await admin.get(`/api/pages/${restored.id}/history?limit=20`)).items.find((item) => item.version === 1);
restored = await admin.post(`/api/pages/${restored.id}/restore`, { revisionId: firstRevision.id, expectedVersion: restored.version });

const tree = () => admin.get(`/api/spaces/${alpha.id}/tree`);
const moved = await createPage(admin, "alphaMoved", alpha.id, "Alfa přesunutá");
await admin.post(`/api/pages/${moved.id}/move`, { parentId: child.id, beforePageId: null, expectedTreeVersion: (await tree()).version });

let trashParent = await createPage(admin, "alphaTrashParent", alpha.id, "Alfa koš rodič");
trashParent = await publish(admin, trashParent);
await createPage(admin, "alphaTrashChild", alpha.id, "Alfa koš potomek", trashParent.id);
await admin.post(`/api/pages/${trashParent.id}/trash`, { includeSubtree: true, expectedTreeVersion: (await tree()).version, expectedVersion: trashParent.version });
let trashed = await createPage(admin, "alphaTrashed", alpha.id, "Alfa v koši");
trashed = await publish(admin, trashed);
await admin.post(`/api/pages/${trashed.id}/trash`, { includeSubtree: false, expectedTreeVersion: (await tree()).version, expectedVersion: trashed.version });

let crossSpace = await createPage(admin, "crossSpaceMoved", alpha.id, "Přesun mezi prostory");
crossSpace = await publish(admin, crossSpace);
const betaTree = await admin.get(`/api/spaces/${beta.id}/tree`);
await admin.post(`/api/pages/${crossSpace.id}/move-space`, { targetSpaceId: beta.id, parentId: null, beforePageId: null, includeSubtree: true,
  expectedSourceTreeVersion: (await tree()).version, expectedTargetTreeVersion: betaTree.version, expectedVersion: crossSpace.version, acknowledgeAccessChange: true });

let handbook = await createPage(admin, "betaHandbook", beta.id, "Beta příručka", null, ["Sdílená příručka pro externí čtenáře."]);
handbook = await publish(admin, handbook);
await admin.put(`/api/pages/${handbook.id}/tags`, { names: ["upgrade", "beta"], expectedTagIds: [] });
let collab = await createPage(admin, "betaCollaboration", beta.id, "Beta společné úpravy", null, ["Výchozí publikovaný obsah."]);
collab = await publish(admin, collab);
const groupWriter = await Session.login(accounts.groupWriter.username);
await createPage(groupWriter, "betaGroupDraft", beta.id, "Beta koncept skupinového pisatele");
const startPage = await createPage(admin, "startPage", start.id, "Začínáme s upgradem");
await publish(admin, startPage);
log("pages", Object.keys(manifest.pages).length);

// --- external sharing: active, expired and revoked links ----------------------------------------
await admin.put(`/api/spaces/${beta.id}/external-sharing`, { enabled: true, expectedVersion: null });
const link = (name, expiresAt) => admin.post(`/api/pages/${handbook.id}/share-links`, { name, expiresAt: expiresAt.toISOString() });
const active = await link("Aktivní odkaz", new Date(Date.now() + 14 * 86400_000));
const expiring = await link("Brzy vypršelý odkaz", new Date(Date.now() + 8_000));
const revoked = await link("Zrušený odkaz", new Date(Date.now() + 7 * 86400_000));
await admin.delete(`/api/pages/${handbook.id}/share-links/${revoked.link.id}`);
for (const [kind, created] of [["active", active], ["expired", expiring], ["revoked", revoked]])
  manifest.shareLinks.push({ kind, id: created.link.id, pageKey: "betaHandbook", name: created.link.name, url: created.url, secret: created.url.split("/").pop() });

// --- Wiki.js import of the fixture ZIP ----------------------------------------------------------
const archive = wikiFixture();
const preview = await admin.post("/api/admin/imports/wiki-js/preview", undefined, { multipart: {
  options: JSON.stringify({ sourceKey: "upgrade-wikijs", spaceName: "Upgrade import Wiki.js" }),
  file: { name: "wikijs-fixture.zip", mimeType: "application/zip", buffer: archive } } });
const committed = await admin.post(`/api/admin/imports/${preview.id}/commit`, { reviewToken: preview.reviewToken, acknowledgeQuarantine: true, acknowledgeWarnings: true });
manifest.import = { id: committed.id, spaceId: committed.spaceId, archiveSha: sha256(archive), counts: committed.counts,
  items: committed.items.filter((item) => item.pageId).map((item) => ({ path: item.path, pageId: item.pageId, title: item.title, action: item.action })) };
committed.items.filter((item) => item.pageId).forEach((item, index) => { manifest.pages[`import${index}`] = { id: item.pageId }; });
if (committed.spaceId) manifest.spaces.imported = { id: committed.spaceId, name: "Upgrade import Wiki.js" };
log("wiki import", JSON.stringify(committed.counts));

// --- collaboration bound to the old schema, left unsaved ----------------------------------------
// From 4.0 (3.6) live edits do not stay unsaved by themselves: the tab saves them (PUT /api/pages/{id}) and the server stores them when
// the last editor leaves and at the latest 30 s after the first one (LiveCheckpoints). What stays unsaved in the log are the edits of
// an author who may no longer edit (deactivated, rights taken) until the next editor's save. So the edits are typed by the account
// "inactive", whose tab cannot save, and the account is deactivated before it leaves. Before 4.0 nothing saves them either way.
await grant(beta.id, { userId: manifest.users.inactive.id }, "Writer");
const browser = await chromium.launch();
const stamp = Date.now();
const markers = [`Neuložená změna před upgradem ${stamp}`, `Druhá neuložená změna ${stamp}`];
try {
  const { context, page } = await browserLogin(browser, accounts.inactive.username);
  await page.route(`**/api/pages/${collab.id}`, (route) => route.request().method() === "PUT" ? route.abort() : route.fallback());
  const lease = await startEditing(page, collab.id);
  const editor = page.locator(LIVE_EDITOR);
  // press focuses the editor directly; its center can be covered by the block insertion toolbar.
  const append = async (text) => { await editor.press("Control+End"); await editor.press("Enter"); await page.keyboard.insertText(text); };
  await append(markers[0]);
  await page.waitForTimeout(6_000); // let the relay persist (and possibly snapshot) the first burst
  await append(markers[1]);
  await page.waitForTimeout(1_500);
  for (const marker of markers) if (!(await editor.innerText()).includes(marker)) throw new Error(`editor lost ${marker}`);
  await page.screenshot({ path: `${OUT}/old-collaboration.png` });
  // --- deactivate one account (after it contributed a grant and the unsaved edits), while its tab is still open ---
  const inactiveSummary = (await admin.get("/api/admin/users")).find((user) => user.id === manifest.users.inactive.id);
  await admin.put(`/api/admin/users/${inactiveSummary.id}`, { displayName: inactiveSummary.displayName, isActive: false, isWikiAdmin: false, expectedVersion: inactiveSummary.version });
  manifest.users.inactive.isActive = false;
  await context.close(); // abrupt: no "Uložit koncept"
  const after = await admin.get(`/api/pages/${collab.id}`);
  const afterMarkdown = await pageMarkdown(admin, after);
  manifest.collaboration = { pageId: collab.id, pageKey: "betaCollaboration", markers, generation: lease.generation, leaseVersion: lease.currentVersion,
    savedVersion: after.version, savedMarkdownHasMarker: markers.some((marker) => afterMarkdown.includes(marker)) };
  if (manifest.collaboration.savedMarkdownHasMarker) throw new Error("collaborative edits unexpectedly reached a saved revision");
} finally { await browser.close(); }
log("collaboration", JSON.stringify(manifest.collaboration));

// --- Projector 3.3: a deployment summary and its deployment board (migrations 2026110470, 2026110480) ---
// Only where the old server runs the module (3.3 with Modules__Projector__Enabled); its own space and words no search above looks for.
{
  const modules = await admin.get("/api/modules", { expect: [200, 404] });
  // 3.4 and later already ran 2026110470/80 (the board is gone, pages are converted): the 3.3 checks of verify-new.mjs apply only to an older baseline.
  const [major, minor] = String((await admin.get("/api/setup/status")).version).split(".").map(Number);
  manifest.oldVersion = `${major}.${minor}`;
  if (!modules?.modules?.some((module) => module.id === "projector") || major > 3 || (major === 3 && minor >= 4)) manifest.notes.push("The old server has no 3.3 Projector deployment board; 2026110470/80 run over no 3.3 content.");
  else {
    const space = await admin.post("/api/spaces", { name: "Projektor nasazení", icon: "workflow", color: "blue" });
    const block = (key, title, state) => ({ type: "requirement", attrs: { id: randomUUID(), key, direction: "inbound", state, date: "", party: "", where: "", requires: "" },
      content: [{ type: "paragraph", content: [{ type: "text", text: title }] }] });
    // The OLD server stored the format id "scalesoft-rich"; the upgrade test proves it is still read.
    const content = (...blocks) => JSON.stringify({ format: "scalesoft-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 1 }],
      document: { type: "doc", content: [{ type: "requirementSummary", attrs: { id: randomUUID() } }, ...blocks] } });
    let deployment = await admin.post("/api/pages", { spaceId: space.id, parentId: null, title: "Nasazení KBN", contentJson: content(block("vpn", "Přístup VPN", "done")), markdown: "Nasazení KBN" });
    deployment = await publish(admin, deployment);
    // A working draft over the publication: the job converts both revisions in place, history as stored.
    deployment = await admin.put(`/api/pages/${deployment.id}`, { title: "Nasazení KBN", expectedVersion: deployment.version,
      contentJson: content(block("vpn", "Přístup VPN", "done"), block("db", "Databázový server", "waiting_for_input")), markdown: "Nasazení KBN" });
    // v3.5.2 has no /api/projector/pages/{id}/board: the page's own board is the generic create with purpose projector.board.
    const board = await admin.post(`/api/spaces/${space.id}/boards`, { title: "Tabule nasazení", parentPageId: deployment.id, purpose: "projector.board" });
    manifest.projector = { spaceId: space.id, pageId: deployment.id, boardId: board.id };
    log("projector", JSON.stringify(manifest.projector));
  }
}

// --- backup archive export (last content change before the snapshot) ----------------------------
await admin.put("/api/admin/backups", { enabled: false, intervalMinutes: 1440, mode: "full", destination: "/data/backups", username: "", domain: "", retentionCount: 5 });
let run = await admin.post("/api/admin/backups/run");
if (run.runId !== undefined) {
  // 3.3 and later: a run of the job backups.zip; its archive is the newest of the archives, downloaded after its own authorization.
  let job = null;
  for (let tries = 0; tries < 240 && !["succeeded", "failed", "cancelled"].includes(job?.state); tries++) {
    await sleep(500);
    job = await admin.get("/api/admin/backups/run", { expect: [200, 204] });
    if (job?.id !== run.runId) job = null;
  }
  run = (await admin.get("/api/admin/backups")).runs.toSorted((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? { status: job?.state, message: job?.message };
  if (run.status === "succeeded") await admin.post(`/api/admin/backups/runs/${run.id}/download/authorize`);
}
if (run.status !== "succeeded") throw new Error(`backup run ${run.status}: ${run.message}`);
const backup = await admin.bytes(`/api/admin/backups/runs/${run.id}/download`);
writeFileSync(`${OUT}/old-backup.zip`, backup);
manifest.backup = { runId: run.id, fileName: run.fileName, size: backup.length, sha256: sha256(backup), mode: run.mode };
log("backup", run.fileName, backup.length);

// --- wait for the short link to lapse, then record statuses --------------------------------------
while (Date.now() < new Date(expiring.link.expiresAt).getTime() + 1_000) await sleep(500);
const anonymous = await Session.anonymous();
const listed = (await admin.get(`/api/pages/${handbook.id}/share-links?offset=0&limit=50`)).items;
for (const entry of manifest.shareLinks) {
  const item = listed.find((row) => row.id === entry.id);
  Object.assign(entry, { status: item?.status, revisionVersion: item?.revisionVersion, expiresAt: item?.expiresAt, revokedAt: item?.revokedAt });
  entry.anonymousStatus = await anonymous.status(`/api/shared/${entry.secret}`);
}
await anonymous.dispose();

// --- the observable state that must survive the upgrade -------------------------------------------
const pageIds = Object.fromEntries(Object.entries(manifest.pages).map(([key, value]) => [key, value.id]));
manifest.pageIds = pageIds;
for (const [key, id] of Object.entries(pageIds)) manifest.pages[key] = await pageSnapshot(admin, id);
for (const space of Object.values(manifest.spaces)) {
  manifest.trash[space.id] = (await admin.get(`/api/spaces/${space.id}/trash?offset=0&limit=50`)).items
    .map((item) => ({ id: item.id, title: item.title, version: item.version, trashBatchId: item.trashBatchId })).sort((a, b) => a.id.localeCompare(b.id));
  space.tree = (await admin.get(`/api/spaces/${space.id}/tree`)).items
    .map((item) => ({ id: item.id, parentId: item.parentId, title: item.title, sortOrder: item.sortOrder, isPublished: item.isPublished })).sort((a, b) => a.id.localeCompare(b.id));
}
manifest.tags = (await admin.get("/api/tags?q=")).items.map((tag) => ({ id: tag.id, name: tag.name })).sort((a, b) => a.name.localeCompare(b.name));
manifest.groupsState = {};
for (const group of await admin.get("/api/admin/groups"))
  manifest.groupsState[group.id] = { name: group.name, members: (await admin.get(`/api/admin/groups/${group.id}/members`)).map((row) => row.id).sort() };
manifest.usersState = Object.fromEntries((await admin.get("/api/admin/users")).map((user) => [user.id, { username: user.username, displayName: user.displayName, isActive: user.isActive, isWikiAdmin: user.isWikiAdmin }]));
manifest.spaceAccess = {};
for (const space of Object.values(manifest.spaces))
  manifest.spaceAccess[space.id] = (await admin.get(`/api/spaces/${space.id}/access`)).grants
    .map((row) => ({ id: row.id, userId: row.userId, groupId: row.groupId, role: row.role })).sort((a, b) => a.id.localeCompare(b.id));
manifest.favoritesState = (await writer.get("/api/me/library/favorites?offset=0&limit=50")).items.map((item) => item.id).sort();

manifest.access = { pages: {}, users: {} };
for (const [key, id] of Object.entries(pageIds)) if (manifest.pages[key].status === 200) manifest.access.pages[key] = await pageAccess(admin, id);
for (const [key, user] of Object.entries(manifest.users)) {
  let session;
  try { session = key === "admin" ? admin : await Session.login(user.username); }
  catch (error) { manifest.access.users[key] = { login: error.status ?? String(error) }; continue; }
  manifest.access.users[key] = { login: 200, ...(await userProbe(session, pageIds)) };
  if (session !== admin) await session.dispose();
}
// AI-24: without AI configured the new build answers GET /api/search with exactly these bytes (verify-new.mjs, first check).
manifest.searches = [];
for (const q of ["Alfa", "Alfa přílohy", "upgrade testu"]) {
  const path = `/api/search?q=${encodeURIComponent(q)}`;
  const response = await admin.raw("GET", path);
  manifest.searches.push({ path, status: response.status(), body: await response.text() });
}
writeFileSync(`${OUT}/manifest.json`, JSON.stringify(manifest, null, 2));
log("manifest written:", Object.keys(manifest.pages).length, "pages,", manifest.attachments.length, "attachments,", manifest.shareLinks.length, "share links");
await admin.dispose(); await writer.dispose(); await groupWriter.dispose();

