# Obsahové komponenty

UI všech současných i budoucích komponent se řídí
[rozhodnutím a soupisem společného UI/UX](decision-editor-component-ui.md).
Referencí je kontextové ovládání tabulek; dokument obsahuje závazná pravidla,
mapu sdílených prvků i dosud nedokončené sjednocení.

## Základ a volitelná rozšíření

Obrázky a informační boxy jsou součástí rich editoru.
Ostatní komponenty se registrují přes samostatné importy:

| Import za `@atlas/markdown/` | Factory | Dodatečná knihovna |
| --- | --- | --- |
| `plugins/contacts` | `createContactsPlugin` | žádná |
| `plugins/secrets` | `createSecretsPlugin` | žádná; [hranice hostitele](../../src/Atlas.Markdown/src/plugins/secrets/README.md) |
| `plugins/concealed` | `createConcealedContentPlugin` | žádná; jen vizuální skrytí, [popis](../../src/Atlas.Markdown/src/plugins/concealed/README.md) |
| `plugins/attachments` | `createAttachmentsPlugin` | žádná |
| `plugins/charts` | `createChartsPlugin` | `echarts` |
| `plugins/toc` | `createTocPlugin` | žádná |
| `plugins/diagrams` | `createDiagramsPlugin` | `mermaid` |
| `plugins/math` | `createMathPlugin` | `katex` |

Apache ECharts (`^6.1.0`), Mermaid a KaTeX jsou **volitelné peer dependencies**: aplikace je
instaluje pouze pro povolená rozšíření. Základní import je nenačítá. Definice
schématu a synchronní export pluginu jsou malé; runtime, formuláře a styly
modulů se importují až při zobrazení uzlu. Úspěšné importy se cachují, po
chybě je možné načítání zopakovat. Již otevřený dokument nikdy neztrácí data
kvůli nedostupné vykreslovací knihovně.

```ts
import { ref } from 'vue'
import { MarkdownEditor } from '@atlas/markdown/vue'
import { createContactsPlugin } from '@atlas/markdown/plugins/contacts'
import { createChartsPlugin } from '@atlas/markdown/plugins/charts'
import { createTocPlugin } from '@atlas/markdown/plugins/toc'
import type { MarkdownRichValue } from '@atlas/markdown/core'
import '@atlas/markdown/styles/base.css'

const plugins = [createContactsPlugin(), createChartsPlugin(), createTocPlugin()]
const value = ref<MarkdownRichValue>({
  format: 'atlas-rich', version: 1,
  document: { type: 'doc', content: [{ type: 'paragraph' }] },
})
// <MarkdownEditor v-model="value" profile="rich" :plugins="plugins"
//   ariaLabel="Dokument" />
```

Stejnou sadu pluginů předejte do `MarkdownRenderer`. Uzel už ve vstupních
datech vyžaduje odpovídající `plugins: [{ id, version: 1 }]` v rich obálce.
Prázdný dokument tato metadata nepotřebuje; controller je při ukládání doplní.
`createContactsPlugin` registruje kartu i inline kontakt pod jedním ID
`contacts`; `createMathPlugin` obdobně obě podoby vzorců pod ID `math`.

