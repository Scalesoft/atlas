import { test, expect, type Locator } from '@playwright/test';
import { startEditing, expectSaved, PAGE_TEXT, LIVE_EDITOR } from './editing';
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace:'off', screenshot:'off', video:'off' });
async function secret(field:Locator,value:string){await expect(field).toBeVisible();await field.evaluate((el,v)=>{(el as HTMLInputElement).value=v;el.dispatchEvent(new Event('input',{bubbles:true}));},value);}

test('all appearance combinations preserve the live editor, narrow navigation and omnisearch',async({page,context})=>{
  test.setTimeout(180_000);
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB!=='1','Only an installed isolated acceptance wiki.');
  await page.goto('/');
  await page.getByLabel('Uživatelské jméno',{exact:true}).fill('browser-admin');
  await secret(page.getByLabel('Heslo',{exact:true}),process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole('button',{name:'Přihlásit se',exact:true}).click();
  await expect(page.getByRole('button',{name:/^Hledat ve wiki/})).toBeVisible();
  const spaces=await (await context.request.get('/api/spaces')).json();
  const csrf=await (await context.request.get('/api/auth/csrf')).json();
  const response=await context.request.post('/api/pages',{headers:{'X-CSRF-TOKEN':csrf.token},data:{spaceId:spaces[0].id,parentId:null,title:'Vzhled a ovládání',contentJson:JSON.stringify({format:'atlas-rich',version:1,document:{type:'doc',content:[{type:'paragraph',content:[{type:'text',text:'Zachovaný dokument'}]}]}}),markdown:'Zachovaný dokument'}});
  expect(response.status()).toBe(200);const created=await response.json();
  await page.goto(`/#/pages/${created.id}`);
  await startEditing(page);
  const editor=page.locator(LIVE_EDITOR);await expect(editor).toBeVisible();
  await editor.fill('Rozepsaný obsah zůstává při změně vzhledu.');
  // A DOM marker detects remounting even when text happens to survive Yjs reload.
  await editor.evaluate(el=>el.setAttribute('data-acceptance-instance','original'));
  for(const [layout,label] of [['connected','Propojené'],['islands','Ostrovy']])
    for(const [accent,color] of [['blue','Modrá'],['green','Zelená']])
      for(const [mode,modeLabel] of [['light','Světlý'],['dark','Tmavý']]){
        await page.getByRole('button',{name:/Test správce.*Profil a vzhled/}).click();
        const profile=page.getByRole('region',{name:'Profil',exact:true});
        // Účet a vzhled is one page (2.5): the appearance choices are right there.
        // Each choice is a radio card named by its label and description.
        for(const choice of [label,color,modeLabel]) await profile.getByRole('radio',{name:new RegExp(`^${choice}`)}).check();
        // The confirmation is a toast; the previous combination's toast may still be stacked, so
        // wait for this save's own response and then for its toast.
        // The SaveBar appears only for a combination that differs from the saved one.
        const save=profile.getByRole('button',{name:'Uložit vzhled',exact:true});
        if(await save.isVisible()){
          const saved=page.waitForResponse(response=>new URL(response.url()).pathname==='/api/profile/appearance'&&response.request().method()==='PUT');
          await save.click();
          expect((await saved).status()).toBe(200);
          await expect(page.getByText('Vzhled uložen.',{exact:true}).last()).toBeVisible();
        }
        await page.locator('[data-profile-back]').click();await expect(profile).not.toBeVisible();
        await expect(page.locator(`[data-layout="${layout}"][data-accent="${accent}"][data-mode="${mode}"]`)).toBeVisible();
        await expect(editor).toHaveAttribute('data-acceptance-instance','original');await expect(editor).toContainText('Rozepsaný obsah zůstává');
        await page.screenshot({path:`../../artifacts/appearance-${layout}-${accent}-${mode}.png`,fullPage:true});
      }
  await page.getByRole('button',{name:/Test správce.*Profil a vzhled/}).click();
  await expect(page.getByRole('region',{name:'Profil',exact:true})).toBeVisible();
  const systemSaved=page.waitForResponse(response=>new URL(response.url()).pathname==='/api/profile/appearance'&&response.request().method()==='PUT');
  await page.getByRole('radio', { name: /^Podle systému/ }).click();await page.getByRole('button',{name:'Uložit vzhled',exact:true}).click();
  expect((await systemSaved).status()).toBe(200);
  await page.locator('[data-profile-back]').click();
  // The resolved theme lives on <html> (Tailwind's dark: variant and portalled overlays inherit it).
  await page.emulateMedia({colorScheme:'light'});await expect(page.locator('html')).toHaveAttribute('data-mode','light');
  await page.emulateMedia({colorScheme:'dark'});await expect(page.locator('html')).toHaveAttribute('data-mode','dark');
  await expect(editor).toHaveAttribute('data-acceptance-instance','original');
  await expectSaved(page);
  await page.setViewportSize({width:360,height:800});
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.getByRole('button',{name:'Zobrazit boční navigaci',exact:true}).click();
  await expect(page.getByRole('complementary',{name:'Hlavní navigace',exact:true})).toBeVisible();
  await page.getByRole('button',{name:/^Hledat ve wiki/}).click();
  const search=page.getByRole('dialog',{name:'Hledat ve wiki',exact:true});
  // The field is named after the active content scope ("Hledat ve wiki – Vše" by default).
  const input=search.getByRole('combobox',{name:/^Hledat ve wiki – /});
  await expect(search).toHaveCSS('animation-name', 'none');
  await expect(input).toBeFocused();await input.fill('pristupi');
  await expect(search.getByRole('listbox',{name:'Výsledky hledání'}).getByRole('option').first()).toBeVisible();
  const overflow = await page.evaluate(() => ({ viewport:innerWidth, width:document.documentElement.scrollWidth, elements:[...document.querySelectorAll('body *')].map(el=>{ const r=el.getBoundingClientRect(),s=getComputedStyle(el);return {tag:el.tagName,class:el.className,role:el.getAttribute('role'),left:Math.round(r.left),right:Math.round(r.right),width:Math.round(r.width),minWidth:s.minWidth,display:s.display}; }).filter(item=>item.right>innerWidth+1&&item.width>0).slice(0,35) }));
  if(overflow.width>overflow.viewport) { console.log(JSON.stringify(overflow)); await page.screenshot({path:'../../artifacts/appearance-mobile-overflow.png',fullPage:true}); }
  expect(overflow.width<=overflow.viewport).toBe(true);
  await page.screenshot({path:'../../artifacts/appearance-mobile-search.png',fullPage:true});
  // A reduced visual viewport models the layout pressure of an on-screen keyboard.
  // It does not claim to exercise a native mobile keyboard or screen reader.
  await page.setViewportSize({ width: 360, height: 420 });
  await input.focus();
  const keyboardInput = await input.boundingBox();
  expect(keyboardInput!.y).toBeGreaterThanOrEqual(0);
  expect(keyboardInput!.y + keyboardInput!.height).toBeLessThanOrEqual(420);
  // The shared dialog chrome has one close control named "Zavřít".
  await expect(search.getByRole('banner').getByRole('button', { name: 'Zavřít', exact: true })).toBeInViewport();
  await page.screenshot({ path: '../../artifacts/search-reduced-viewport.png', fullPage: true });
  // The first Esc empties the typed text and keeps the search open; the next one closes it (docs/interaction-rules.md P3).
  await input.press('Escape');await expect(input).toHaveValue('');await expect(search).toBeVisible();
  await input.press('Escape');await expect(search).not.toBeVisible();
  expect(new URL(page.url()).searchParams.has('search')).toBe(false);
  await expect(editor).toContainText('Rozepsaný obsah zůstává');
  // The CSS viewport at desktop 200% zoom is half its original size.
  // Reflow is verified here; native browser zoom remains a manual platform check.
  await page.setViewportSize({ width: 640, height: 450 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(640);
  await expect(page.getByRole('button', { name: 'Zobrazit boční navigaci', exact: true })).toBeInViewport();
  await page.screenshot({ path: '../../artifacts/workspace-zoom-reflow.png', fullPage: true });
  const columns = Array.from({ length: 8 }, (_, index) => ({ type: 'tableCell', attrs: { colspan: 1, rowspan: 1, colwidth: [140] }, content: [{ type: 'paragraph', content: [{ type: 'text', text: `Konfigurační sloupec ${index + 1}` }] }] }));
  const tableResponse = await context.request.post('/api/pages', { headers: { 'X-CSRF-TOKEN': csrf.token }, data: { spaceId: spaces[0].id, title: 'Široká tabulka v článku', parentId: null, contentJson: JSON.stringify({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'table', content: [{ type: 'tableRow', content: columns }] }, { type: 'paragraph', content: [{ type: 'text', text: 'Obsah za tabulkou.' }] }] } }), markdown: '| A | B | C | D | E | F | G | H |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n| 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 |' } });
  expect(tableResponse.status()).toBe(200);
  const tablePage = await tableResponse.json();
  await page.goto(`/#/pages/${tablePage.id}`);
  await expect(page.locator(`${PAGE_TEXT} table`)).toBeVisible();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
    await expect(page.locator(PAGE_TEXT)).toContainText('Obsah za tabulkou.');
    // Measured once the table is laid out: a wrapper read while the editor takes the page over measures 0 × 0.
    await expect(async () => {
      const scroll = await page.locator(`${PAGE_TEXT} .tableWrapper`).evaluate(element => {
        element.scrollLeft = element.scrollWidth;
        return { offset: element.scrollLeft, width: element.clientWidth, contentWidth: element.scrollWidth };
      });
      expect(scroll.width).toBeGreaterThan(0);
      expect(scroll.contentWidth).toBeGreaterThan(scroll.width);
      expect(scroll.offset).toBeGreaterThan(0);
    }).toPass();
    await page.screenshot({ path: `../../artifacts/workspace-wide-table-${width}.png`, fullPage: true });
  }
  await startEditing(page);
  const tableViewport = page.locator(`${PAGE_TEXT} .tableWrapper`);
  await tableViewport.evaluate(element => { element.scrollLeft = 0; });
  await page.locator(`${PAGE_TEXT} td`).first().hover();
  const rowRail = page.getByRole('button', { name: 'Řádek 1', exact: true });
  await expect(rowRail).toBeInViewport();
  expect(await rowRail.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    return document.elementFromPoint(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2)?.closest('.tt-table-row-chip') === element;
  }), 'The row control remains reachable inside the scroll gutter.').toBe(true);
  await page.locator(`${PAGE_TEXT} td`).last().hover();
  await expect(page.getByRole('button', { name: 'Sloupec 8', exact: true })).toBeInViewport();
  expect(await tableViewport.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  await page.screenshot({ path: '../../artifacts/workspace-wide-table-editing-390.png', fullPage: true });
});
