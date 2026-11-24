# Atlas 5.2 — analýza aktualizace JS knihoven (Tiptap, ProseMirror, Yjs a ostatní)

Stav: **analýza (2026-11-24) a krok 1 provedený 2026-11-25** (§ 0). Analýza vznikla pokusnými instalacemi ve vedlejším worktree
(vráceno na `--frozen-lockfile`); typecheck a vitest `@atlas/markdown` s prioritou BelowNormal. Krok 2 (`y-tiptap` 3.3.1) a ostatní
body jsou dál jen doporučení.

## 0. Krok 1 provedený: Tiptap 3.35.0 a ProseMirror

`@tiptap/*` 3.31.3 → 3.35.0 (peer `^3.35.0`), přes `@tiptap/pm` `prosemirror-model` 1.25.13 a `prosemirror-view` 1.43.1.
`@tiptap/y-tiptap` zůstává 3.0.9 s patchem. Postup: porovnány zdroje (`src`) všech změněných balíčků Tiptapu mezi 3.31.3
a 3.35.0 a changelogy ProseMirroru, hledáno, co z toho se dotýká našeho kódu; teprve pak testy.

Co ve zdrojích zasáhlo Atlas a co se s tím stalo:

| Změna upstreamu | Dopad na Atlas | Řešení |
|---|---|---|
| **Input rules** (`core/InputRule.ts`): napsaný znak se vloží do dokumentu jako vlastní transakce **před** pravidlem, `range` sahá až za něj, před konverzí `closeHistory`. Cesty bez psaní (compositionend, Enter, `insertContent`) zůstaly po starém | `projector/requirementInline.ts` („do pátku “) předpokládal, že znak v dokumentu není (`range.to - range.from === match.length - 1`): podmínka by byla vždy nepravdivá a fráze by přestala fungovat. Pravidla emoji, math a callout fungují beze změny (čteno, otestováno) | handler pozná psaný znak podle velikosti dokumentu (o 1 delší než dokument editoru) a frázi bere bez něj; test `requirementInline` prošel |
| Totéž: Ctrl+Z po automatické konverzi (`:smile:`, `$x$ `) | vrací text tak, jak byl napsán, **včetně spouštěcího znaku** (dřív bez něj) | test `chipText` upraven, zapsáno v CHANGELOGu editoru; je to chování, ne chyba |
| `prosemirror-model` 1.25.12: atributy se validují v `NodeType/MarkType.create` (jen explicitní hodnoty) | neplatný atribut už nejde postavit; y-tiptap takový element **maže ze sdíleného dokumentu**; 2 testy obrázkové politiky; **materializer v .NET** ho dál ukazoval | testy upraveny (filtr transakcí se kryje ručně podvrženým uzlem); manifest schématu má u atributů s vlastním validátorem `validated: true`, `YjsDocumentMaterializer` je odmítne (`InvalidAttributeValue`); 4 případy corpusu (callout `variant`, `concealedContent.reveal`, 2 hostile) jsou nově „odmítnuto“ a vypadly z merge corpusu |
| `prosemirror-view` 1.43: `mousedown` v editoru vždy `preventDefault`, myš řeší knihovna | test `modules` (stisk mimo formulář) | test hlídá `click`, ne `mousedown`; **v prohlížeči neověřeno** |
| `prosemirror-view` 1.43: DOM výběr se čte před klávesou | `DomCaretFirst` je asi zbytečný; `flushDomSelection` ale zůstává nutný (test „klik a pak text najednou“ bez něj padá) | nic neodstraněno, kandidát po ověření v prohlížeči |
| `FindAndReplace` 3.33: první `goToNextResult()` vybere zvýrazněný první výsledek | panel hledání: první Enter výsledek vybere, neskočí na druhý | test upraven; `SearchPanel` ukazuje „1 z N“ a první Enter ho teď opravdu vybere |
| `Table` 3.35: `TableView.update` při změně atributů tabulky vytvoří node view znovu, `View: null` pro `columnResizing`, nový plugin `preserveCellSelectionOnContextMenu`, `colspan/rowspan = 1` se nevypisuje | naše `MarkdownTable` mapuje pluginy rodiče a nahrazuje `appendTransaction` `tableEditing` (stále platí); překreslení tabulky při změně jejích atributů může rušit overlay (`features/table/overlayView.ts`) | testy prošly; **v prohlížeči neověřeno** |
| `Collaboration` 3.31.4+: plugin s `filterTransaction`, který u transakce `inputRule` volá `undoManager.stopCapturing()`; `getUndoManager` hází bez `yUndoPlugin` | undo kolaborace a naše `CollaborationHistoryBoundary` | testy kolaborace prošly |
| `Link` 3.31.4+: `unlinkTrailingWhitespace` v `appendTransaction` autolinku (i pro vzdálené transakce) | mezera za odkazem se z odkazu odebere místně; u vzdálené změny se do Yjs nezapíše (stejná třída jako dřívější přidávání autolinků) | nízké riziko, sledovat |
| `toggleList` 3.32: převod mezi typy seznamů s různými položkami | `toggleTaskList` na odrážkovém seznamu ho nově převede; sekvence `editor-session` v corpusu se změnila | fixtures `editor-session` záměrně nepřegenerovány (kontrola corpusu na starých datech prochází) |
| `NodeView.ignoreMutation` 3.31.4+: výběr v listovém node view čte ProseMirror | vlastní node views s vnitřními prvky | testy prošly; **v prohlížeči neověřeno** |

