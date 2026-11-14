import { test, expect, type Locator, type Page } from "@playwright/test";
import { PAGE_TEXT } from "./editing";
import { readFile } from "node:fs/promises";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });

// Tiny deterministic ZIP/store fixture: no company export, dependency, file system
// secret or generated test artifact is needed to prepare the upload.
function zip(files: Record<string, Buffer>) {
  const local: Buffer[] = [], central: Buffer[] = []; let offset = 0;
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
async function secret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, password) => { (element as HTMLInputElement).value = password; element.dispatchEvent(new Event("input", { bubbles: true })); }, value);
}
async function login(page: Page, username: string, password: string) {
  await page.goto("/"); await page.getByLabel("Uživatelské jméno", { exact: true }).fill(username);
  await secret(page.getByLabel("Heslo", { exact: true }), password);
  await page.getByRole("button", { name: "Přihlásit se", exact: true }).click();
  await expect(page.getByRole("button", { name: /^Hledat ve wiki/ })).toBeVisible();
}
async function openImport(page: Page) {
  await page.getByRole("button", { name: /^Správa wiki/ }).click();
  // Import z Wiki.js has its own item in the settings rail (group Provoz) since 2.5.
  await page.getByRole("navigation", { name: "Správa wiki", exact: true }).getByRole("link", { name: "Import z Wiki.js", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Import Wiki.js", exact: true })).toBeVisible();
  return page.locator(".content-import");
}
async function download(page: Page, link: Locator) {
  const receiving = page.waitForEvent("download"); await link.click();
  const file = await receiving, path = await file.path(); expect(path).toBeTruthy();
  return readFile(path!);
}

test("Wiki.js preview and confirmation import private rich pages, preserve originals and skip an identical repeat", async ({ page, context, browser }) => {
  test.setTimeout(120_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_IMPORT !== "1", "Requires an isolated installed acceptance wiki and explicit ATLAS_E2E_ALLOW_IMPORT=1.");
  const password = process.env.ATLAS_E2E_PASSWORD!;
  expect(password?.length).toBeGreaterThanOrEqual(12);
  const image = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jO1sAAAAASUVORK5CYII=", "base64");
  const archive = zip({
    "guide.md": Buffer.from('---\ntitle: Importovaný průvodce\n---\n\n# První kroky\n\n**Zachovaný tučný text.**\n\n[Navazující postup](guide/child.md)\n\n![Kontrolní obrázek](assets/pixel.png)\n\n<strong data-original-html="true">Původní HTML</strong>\n\n<!-- interní poznámka -->\n'),
    "guide/child.md": Buffer.from('---\ntitle: Navazující postup\n---\n\n## Druhá stránka\n\nNavazující ověřený obsah.\n\n[Zpět na přehled](../guide.md)\n'),
    "assets/pixel.png": image,
    "legacy.html": Buffer.from('<!--\ntitle: Původní HTML export\n-->\n<article data-original-html="true"><h1>Původní HTML export</h1><p>Text z vizuálního editoru.</p></article>'),
    "archiv/poznamky.txt": Buffer.from("jen zachovaný originál"),
  });
  const stamp = Date.now(), sourceKey = `acceptance-wiki-${stamp}`, spaceName = `Přenos Wiki.js ${stamp}`;
  await login(page, "browser-admin", password);
  await page.evaluate(() => document.documentElement.setAttribute("data-import-session", "original"));
  let panel = await openImport(page);
  async function preview() {
    await panel.getByLabel("Identifikátor zdroje", { exact: true }).fill(sourceKey);
    await panel.getByLabel("Název importního prostoru", { exact: true }).fill(spaceName);
    await panel.getByLabel("Přetáhněte ZIP sem nebo vyberte soubor", { exact: true }).setInputFiles({ name: "wiki-acceptance.zip", mimeType: "application/zip", buffer: archive });
    const response = page.waitForResponse(response => response.url().endsWith("/api/admin/imports/wiki-js/preview") && response.request().method() === "POST");
    await panel.getByRole("button", { name: "Připravit náhled", exact: true }).click();
    const result = await response; expect(result.status()).toBe(200); return result.json();
  }
  async function confirm(id: string) {
    const submit = panel.getByRole("button", { name: "Potvrdit import konceptů", exact: true });
    await expect(submit).toBeDisabled();
    await panel.getByRole("checkbox", { name: /^Zkontroloval/ }).check(); await expect(submit).toBeDisabled();
    await panel.getByRole("checkbox", { name: /^Souhlasím s importem/ }).check();
    const response = page.waitForResponse(response => response.url().endsWith(`/api/admin/imports/${id}/commit`) && response.request().method() === "POST");
    await submit.click(); const committed = await response; expect(committed.status()).toBe(200);
    await expect(panel.getByRole("status")).toContainText("Import byl dokončen.");
    return committed.json();
  }
  const planned = await preview();
  expect(planned.counts).toMatchObject({ create: 3, skip: 0, assets: 1, unsupported: 1, conflict: 0 });
  await expect(panel.getByRole("listitem").filter({ hasText: "poznamky.txt" })).toContainText("Pouze v ZIP");
  await expect(panel.getByRole("listitem").filter({ hasText: "Původní HTML export" })).toContainText("legacy.md");
  // Inline HTML becomes editor marks; the hidden comment is reported with its source line.
  const guideFindings = panel.getByRole("list", { name: "Převod stránky Importovaný průvodce" });
  // One comment finding now covers HTML <!-- --> and Obsidian %%…%% comments (ImportFindings.cs "comment").
  await expect(guideFindings).toContainText("Komentář (HTML <!-- --> nebo Obsidian %%…%%) byl vypuštěn"); await expect(guideFindings).toContainText("řádek 15");
  const spacesBefore = await (await context.request.get("/api/spaces")).json();
  expect(spacesBefore.some((space: { name: string }) => space.name === spaceName)).toBe(false);
  const completed = await confirm(planned.id);
  await expect(page.locator("html")).toHaveAttribute("data-import-session", "original");
  expect(await download(page, panel.getByRole("link", { name: "Původní ZIP", exact: true }))).toEqual(archive);
  const report = JSON.parse((await download(page, panel.getByRole("link", { name: "Stáhnout podrobný report", exact: true }))).toString("utf8"));
  expect(report.id).toBe(completed.id); expect(report.completedAt).toBeTruthy();
  const tree = await (await context.request.get(`/api/spaces/${completed.spaceId}/tree`)).json();
  expect(tree.items).toHaveLength(3); expect(tree.items.every((item: { isPublished: boolean }) => !item.isPublished)).toBe(true);
  expect(tree.items.some((item: { title: string }) => item.title === "Původní HTML export")).toBe(true);
  const guide = tree.items.find((item: { title: string }) => item.title === "Importovaný průvodce");
  const child = tree.items.find((item: { title: string }) => item.title === "Navazující postup");
  expect(child.parentId).toBe(guide.id);
  // The imported space is known without a reload: Knihovna lists it among all spaces. Settings hold the rail (2.5).
  await page.getByRole("button", { name: "Zpět do wiki", exact: true }).click();
  await page.getByRole("button", { name: "Knihovna", exact: true }).click();
  await page.locator(".space-directory").getByRole("link", { name: new RegExp(`^${spaceName}`) }).click();
  // A child row also shows its location (the parent's title), so match the row's own title at the start.
  await page.locator(".space-page-table").getByRole("link", { name: /^Importovaný průvodce/ }).click();
  const rendered = page.locator(PAGE_TEXT);
  await expect(rendered.locator("strong").filter({ hasText: "Zachovaný tučný text." })).toHaveCount(1);
  await expect(rendered.locator("strong").filter({ hasText: "Původní HTML" })).toHaveCount(1);
  await expect(rendered).toContainText("Původní HTML"); await expect(rendered.locator("[data-original-html]")).toHaveCount(0);
  const importedImage = rendered.getByRole("img", { name: "Kontrolní obrázek", exact: true }); await expect(importedImage).toBeVisible();
  const imageUrl = await importedImage.getAttribute("src"); expect(imageUrl).toMatch(/^\/api\/attachments\/[a-f0-9-]+\/content$/);
  expect(await (await context.request.get(imageUrl!)).body()).toEqual(image);
  const link = rendered.getByRole("link", { name: "Navazující postup", exact: true }); await expect(link).toHaveAttribute("href", `/#/pages/${child.id}`);
  // In the text being written a click only puts the caret into the link (4.0); its bar opens a wiki page in this window (3.6-G N1).
  await link.click(); await page.getByRole("button", { name: "Otevřít odkaz", exact: true }).click(); await expect(page).toHaveURL(new RegExp(`#/pages/${child.id}$`));
  await expect(page.locator(PAGE_TEXT)).toContainText("Navazující ověřený obsah.");
  panel = await openImport(page);
  const repeated = await preview(); expect(repeated.counts).toMatchObject({ create: 0, update: 0, skip: 3, conflict: 0 });
  const second = await confirm(repeated.id); expect(second.spaceId).toBe(completed.spaceId);
  const after = await (await context.request.get(`/api/spaces/${completed.spaceId}/tree`)).json();
  expect(after.items.map((item: { id: string; version: number }) => [item.id, item.version]).sort()).toEqual(tree.items.map((item: { id: string; version: number }) => [item.id, item.version]).sort());

  const anonymous = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  const reader = await browser.newContext({ baseURL: process.env.ATLAS_URL });
  try {
    expect((await anonymous.request.get(`/api/admin/imports/${completed.id}/source`)).status()).toBe(401);
    const readerPage = await reader.newPage(); await login(readerPage, "browser-reader", process.env.ATLAS_E2E_READER_PASSWORD ?? `${password}-reader`);
    await expect(readerPage.getByRole("button", { name: /^Správa wiki/ })).toHaveCount(0);
    expect((await reader.request.get(`/api/admin/imports/${completed.id}`)).status()).toBe(403);
    expect((await reader.request.get(`/api/admin/imports/${completed.id}/source`)).status()).toBe(403);
    expect((await reader.request.get(`/api/admin/imports/${completed.id}/report.json`)).status()).toBe(403);
    expect((await reader.request.get(`/api/pages/${guide.id}`)).status()).toBe(404);
    expect((await reader.request.get(imageUrl!)).status()).toBe(404);
    const readerCsrf = await (await reader.request.get("/api/auth/csrf")).json();
    const denied = await reader.request.post("/api/admin/imports/wiki-js/preview", { headers: { "X-CSRF-TOKEN": readerCsrf.token }, multipart: { options: JSON.stringify({ sourceKey, spaceName }), file: { name: "wiki-acceptance.zip", mimeType: "application/zip", buffer: archive } } });
    expect(denied.status()).toBe(403);
  } finally { await anonymous.close(); await reader.close(); }
});
