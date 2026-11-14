# Zdrojový editor stránky (Markdown s chráněnými bloky)

Stav: schválený návrh k implementaci, 26. 9. 2026. Navazuje na [detail workspace](openapi-detail-workspace.md) a [UI rozhodnutí](decision-editor-component-ui.md).

## Účel a rozhodnutí

Správce prostoru (v Atlasu space Admin + WikiAdmin) opraví špatně importovanou nebo poškozenou stránku v Markdownu. Bloky, které Markdownem neprojdou beze ztráty, jsou chráněné.

| Otázka | Rozhodnutí |
| --- | --- |
| Editor | **Stejný Monaco jako YAML zdroj OpenAPI**; žádný CodeMirror ani další editor. |
| Otevření | Přepínač **Dokument \| Zdroj** nad stránkou; chování návrhu jako YAML (Použít / Zahodit / Pokračovat, dirty guard). V Atlasu od 4.1 je to jediný přepínač dvou pohledů (viz „Přepínač v Atlasu“ níže); `MarkdownDetailWorkspace` se záložkami Dokument/Zdroj zůstává v balíčku editoru a používá ho už jen jeho galerie (`PageSourceTabsPrototype.vue`). |
| Výkon | Monaco se nikdy nenačítá při načtení stránky; jen při otevření záložky, případně prefetch na hover/focus. Skeleton bez posunu layoutu. Rozpočty velikosti v CI. |
| Základ | Živý dokument: při otevření se nejprve uloží vlastní rozpracované úpravy. |
| Dokument během návrhu | Dokud je ve Zdroji neuložený návrh, do dokumentu se nepíše. V Atlasu je dokument po dobu Zdroje skrytý (zůstává připojený) a návrat na něj se na návrh nejdřív zeptá; v galerii balíčku je záložka Dokument jen pro čtení. Cizí živá změna dokumentu rozpracovaný návrh označí `stale` a zablokuje Použít. |
| Uložení | Hostitel nahradí stránku na serveru jako obnova z historie: nová pracovní revize + auditní událost; odmítnout při cizích neuložených živých úpravách; 409 při změně verze se zachováním návrhu. Není to undo krok (dokumentovaná výjimka z pravidla 6). |
| Ne-admini | Záložka se nezobrazí; zůstává stávající read-only Markdown náhled (`showMarkdownSource`). Monaco se jim nenačítá. |
| Granularita (MVP) | Celé top-level bloky; inline ztrátový obsah (podtržení, YouTrack odkaz…) chrání celý odstavec. Inline tokeny později. |
| Chráněné bloky | Celý blok lze smazat nebo přesunout; úprava uvnitř a duplikace (stejné id dvakrát) je chyba s diagnostikou. |
| Reimport ze ZIPu | Později. |

## Architektura

- **`@atlas/markdown/source`** (nový veřejný vstup podle vzoru `concealment`; smí ho importovat pluginy i `./vue`, core ne): tenký vstup s líným `mountSourceEditor`, headless session a typy.
  - `source/monaco/environment.ts` — jediný vlastník `MonacoEnvironment` (registr workerů s ref-countingem, řetězení hostitelského prostředí).
  - `source/monaco/editor.ts` — `mountSourceEditor(host, { language: 'yaml'|'json'|'markdown', value, ariaLabel, readonly, readOnlyMessage, modelScope, protectedBlocks, onChange, onProtectedEditRejected, onEscape, setup })` → `{ getValue, setValue, setLanguage, setDiagnostics, setReadonly, revealLine, focus, layout, destroy }`. Veřejné typy mají předponu `Source*`/`SourceEditor*` (`SourceProtectedBlock`, `SourceDiagnostic`, …); `MarkdownSourceDiagnostic` je týž typ jako `SourceDiagnostic`. Ctrl+M (macOS Ctrl+Shift+M) přepne Tab na přesun fokusu, klávesnice z editoru vždy odejde.
  - `source/protectedFence.ts` — jediná syntaxe chráněné fence (regex otevření/uzavření, id `[A-Za-z0-9_-]{1,64}`, předpona `Chráněný blok: `), kterou sdílí session i guard.
  - `source/monaco/protectedBlocks.ts` — guard na `model.pushEditOperations` (všechny uživatelské cesty úprav tudy vedou; undo/redo ne a nemusí), dekorace se zámkem, re-scan po každé změně, akce „Vybrat chráněný blok“, aria ohlášení.
  - `source/session.ts` — `createMarkdownSourceSession({ value, plugins })` → `{ text, blocks, protectedBlocks, apply(text), destroy }`. Chráněný je blok, který je ztrátový podle `codec.getMarkdownLossy(block, { ignoreIdentity: true })` (nepočítá se, co session přenáší sama: `headingId`, prezentace obrázku a odkaz na stránku, viz „Co zdroj přenáší sám“), neprojde skutečným round tripem, nebo je existující code block s jazykem `md-protected*`; plus kontrola celého textu. `apply` rozdělí text podle fencí, nahradí je původními uzly, zarovná bloky LCS (nad ~4 mil. buněk tabulky jen společný začátek a konec) a znovu použije původní JSON; `headingId` přenese i na upravený nadpis. Beze změn vrací přesně původní hodnotu.