Fixtures: přegenerováno jen to, co upgrade skutečně mění. Opakovaná regenerace materializer corpusu na **čistém** `develop`
dává změny i bez upgradu (`editor-concurrent`, `fixture-imported-*`: uložená data se od generátoru rozešla), proto jsou ty
adresáře ponechané. `Atlas.Yjs` writer fixtures: 0 rozdílů z 857 případů, mění se jen verze v metadatech (`prosemirror-model` 1.25.13).

Ověřeno: typecheck editoru i Web, vitest editoru (2 922) a Web (4 584), `architecture.mjs`, rozpočet balíčku, `Atlas.Yjs.Tests` (867),
`Atlas.Markdown.Sync.Tests` (814). **Neověřeno:** `Atlas.Server.Tests` (testovací Postgres v Podmanu na tomto stroji nepřijímal spojení),
Playwright e2e, reálný prohlížeč (myš ve view 1.43, tabulky, kurzory, výkon na velké stránce).

Zůstává k rozhodnutí: `TypedTextInput` (kit) i `boardTextInput` (tabule, komentář uvádí chybu jádra 3.31): obě mají testy nebo
jinou sémantiku, jádro 3.35 samo `**x**` napsané najednou převede, ale „každý znak přes pravidla“ a „všechno kromě posledního“ se liší.


## 1. Shrnutí

1. **Tiptap u nás není fork ani vendorovaná kopie.** Je to stock Tiptap **3.31.3** (asi 40 balíčků, sjednocené přes `overrides`),
   upravený jen veřejným `.extend()`/`.configure()` a **dvěma pnpm patchi**: `@tiptap/y-tiptap@3.0.9` (260 řádků, 8 zásahů)
   a `reka-ui@2.11.0` (230 řádků, 3 zásahy). Vlastní je kolaborační vrstva `src/Atlas.Markdown/src/collab` (~2 400 řádků).
2. **Nový major Tiptapu neexistuje.** Poslední je **3.35.0** (20. 11. 2026), tedy stále řada 3. Změna 3.31.3 → 3.35.0 jsou minory,
   ale s sebou táhne novější ProseMirror (view 1.43, model 1.25.13) a hlavně zásadně přepsaný `@tiptap/y-tiptap` (3.0.9 → 3.3.1).
3. **Skutečné riziko není Tiptap, ale tři věci kolem něj:** (a) `y-tiptap` binding (náš patch se mění ze „zásahu do 3.0.9“ na
   rozhodnutí, co z něj ještě potřebujeme), (b) ProseMirror view 1.43 (myš a výběr řeší knihovna, ne prohlížeč), (c) **serverové
   porty v .NET** (`Atlas.Yjs`, materializer v `Atlas.Markdown.Sync`) svázané s přesnými verzemi Yjs, lib0, y-tiptap a prosemirror-model.
