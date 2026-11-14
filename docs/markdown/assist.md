# Volitelný modul `@atlas/markdown/assist`

Návrhy u bloku (značka, seznam na Alt+Enter, varovný pruh) a ukotvený panel návrhu změny (před/po). Modul jen **nabízí**: nic nevloží sám, zápis vždy provede hostitel po Použít. Veřejné API je zmrazené v [README balíčku](../../src/Atlas.Markdown/README.md#optional-assist-module-atlasmarkdownassist); živá ukázka je v galerii „Volitelný modul / Návrhy u bloku“ (`web/playground/src/gallery/examples/AssistExample.vue`).

## Hranice: oddělitelnost (rozhodnutí 2026-10-04)

- Vlastní vstup `package.json` `exports["./assist"]`, vlastní Vite lib entry (`assist`) a vlastní stylopis `./assist/style.css` (kopírovaný jako stylopis neviditelného inkoustu, takže se nikdy nesloučí do `style.css`).
- Core, `./vue`, kořen `.` ani žádný `./plugins/*` ho **nikdy neimportují**, ani dynamicky, ani jako typ. Modul sám importuje jen veřejné `@atlas/markdown/core` a nepoužívá Vue ani transport.
- Bez `createAssistPlugin` v `plugins` editoru nemá vliv na chování, DOM, CSS, schéma, klávesnici ani velikost bundle. Import modulu nemá vedlejší efekty (žádné styly, žádné globální posluchače, žádný zásah do slovníku `markdownStrings`).
- Plugin nepřidává uzly, značky ani atributy dokumentu: žádné `schemaVersion`, žádná nová položka `CollaborationSchema.KnownHostSchemas`. Značky, seznam a panel jsou mimo `contenteditable`; tón varování, pruh, podtržení, dosvit a zamíchání jsou dekorace, které se nikdy neserializují, nejdou do historie ani do Yjs.
- Hostitel modul načítá líně a podmíněně (`import('@atlas/markdown/assist')`) a stylopis importuje spolu s ním.

**Ověření** (každá kontrola má negativní kontrolu v historii větve):

| Kontrola | Kde |
| --- | --- |
| Žádný soubor mimo `src/assist/` neimportuje `src/assist/*` ani `@atlas/markdown/assist` (statické, dynamické i typové importy, `.vue` skripty) | `tests/contracts/architecture.mjs` |
| `assist/` importuje jen `core/index.ts` / `@atlas/markdown/core`, žádné Vue, Yjs, SignalR | `tests/contracts/architecture.mjs` |
| Celý graf (včetně `import()`) z každé jiné lib entry modul nedosáhne; entry `assist` existuje | `tests/contracts/architecture.mjs` |
| Žádný jiný stylopis neobsahuje selektor `.md-assist` | `tests/contracts/architecture.mjs` |
| Z žádného jiného veřejného vstupu není v buildu dosažitelný chunk s kódem `src/assist/` (staticky ani přes `import()`); žádný jiný stylopis v `dist` nemá `.md-assist` | `tests/package/bundle-budget.test.mjs` |
| `./assist` přidá k `./core` jen svůj chunk, nic líného; rozpočty `assistEntry` a `assistCss`; `richEager` a `globalCss` beze změny čísel | `tests/package/bundle-budget.test.mjs` |
| Editor bez pluginu: žádné rozšíření, panel, zkratka ani DOM modulu, beze změny hodnoty | `src/assist/plugin.test.ts` („without registration“) |
| Balíček z tarballu: export, stylopis, typy a import bez DOM | `tests/package/consumer/public-exports.mjs`, `assist-contract.ts` |

## Chování

**Kdy se analyzuje** (pravidla klidu, specifikace Atlas 2.0 §5.7):

- po opuštění bloku, který autor upravil (Enter, šipka, klik); pouhý průchod kurzoru existujícím textem nic nespouští;
- po `idleMs` (1 500 ms) nečinnosti v upraveném bloku s alespoň `minLength` (12) znaky;
- hned po vložení (E6); Shift+vložení nespouští nic;
- nikdy během IME, v readonly, ani během běžícího Použít;
- Alt+Enter bez značky analyzuje blok s kurzorem (`trigger: 'request'`) a otevře seznam, případně s textem „Pro tento odstavec nejsou žádné návrhy.“

Zdroje dostanou `signal`, který se zruší při změně bloku; pozdní výsledek zahodí i generation guard. Rozsahy z výsledku se mapují přes transakce, které mezitím proběhly. `block.text` má jeden znak na pozici dokumentu (tvrdý zlom `\n`, jiný inline atom U+FFFC), takže `block.rangeOf(start, end)` převede nález regulárního výrazu na rozsah dokumentu.

**Značky:** nejvýše jedna běžná značka na editor: u bloku s kurzorem, nebo u právě dokončeného bloku, dokud kurzor zůstává v bloku, který po něm následuje (Enter). Značky `tone: 'warning'` zůstávají u svých bloků i bez kurzoru. Značka je skutečné `button` s názvem „Návrh: {titulek}“, `tabindex=-1`, mimo `contenteditable` v překryvu registrovaném přes `registerMarkdownOverlayForElement`, polohovaná floating-ui 32 px vlevo od bloku (vedle úchytu bloku, do kterého nezasahuje). Objevení značky se neoznamuje a fokus se nepřesouvá.

**Seznam:** sdílený `createSuggestionList` + `createSuggestionPopup` (stejné řádky, navigace a registr aktivního panelu jako `/`, `@` a `[[`). Fokus zůstává v textu (`aria-activedescendant`). Šipky a Home/End vybírají, Enter = Použít, Escape zavře (spotřebuje událost), **Tab zavře a nikdy nepoužije** (výchozí chování Tabu zůstává), Delete návrh zahodí, Alt+Enter seznam zavře. Při otevření živá oblast ohlásí „{n} návrhy pro tento odstavec.“ Patička: „Proč tento návrh?“ (rozbalí `reason`) a „Nenabízet v této stránce“ (potlačí `kind`). Výběr řádku podtrhne rozpoznaný úsek.

Zdroj může vrátit také informační `MarkdownAssistNotice` (`informational: true`, bez `apply`). Zobrazí se stejnou značkou a v seznamu, ale Enter seznam jen zavře a dokument nemění. Hodí se pro rozpoznaný úsek, jehož cíl nelze ověřit; nedělá z něj ověřenou komponentu.

**Použít:** modul znovu ověří zmapovaný rozsah a nezměněný text (bez rozsahu celý blok). Jinak ve stavovém panelu (`placement: 'status'`; bez Vue editoru jako poznámka pod blokem) `role="status"` ohlásí „Návrh už neodpovídá textu.“ a nic nezapíše. Otevřený seznam zůstává otevřený přes úpravu spolupracovníka, aby Enter mohl stav ohlásit; vlastní psaní seznam zavře. Zápis hostitele je ohraničen `closeMarkdownHistory` před i po; `context.dispatch(command, { replaceRange })` nastaví výběr na úsek a zapíše jeden samostatný krok Zpět. `context.range` vrací `null`, jakmile se úsek změnil. Po úspěšném Použít návrh ze seznamu bloku odejde i se svými alternativami nad stejnými slovy (návrhy, jejichž rozsah se překrývá), i když zápis text bloku nezměnil (značka odkazu přes slova, karta pod blok); ostatní návrhy bloku zůstanou a značka zmizí s posledním. Nic se tím nezahazuje pro relaci: pozdější úprava bloku je smí nabídnout znovu.

**Zahodit:** tentýž `id` se do konce relace editoru nenabídne, „Nenabízet v této stránce“ potlačí celý `kind` (`onDismissed(suggestion, 'suggestion' | 'kind')`). Sekundární akce varovného pruhu (např. „Je to jen příklad“) návrh po doběhnutí také zahodí.

**Readonly, odpojení, zničení:** všechny značky, seznam, dekorace a stavová zpráva zmizí, probíhající `suggest` i Použít dostanou `abort`.

**Panel návrhu** (`openMarkdownProposalPanel`, také `context.propose`): ukotvený u rozsahu přes `openMarkdownContextPopover({ variant: 'panel' })`, jeden na editor, bez křížku. Před/po: u přepsaného textu slovní rozdíl (`del` přeškrtnuté, `ins` podtržené, nikdy jen barvou), u struktury (tabulka, seznam) starý obsah přeškrtnutý a nový vykreslený přes schéma editoru. Textová alternativa `summary` („Odebráno 7 slov, přidáno 2 slova“). Použít zapíše jeden krok Zpět (`dispatchMarkdownHistoryOperation`), Upravit před použitím zapíše totéž a vybere výsledek k úpravě, Zrušit / Escape / klik mimo / readonly / `signal` nezapíší nic. Změněný rozsah (i úpravou spolupracovníka) nikdy nepřepíše: Použít se zakáže s „Návrh už neodpovídá textu.“ Volitelný `write(range)` nahradí výchozí zápis vlastním příkazem hostitele (stále jeden krok Zpět).

**Ikona AI:** modul ji nikdy nekreslí sám; ikonu dodává zdroj. Pro výstup modelu (fáze 4) zdroj předá `ai: true` a ikonu cílového typu, modul pak u titulku ukáže štítek „AI“. Pravidla ho nenastavují nikdy.

**Napojení na Alt+F10:** `assistMarkersFor(editor, blockPos)` vrací skutečné viditelné značky daného bloku. Host je přidá do obecného `registerMarkdownBlockKeyboardHook`; geometrie překryvu ani odhad vzdálenosti k bloku nejsou kontrakt.

## Pohyb

Všechny pohyby jsou Web Animations (globální pravidlo omezeného pohybu editoru by CSS animace zkrátilo na 0,01 ms a informace by se ztratila).

| Prvek | Pohyb |
| --- | --- |
| Psaní | nic se neobjevuje |
| Značka | kroužek `rotate(-140deg) scale(.5)` → 0, 280 ms (`--md-assist-slow`); ikona scale .4 → 1, 240 ms se zpožděním 60 ms |
| Varování | plný jantarový kroužek, jedno zvlnění scale 1 → 2.4, opacity .7 → 0, 600 ms `cubic-bezier(.2,.7,.3,1)`; pruh se rozvine 280 ms |
| Seznam | opacity 0, `scale(.94) translateY(-4px)` → 1, 160 ms (`--md-assist-menu`); ven 120 ms `scale(.97)` |
| Výběr řádku | úsek se podtrhne zleva, `background-size` 0 → 100 % 1.5px, 200 ms |
| Použít, inline komponenta | morph z šířky úseku: opacity .35, scale .96 → 1.03 (70 %) → 1, 380 ms (`--md-assist-morph`), pak dosvit 1,6 s |
| Použít, nový odkaz | podtržení se protáhne zleva a barva přejde do akcentu, 280 ms, pak dosvit |
| Použít, text nebo blok | dosvit 1,6 s (`--md-assist-glow`, `--md-assist-glow-color`) |
| Zahození značky | scale .6 + fade, 160 ms |
| `effects.conceal` | znaky se po 60 ms zamíchají na tečky (7 kol, pak tečky), jen dekorace |
| Panel návrhu | přerušovaný SVG obrys (`stroke-dasharray` 6/5); škrty se dokreslí zleva 240 ms, vložené části vyrostou z rozostření 300 ms, odstup 40 ms; Použít: `solidify` 6/5 → 11/0 za 360 ms (`--md-assist-solidify`, ease-in-out) a panel se složí, pak dosvit |

**Tokeny** (od 0.10.0 na jádru, `styles/motion.css`, README „Motion tokens“): `--md-assist-fast` = `--md-motion-fast` 120 ms, `-base` = `--md-motion-base` 200 ms, `-slow` = `--md-motion-slow` 280 ms, `-menu` = `--md-motion-target` 160 ms, `-glow` = `--md-motion-glow` 1 600 ms, `-ease` = `--md-motion-ease`, `-ease-io` = `--md-motion-ease-io`, `--md-assist-time-scale` = `--md-motion-time-scale`; vlastní zůstávají jen `-morph` 380 ms, `-solidify` 360 ms a `--md-assist-glow-color` (akcent 18 %). Hostitel mapuje jednou jádro (`--md-motion-*`) a modul ho zdědí; staré názvy `--md-assist-*` na editoru dál přebíjejí. Délky z tokenů se berou tak, jak je hostitel vyřešil (zpomalení nesou tokeny); `--md-assist-time-scale` násobí jen pevné literály prototypu (zvlnění 600, ikona 240/60, kroky `conceal`). Modul čte délky z editoru při každé animaci (sdílený `markdownMotion` z `/core`) a kopíruje tokeny na své překryvy.

**Omezený pohyb** (`prefers-reduced-motion`, `data-md-motion="reduced"` na předkovi nebo `reducedMotion()` hostitele, čtené při každé animaci): z klíčových snímků zůstane jen opacity, barva a pozadí, nejvýše 160 ms lineárně. Rotace, zvlnění, morph a posuny se vypnou, dosvit je krátké prolnutí pozadí, `conceal` ukáže tečky hned.

## Testy

`src/assist/plugin.test.ts`, `proposal.test.ts` a `motion.test.ts` (Web Animations nahrazené záznamem volání) pokrývají: nulový vliv bez registrace, životní cyklus značky, průchod bez analýzy, IME / readonly / Shift+vložení, abort při změně bloku, Alt+Enter, Tab a Escape bez použití, jeden krok Zpět, odchod použitého návrhu a jeho alternativ i při nezměněném textu, zastaralý návrh, zahození a potlačení druhu, trvalá varování, žádnou transakci ani aktualizaci Yjs při navrhování, panel (před/po, souhrn, Použít / Upravit / Zrušit, Escape, klik mimo, readonly, signal, struktura) a klíčové snímky a délky prototypu včetně omezeného pohybu a škálování tokenem.

## Výběr a asynchronní pracovní návrh (0.11)

`selectionMenu` je volitelné hostitelské menu (`label`, optional `icon` nebo framework-neutral `renderIcon(container)`, `available(editor, range)`, `actions`). Používá stejnou formátovací bublinu a block selection controls; unavailable akce má `hideWhenDisabled` a žádný focusable DOM. Core nezná AI. `emptyAction` je volitelný hostitelský placeholder widget; host určuje, kdy je stránka prázdná a kdy je akce dostupná. Widget není součást hodnoty, schématu ani historie.

`projectMarkdownAssistSelection(editor, range)` vytvoří disposable lokální snapshot ještě před čekáním. Posílá se **jen `text`**, nikdy `before` ani uzly. Standardní Markdown se serializuje existujícím veřejným codec `serializeMarkdownContent`; komponenty, obrázky, odkazy a nestandardní marks jsou opaque `⟦c:<16 lowerhex>:N⟧`. Tajné/concealed uzly a kolize rezervovaných delimitérů vrátí null. Detekci tajemství v obyčejném textu a capability kontrolu dodává host před projekcí/odesláním.

`range` mapuje vzdálené úpravy a je null po změně původního slice (včetně attrs/marks), zničení nebo `dispose()`. `restore(text)` použije existující `parseMarkdownContent`, vyžaduje přesné původní pořadí a počet tokenů, odmítne nové komponenty, host directives a secret assignment, a zástupce nahradí pouze původními lokálními uzly. Po použití snapshot vždy dispose.

`openMarkdownProposalPanel` buď dostane stávající `after`, nebo `load(signal, mappedRange)` vracející `{after, before?, summary?}`. Tentýž panel ukáže čekání/chybu/retry; zavření/readonly/destroy abortují signal. Retry znovu zachytí aktuální range a host musí znovu zkontrolovat bezpečnost a vytvořit novou projekci. Pozdní odpověď se ignoruje. Použít i Upravit jsou jeden undo krok; Upravit ponechá výsledek vybraný a do opuštění bloku ukáže dočasný status mimo dokument. Žádné automatické zápisy.

`onShown` reportuje první skutečný marker/list display daného suggestion ID v editor session. `onApplied`, `onDismissed` a optional proposal `onFeedback('up'|'down')` jsou pouze host callbacky. Modul nemá síť ani telemetrické úložiště; chyba callbacku shown/feedback nenaruší editor. Feedback controls jsou přítomné pouze pokud host callback dodá.

### Release note for 0.11.0 (unreleased)

Additive host seams provide safe opaque projections through the existing Markdown codec, selection and empty-document actions, asynchronous proposal prepare/retry/cancel and feedback. Independent sources show fast rules while a model request is pending. Proposals revalidate exact mapped content and fresh host authorization before one-undo apply. Public marker ownership, contact avatars and caret handling remove host geometry/selection workarounds. Reduced motion preserves the detail tabs. No model transport or AI imports enter the core.

The required P4 implementation measures 63,068 raw / 18,514 gzip bytes of **isolated optional assist JavaScript**, and 11,868 / 3,490 bytes of its separately imported CSS. The deliberate budget update keeps under 5% headroom for those two optional assets only; core, rich eager, global CSS, lazy reachability and no-assist import assertions stay unchanged.


The final Atlas slash regression exposed late outline plugin registration destroying an active suggestion view before its debounced search. A small stable hook is now installed during controller construction, while highlight ownership/rendering remains in the lazy outline chunk. Outline mount/unmount never reconfigures plugins; mapping runs in plugin state before redraw, including heading deletion and document shrink. Targeted regressions cover the open page picker and independent owners, direct host decorations, remount and shrink.

Against the `100caffe0986b20bfeacfcd23bfb710a43deaf82` local pack, rich eager JavaScript measured **622,430 raw / 193,691 gzip bytes before**, **623,123 / 194,127 after**: **+693 raw / +436 gzip** (0.111% raw). The owner explicitly approved only the rich raw budget change **608 → 609 KiB** for the stable lifecycle hook; gzip remains **190 KiB**, and CSS, optional assist and lazy reachability gates remain unchanged. No delay, retry, timeout increase or lower parallelism masks the race.
