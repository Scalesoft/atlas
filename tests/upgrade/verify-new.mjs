// Verifies on the NEW server that everything seed-old.mjs recorded in manifest.json survived the upgrade,
// then exercises the upgrade-sensitive paths (collaboration schema move, old backup restore, Wiki.js import).
// Writes verify.json: [{check, ok, detail}].
import { readFileSync, writeFileSync } from "node:fs";
import { ADMIN, LIVE_EDITOR, OUT, Session, browserLogin, chromium, pageAccess, pageMarkdown, pageSnapshot, sha256, sleep, startEditing, userProbe, wikiFixture } from "./lib.mjs";

const manifest = JSON.parse(readFileSync(`${OUT}/manifest.json`, "utf8"));
const results = [];
const record = (check, ok, detail = "") => { results.push({ check, ok, detail }); console.log(`${ok ? "PASS" : "FAIL"} ${check} ${detail}`); };
// Phase "main" checks preserved data; phase "backup" (run after the harness inspected the collaboration row) restores the old archive.
const phase = process.argv[2] ?? "main";
const save = () => writeFileSync(`${OUT}/verify-${phase}.json`, JSON.stringify(results, null, 2));

function diff(expected, actual, path = "", out = []) {
  if (out.length >= 8) return out;
  if (typeof expected !== typeof actual || Array.isArray(expected) !== Array.isArray(actual) || (expected === null) !== (actual === null)) {
    out.push(`${path || "."}: ${JSON.stringify(expected)?.slice(0, 120)} -> ${JSON.stringify(actual)?.slice(0, 120)}`);
  } else if (expected && typeof expected === "object") {
    for (const key of new Set([...Object.keys(expected), ...Object.keys(actual)])) diff(expected[key], actual[key], `${path}.${key}`, out);
  } else if (expected !== actual) out.push(`${path}: ${JSON.stringify(expected)} -> ${JSON.stringify(actual)}`);
  return out;
}
async function check(name, expected, produce) {
  try {
    const actual = await produce();
    const differences = diff(expected, actual);
    record(name, differences.length === 0, differences.join("; "));
    return actual;
  } catch (error) { record(name, false, String(error.message ?? error)); }
}
const pick = (object, keys) => Object.fromEntries(keys.map((key) => [key, object[key]]));

// The 3.3 Projector content of seed-old.mjs: migration 2026110470 deletes the deployment board, 2026110480 queues the job
// pages.requirements-overview (a restore of a backup requests both again). Waits for the job's first run newer than afterRunId: it converts
// the page's working and published revisions (a page another session holds is deferred to a later run).
const RUNS = "/api/admin/jobs/pages.requirements-overview/runs";
async function projectorConverted(label, afterRunId = 0, revisions = 2) {
  if (!manifest.projector) return;
  try {
    let run;
    for (const deadline = Date.now() + 180_000; Date.now() < deadline; await sleep(1_000)) {
      run = (await admin.get(RUNS)).runs.filter((item) => item.id > afterRunId).at(-1);
      if (run && !["queued", "running"].includes(run.state)) break;
    }
    record(`projector (${label}): pages.requirements-overview converted the 3.3 deployment summary`, run?.state === "succeeded" && run.summary?.revisions >= revisions,
      JSON.stringify(run ? pick(run, ["id", "state", "summary"]) : null));
    record(`projector (${label}): the 3.3 deployment board is deleted`, await admin.status(`/api/boards/${manifest.projector.boardId}`) === 404);
    const page = await admin.get(`/api/pages/${manifest.projector.pageId}`);
    record(`projector (${label}): the page shows an overview and keeps its requirements`,
      page.contentJson.includes('"type":"overview"') && !page.contentJson.includes("requirementSummary") && page.contentJson.includes("Databázový server"), `version ${page.version}`);
  } catch (error) { record(`projector (${label})`, false, String(error.message ?? error)); }
}