- **OpenAPI:** `plugins/openapi/monacoSetup.ts` je líný setup chunk (schéma, `configureMonacoYaml`, JSON mód a registrace workerů přes `setup`); `plugins/openapi/sourceEditorLoader.ts` je v eager vstupu pluginu a nese veřejné `mountOpenApiSourceEditor` a `prefetchOpenApiSourceEditor(): boolean` (jako `prefetchSourceEditor`, `false` při Save-Data). `modelScope` obou stran je konstanta `OPENAPI_MODEL_SCOPE` (`plugins/openapi/modelScope.ts`). Jazyk YAML/JSON přepíná `setLanguage` podle přípony souboru.
- **`./vue`:** `MarkdownSourcePanel.vue` + `useMarkdownSourceDraft({ value, plugins, readonly, save })` → `{ text, dirty, stale, busy, diagnostics, apply, discard, reload }`; `save` je hostitelský endpoint, nikdy živý controller. `MarkdownDetailWorkspace` dostane volitelný `label`.

### Přepínač v Atlasu (4.1)

`DocumentEditor.vue` má jeden přepínač `.source-switch` (skupina dvou tlačítek s `aria-pressed`, posuvný palec ze `segmentedThumb.ts` jako ostatní segmentové přepínače) a pod ním dva pohledy: `.document-pane` (`v-show`, editor zůstává připojený, takže přepnutí nemění jeho instanci, výběr ani historii undo) a `.source-pane` (`v-if`, `MarkdownSourcePanel`). Žádná hlavička, titulek ani „Zavřít“: přepínač stojí na místě a stav „otevřená plocha se záložkou Dokument“ neexistuje.

- **Zdroj:** hostitel nejdřív odešle vlastní rozpracované úpravy (`openPageSource` v `App.vue`), pak `openSource()`. Kurzor přejde do Monaca, jakmile je připojené (panel ho bere od záložky i od stisknutého tlačítka přepínače, nikdy z pole, kam čtenář mezitím odešel).
- **Dokument** (klik, Enter/mezerník na tlačítku, nebo Esc ve Zdroji): čistý zdroj se zavře hned, rozpracovaný se zeptá `MarkdownDraftConfirmation` (Pokračovat v úpravách / Zahodit / Použít a zavřít). Kurzor se vrátí do textu dokumentu tam, kde byl. Esc v Monacu nejdřív slouží Monacu (zavře hledání, návrhy, zruší výběr nebo další kurzory); teprve když nemá co zavřít, ohlásí ho panel událostí `escape` (`onEscape` v `mountSourceEditor`). Monaco totiž u každého Esc volá `preventDefault`, takže zvenčí to rozlišit nejde. Esc v dialogu patří dialogu.
- **Návrh nezmizí bez dotazu:** odchod ze stránky, obnova z historie a zamknutí stránky se ptají přes `resolveSourceDraft()` (Pokračovat / Zahodit, bez Použít). Výjimka: ztráta práva nebo selhání editoru zdroj zavře hned, protože návrh už uložit nelze.
- **Výška:** `.source-pane` sahá od svého začátku ke spodnímu okraji okna (na telefonu k liště stránky `PageMobileBar`) s odstupem 16 px, nejméně 320 px. Hlavička stránky nad ním nemá pevnou výšku a sloupec stránky se neroztahuje, proto se začátek panelu měří (`fitSource`: při zobrazení a při změně velikosti okna); žádná konstanta odhadující výšku hlavičky. Stránka se při přepnutí neposouvá.
- **Pohyb:** palec přepínače klouže (`useSegmentedThumb`), zobrazený pohled najede třídou `atlas-view-enter`; při omezeném pohybu se palec nekreslí a pohled se jen krátce prolne bez posunu (tokeny `motion.css`).

