import { test, expect, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { startEditing, expectSaved, PAGE_TEXT, LIVE_EDITOR } from "./editing";
process.env.PLAYWRIGHT_NO_COPY_PROMPT = "1";
test.use({ trace: "off", screenshot: "off", video: "off", actionTimeout: 15_000 });
async function enterSecret(field: Locator, secret: string) {
  await expect(field).toBeVisible();
  await field.evaluate((element, value) => { const input=element as HTMLInputElement; input.value=value; input.dispatchEvent(new Event("input",{bubbles:true})); input.dispatchEvent(new Event("change",{bubbles:true})); },secret);
}
async function mutate(context:BrowserContext,path:string,data:unknown) {
  const csrf=await (await context.request.get("/api/auth/csrf")).json();
  return context.request.post(path,{data,headers:{"X-CSRF-TOKEN":csrf.token}});
}
async function dropFile(page:Page,name:string,type:string,base64:string) {
  const editor=page.locator(LIVE_EDITOR);
  await editor.click(); await editor.press("Control+End"); await editor.press("Enter");
  await editor.evaluate((element,file)=>{
    const bytes=Uint8Array.from(atob(file.base64),c=>c.charCodeAt(0));
    const transfer=new DataTransfer(); transfer.items.add(new File([bytes],file.name,{type:file.type}));
    const box=element.getBoundingClientRect();
    element.dispatchEvent(new DragEvent("drop",{bubbles:true,cancelable:true,dataTransfer:transfer,clientX:box.left+30,clientY:box.bottom-12}));
  },{name,type,base64});
}
test("new document uploads real image and attachment, copies heading route, and survives unconfigured YouTrack",async({browser})=>{
  test.setTimeout(120_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_UPLOADS!=="1","Requires an isolated installed acceptance database and explicit ATLAS_E2E_ALLOW_UPLOADS=1.");
  const password=process.env.ATLAS_E2E_PASSWORD!;
  expect(password?.length).toBeGreaterThanOrEqual(12);
  const context=await browser.newContext({baseURL:process.env.ATLAS_URL??"https://localhost:5220",permissions:["clipboard-read","clipboard-write"],viewport:{width:1280,height:900}});
  const anonymous=await browser.newContext({baseURL:process.env.ATLAS_URL??"https://localhost:5220"});
  try {
    const page=await context.newPage(); page.on("dialog",dialog=>dialog.accept());
    await page.goto("/"); await page.getByLabel("Uživatelské jméno",{exact:true}).fill("browser-admin");
    await enterSecret(page.getByLabel("Heslo",{exact:true}),password); await page.getByRole("button",{name:"Přihlásit se",exact:true}).click();
    await expect(page.getByRole("button",{name:/^Hledat ve wiki/})).toBeVisible();
    const settings=await (await context.request.get("/api/admin/integrations/youtrack")).json();
    expect(settings.enabled,"This acceptance must not configure or contact a real YouTrack server.").toBe(false);
    const spaces=await (await context.request.get("/api/spaces")).json(); expect(spaces.length).toBeGreaterThan(0);
    const title=`Soubory a nadpisy ${Date.now()}`;
    const created=await mutate(context,"/api/pages",{spaceId:spaces[0].id,title,parentId:null,contentJson:JSON.stringify({format:"atlas-rich",version:1,plugins:[{id:"heading-anchors",version:1}],document:{type:"doc",content:[{type:"heading",attrs:{level:2,headingId:"upload-section"},content:[{type:"text",text:"Přílohy ke stránce"}]},{type:"paragraph",content:[{type:"text",text:"Ověření interních souborů."}]}]}}),markdown:"## Přílohy ke stránce\n\nOvěření interních souborů."});
    expect(created.status()).toBe(200); const initial=await created.json(); const id=initial.id;
    await page.goto(`/#/pages/${id}`); await expect(page.locator(PAGE_TEXT)).toBeVisible(); await startEditing(page);
    await expect(page.locator(LIVE_EDITOR)).toBeVisible();
    const imageUpload=page.waitForResponse(response=>response.url().endsWith(`/api/pages/${id}/attachments`)&&response.request().method()==="POST");
    await dropFile(page,"pixel.png","image/png","iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jO1sAAAAASUVORK5CYII=");
    const uploadedImage=await imageUpload; expect(uploadedImage.status()).toBe(201); const image=await uploadedImage.json();
    await expect(page.locator(`${PAGE_TEXT} img[src="${image.url}"]`)).toBeVisible();
    const fileUpload=page.waitForResponse(response=>response.url().endsWith(`/api/pages/${id}/attachments`)&&response.request().method()==="POST");
    const fileBytes=Buffer.from("%PDF-1.4\nAtlas acceptance fixture\n%%EOF\n");
    await dropFile(page,"Acceptance.pdf","application/pdf",fileBytes.toString("base64"));
    const uploadedFile=await fileUpload; expect(uploadedFile.status()).toBe(201); const file=await uploadedFile.json();
    const download=page.getByRole("link",{name:"Stáhnout přílohu Acceptance.pdf",exact:true}); await expect(download).toBeVisible();
    expect((await context.request.get(file.downloadUrl)).status()).toBe(200);
    expect((await anonymous.request.get(file.downloadUrl)).status()).toBe(401);
    // A block drop may leave a NodeSelection/GapCursor. YouTrack is an inline
    // insertion and correctly requires a textblock; place a real caret first.
    // The editor renders no toolbar in Atlas (toolbar-presentation="hidden"); its plugin
    // actions are menu items of the page menu "Další akce stránky" while editing.
    const pageMenu = page.getByRole("button",{name:"Další akce stránky",exact:true});
    const youTrackAction = page.getByRole("menu",{name:"Další akce stránky",exact:true}).getByRole("menuitem",{name:"Úkol z YouTracku",exact:true});
    await page.locator('.md-attachment-body strong').filter({hasText:'Acceptance.pdf'}).click();
    await expect(page.locator(`${PAGE_TEXT} .ProseMirror-selectednode`)).toBeVisible();
    await pageMenu.click();
    await expect(youTrackAction).toBeDisabled();
    await page.keyboard.press('Escape'); await expect(youTrackAction).toHaveCount(0);
    // Keyboard events belong to the contenteditable host, never a nonfocusable p.
    const editor = page.locator(LIVE_EDITOR);
    await editor.press('Control+Home'); await editor.press('ArrowDown'); await editor.press('End');
    await expect(editor.locator('.ProseMirror-selectednode')).toHaveCount(0);
    await pageMenu.click();
    await expect(youTrackAction).toBeEnabled();
    // The unavailable integration must leave existing editor data intact and editable.
    await youTrackAction.click();
    const picker=page.getByRole("dialog",{name:"Vybrat úkol z YouTracku"}); await picker.getByRole("combobox").fill("TEAM-123");
    await expect(picker.getByRole("alert")).toContainText("nepodařilo načíst"); await picker.press("Escape"); await expect(picker).not.toBeVisible();
    await expect(download).toBeVisible(); await expect(page.locator(LIVE_EDITOR)).toBeVisible();
    // Copy uses the host hash route, not the editor's per-view DOM id.
    // A caret in an anchored heading shows the heading's own selection toolbar.
    await page.locator('h2[data-heading-id="upload-section"]').click();
    await page.getByRole("button",{name:"Kopírovat odkaz na nadpis",exact:true}).click();
    await expect.poll(()=>page.evaluate(()=>navigator.clipboard.readText())).toContain(`#/pages/${id}?heading=upload-section`);
    const copied=await page.evaluate(()=>navigator.clipboard.readText());
    await expectSaved(page);
    const saved=await (await context.request.get(`/api/pages/${id}`)).json(); const rich=JSON.parse(saved.contentJson);
    expect(JSON.stringify(rich.document)).toContain(file.downloadUrl); expect(JSON.stringify(rich.document)).toContain(image.url); expect(JSON.stringify(rich.document), "PageView carries no Markdown projection; the attachment node names the file.").toContain("Acceptance.pdf");
    const attachmentSearch = await context.request.get('/api/search?q=Acceptance.pdf');
    expect(attachmentSearch.status()).toBe(200);
    expect((await attachmentSearch.json()).items.some((item: { id: string }) => item.id === id), 'An uploaded filename finds its published host article.').toBe(true);
    await page.goto(copied); await expect(page.locator('h2[data-heading-id="upload-section"]')).toBeFocused(); await expect(page.getByRole("link",{name:"Stáhnout přílohu Acceptance.pdf",exact:true})).toBeVisible();
    expect(await (await context.request.get(file.downloadUrl)).body()).toEqual(fileBytes);
  } finally { await context.close(); await anonymous.close(); }
});