let admin;
try {
  admin = await Session.login(ADMIN);
  const pageIds = manifest.pageIds;

  if (phase === "main") {
  // --- AI-24: an upgraded wiki without an AI profile behaves exactly as before ------------------
  // First, before any read below could change what search returns.
  for (const search of manifest.searches ?? []) {
    const response = await admin.raw("GET", search.path);
    const body = await response.text();
    let at = 0;
    while (at < body.length && body[at] === search.body[at]) at++;
    record(`search (AI-24): ${decodeURIComponent(search.path)} answers with the same bytes as before the upgrade`,
      response.status() === search.status && body === search.body,
      body === search.body ? `${body.length} bytes` : `status ${search.status} -> ${response.status()}; first difference at ${at}: ${JSON.stringify(search.body.slice(at, at + 80))} -> ${JSON.stringify(body.slice(at, at + 80))}`);
  }
  {
    const response = await admin.raw("GET", "/api/ai/capabilities");
    if (response.status() === 404) record("ai: capabilities endpoint (A3) not in this build yet", true, "404");
    else {
      const capabilities = response.status() === 200 ? await response.json() : {};
      const enabled = Object.entries(capabilities).filter(([, value]) => value !== false).map(([key]) => key);
      record("ai: every capability is false without an AI profile (AI-24)", response.status() === 200 && Object.keys(capabilities).length > 0 && enabled.length === 0,
        `status ${response.status()}; not false: ${enabled.join(", ")}`);
    }
  }
  save();

  // --- identities, groups, spaces -------------------------------------------------------------
  await check("users: accounts, names, active and wiki-admin flags", manifest.usersState, async () =>
    Object.fromEntries((await admin.get("/api/admin/users")).map((user) => [user.id, pick(user, ["username", "displayName", "isActive", "isWikiAdmin"])])));
  await check("groups: names and current members", manifest.groupsState, async () => {
    const state = {};
    for (const group of await admin.get("/api/admin/groups"))
      state[group.id] = { name: group.name, members: (await admin.get(`/api/admin/groups/${group.id}/members`)).map((row) => row.id).sort() };
    return state;
  });
  const grantExtras = [];
  await check("spaces: grants (ids, subjects, roles)", manifest.spaceAccess, async () => {
    const state = {};
    for (const spaceId of Object.keys(manifest.spaceAccess)) {
      const grants = (await admin.get(`/api/spaces/${spaceId}/access`)).grants;
      grantExtras.push(...grants.filter((row) => row.startAt != null || row.expiresAt != null).map((row) => row.id));
      state[spaceId] = grants.map((row) => pick(row, ["id", "userId", "groupId", "role"])).sort((a, b) => a.id.localeCompare(b.id));
    }
    return state;
  });
  record("spaces: legacy grants carry no validity bounds in the API", grantExtras.length === 0, grantExtras.join(", "));
  await check("spaces: page trees (parent, order, title, publication)", Object.fromEntries(Object.values(manifest.spaces).map((space) => [space.id, space.tree])), async () => {
    const state = {};
    for (const space of Object.values(manifest.spaces))
      state[space.id] = (await admin.get(`/api/spaces/${space.id}/tree`)).items
        .map((item) => pick(item, ["id", "parentId", "title", "sortOrder", "isPublished"])).sort((a, b) => a.id.localeCompare(b.id));
    return state;
  });
  await check("trash: trashed pages and batches", manifest.trash, async () => {
    const state = {};
    for (const spaceId of Object.keys(manifest.trash))
      state[spaceId] = (await admin.get(`/api/spaces/${spaceId}/trash?offset=0&limit=50`)).items
        .map((item) => pick(item, ["id", "title", "version", "trashBatchId"])).sort((a, b) => a.id.localeCompare(b.id));
    return state;
  });

  // --- pages: current revision, every history revision, publication, tags, decoration ----------
  for (const [key, id] of Object.entries(pageIds)) await check(`page ${key}: revisions, publication, content, tags, decoration`, manifest.pages[key], () => pageSnapshot(admin, id));
  await check("tags: every tag id and name", manifest.tags, async () =>
    (await admin.get("/api/tags?q=")).items.map((tag) => pick(tag, ["id", "name"])).sort((a, b) => a.name.localeCompare(b.name)));
  for (const attachment of manifest.attachments) {
    await check(`attachment ${attachment.name}: bytes (sha256)`, attachment.sha256, async () => sha256(await admin.bytes(attachment.url)));
  }

  // --- external sharing --------------------------------------------------------------------------
  const pageOf = (key) => pageIds[key];
  await check("share links: status, pinned revision, expiry, revocation", manifest.shareLinks.map((entry) => pick(entry, ["id", "status", "revisionVersion", "expiresAt", "revokedAt"])), async () => {
    const items = (await admin.get(`/api/pages/${pageOf("betaHandbook")}/share-links?offset=0&limit=50`)).items;
    return manifest.shareLinks.map((entry) => ({ id: entry.id, ...pick(items.find((item) => item.id === entry.id) ?? {}, ["status", "revisionVersion", "expiresAt", "revokedAt"]) }));
  });
  const anonymous = await Session.anonymous();
  await check("share links: anonymous access (active opens, expired/revoked do not)", manifest.shareLinks.map((entry) => ({ kind: entry.kind, status: entry.anonymousStatus })), async () => {
    const out = [];
    for (const entry of manifest.shareLinks) out.push({ kind: entry.kind, status: await anonymous.status(`/api/shared/${entry.secret}`) });
    return out;
  });
  const shared = await anonymous.get(`/api/shared/${manifest.shareLinks.find((entry) => entry.kind === "active").secret}`).catch(() => null);
  record("share links: active link still serves the pinned published revision", shared?.title === manifest.pages.betaHandbook.title, shared ? `${shared.title} v${shared.revisionVersion}` : "no response");
  await anonymous.dispose();

  // --- access decisions ----------------------------------------------------------------------------
  const writer = await Session.login(manifest.users.writer.username);
  await check("favorites: personal library preserved", manifest.favoritesState, async () => (await writer.get("/api/me/library/favorites?offset=0&limit=50")).items.map((item) => item.id).sort());
  await writer.dispose();
  for (const [key, expected] of Object.entries(manifest.access.pages)) await check(`access list ${key}: role/read/edit of every person`, expected, () => pageAccess(admin, pageIds[key]));
  for (const [key, expected] of Object.entries(manifest.access.users)) {
    await check(`access as ${key} (${manifest.users[key].username}): login, spaces+roles, page read/edit`, expected, async () => {
      let session;
      try { session = key === "admin" ? admin : await Session.login(manifest.users[key].username); }
      catch (error) { return { login: error.status ?? String(error) }; }
      const probe = { login: 200, ...(await userProbe(session, pageIds)) };
      if (session !== admin) await session.dispose();
      return probe;
    });
  }
  save();

  // --- search over migrated content ----------------------------------------------------------------
  try {
    const found = await admin.get(`/api/search?q=${encodeURIComponent(manifest.pages.alphaChild.title)}`);
    record("search: migrated published page is found", JSON.stringify(found).includes(pageIds.alphaChild), "");
  } catch (error) { record("search: migrated published page is found", false, error.message); }

  // --- collaboration: old 9-plugin binding moves to the current schema, unsaved updates survive ---
  const browser = await chromium.launch();
  try {
    const { context, page } = await browserLogin(browser, ADMIN);
    const lease = await startEditing(page, manifest.collaboration.pageId);
    // A new generation is expected only when the host schema moved between the releases; with the same schema the unsaved
    // updates keep their generation. Either way the session must open and keep the markers checked below.
    record("collaboration: session opens after the upgrade", !!lease.generation, `${manifest.collaboration.generation} -> ${lease.generation}${lease.generation === manifest.collaboration.generation ? " (same schema, generation kept)" : ""}`);
    const text = await page.locator(LIVE_EDITOR).innerText();
    for (const marker of manifest.collaboration.markers) record(`collaboration: unsaved update visible after upgrade (${marker})`, text.includes(marker));
    await page.screenshot({ path: `${OUT}/new-collaboration.png` });
    const saved = page.waitForResponse((response) => response.url().endsWith(`/api/pages/${manifest.collaboration.pageId}`) && response.request().method() === "PUT", { timeout: 30_000 });
    // The recovered updates count as a change once editing starts, and the page saves them by itself (3.6: no Uložit koncept).
    const response = await saved;
    const draft = await admin.get(`/api/pages/${manifest.collaboration.pageId}`);
    const draftMarkdown = await pageMarkdown(admin, draft);
    record("collaboration: unsaved updates become a draft in the new build", response.status() === 200 && manifest.collaboration.markers.every((marker) => draftMarkdown.includes(marker)),
      `PUT ${response.status()}${response.status() === 200 ? "" : ` ${await response.text()}`}, version ${manifest.collaboration.savedVersion} -> ${draft.version}`);
    await context.close();
  } catch (error) { record("collaboration: open the old-schema page in the new build", false, error.message); }
  finally { await browser.close(); }
  save();

  // --- Wiki.js import still works (and remembers the old import) ----------------------------------
  try {
    const archive = wikiFixture();
    const repeat = await admin.post("/api/admin/imports/wiki-js/preview", undefined, { multipart: {
      options: JSON.stringify({ sourceKey: "upgrade-wikijs", spaceName: "Upgrade import Wiki.js" }), file: { name: "wikijs-fixture.zip", mimeType: "application/zip", buffer: archive } } });
    record("wiki import: repeat of the pre-upgrade import creates nothing new", repeat.counts.create === 0, JSON.stringify(repeat.counts));
    const preview = await admin.post("/api/admin/imports/wiki-js/preview", undefined, { multipart: {
      options: JSON.stringify({ sourceKey: "upgrade-wikijs-after", spaceName: "Upgrade import po upgradu" }), file: { name: "wikijs-fixture.zip", mimeType: "application/zip", buffer: archive } } });
    const committed = await admin.post(`/api/admin/imports/${preview.id}/commit`, { reviewToken: preview.reviewToken, acknowledgeQuarantine: true, acknowledgeWarnings: true });
    record("wiki import: fixture ZIP imports into a new space", committed.completedAt != null && committed.counts.create === manifest.import.counts.create,
      `before ${JSON.stringify(manifest.import.counts)} after ${JSON.stringify(committed.counts)}`);
  } catch (error) { record("wiki import: fixture ZIP imports after upgrade", false, error.message); }
  save();

  await projectorConverted("upgrade");

  // --- „Co je nového“: an account from before the upgrade is told about the new release once ------------------
  // The smoke specs sign in as these accounts next; the notice would cover their clicks, so it is marked seen as closing it does.
  try {
    const release = (await admin.get("/api/setup/status")).version.split("+")[0];
    for (const username of [ADMIN, "browser-reader"]) {
      const session = username === ADMIN ? admin : await Session.login(username);
      const seen = (await session.get("/api/auth/me")).releaseNotesSeen;
      record(`release notice: ${username} is told about ${release} once`, typeof seen === "string" && seen !== release, `seen before: ${seen}`);
      await session.put("/api/profile/release-notes", { version: release });
      if (session !== admin) await session.dispose();
    }
  } catch (error) { record("release notice", false, error.message); }
  save();

  }

  // --- the old backup archive restores into the new build (last: it rewrites content) -------------
  if (phase === "backup") try {
    const archive = readFileSync(`${OUT}/old-backup.zip`);
    record("backup: archive downloaded from the old build is intact", sha256(archive) === manifest.backup.sha256, `${archive.length} bytes`);
    const form = (mode, acknowledge) => ({ mode, ...(acknowledge ? { acknowledge: "true" } : {}), file: { name: manifest.backup.fileName, mimeType: "application/zip", buffer: archive } });
    const previewRestore = await admin.post("/api/admin/backups/import/preview", undefined, { multipart: form("restore") });
    const previewAdd = await admin.post("/api/admin/backups/import/preview", undefined, { multipart: form("add") });
    record("backup: new build previews the old archive (restore and add)", previewRestore.pages > 0 && previewAdd.pages === previewRestore.pages,
      `restore ${previewRestore.pages} pages/${previewRestore.attachments} attachments, add suffix '${previewAdd.suffix}'`);
    const added = await admin.post("/api/admin/backups/import", undefined, { multipart: form("add", true) });
    record("backup: old archive imports side by side (add)", added.pages === previewAdd.pages, added.message);
    const restored = await admin.post("/api/admin/backups/import", undefined, { multipart: form("restore", true) });
    record("backup: old archive restores by ID (restore)", restored.pages === previewRestore.pages, restored.message);
    for (const key of ["alphaRoot", "alphaChild", "betaHandbook"]) {
      const page = await admin.get(`/api/pages/${pageIds[key]}`);
      // The stored document, not its Markdown: since 2.7.0 the server recomputes the projection on restore (no trailing newline).
      record(`backup: ${key} content after restore equals the pre-upgrade content`, sha256(page.contentJson) === manifest.pages[key].contentSha && page.title === manifest.pages[key].title,
        `version ${manifest.pages[key].version} -> ${page.version}`);
    }
    for (const attachment of manifest.attachments)
      record(`backup: attachment ${attachment.name} readable after restore`, sha256(await admin.bytes(attachment.url)) === attachment.sha256);
    // The restore requests the conversion again; it merges into a run already queued (one deferred for a held page waits a while), so it
    // runs now: the restored revisions are in the 3.3 shape again.
    if (manifest.projector) await projectorConverted("restore", (await admin.post(RUNS)).runId - 1, 1);
  } catch (error) { record("backup: restore the old archive", false, error.message); }
} catch (error) {
  record("verify harness", false, String(error.stack ?? error));
} finally {
  save();
  await admin?.dispose();
}
process.exit(results.every((item) => item.ok) ? 0 : 1);