4. **Jediný nový major napříč ostatními závislostmi je TypeScript 7** (blokován `vue-tsc`) a „0.x major“ **Monaco 0.58**
   (rozbité hluboké importy). Vše ostatní je na poslední verzi nebo je to minor/patch (řeší druhá session).
5. **Doporučení pro Tiptap:** aktualizovat v 5.2, ale **ve dvou krocích a jako samostatná změna mimo „bezpečné“ aktualizace**
   (krok 1: Tiptap 3.35 + ProseMirror; krok 2: `y-tiptap` 3.3.1 bez patche, se zachováním jen toho, co upstream nepokrývá).

### Souhrnná tabulka

| Knihovna | Teď → latest | Co se láme | Dotčeno | Úsilí | Riziko | Doporučení |
|---|---|---|---|---|---|---|
| `@tiptap/*` (core, rozšíření, vue-3, pm) | 3.31.3 → 3.35.0 | žádný major; 3 testy z chování rozšíření (find-and-replace, undo input rule, myš) | 170 souborů v editoru, 56 ve Web | **M** (2–3 dny) | střední | **5.2, krok 1** |
| `prosemirror-model` / `-view` (přes `@tiptap/pm`) | 1.25.11 → 1.25.13 / 1.42.4 → 1.43.1 | model: validace atributů v `NodeType.create`; view: myš a výběr řídí knihovna | testy obrázků, moduly, materializer .NET | **M** | **střední až vyšší** | **5.2, krok 1** (s e2e) |
| `@tiptap/y-tiptap` + náš patch | 3.0.9 → 3.3.1 | patch se nedá aplikovat; 4 testy chování patche padají; 2 testy workaroundu „už nepadá“ | `collab/*`, `Atlas.Yjs`, fixtures | **M–L** (3–5 dní) | **vysoké** | **5.2, krok 2** s bránou výkonu a e2e |
| `yjs`, `y-protocols`, `lib0` | 13.6.33 / 1.0.7 / 0.2.117 (lib0 latest 0.2.120) | Yjs 14 je teprve `rc.30` | `Atlas.Yjs` (port v C#) | **L+** pro Yjs 14 | vysoké | Yjs 14: **později** (až stabilní); ostatní nic |
| `tiptap-markdown` | 0.9.0 = latest | – (komunitní; existuje oficiální `@tiptap/markdown` 3.35) | ~2 500 řádků kolem (`kit`, `tableMarkdown`, `documentCodec`, `markdownInput`) | **L** na přechod | vysoké | **později / nikdy**, dokud je udržovaný |
| `reka-ui` + patch | 2.11.0 = latest | patch sedí na latest | 64 importů, 3 zásahy | **S** na upstream PR | nízké | **5.2**: poslat PR a patch odstranit |
| `typescript` | 6.0.3 → 7.0.2 | `vue-tsc` nemá JS API TS 7 | oba `package.json`, typecheck | **S** po podpoře Volaru | nízké | **později** |
| `monaco-editor` | 0.52.2 → 0.58.0 | `exports` v `package.json`, přesun souborů; 25 chyb typů, build padá | ~30 hlubokých importů ve 4 souborech + aliasy Vite | **M** | střední | **později** (bez důvodu) |
| `@scalar/api-reference` | 1.78.1 → 1.83.0 | není major; testy `source` + `openapi` (144) projdou | iframe se standalone bundlem | **S** | nízké | **5.2** s bezpečnými aktualizacemi |
| ostatní (`vitest`, `vue`, `vue-tsc`, `jsdom`, `@vueuse/core`, `@vue/test-utils`, `tailwind-merge`, `pnpm`) | minor/patch | – | – | – | – | druhá session |

`pnpm outdated -r` (24. 11. 2026) nevrací žádný další major: `echarts`, `mermaid`, `katex`, `lowlight`, `highlight.js`, `vue-flow`,
`floating-ui`, `yaml`, `uqr`, `tailwindcss` a další už jsou na poslední verzi.

## 2. Jak je Tiptap upravený

| Vrstva | Co přesně | Kde |
|---|---|---|
| Verze | stock 3.31.3, všechny `@tiptap/*` připnuté a sjednocené přes `overrides` | `src/Atlas.Web/package.json`, `src/Atlas.Markdown/package.json` (peer `^3.27.4`) |
| Patch | `@tiptap/y-tiptap@3.0.9` | `src/Atlas.Web/patches/@tiptap__y-tiptap@3.0.9.patch` |
| Patch | `reka-ui@2.11.0` (Tooltip owner, `useSize` vynucený layout, `useIsUsingKeyboard`) | `src/Atlas.Web/patches/reka-ui@2.11.0.patch` |
| Veřejné `.extend()` | `autolinkText`, `outsideInlineCode` (paste rules), `dialectCodeBlock`, `headingToText`, `caretHiddenAtGap`, `quickListBackspace`, `ReaderTaskItem`, `MarkdownTable` (nahrazuje `tableEditing` `appendTransaction`), `RichImage`, `SafeFindAndReplace`, `LazyInvisibleCharacters`, `UniqueID`, `CodeBlockLowlight` s vlastním node view | `core/kit.ts`, `core/tableMarkdown.ts`, `plugins/editor-tools`, `plugins/heading-anchors` |
| Kontrakt `storage.markdown` | serializéry v rozšířeních podle `tiptap-markdown` | `kit.ts`, `tableMarkdown.ts`, `plugins/secrets/markdown.ts` |
| Soukromé API | `view.domObserver.flush()` (`core/internal.ts`), `__pastedCells` z `prosemirror-tables` (`engine/pasteInputGuard.ts`), přímé přiřazení `editor.options` (`core/controller.ts:145`), `extensionManager.destroy()` | viz soubory |
| Vnitřnosti bindingu | `binding.beforeTransactionSelection`, `.mapping`, `.prosemirrorView`, `.type`, `Y.Item._item`, `stack-item.meta` | `collab/historyBoundary.ts`, `remoteSelection.ts`, `remoteRemoval.ts`, `tableRepair.ts`, `SignalRYjsProvider.ts:962` |
| Serverové porty | `Atlas.Yjs`: C# port Yjs 13.6.33, lib0 0.2.117, `updateYFragment` z y-tiptap 3.0.9 a `Node.fromJSON` z prosemirror-model 1.25.11; `Atlas.Markdown.Sync`: materializer dokumentu | `src/Atlas.Yjs`, `src/Atlas.Markdown.Sync`, fixtures v `tests/` |

Veřejná rozšíření přes `.extend()` jsou stabilní kontrakt; při pokusu se jich dotkly jen tři testy (viz § 4). Soukromé API a
vnitřnosti bindingu jsou v 3.3.1 pořád přítomné a jejich testy (`historyBoundary`, `remoteRemoval`, `tableRepair`) prošly.

### 2.1 Patch `@tiptap/y-tiptap`: osm zásahů a stav v 3.3.1

| # | Zásah patche | Upstream 3.3.1 | Co s tím | Úsilí |
|---|---|---|---|---|
| 1 | `ySyncPlugin.view`: nepřestavět dokument z Yjs, když se plugin view znovu vytvoří (pozdní `registerPlugin`, bubble menu, drag handle) | **není** | upstream PR, nebo registrovat pluginy předem | M |
| 2 | `_typeChanged`: nahradit jen změněný úsek, `mapping.clear()` jen při změně potomků | **je** (`replaceChangedChildren`, po celých blocích; text-rozdíl uvnitř bloku ne) | zrušit; změřit výkon na velké stránce | S + měření |
| 3 | zapomenout bloky, které plugin změnil mimo Yjs (oprava tabulky) | **není**, ale testy `tableRepair` a `block-concurrency` prošly bez něj | ověřit v prohlížeči, pravděpodobně zrušit | S |
| 4 | `MetaEntry.apply`: `domObserver.flush()` před dekoračním updatem | **není**; PM view 1.43 uvádí podobnou opravu, ale test v jsdom bez patche padá | ověřit v reálném prohlížeči | S–M |
| 5 | aktualizace dekorací kurzorů jednou za animation frame | **není** (`timeout(0)`) | vlastní caret plugin nebo upstream | M |
| 6 | kurzor kolegy, který předběhl svou editaci, zůstane (`previous` v `createDecorations`) | **není** | vlastní caret plugin nebo upstream | M |
| 7 | awareness listener ignoruje změny vlastního klienta | **není** | přesunout do `collab/caretAwareness.ts` (už je to proxy) | S |
| 8 | vlastní kurzor se publikuje až po první klávese/kliknutí, ne při programovém fokusu | **není** | přesunout do `caretAwareness.ts` (zahodit `cursor` do první interakce) | S–M |

Po přechodu zbývají zásahy 1, 4–6; 7–8 se dají přesunout do našeho kódu bez patche. Podle zásady „bez patchů třetích stran“
doporučuji **vlastní caret plugin** (zásahy 5–6, ~200 řádků, testy `remoteApply` už chování pinují) a upstream PR pro zásah 1.
Rebase patche na 3.3.1 (z 260 na ~100 řádků) je nouzová varianta.

### 2.2 Co 3.3.1 přináší navíc

- **Opravuje pád výběru, který dnes obcházíme** (`collab/remoteSelection.ts`, „Position N out of range“). Oba záznamy
  z `fixtures/y-tiptap-selection-crash.json` v 3.3.1 už nepadají; test sám říká „když to přestane padat, workaround může pryč“.
  Odpadne soubor a rozšíření `CollaborationRemoteSelection`.
- `onInvalidContent` / `contentError` (3.33): obsah, který editor neumí, zůstane ve sdíleném dokumentu a editor se jen pozastaví.
  Dnes `y-tiptap` takový prvek maže (viz `docs/markdown/collaboration-schema.md`, `document-materialization.md`). Náš
  `enableContentCheck` ani `onInvalidContent` nepoužívá. Volitelné vylepšení, ne podmínka (M, mění sémantiku materializeru).
- Nové awareness pole `lastSelection` (3.33.1): větší payload každého klienta; `caretAwareness.ts` na něj nereaguje, ale přenos
  vzroste. Změřit.
- 3.32.3 maže „persistent position migrations“ (`__tiptap_position_migrations:*`) z dokumentu při otevření. Naše dokumenty je
  nikdy neměly (nepoužili jsme 3.1–3.2), takže je to no-op; ověřeno čtením zdroje `clearPositionMigrations`.

## 3. Ostatní knihovny

**ProseMirror** (jen přes `@tiptap/pm`, žádný přímý import `prosemirror-*`). Tiptap 3.35 chce `prosemirror-view ^1.42.6`.
Pozor: obnova lockfilu v rámci rozsahů přinese view 1.43.1 a model 1.25.13 i bez změny verze Tiptapu, takže ji nedělat jako
„bezpečnou“ aktualizaci mimo krok 1.
- `prosemirror-model` 1.25.12: validace atributů se provádí v `NodeType/MarkType.create`. Padají 2 testy obrázkové politiky
  a **materializer corpus** (uzly s neplatným atributem, např. callout, se nově „nedají postavit“). Port `PmModel.cs`
  (1.25.11) a corpus se musí zvednout společně.
- `prosemirror-view` 1.43.0: myš a výběr řeší knihovna, `adjustSelectionFromDOM`, „DOM selection se čte před klávesou“. Může
  zrušit naše `flushDomSelection`/`DomCaretFirst` (soukromé API), ale mění i chování mousedown (padá test modulů). Potřebuje e2e.

**Yjs.** 13.6.33 je latest; 14 je `rc.30` (npm tag `beta`). Port `Atlas.Yjs` se drží formátu v1 a chování 13.6.x; přechod na 14 je
přepis portu a fixtures. Čekat na stabilní verzi a rozhodnout zvlášť.

**`tiptap-markdown` vs `@tiptap/markdown`.** 0.9.0 je poslední (publikováno 13. 11. 2026), tedy udržované. Oficiální
`@tiptap/markdown` existuje, ale naše dialekt (`engine/markdownDialect.ts`, `a-` jmenný prostor, tabulky, sekce) stojí na
kontraktu `storage.markdown`. Přechod je L bez nové hodnoty, dokud komunitní balíček žije.

**`reka-ui`.** 2.11.0 je latest, patch na něj sedí. Tři zásahy jsou malé a jednotlivě odůvodněné v komentářích; každý je kandidát
na upstream PR (S). Po vydání odstranit `patchedDependencies`.

**TypeScript 7.0.2.** Balíček nemá JS API (`main` je `lib/version.cjs`, `./lib/tsc` není v `exports`): `vue-tsc 3.3.11` padá hned
(`ERR_PACKAGE_PATH_NOT_EXPORTED`). `vue-tsc 3.3.14` umí TS 7 jen přes alias `typescript → @typescript/typescript6`, takže
kontrolu pořád dělá engine TS 6 a nic se nezrychlí. `tsconfig` (Bundler, ES2022, strict, `paths` bez `baseUrl`) je s TS 7
kompatibilní. Počkat na nativní podporu ve Vue Language Tools.

**Monaco 0.58.0.** `package.json` má nově `exports` (`./*.js → esm/vs/*.js`) a jazyky se přesunuly
(`basic-languages/markdown/markdown.js` → `languages/definitions/markdown/markdown.js`). Typecheck: 25 chyb; `vite build` padá
na `UNLOADABLE_DEPENDENCY`. Přepsat: `source/monaco/slim.ts` (23 importů), `protectedBlocks.ts` (2, včetně interního
`base/common/platform.js`), `plugins/openapi/monacoSetup.ts` (2), `monaco-languages.d.ts`, aliasy ve `vite.config.ts`
(82–85), `scripts/editorSourceMap.mjs` (157–159) a `atlasBundle.mjs`, rozpočty balíčku. `monaco-yaml 5.5.1` deklaruje
`>=0.36`, kompatibilita nebyla ověřena. Bez bezpečnostního důvodu nespěchat.

**`@scalar/api-reference` 1.83.0.** Minor v řadě 1.x. Testy `src/source` a `src/plugins/openapi` (12 souborů, 144 testů) prošly;
vizuálně neověřeno (iframe se standalone bundlem, `scalar-standalone?url`).

## 4. Výsledky pokusů

Základ (beze změny): typecheck 35 s, vitest `@atlas/markdown` 212 souborů / 2 898 testů, 68 s na 64 vláknech.

| Pokus | Typecheck | Testy | Poznámka |
|---|---|---|---|
| Krok 1: `@tiptap/*` 3.35.0, `y-tiptap` 3.0.9 s patchem | v pořádku | **6 z 2 922 padá** | viz tabulka níže |
| Krok 1 + 2: navíc `y-tiptap` 3.3.1 bez patche | 3 chyby, jen v testech | **12 z 2 922 padá** | + 6 níže |
| TypeScript 7.0.2 | `vue-tsc` neběží | – | § 3 |
| Monaco 0.58.0 + Scalar 1.83.0 | 25 chyb | `source`+`openapi`: 144 v pořádku | build Monaca padá |
| Fixtures `Atlas.Yjs` (857 případů) znovu nahrané s y-tiptap 3.3.1 + PM 1.25.13 | – | **0 rozdílů** | jen metadata verzí |

Šest pádů už v kroku 1:

| Test | Příčina | Jistota | Oprava |
|---|---|---|---|
| `resourcePolicy` ×2 | model 1.25.12 validuje atributy při `create` (test staví neplatný uzel přímo) | potvrzeno changelogem | upravit testy (S) |
| `yjsMaterializerCorpus` | totéž: uzly s neplatným atributem se nedají postavit | potvrzeno diffem corpusu | znovu nahrát corpus, upravit .NET materializer, `YjsMaterializerCorpusTests` (M) |
| `editor-tools` (hledání, Enter) | 3.33.1: první `goToNextResult()` už vybere první výsledek | podle changelogu, nebisektováno | upravit test (S) |
| `chipText` (Ctrl+Z po input rule) | změna undo input rule v Tiptap core 3.31.4/3.34 | domněnka | ověřit a upravit chování nebo test (S–M) |
| `modules` (stisk mimo formulář) | pravděpodobně `prosemirror-view` 1.43 (mousedown) | domněnka | ověřit v prohlížeči (S–M) |

Přibývá v kroku 2: `remoteApply` ×4 (testy pinují chování patche: změna po textovém úseku, kurzor před editací, nezpracovaný klik,
publikace kurzoru až po interakci) a `remoteSelection` ×2 (workaround už není potřeba). Tři chyby typů v testech
(`historyBoundary.test.ts:31`, `remoteApply.test.ts:124,163`): `getState()` je nově `| undefined` a `binding.mapping` má typ `never`.
Produkční kód v `src/` prošel typecheckem bez chyby.

Neověřeno: Playwright e2e (Podman), reálný prohlížeč (kurzory kolegů, myš ve view 1.43, výkon na stránce ~750 KB),
.NET testy (`Atlas.Yjs.Tests`, `Atlas.Markdown.Sync.Tests`, `BrowserSeedParityTests`), vitest a `vue-tsc` části `Atlas.Web`.

## 5. Doporučené pořadí

1. **Teď (S, bez editoru):** Scalar 1.83; upstream PR do `reka-ui` (3 zásahy). Obnovu lockfilu nedělat jako „bezpečnou“
   aktualizaci, ať ProseMirror nepřijde bez kroku 2.
2. **Krok 1, 5.2 (M, 2–3 dny):** `@tiptap/*` 3.35.0 + `@tiptap/pm` jako jedna změna. Opravit 6 testů, znovu nahrát materializer
   corpus a upravit .NET materializer, projet Playwright e2e (myš ve view 1.43, tabulky, drag handle). `y-tiptap` zatím 3.0.9.
3. **Krok 2, 5.2 (M–L, 3–5 dní):** `y-tiptap` 3.3.1. Odstranit patch a `CollaborationRemoteSelection`, přesunout zásahy 7–8 do
   `caretAwareness.ts`, napsat vlastní caret plugin (5–6), poslat upstream PR pro zásah 1. **Brána:** změřit výkon vzdáleného
   příjmu na velké stránce a zkontrolovat kolegovy kurzory ve dvou prohlížečích. Když brána neprojde, krok 2 přesunout do 5.3
   a zůstat na 3.0.9 + patch.
4. **Později:** TypeScript 7 (po podpoře Volaru, S); Monaco 0.58 (M, jen s důvodem); `onInvalidContent` místo mazání neznámého
   obsahu (M); Yjs 14 až po stabilním vydání (L+).
5. **Nikdy / zatím ne:** přechod z `tiptap-markdown` na `@tiptap/markdown`, dokud je první udržovaný.

## 6. Hlavní rizika

1. **Kolaborace se rozbije jen v reálném prohlížeči.** Jednotkové testy chování patche padají záměrně a jsou v jsdom; kurzory,
   výběr a výkon na velké stránce se ověří až ručně nebo e2e.
2. **Serverové porty jsou svázané s přesnými verzemi.** Zvednutí `prosemirror-model` už samo mění, co se „dá postavit“, takže
   `Atlas.Yjs` (fixtures beze změny) i materializer (corpus se mění) se musí kontrolovat při každé změně verze, ne jen u `yjs`.
3. **ProseMirror view 1.43 mění zpracování myši.** Dotýká se node views, výběru buněk tabulky, drag handle a našich
   `mousedown` handlerů (19 souborů); pád jednoho testu je nejspíš první příznak.
4. **Patch zůstává, pokud ho nenahradíme.** Bez vlastního caret pluginu nebo upstreamu je rebase patche (~100 řádků) opakovaná
   daň při každé verzi y-tiptap, což je proti zásadě „bez patchů třetích stran“.