### Panel a návrh (fáze 4, implementováno)

- `useMarkdownSourceDraft(options)` vrací navíc `baseText`, `protectedBlocks`, `status` (`closed`/`loading`/`ready`/`error`), `notice` (`{ kind, detail? }`, slova doplní panel), `canApply`, `readonly`, `revision`, `open()` a `close()`. Eager je jen tenká reaktivní skořápka; chování (`vue/sourceDraft.ts`), vstup `./source`, session i Monaco se načtou až v `open()` (zároveň se spustí `prefetchSourceEditor()`). `close()` session zničí a přeruší běžící uložení; při zániku scope se zavře sám. `MarkdownSourcePanel` je async komponenta (při selhání stažení chunku ukáže `role=alert` na místě; nové vykreslení načítání zopakuje); drobná `MarkdownDraftConfirmation` je eager, protože se musí ukázat hned — eager rozpočet `./vue` zůstal beze změny limitu. `value`, `plugins` a `readonly` jsou ref/getter.
- `save(value, text, { signal })` vrací `void | { ok: true } | { ok: false, reason: 'conflict' | 'live-edits' | 'error', message? }`; vyhozená chyba se `status: 409` je konflikt. Úspěch: uložená hodnota se stane novým základem (text se znovu vygeneruje, editor začne novou historii); pozdější příchod téže stránky od hostitele nic nemění. Konflikt: návrh zůstává, `stale`, Použít zablokováno do „Načíst aktuální verze“ (hostitel mezitím aktualizuje `value`). Cizí živé úpravy: návrh zůstává, `stale`, Použít lze zopakovat. Chyba: zpráva, návrh zůstává.
- Změna základu: čistý návrh ji převezme (debounce 250 ms), rozpracovaný se označí `stale` („Stránka se mezitím změnila“). Dokud je fokus v editoru, čistý návrh se nepřegeneruje (nerozbije kurzor, scroll ani undo čtenáře): převezme změnu po odchodu z editoru, a začne-li čtenář psát na starém základu, návrh se hned označí `stale`. Během ukládání se Zahodit ignoruje. Reload/Zahodit znovu generují z aktuální hodnoty; panel před zahozením rozpracovaného návrhu žádá potvrzení.
- Diagnostiky z `session.apply` jsou značky v Monacu i seznam pod editorem (klik = přechod na řádek); chyby blokují uložení, varování ne.
- `MarkdownSourcePanel :draft` (volitelně `ariaLabel`, výchozí „Zdroj stránky“; události `applied` a `escape`, Esc, který Monaco samo nevyužilo): jediný zpožděný stav `role=status` panelu (po 300 ms „Připravuje se zdroj stránky…“, pak „Načítá se editor zdroje…“, `aria-busy`), mezitím skeleton s textem, který uvnitř `aria-busy` zůstává tichý, a Monaco s chráněnými bloky; oprávnění změněná během načítání se po připojení znovu použijí, nahrazený `draft` editor připojí znovu; Použít až po připojení editoru. Selhání Monaca → `role=alert` se „Zkusit znovu“ a čitelný `<pre>`; selhání session → alert se „Zkusit znovu“. Readonly: editor jen pro čtení, bez Použít/Zahodit. Během ukládání je editor jen pro čtení. Panel importuje pouze malý vstup `./source`; Monaco i session jsou za `import()`.
- Galerie: příběh `page-source` hostí `PageSourceTabsPrototype.vue` (Dokument/Zdroj, falešné uložení: úspěch, 409, cizí úpravy, chyba; odebrání oprávnění; téma). e2e `tests/e2e/page-source.spec.ts`.
- Omezení: Monaco při živé změně motivu nepřepíná světlý/tmavý základ (volí jej při připojení).


Formát chráněného bloku (sloupec 0, prázdný řádek před a za):

````
```md-protected b3
Chráněný blok: Graf „Tržby 2025“
```
````

### Co zdroj přenáší sám (doplněk 11. 11. 2026)

