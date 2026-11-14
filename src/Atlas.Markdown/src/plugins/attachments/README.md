# Přílohy

Volitelný vstup `@atlas/markdown/plugins/attachments`. Data a schéma jsou
malé synchronní definice; formulář, upload UI a styly jsou v lazy runtime.
Plugin vyžaduje profil `rich`.

```ts
import { createAttachmentsPlugin, insertAttachment } from '@atlas/markdown/plugins/attachments'

const attachments = createAttachmentsPlugin({
  upload: {
    async upload(file, { signal }) {
      const result = await fileService.upload(file, { signal })
      return { name: file.name, url: result.url, size: file.size, mimeType: file.type || undefined }
    },
  },
})
// Register attachments in the controller's plugins array.
controller.dispatch(insertAttachment({
  version: 1, name: 'Průvodce.pdf', url: '/files/guide.pdf',
  size: 248000, mimeType: 'application/pdf', description: 'Pokyny pro nový tým',
}))
```

Upload je volitelný: bez služby uživatel vloží adresu souboru pod „Další“ na kartě.
Hostitel zajišťuje úložiště, přístupová práva a zpracování souborů. Do dokumentu
se ukládají metadata a HTTP(S) nebo relativní adresa, nikoliv samotný obsah.
Karta se upravuje přímo na místě: název a popis (klik nebo Enter; Enter či opuštění pole uloží,
Esc zruší, popis ukládá Ctrl+Enter; změna jde vrátit Ctrl+Z). Prázdná karta má tlačítko
„Nahrát soubor“ a přijme soubor přetažený na kartu, karta se souborem malé „Nahradit“.
Průběh, zrušení i chyba nahrávání (a odmítnutý název či odkaz) se ukáže na kartě; úprava karty
nahrávání nepřeruší a fokus zůstane na upraveném poli. Odkaz na soubor je schovaný pod „Další“. `edit` runtime
(klik či Enter na bloku) zaměří název. Data uzlu ani projekce do Markdownu se nemění.
Nahrávání se nabízí také z kontextové lišty nad vybranou přílohou. Ukotvený panel
má výběr souboru, průběžný stav a tlačítko zrušení; zavření panelu ruší upload.
Odstranění bloku nebo zničení
editoru ruší příslušný `AbortSignal`; pozdní výsledky nemění dokument.
Nová metadata se zapíšou až po úspěšném a validním uploadu.

Stejná `upload` služba automaticky zapojí přetažení neznámých souborů do
editoru. Známé textové/Markdown soubory se importují jako obsah, obrázky
používají `imageUpload` a Mermaid soubory mohou použít registrovaný modul
diagramů. Ostatní soubory se uloží přes přílohovou službu. Bez ní editor
ohlásí, že úložiště pro daný typ není připojené; nevytváří neplatný odkaz.
Při přetažení více souborů se jejich příprava provádí souběžně, ale úspěšné
výsledky se vloží najednou v původním pořadí na mapovanou pozici dropu.

Galerie dodává vlastní dočasný adapter `createDemoFileUpload`; není součástí
knihovny. Dev/preview server ukládá skutečné bajty do omezené paměti
(10 MiB soubor, 40 MiB celkem), vrací relativní HTTP URL a umožňuje stažení.
Soubory zaniknou při resetu ukázky, jejím opuštění, restartu serveru nebo
nejpozději po hodině. Produkční host musí dodat vlastní trvalé úložiště;
produkční schéma nepovoluje uchovávat dočasné `blob:` URL jako přílohu.

Karta obsahuje ikonu s příponou, název, velikost, popis a odkaz „Stáhnout“.
Formulář pro název, adresu a popis je ukotvený pod kontextovou lištou;
technická metadata se doplní při nahrání a nejsou součástí běžného formuláře.
Pro stažení z cizí domény musí server vracet odpovídající
`Content-Disposition`; prohlížeč může jinak soubor otevřít. Původní soubor se
automaticky nemaže při nahrazení přílohy.

`attachmentNode(data)` pomáhá sestavit rich dokument. Ten musí mít metadata
`plugins: [{ id: 'attachments', version: 1 }]`. `normalizeAttachment` validuje
data včetně protokolu URL a nezáporné celočíselné velikosti. Markdown export
obsahuje název, velikost, popis a adresu; hlásí ztrátu strukturovaných metadat.