Moduly se vkládají přes slash nabídku nebo toolbar doplňků. Výběr komponenty
zobrazí společnou lištu nad ní; nastavení se otevírá v označeném ukotveném
panelu u příslušné akce. Uložit zapisuje jeden výsledek do dokumentu, Zrušit
ponechá obsah. Samostatné hranice undo vůči sousedním akcím jsou součástí
[požadovaného kontraktu a jeho zbývajícího ověření](decision-editor-component-ui.md#zbývající-sjednocení-a-hranice).
Readonly umožňuje navigaci, stažení a rozbalení; editační akce jsou zakázané.
Kalendář a události zatím součástí nejsou.

## Ovládání klávesnicí

Z kurzoru v dokumentu přejde **Alt+F10** na úchyt aktuálního vrchního bloku bez
předchozího najetí myší. Enter nebo mezerník vybere komponentu; Tab otevře její
kontextovou lištu. Stejný Tab funguje po nativním klávesnicovém výběru atomu.
Na liště fungují šipky vlevo/vpravo, Home/End a běžné Tab pořadí. Shift+Tab
z první akce nebo Escape vrací focus do editoru. Escape z formuláře nejdřív
zruší návrh; fullscreen se ukončí až po návratu do editoru.

U textových bloků úchyt vybírá blokový rozsah; nadpis standardně celou sekci.
Shift+Enter rozšíří výběr. Tab nebo šipka dolů na úchytu otevře lištu výběru,
kde lze změnit rozsah nadpisu a lokálně přesouvat bloky. Šipka dolů otevře tuto
lištu i pro komponentu místo jejích vlastností. Tabulky si zachovávají vlastní
ovládání buněk a os; Alt+F10 nad tabulkou nepřekrývá jejich ovladače.

Plugin může do téže cesty zapojit vlastní ovládací prvek bloku přes `registerMarkdownBlockKeyboardHook`: Tab z úchytu přejde nejdřív na jeho prvky, teprve potom na lištu výběru ([API pluginů](editor-plugin-api.md)).

Ve spolupráci je výběr a změna vlastností dostupná, přesuny zůstávají zakázané.
Lišta výběru obsahuje viditelné vysvětlení a neaktivní tlačítka přesunu, dostupné
je i zcela klávesnicové otevření vysvětlení. Toto není záruka bezpečných přesunů
pomocí cut/paste; jejich omezení a následná obnova zůstávají otevřeným bodem
plánu R04/D2.

## Obrázky a základní bloky

Obrázek zachovává původní `src`. V `presentation` drží šířku v procentech,
zarovnání, popisek, rotaci, převrácení, jas/kontrast/sytost, černobílý nebo
sépiový filtr a ořez z každé strany. Šířku lze měnit tažením nebo šipkami na
levém/pravém vodorovném úchytu; proporce se zachovávají. Rozložení, ořez,
efekty a text mají menší samostatné panely s návrhem a potvrzením. Nastavení
obsahuje také alternativní text a nahrazení URL.
Nahrávání přes paste/drop/picker používá stávající `imageUpload` službu hosta.

Zvětšení otevře dialog. „Obnovit vzhled“ vrátí výchozí úpravy. „Stáhnout
upravený“ vytvoří PNG s aplikovaným ořezem, rotací a filtry. Prohlížeč musí
mít ke zdroji přístup pro canvas (stejný origin nebo CORS). Šířka v dokumentu
nemění rozlišení exportovaného souboru. Obrázky neobtékají text.

Rozměry původního obrázku se zaznamenají při uživatelské úpravě; to umožňuje
statickému rendereru rezervovat přesný půdorys ořezu/rotace. Starší data bez
rozměrů zobrazí ve statickém HTML přirozený obrázek; editor rozměry zjistí po
načtení zdroje, aniž by tím sám měnil dokument.

`insert-callout` podporuje `note`, `tip`, `warning`, `important`, `caution`. V Markdownu
je informační box GitHub alert (`> [!NOTE]`, `[!TIP]`, `[!WARNING]`,
`[!IMPORTANT]`, `[!CAUTION]`); všech pět se načte a zapíše beze změny. V editoru
i v režimu čtení má box stejné záhlaví s ikonou a názvem varianty (Poznámka, Tip,
Upozornění, Důležité, Pozor).
Obsah boxu zůstává běžně editovatelný, včetně vložených odstavců a dalších bloků.

Jak komponenty zapisují agenty (MCP, REST) popisuje [oddíl Pravidla pro text stránek](../mcp-claude-code.md#pravidla-pro-text-stránek)
a nástroj `atlas_get_syntax`; tento dokument to neopakuje.

## Kontakty a přílohy

Kontakty fungují jako vizitka, seznam nebo zmínka v textu. Mají více telefonů
a e-mailů a export do UTF-8 vCard 4.0. „Uložit kontakt“ stáhne `.vcf`; import
do adresáře dokončuje uživatel v telefonu. Seznam lze stáhnout společně.
Firemní adresář dodává host přes `search(query, { signal })` a
`get(id, { signal })`. Dokument uchovává snapshot, který uživatel obnovuje
tlačítkem „Aktualizovat z adresáře“. Přihlášení a oprávnění řeší aplikace.

Přílohy zobrazují jméno, popis, typ a velikost souboru. Mají ruční adresu
nebo volitelnou službu `upload.upload(file, { signal })`; data souborů nejsou
ukládána do dokumentu. Cross-origin odkaz může prohlížeč otevřít místo
vynuceného stažení; server může poslat `Content-Disposition: attachment`.

Podrobná API: [kontakty](../../src/Atlas.Markdown/src/plugins/contacts/README.md),
[přílohy](../../src/Atlas.Markdown/src/plugins/attachments/README.md).

## Přetažení souborů do editoru

Editor přijímá soubory na obsahu i okolní prázdné ploše. Rozpoznává obrázky
podle podporovaného MIME nebo známé přípony bez MIME, `.md`/`.markdown`
importuje nativním Markdown parserem, `.txt` jako doslovné odstavce a
`.mmd`/`.mermaid` jako diagram, pokud je jeho plugin registrovaný. Soubor bez
přípony s MIME `text/plain` se také importuje jako doslovný text. Textové
importy mají limit 5 MiB. Zbývající typy zpracuje přílohový plugin přes
hostitelskou `createAttachmentsPlugin({ upload })` službu; obrázky používají
stávající `imageUpload`. Bez odpovídající služby se zobrazí chyba, úspěšné
ostatní soubory z dávky se zachovají.

Všechny soubory jedné dávky se připravují souběžně a vloží v původním pořadí
jako jedna operace historie. Pozice se mapuje přes změny dokumentu; smazání
cíle, přepnutí readonly, výměna obrazové upload služby nebo zničení editoru
zruší rozpracovanou dávku. Asynchronní dokončení nemění uživatelský focus ani
aktivní výběr. Běžný přesun textu nebo bloků používá původní editorové cesty.

Pro vlastní hostitelský povrch jsou z `/core` dostupné:

- `handleMarkdownFileDrop(editor, event, position?)`: vrací, zda převzal
  souborový drop. Volitelná pozice je ProseMirror pozice; bez ní se hledá
  místo podle souřadnic a poté aktuálního výběru. Stejná událost je zpracována
  pouze jednou i při průchodu ProseMirrorem a obalovým povrchem.
- `canHandleMarkdownFiles(editor, files)`: dotaz pro již dostupné objekty
  `File`. Při `dragover` bývá `files` chráněné/prázdné, proto vlastní povrch
  kontroluje `dataTransfer.types.includes('Files')` a editovatelnost.
- `registerMarkdownFileDropHandler(editor, { id, accept, resolve })`:
  doplnění volitelného typu bez importu jeho knihovny do core. `resolve`
  přijímá `(file, { signal })` a vrací Promise jednoho či více JSON uzlů;
  návrat registrace je cleanup funkce. Vestavěné importy mají přednost.
- `markdownFileDropStateEvent` (`md-file-drop-state`) se stavem
  `detail.pending` udává počet souborů v dosud nepotvrzených dávkách;
  při dokončení nebo zrušení se vrátí na nulu.
- `markdownFileDropErrorEvent` (`md-file-drop-error`) s `detail.message`,
  případně `detail.fileName`, hlásí konkrétní selhání. Obě události bublají
  z DOM editoru a výchozí Vue povrch je převádí na stav/chybu.

Upload síť, autentizaci a trvalé URL zajišťuje host. Knihovna nevytváří
backendový endpoint ani falešné trvalé `blob:` odkazy. Galerie má oddělené
dočasné úložiště skutečných souborů; jeho limity a životnost popisuje
[přílohový modul](../../src/Atlas.Markdown/src/plugins/attachments/README.md).

## Grafy

Grafy vykresluje modulární Apache ECharts pomocí SVG. Jedna instance zůstává
u bloku a aktualizuje se při změně hodnot nebo typu; změna rozměrů nevyžaduje
nový graf. Při odstranění se instance i observer uvolní. Nabídka typu používá
náhledové ikonky. Krátké běžné přechody respektují omezené animace systému;
Universal Transition, datové transformace ani druhý grafový engine nejsou
součástí této verze. Datové schéma a propojení s tabulkami zůstávají stejné.

Podporované typy jsou sloupcový, spojnicový, koláčový a prstencový. První
řádek dat pojmenovává řady, první sloupec kategorie. Editor dat má jednotlivé
buňky, přidávání/odstraňování řádků a řad a validaci českých i desetinných
čísel s tečkou. Prázdná hodnota je mezera, u koláče nula.

Pro vytvoření propojeného grafu označte obdélníkovou oblast v tabulce včetně
záhlaví a popisků a použijte „Graf z výběru tabulky“. Identifikátory tabulky,
řádků a buněk zachovávají vazbu při přesouvání. Graf sleduje původní výběr
a pořadí kategorií; nové řádky se automaticky nepřidávají. Nastavení dovoluje
zdroj odpojit a dále upravovat vlastní data. Pro jinou oblast tabulky lze
vytvořit nový propojený graf z příslušného výběru.

Při odstranění zdroje nebo neplatné hodnotě graf zobrazí poslední platná data
a vysvětlení. Pod každým grafem je přístupná datová tabulka. Vzorce ani
kontingenční tabulky nejsou implementované.

Podrobnosti: [grafy](../../src/Atlas.Markdown/src/plugins/charts/README.md).

## Obsah, diagramy a matematika

Obsah dokumentu odvozuje nadpisy z dat a reaguje na úpravy. V editoru přejde
na vybraný nadpis, ve statickém rendereru vytvoří kotvy izolované pro každý
dokument. Nastavení dovoluje název a rozsah úrovní nadpisů.

Diagramy používají zdroj Mermaid, popisek a bezpečný render v režimu strict.
HTML a konfigurační direktivy ve zdroji nejsou povolené. SVG se zobrazuje
izolovaně jako obrázek; při syntaktické chybě zůstane dostupný zdroj a oprava.
Matematika podporuje blok i inline výraz v LaTeXu; KaTeX vytváří nativní
MathML bez globálního stylesheetu a bez důvěry v příkazy načítající zdroje.

## Runtime, který zachovává vykreslenou instanci

`MarkdownModuleRuntime<T>.render(context)` může vedle dosavadní cleanup funkce
vrátit také `MarkdownModuleView<T>` (přímo nebo asynchronně):

```ts
import type { MarkdownModuleView, MarkdownModuleContext } from '@atlas/markdown/core'

// T je vlastní validovaný datový typ modulu.
function render(context: MarkdownModuleContext<T>): MarkdownModuleView<T> {
  const view = createOwnRenderer(context.container, context.data)
  return {
    update(nextContext) { view.update(nextContext.data) },
    destroy() { view.destroy() },
  }
}
```

Jde o schematický příklad; `T` a `createOwnRenderer` dodává modul. Továrna
bloku pro tento výsledek zachová DOM i instanci při změně dat a zavolá
`update` s aktuálním kontextem. Runtime musí používat nová data z předaného
kontextu a při `destroy` uvolnit vlastní listenery, observéry a renderer.
Dosavadní `void` nebo návrat cleanup funkce zůstává kompatibilní a zachovává
dosavadní znovuvykreslení. `signal` a ochrana proti pozdním výsledkům platí
pro oba režimy.

Graf tuto volitelnou cestu používá pro aktualizace jedné instance ECharts a
její běžné animované přechody. Nemění uložené JSON ani verzi schématu,
nepřidává datové transformace nebo podporu více grafových enginů.

## Ukládání, spolupráce a ověření

Rich JSON je autoritativní. Markdown obsahuje čitelné náhradní výstupy a
projekce označuje ztrátu významu (`markdownLossy`). Rich-only komponenty se
v profilu markdown-safe nenabízejí. Schéma nelze měnit za běhu; sada
povolených pluginů musí být známá při otevření dokumentu.

Modul může volitelnou funkcí `markdown(data)` v `defineMarkdownBlockModule`
vrátit Markdown, který dialekt editoru načte zpět na stejná data; takový blok
není `markdownLossy`. `undefined` ponechá textový výstup `text(data)` a blok
zůstává ztrátový. Blokový zdroj se zapisuje po řádcích pod prefixem nadřazeného
kontejneru (citace, položka seznamu), zdroj inline modulu je jeden řádek. Zpět
ho převezme `parseHTML` modulu nad obalem, který jádro vytvoří z Markdownu;
hook novou syntaxi do parseru nepřidává. Diagram bez popisku se zapisuje jako
```` ```mermaid ````, matematika jako `$$` řádky, ```` ```math ```` nebo
`$…$` ([dialekt editoru](../../src/Atlas.Markdown/README.md#pasting-and-dropping-foreign-wiki-markdown)).
Informační boxy (`> [!NOTE]` …) a zvýraznění (`==text==`) patří do dialektu
také. Rozbalovací blok editor nemá (sbaluje se každý nadpis); uložený se při otevření stane nadpisem s obsahem.

Komponentu jmenného prostoru `a-` (`a-contact`, `a-chart`) modul popisuje volbou
`dialect: { tag, lossy, write, read }` místo `markdown`. Čtení a zápis patří do
líně načítaného modulu pluginu (`registerMarkdownDialectForm(() => import('./markdown'))`),
který se načítá s dialektem; synchronní `lossy(data, document)` zůstává v pluginu,
aby kontrola ztrátovosti nic nenačítala, a vrací `true` právě tehdy, když by `write`
vrátil `undefined`.

Nové vestavěné bloky mění rich schéma pro spolupráci. Při nasazení dodržte
[postup verzování a migrací](collaboration-schema.md); klienti odlišných
revizí se nesmějí připojit ke stejnému Yjs dokumentu bez migrace.

Ukázka všech komponent je v galerii `/?story=modules`, včetně samostatného
readonly rendereru a skutečného vCard downloadu. `pnpm check` kontroluje
architekturu, typy, jednotkové testy, zabalený npm artefakt a produkční galerii.
Browserové testy modulů mají vlastní konfiguraci bez potřeby backendu:

```sh
pnpm --dir tests/e2e exec playwright test --config playwright.modules.config.ts
```

Použité primární specifikace: [Apache ECharts modular imports](https://echarts.apache.org/handbook/en/basics/import/),
[vCard RFC 6350](https://www.rfc-editor.org/info/rfc6350/),
[Mermaid security](https://mermaid.js.org/config/usage.html),
[KaTeX options](https://katex.org/docs/options.html).

## Interní kontakty, import souborů a živý zdroj

Kontakty rozlišují automatický, interní a externí vzhled pomocí `kind`.
`directory.resolveInternal(contact, { signal })` dodává host aplikace; knihovna
nepředpokládá existenci firemního endpointu. Viz [API kontaktů](../../src/Atlas.Markdown/src/plugins/contacts/README.md).

`MarkdownEditor` a `EditorSurface` přijímají volitelné `showMarkdownSource`.
Výchozí hodnota je false. V dokumentové variantě nabídnou horní bublinu s živou
Markdown projekcí pouze pro čtení, také v readonly. Compact ji nikdy nezobrazuje.
Rich komponenty mohou mít zjednodušený textový fallback; panel na to upozorňuje.

Mermaid se upravuje přímo pod diagramem: lišta otevře zdroj a popisek,
změny se zapisují průběžně, vykreslení má 200ms debounce. Ctrl/⌘ Z a redo
používají historii dokumentu. Zavření editoru zdroje změny nezahazuje.
Neplatná syntaxe zachová poslední platný obrázek, prázdný zdroj je platný stav.
Barvy a fonty sledují téma editoru. HTML a konfigurační direktivy zůstávají zakázané.

Pluginy `contacts` a `diagrams` mají schemaVersion 2 a migraci 1→2.
Vlastní datové objekty zůstávají version 1. U lokálních rich hodnot použijte
běžnou migrační cestu; u spolupráce dodržte migraci dokumentu a klientské
shody popsané v [collaboration-schema.md](collaboration-schema.md).
Nepřepisujte pouze metadata živého Yjs dokumentu.
## Viewport activation and export preparation

Block-module data, validation and text fallback remain synchronous. The expensive runtime is activated when the module approaches the viewport (300 px margin), receives pointer/focus interaction, or is selected through the editor's `NodeSelection`. Inline modules activate immediately. Environments without `IntersectionObserver` also activate immediately. Activation is one-way: scrolling an already rendered module away does not destroy its focused controls or retained view.

All module roots in the same owner document share a queue of at most two initial renderer activations or readiness preparations. A cancelled queued operation never starts. Running work keeps its slot until it actually settles, including a third-party renderer that ignores its abort signal. Such an uncooperative renderer cannot be forcibly terminated by this DOM API; the readable fallback remains available. The queue does not schedule every internal animation, theme reaction or synchronous retained-view update.

Every deferred, queued or loading module retains a labelled text preview from its saved data. Runtime failure keeps that preview and exposes the existing retry action. Rendering UI does not change the document or create undo steps. Redraws reserve the currently measured height while waiting; initial fallback geometry is based on its readable content, not a promise to predict the exact height of an unseen diagram.

Native `beforeprint` activates all mounted modules. A synchronous print cannot await imports, so modules that are not ready retain their explicit text preview and carry `data-md-print-fallback="true"` for that print boundary. Runtime state is exposed as `data-md-render-state` (`deferred`, `queued`, `loading`, `ready`, or `fallback`). Use the explicit asynchronous API before an export that requires all mounted runtime attempts to finish:

```ts
import { prepareMarkdownModulesForExport } from '@atlas/markdown/core'

const result = await prepareMarkdownModulesForExport(renderRoot, { signal })
// { ready, fallback }: fallback is an explicit readable representation, not missing content.
// The host decides whether fallback quality is sufficient for this particular export.
```

The call captures the modules contained by `renderRoot` when invoked; it does not activate another editor's roots or freeze a live document. Export a stable readonly snapshot when exact content consistency is required. Live replacement or destruction of a captured module may abort preparation; newly inserted modules need another call. Abort rejects the caller's wait promptly. It does not cancel shared module rendering that the reader may still need. Removing a module or destroying its view aborts its owned work and prevents late results from replacing current DOM. Static/readonly renderers use the same activation and fallback rules.

Retained runtimes can implement optional `prepare(): Promise<boolean>` to describe their current asynchronous paint. Returning the view object alone is insufficient when its renderer continues asynchronously. The block factory awaits this method during initial activation and explicit export, and after retained updates; `false` retains a labelled source fallback. The built-in chart waits for ECharts' `finished` event, including its initial animation. The diagram waits for Mermaid and decoding of its generated SVG image. A pending retained repaint exposes the latest saved source synchronously for printing while preserving the previous visual for continuity. KaTeX syntax errors are reported as fallback rather than successful visual preparation.

A retained view may declare `fallback: 'runtime'` when it always supplies the
current readable source and its own status, including during failed or pending
preparation. In that case the factory does not add a second generic status/source
over a nonempty runtime. An empty runtime still gets the generic fallback. This
does not turn a failed `prepare()` into success: export and `data-md-render-state`
continue to report fallback. Mermaid uses this contract to keep the latest source,
last valid image and a single explanation of syntax errors.

Custom live-source controls can use `trackMarkdownComposition(element)` and
`isMarkdownComposition(event, editor?)` from `/core`. While composition is active,
leave Escape, Enter, undo/redo and composing `beforeinput` events to the IME.
Dispose the composition tracker with the control. Standard module forms already
apply the shared IME guard.
