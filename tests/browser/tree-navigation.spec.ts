import { test, expect, type Locator } from "@playwright/test";
import { startEditing, PAGE_TEXT, LIVE_EDITOR } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace:"off", screenshot:"off", video:"off", actionTimeout:15_000 });
async function enterSecret(field: Locator, value: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, secret) => { (element as HTMLInputElement).value = secret; element.dispatchEvent(new Event("input", { bubbles:true })); }, value);
}
test("native tree drag moves a saved page, retains its editor, and keeps typed content across a move", async ({ page, context }) => {
  test.setTimeout(60_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB !== "1", "Only an installed isolated acceptance wiki.");
  await page.goto("/");
  await page.getByLabel("Uživatelské jméno",{exact:true}).fill("browser-admin");
  await enterSecret(page.getByLabel("Heslo",{exact:true}),process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole("button",{name:"Přihlásit se",exact:true}).click();
  await expect(page.getByRole("button",{name:/^Hledat ve wiki/})).toBeVisible();
  const spaces = await (await context.request.get("/api/spaces")).json();
  const csrf = await (await context.request.get("/api/auth/csrf")).json();
  const stamp = Date.now();
  async function create(title:string) {
    const response = await context.request.post("/api/pages",{headers:{"X-CSRF-TOKEN":csrf.token},data:{spaceId:spaces[0].id,parentId:null,title,contentJson:JSON.stringify({format:"atlas-rich",version:1,document:{type:"doc",content:[{type:"paragraph",content:[{type:"text",text:"Obsah zůstává na místě."}]},{type:"codeBlock",attrs:{language:"javascript"},content:[{type:"text",text:"const place = 'stable';"}]}]}}),markdown:"Obsah zůstává na místě.\n\n```javascript\nconst place = 'stable';\n```"}});
    expect(response.status()).toBe(200); return response.json();
  }
  const parent = await create(`Rodič ${stamp}`), child = await create(`Podstránka ${stamp}`);
  // The bar keeps its actions and the draft status at full width: up to 1100 px of workspace the parents fold into „…“,
  // so this scenario runs on a desktop wide enough for the parent crumb to be shown.
  await page.setViewportSize({ width: 1600, height: 900 });
  await page.goto(`/#/pages/${child.id}`);
  const editor = page.locator(PAGE_TEXT); await expect(editor).toBeVisible();
  // The session's first DocumentEditor re-keys once when lazy code highlighting arrives
  // (DocumentEditor.vue, codeBlockGeneration); a highlighted block marks the final instance.
  await expect(editor.locator('pre [class^="hljs-"]').first()).toBeVisible();
  await editor.evaluate(el => el.setAttribute("data-tree-acceptance", "original"));
  const source = page.locator(".tree-row").filter({has:page.locator(".tree-page").filter({hasText:child.title})});
  const destination = page.locator(".tree-row").filter({has:page.locator(".tree-page").filter({hasText:parent.title})});
  const moved = page.waitForResponse(response=>response.url().endsWith(`/api/pages/${child.id}/move`)&&response.request().method()==="POST");
  await source.dragTo(destination);
  expect((await moved).status()).toBe(200);
  await expect(page.getByText("Stránka byla přesunuta.",{exact:true})).toBeVisible();
  expect((await (await context.request.get(`/api/pages/${child.id}`)).json()).parentId).toBe(parent.id);
  await expect(editor).toHaveAttribute("data-tree-acceptance","original");
  await expect(page.locator("a.breadcrumb-ancestor")).toHaveText(parent.title);
  await page.locator("a.breadcrumb-ancestor").click();
  await expect(page.getByRole("textbox",{name:"Název stránky",exact:true})).toHaveValue(parent.title);
  await page.getByRole("button",{name:`Sbalit ${parent.title}`,exact:true}).click();
  await expect(page.getByRole("button",{name:`Rozbalit ${parent.title}`,exact:true})).toBeVisible();
  await page.reload();
  await expect(page.getByRole("textbox",{name:"Název stránky",exact:true})).toHaveValue(parent.title);
  await expect(page.getByRole("button",{name:`Rozbalit ${parent.title}`,exact:true})).toBeVisible();
  await page.getByRole("button",{name:/^Hledat ve wiki/}).click();
  await page.getByRole("dialog",{name:"Hledat ve wiki",exact:true}).getByRole("combobox",{name:/^Hledat ve wiki – /}).fill(child.title);
  await page.getByRole('button', { name: /^Filtry/ }).click();
  await page.getByRole("radio", { name: "Dostupné koncepty", exact: true }).click();
  const foundChild = page.getByRole("listbox",{name:"Výsledky hledání"}).getByRole("option",{name:new RegExp(child.title)});
  await expect(foundChild).toBeVisible();
  await foundChild.click();
  await expect(page.getByRole("button",{name:`Sbalit ${parent.title}`,exact:true})).toBeVisible();
  await expect(source.locator(".tree-page")).toHaveAttribute("aria-current","page");
  await expect(source.locator(".tree-page")).toBeInViewport();
  await source.locator(".tree-page").click();
  await startEditing(page);
  const writable = page.locator(LIVE_EDITOR); await expect(writable).toBeVisible();
  await writable.fill("Rozepsaný obsah se při přesunu neztratí.");
  // 3.6: the page saves by itself and a move leaves its content alone, so typed content no longer refuses it (only a settings form does).
  const movedToRoot = page.waitForResponse(response=>response.url().endsWith(`/api/pages/${child.id}/move`)&&response.request().method()==="POST");
  // The space row is the drop target for the space's root.
  await source.dragTo(page.locator(`[data-sidebar-space="${spaces[0].id}"] .sidebar-space-row`));
  expect((await movedToRoot).status()).toBe(200);
  await expect(writable).toContainText("Rozepsaný obsah se při přesunu neztratí.");
  await page.getByRole("button",{name:"Další akce stránky",exact:true}).click();
  await expect(page.getByRole("menu",{name:"Další akce stránky",exact:true}).getByRole("menuitem",{name:"Přesunout…",exact:true})).toBeVisible();
});