Ochrana je výjimka: měření nad skutečnou wiki (352 stránek, 11 068 bloků) ukázalo, že většinu zamčených běžných bloků (seznamy kroků se snímky, odstavce a tabulky s adresami) zamykaly čtyři věci, které text beze ztráty unese. Blok s nimi je proto upravitelný:

| Co | Jak |
| --- | --- |
| Prezentace obrázku (šířka, zarovnání, ořez, popisek) | V textu je `![alt](adresa)`. Obrázek s adresou, kterou stránka už obsahuje, dostane při `apply` prezentaci toho původního; nová adresa je nový obrázek s výchozí prezentací. Dva obrázky se stejnou adresou a různou prezentací zůstávají chráněné. |
| Odkaz na stránku (`pageReference`) | V textu je `[název](adresa)`. Odkaz se stejnou adresou i názvem je při `apply` zase odkazem na stránku; s jiným textem je to obyčejný odkaz. |
| Text, který jen vypadá jako adresa | Zdroj čte doslovně: holá adresa nebo doména je text, odkaz se píše `<adresa>` nebo `[text](adresa)`. Stejně čte Markdown server (`MarkdownToRich`). |
| Víc mezer za sebou uvnitř řádku | Zůstávají, jak jsou napsané. |

| Seznamy stejného druhu hned za sebou | Mezi nimi je řádek `<!-- -->`; bez něj by je Markdown přečetl jako jeden seznam. Řádek lze i napsat nebo smazat. Jinde než samostatně mezi dvěma seznamy je to obyčejný text. |
| Tabulka, kterou GFM neunese (spojené buňky, víc bloků v buňce) | Píše se jako ```` ```a-table ```` a o ochraně rozhoduje skutečný round trip. |

Přenáší se jen to, co text na stránce určuje jednoznačně: adresa obrázku ve dvou velikostech, nebo odkaz, který je jinde na stránce obyčejným odkazem se stejnou adresou i textem, se nepřenáší (blok, který by tím ztratil, zůstane chráněný). Odkaz s vlastním popiskem (`"tip"`) zůstává odkazem. Mezery se drží jen uvnitř řádku: text bloku začíná a končí bez nich, jak ho čte editor dokumentu.

Dál se neliší blok kódu s jazykem `""` od bloku bez jazyka a seznam, jehož uložená těsnost (`tight`) neodpovídá obsahu, se ukáže tak, jak ho Markdown přečte.

### Nadpis se jmenuje v textu (doplněk 11. 11. 2026)

Nadpis, který nese víc než text, se ve zdroji píše se svým ID, jak ho píše server (`RequirementHeadingMarkdown.cs`, port v `src/Atlas.Web/src/projector/requirementHeadingMarkdown.ts`, shodu hlídá `tests/fixtures/content/requirement-heading-lines.json`):

```
### Certifikát {#portal.cert state=waiting_for_input kind=inbound party="Jan Novák" system=NIA stateLabel="čeká na IT"}
## Starý název {#novy-nazev}
```

- Požadavek: `state` (planned, in_progress, waiting_for_input, done, blocked, not_applicable), `kind` (inbound, outbound; internal se nepíše), `party`, `partyContact`, `system`, `from`, `stateLabel`. Hodnota s mezerou, uvozovkou, složenou závorkou nebo zpětným lomítkem je v uvozovkách. Stejný zápis píše editor i do uloženého Markdownu, jak to dělá server. Změna `state=` je tentýž zápis atributu jako ze stavové nabídky; nic dalšího nabídka neukládá.
- Nadpis s aliasy bez stavu píše `{#id}` jen zdroj; aliasy se vrátí nadpisu se stejným ID.
- Identita je ID, ne místo: přesunutý oddíl si nese stav, stranu i aliasy; smazaný nadpis nic nepředá; nový nadpis bez `{…}` je obyčejný; `{state=planned}` bez ID je nový požadavek. Pojmenovaný je nadpis, kterému čtení dalo ID z jeho bloku (ID smí být kterýkoli token bloku). Obyčejný nadpis (jen `headingId`) se píše jako dřív a ID si drží podle místa, ale nikdy nepřevezme ID pojmenovaného nadpisu.
- `{#id}` bez dalšího u existujícího požadavku znamená „tento nadpis, jak byl“ (jako `RichMarkdownMerge` na serveru): stav i ostatní atributy zůstávají. Udělat z požadavku obyčejný nadpis jde v editoru dokumentu.
- Změní-li se `state=` a vlastní název stavu (`stateLabel`) zůstane, jak byl, název se smaže, jako ve stavové nabídce a v MCP; nově napsaný název zůstane.
- Chyby na řádku, Použít se neprovede: ID, které na stránce není; stejné ID dvakrát; neznámý atribut, stav nebo druh; `partyContact` bez `party`; řádek nadpisu, kterému zmizel blok, zatímco nadpis zůstal (požadavek by zanikl: vraťte `{#id …}`, nebo smažte celý nadpis).
- Požadavek bez trvalého ID (dočasné `v3-h2` nebo žádné) se ve zdroji nejmenuje, proto zůstává chráněný.
- Název končící `#` se píše s `\#`, jinak by ho Markdown četl jako uzavření nadpisu (oba writery).
- Blok čte jen zdroj stránky: vložený nebo importovaný Markdown a text asistenta v editoru dokumentu ho nechávají textem, jako dřív. Čtečku přináší plugin požadavků Atlasu; samotný balík ji nemá, nadpis s aliasy v něm proto zůstává chráněný.

### Chráněný blok ukazuje celý text (doplněk 11. 11. 2026)

Pod popiskem je ve fenci celý text bloku tak, jak ho píše Markdown writer (každá položka, buňka, adresa odkazu, text komponenty): dá se číst i hledat (Ctrl+F), ale nikdy se nečte zpět; `apply` vrací původní uzel a jakoukoli změnu uvnitř hlásí jako chybu na daném řádku. Řádek textu, který by začínal fencí, se ukáže s vlnovkami (`~~~`), aby blok nemohl uzavřít. `MarkdownSourceBlock.text` a `MarkdownSourceProtectedRange.text` nesou tento text, `endLine` už není `startLine + 2`.

Chráněné zůstává (měření po úpravách: 108 bloků mimo staré bloky požadavků, na 57 z 352 stránek):

- nadpis s aliasy uvnitř citace, informačního boxu nebo seznamu (zápis `{#id}` platí jen pro nadpis nejvyšší úrovně) a řádek závislostí pod požadavkem (odkazy `atlas://…` s vlastním tvarem);
- přehled požadavků: oba writery ho píší jako vypočtený řádek „Stav nasazení: …“, který se nečte zpět (```` ```a-overview ```` mají jen ostatní druhy přehledu);
- tabulka, na kterou odkazuje graf: čtení dává buňkám nová id (dialekt N4), samostatně upravená tabulka by se od grafu odpojila;
- tabulka bez řádku záhlaví nebo se záhlavím v těle a s jiným zarovnáním buňky než sloupce: writery pro ni volí GFM, které to neunese;
- komponenty bez zápisu, který by se četl zpět (OpenAPI, diagram s popiskem, skrytý obsah, YouTrack úkol, graf s tabulkou mimo stránku), podtržení, zarovnání odstavce a nadpisu, šířky sloupců, barvy buněk, výšky řádků;
- text s mezerou na začátku nebo konci bloku a adresa, kterou čtení přepíše (znaky mimo ASCII v odkazu).

### Markdown dialekt editoru (doplněk 28. 9. 2026)

Zdroj píše stejný Markdown jako editor ve všech cestách (načtení hodnoty, `insertContent`, vložení, `apply`) a až na doslovné čtení (výše) ho stejně i čte, pokud schéma obsahuje cílový uzel nebo značku. Tyto bloky proto nejsou chráněné a upravují se jako text:

| Zápis | Výsledek |
| --- | --- |
| `#### Nadpis` | Nadpis úrovně 4. `#####` a `######` se převedou na úroveň 4 s varováním. |
| `> [!NOTE]`, `> [!TIP]`, `> [!WARNING]`, `> [!IMPORTANT]`, `> [!CAUTION]` + citovaný obsah | Informační box `note` / `tip` / `warning` / `important` / `caution`; všech pět se zobrazí zpět beze změny. Ostatní typy Obsidianu se mapují jako při vložení; sbalitelný `> [!tip]- Název` vytvoří nadpis nad informačním boxem (o úroveň pod nadpisem před ním; sbaluje se každý nadpis). Obyčejná citace zůstává citací. |
| `==text==` | Zvýraznění; ne v inline kódu, cíli odkazu, autolinku ani holé URL (ani ve zdroji, kde holá URL zůstává textem). |
| ```` ```mermaid ```` | Diagram (plugin `diagrams`); diagram s popiskem zůstává chráněný. |
| `$$` … `$$`, ```` ```math ````, `$…$` | Blok / řádková matematika (plugin `math`). Výraz, který tyto zápisy nepřenesou beze změny, zůstává chráněný. |

Text, který dialekt jen připomíná, se neescapuje: doslovné `==x==` nebo `$x$` v odstavci a dřívější blok kódu s jazykem `mermaid` nebo `math` by se po zpětném načtení změnily, proto je chrání kontrola round tripu jako celý blok (`round-trip`). Wiki.js třídy, wiki odkazy, poznámky pod čarou a další zápisy cizích wiki platí dál jen pro vložení a přetažení souboru.

## Fáze

| Fáze | Obsah | Dny |
| --- | --- | --- |
| 0 | Spike + regresní harness guardu (psaní, IME, paste, drop, cut, multi-cursor, přesun řádků, replace-all, undo/redo) | 0,5 |
| 1 | `./source` entry, sdílené prostředí a editor, převedení OpenAPI, architecture contract, exporty; OpenAPI e2e | 1,5 |
| 2 | Headless session, fixture z reálných importovaných stránek | 3–3,5 |
| 3 | Chráněné bloky v Monacu | 2–2,5 |
| 4 | Panel + draft composable, `label`, galerie `PageSourceTabsPrototype.vue` (fake save, 409, odebrání práv) — hotovo | 2 |
| 5 | e2e Chromium/WebKit, NVDA/VoiceOver, dokumentace, bundle check | 1,5–2 |
| Atlas | Endpoint, audit, oprávnění, 409, reload kolaborace | 3–4 |

Rizika: závislost guardu na připnutém Monacu 0.52.2 (test všech cest úprav), IME ve WebKitu, replace-all zasahující blok se v MVP odmítne celý, stránky s mnoha inline ztrátovými značkami budou z velké části chráněné.

## Načítání na vyžádání (doplněk)

- Jediný líný chunk `src/source/monaco/editor.ts` přes cachovaný `import()` v `src/source/index.ts`; seznam přibalených Monaco funkcí v jednom souboru (`slim.ts`): find, folding, clipboard, contextmenu, linesOperations, multicursor, wordOperations, bracketMatching, cursorUndo, indentation, format, toggleTabFocusMode (Ctrl+M, žádná past klávesnice), hover, message/readOnlyMessage, gotoError, suggest. Tokenizéry Markdown/YAML registrovat přímo (bez `*.contribution.js`, aby nevznikl druhý fetch).
- JSON jazyk + worker, `monaco-yaml` a OpenAPI schéma v líném OpenAPI setup chunku (`plugins/openapi/monacoSetup.ts`); Markdown nepotřebuje worker (vypnout word-based suggestions a quickSuggestions; detekce odkazů ani zvýraznění výskytů se nepřibalují).
- Veřejné typy `./source` nesmí obsahovat Monaco typy; `createMarkdownSourceSession` je async.
- `prefetchSourceEditor()` jen na hover/focus prvků vedoucích ke zdroji (tlačítko úpravy zdroje OpenAPI, tlačítko „Zdroj“ přepínače Atlasu, `tab-intent` z `MarkdownDetailWorkspace` v galerii balíčku); no-op při `saveData`; nikdy při startu stránky ani v idle.
- Čekání: okamžitě read-only `<pre>` se skutečným textem a stejnými metrikami písma jako Monaco (bez posunu layoutu), `aria-busy`, status „Načítá se editor zdroje…“ po ~300 ms (jen když hostitel nehlásí načítání sám přes `aria-busy`), Použít až po načtení; chyba → `role=alert` se „Zkusit znovu“, `<pre>` zůstává.
- Kontrakt v `tests/contracts/architecture.mjs`: z žádného vite entry se po statických importech nedostat na `monaco-editor`/`monaco-yaml`. e2e síťové testy: žádný `sourceEditor-*`/worker request při načtení stránky, otevření workspace v režimu dokumentu ani na záložce API.
- Odhad knihovny po doplnění: 11,5–13,5 dne (+3–4 dny Atlas).
