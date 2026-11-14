import { test, expect, type Locator } from '@playwright/test';
import { PAGE_TEXT } from "./editing";
import { readFile } from 'node:fs/promises';
process.env.PLAYWRIGHT_NO_COPY_PROMPT = '1';
test.use({ trace:'off', screenshot:'off', video:'off' });
async function secret(field:Locator,value:string){await expect(field).toBeVisible();await field.evaluate((el,v)=>{(el as HTMLInputElement).value=v;el.dispatchEvent(new Event('input',{bubbles:true}));},value);}
test('personal favorites and recent pages navigate to a downloadable revision',async({page,context})=>{
  test.skip(process.env.ATLAS_E2E_ALLOW_COLLAB!=='1','Only an installed isolated acceptance wiki.');
  await page.goto('/');await page.getByLabel('Uživatelské jméno',{exact:true}).fill('browser-admin');
  await secret(page.getByLabel('Heslo',{exact:true}),process.env.ATLAS_E2E_PASSWORD!);
  await page.getByRole('button',{name:'Přihlásit se',exact:true}).click();
  await expect(page.getByRole('button',{name:/^Hledat ve wiki/})).toBeVisible();
  const spaces=await (await context.request.get('/api/spaces')).json();
  const csrf=await (await context.request.get('/api/auth/csrf')).json();
  const title=`Osobní knihovna ${Date.now()}`;
  const response=await context.request.post('/api/pages',{headers:{'X-CSRF-TOKEN':csrf.token},data:{spaceId:spaces[0].id,parentId:null,title,contentJson:JSON.stringify({format:'atlas-rich',version:1,plugins:[{id:'diagrams',version:2}],document:{type:'doc',content:[{type:'paragraph',content:[{type:'text',text:'Obsah pro osobní orientaci a export.'}]},{type:'diagram',attrs:{data:{version:1,source:'graph LR\n A[Revize]-->B[PDF]',caption:'Postup exportu'}}}]}}),markdown:'Obsah pro osobní orientaci a export.'}});
  expect(response.status()).toBe(200);const created=await response.json();
  const upload=await context.request.post(`/api/pages/${created.id}/attachments`,{headers:{'X-CSRF-TOKEN':csrf.token},multipart:{kind:'image',file:{name:'kontrola.png',mimeType:'image/png',buffer:Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jO1sAAAAASUVORK5CYII=','base64')}}});
  expect(upload.status()).toBe(201);
  const image=await upload.json();
  const rich=JSON.parse(created.contentJson);
  rich.document.content.push({type:'image',attrs:{src:image.url,presentation:{caption:'Ověřená příloha'}}});
  const saved=await context.request.put(`/api/pages/${created.id}`,{headers:{'X-CSRF-TOKEN':csrf.token},data:{title,contentJson:JSON.stringify(rich),markdown:'Obsah pro osobní orientaci a export.',expectedVersion:created.version}});
  expect(saved.status()).toBe(200); Object.assign(created,await saved.json());
  const published=await context.request.post(`/api/pages/${created.id}/publish`,{headers:{'X-CSRF-TOKEN':csrf.token},data:{expectedVersion:created.version}});expect(published.status()).toBe(200);
  await page.goto(`/#/pages/${created.id}`);
  const favorite=page.getByRole('button',{name:'Přidat do oblíbených',exact:true});await expect(favorite).toBeEnabled();await favorite.click();
  await expect(page.getByRole('button',{name:'Odebrat z oblíbených',exact:true})).toHaveAttribute('aria-pressed','true');
  await page.locator('aside').getByRole('button',{name:'Knihovna',exact:true}).click();
  await page.locator('main').getByRole('navigation',{name:'Části knihovny',exact:true}).getByRole('link',{name:'Oblíbené',exact:true}).click();
  await expect(page).toHaveURL(/#\/library\/favorites$/);
  await page.locator('main').getByRole('link',{name:`Otevřít ${title}`,exact:true}).click();
  await expect(page).toHaveURL(new RegExp(`#/pages/${created.id}$`));
  await expect(page.locator(PAGE_TEXT)).toContainText('Obsah pro osobní orientaci');
  await page.getByRole('button',{name:'Další akce stránky',exact:true}).click();
  const downloading=page.waitForEvent('download');await page.getByRole('menuitem',{name:'Exportovat stránku',exact:true}).click();
  const download=await downloading;expect(download.suggestedFilename()).toBe(`${title}.zip`);
  const path=await download.path();expect(path).toBeTruthy();const bytes=await readFile(path!);expect(bytes.subarray(0,4).toString('hex')).toBe('504b0304');
  await page.getByRole('button',{name:'Další akce stránky',exact:true}).click();
  await page.getByRole('menuitem',{name:'Exportovat do PDF…',exact:true}).click();
  // A formal document: only the revision and its date on the cover, the choice shown on the paper beside it.
  const exportDialog=page.getByRole('dialog',{name:'Export do PDF'});
  await exportDialog.getByRole('radio',{name:'Formální',exact:true}).click();
  await expect(exportDialog.locator('.pdf-paper')).not.toContainText('ID stránky');
  await expect(exportDialog.locator('.pdf-paper')).toContainText('Revize 12');
  const pdfDownloading=page.waitForEvent('download');
  await exportDialog.getByRole('button',{name:'Exportovat PDF',exact:true}).click();
  const pdfDownload=await pdfDownloading;
  expect(pdfDownload.suggestedFilename()).toBe(`${title}.pdf`);
  const pdf=await readFile((await pdfDownload.path())!);
  expect(pdf.subarray(0,5).toString()).toBe('%PDF-');
  const badImage=await context.request.post('/api/pages',{headers:{'X-CSRF-TOKEN':csrf.token},data:{spaceId:spaces[0].id,parentId:null,title:'Nedostupný obrázek',contentJson:JSON.stringify({format:'atlas-rich',version:1,document:{type:'doc',content:[{type:'image',attrs:{src:'http://127.0.0.1:9/private.png'}}]}}),markdown:'Obrázek'}});
  expect(badImage.status()).toBe(200);
  const rejected=await context.request.get(`/api/pages/${(await badImage.json()).id}/export?format=pdf`);
  expect(rejected.status()).toBe(422);
  expect(rejected.headers()['content-type']).toContain('application/problem+json');
  await page.locator('aside').getByRole('button',{name:'Knihovna',exact:true}).click();
  await page.locator('main').getByRole('navigation',{name:'Části knihovny',exact:true}).getByRole('link',{name:'Nedávné',exact:true}).click();
  await expect(page.locator('main')).toContainText(title);
  await page.locator('main').getByRole('link',{name:`Otevřít ${title}`,exact:true}).click();
  await page.getByRole('button',{name:'Odebrat z oblíbených',exact:true}).click();
  await expect(page.getByRole('button',{name:'Přidat do oblíbených',exact:true})).toHaveAttribute('aria-pressed','false');
  await page.locator('aside').getByRole('button',{name:'Knihovna',exact:true}).click();
  await page.locator('main').getByRole('navigation',{name:'Části knihovny',exact:true}).getByRole('link',{name:'Oblíbené',exact:true}).click();
  await expect(page.locator('main').getByRole('link',{name:`Otevřít ${title}`,exact:true})).toHaveCount(0);
});


test('print layout keeps wide table columns and wiki links inside the formal document', async ({ page }) => {
  await page.emulateMedia({ media:'print' });
  await page.goto('/print.html');
  const content=[{type:'paragraph',content:[{type:'text',text:'Odkaz do wiki',marks:[{type:'link',attrs:{href:'#/pages/example'}}]}]},
    {type:'table',content:Array.from({length:12},(_,row)=>({type:'tableRow',content:Array.from({length:8},(_,column)=>({type:row?'tableCell':'tableHeader',attrs:{colspan:1,rowspan:1,colwidth:[160]},content:[{type:'paragraph',content:[{type:'text',text:`${row}/${column+1}`}]}]}))}))}];
  await page.evaluate(async content => {
    await (window as any).atlasPrint({sourceOrigin:'https://wiki.example/',title:'Příručka <lékaře> & administrátora',space:'Příručky',version:1,isPublished:true,activeFrom:'2026-10-05T12:00:00Z',exportedAt:'2026-10-05T12:00:00Z',pageId:'example',details:[],contentJson:JSON.stringify({format:'atlas-rich',version:1,document:{type:'doc',content}})});
  },content);
  await expect(page).toHaveTitle('Příručka <lékaře> & administrátora');
  const pdf = await page.pdf();
  const metadataTitle = Buffer.from('Příručka <lékaře> & administrátora', 'utf16le').swap16().toString('hex').toUpperCase();
  expect(pdf.toString('latin1')).toContain(`/Title <FEFF${metadataTitle}>`);
  await expect(page.locator('.print-content a')).toHaveAttribute('href','https://wiki.example/#/pages/example');
  expect(await page.locator('.print-content').evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
  expect(await page.locator('table').evaluate(el=>el.getBoundingClientRect().width<=document.querySelector('.print-content')!.getBoundingClientRect().width+1)).toBe(true);
  await expect(page.locator('thead th')).toHaveCount(8);
});
