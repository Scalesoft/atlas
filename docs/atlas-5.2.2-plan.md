# Atlas 5.2.2 — plán: opravy a srozumitelnost pro agenty

Stav: **schváleno k implementaci 2026-11-27** (vlastník: všechno včetně MCP jde do 5.2.2, ne do 5.3). Původně založeno jako „5.3“. Implementace běží po částech v samostatných sessions, každá z `main`.

Opravná verze: **bez kódového jména a bez znaku ovoce** (patch). Větev hotfixu začíná na `main` (5.2.1, `1dad90b0b`) a po vydání se slévá do `develop`, který už je na `5.3.0-dev`.

## 0. Cíl verze

Opravná verze: opravy a to, aby s Atlasem uměli pracovat i připojení agenti:

1. **Karta osoby po najetí se nesmí rozpadat.** Blok „Umí“ je nalepený na okraje vyskakovacího okna a místy přetéká (§ 1).
2. **Agent přes MCP musí vědět, jak se komponenty zapisují, a zápis nesmí tiše selhat.** Dnes to musel dohledávat ve zdrojovém kódu a nepoznaný informační box skončil na stránce jako citace s textem `[!WARNING]`. Platí pro všechny komponenty včetně budoucích: informační boxy, diagramy, vzorce, grafy, kontakty a ostatní vlastní (§ 2).
3. **Stav „Uloženo“ v záhlaví stránky** stojí osamoceně daleko od ostatních údajů řádku; po uložení se ztlumí a plný čip zůstane jen u problémových stavů (§ 1a).
4. **Menu „⋯“ u stránky ve stromu problikne a zavře se** (§ 1b); zásadní, menu musí zůstat otevřené.
5. **Vyhledávání má příliš malý text**; zvětšit pole dotazu i výsledky (§ 1c).
6. **Opakované `409` a `404` v konzoli** na instanci s nefunkčními menu; prošetřit společně s § 1b (§ 1d).

Nic nového se nestaví. Opravuje se vzhled existující karty a doplňuje se komunikace, kterou server už umí.

## 1. Karta osoby: blok „Umí“ přetéká a lepí se na okraj

### 1.1 Příznak

Ve vyskakovací kartě osoby (čtenářská karta kontaktu po najetí nebo kliknutí na zmínku) je řádek dovedností („Umí“, čipy témat a „Ohodnotit“) natlačený na okraje okna a v některých případech přetéká.

### 1.2 Pravděpodobná příčina (ověřit v prohlížeči)

Dohledáno ve zdroji, zatím ne potvrzeno na obrazovce:

- Blok vzniká v `skillsOf()` v `src/Atlas.Markdown/src/plugins/contacts/runtime.ts` jako `div.md-contact-skills` a přidává se **přímo do panelu**.
- Panel `.md-contact-reader > .md-context-popover-body` má v `src/Atlas.Markdown/src/plugins/contacts/style.css` `padding: 0 0 14px`, tedy bez bočního odsazení. Ostatní oddíly karty si 16 px odsazení dávají samy (`.md-contact-reader-profile`, `.md-contact-details`, `.md-contact-used`, `.md-contact-reader-footer`).
- `.md-contact-skills` boční odsazení nemá (`margin: -4px 0 14px`), takže čipy a záhlaví jdou od kraje ke kraji. Čip s dlouhým názvem je sice zkrácený elipsou, ale protože nemá okraj, vypadá to jako přetečení.

### 1.3 Návrh opravy

- Doplnit oddílu `.md-contact-skills` stejné boční odsazení jako sousední oddíly (16 px, ideálně ze stejné proměnné). Nevytvářet nový mechanismus: je to oprava jednoho pravidla.
- Zkontrolovat, jestli stejné rozložení nemá i `ContactSkillLine.vue` (Vue varianta téhož řádku v dock panelu a editoru kontaktu); tam má řádek vlastní styl a chybu mít nemusí.
- Zkontrolovat další oddíly stejné karty, které se přidávají mimo odsazené kontejnery.

### 1.4 Ověření

- Karta s mnoha a s dlouhými tématy, s hodnocením i bez, s „Ohodnotit“ rozbaleným, v šířkách 390, 1280 a 1920 px, ve světlém i tmavém tématu, při omezeném pohybu.
- Galerie (`/?gallery=1`, `SkillsExample.vue`, `ContactsExample.vue`): příklad musí ukazovat opravený stav; pokud zatím nemá kartu s mnoha tématy, doplnit ji.
- Pravidla z `AGENTS.md`: žádný horizontální posuvník, dlouhé názvy se zkracují s `title`, hover nic nehýbe.
- Test: jedno pravidlo tohoto typu se pohlídá nejlépe jednou browser specou nebo kontraktem na CSS; rozhodnout při implementaci, nepřidávat matici testů.

## 1a. Stav „Uloženo“ v záhlaví stránky vypadá odtržený

### 1a.1 Příznak

V řádku pod nadpisem stránky (screenshot: „VPN - Scalesoft“) je „Upraveno 27. 11. 2026 10:41“ vlevo a zelený čip „Uloženo“ až daleko vpravo, s velkou prázdnou mezerou mezi nimi. Čip působí, jako by plaval sám pro sebe, a nepatří k řádku, ve kterém stojí.

### 1a.2 Co je dohledáno (příčina zatím neověřena)

- Čip je `SaveState` v `<span class="document-save-state">` jako poslední položka řádku `.document-meta` v `App.vue` (kolem řádku 5025).
- `refresh-article.css` mu dává slot `order: 2; min-width: 88px; min-height: 24px`, aby příchod stavu „Ukládám…“ nepřelamoval řádek (záměrné, kvůli CLS). Řádek je `display: flex; flex-wrap: wrap; gap: 6px 8px`, položky mají `flex: none`.
- Před čipem jsou v řádku další položky, které mohou zabírat místo, i když nic neukazují: `document-status` (v `shell.css` `height: 3.1em; min-height: 3.1em`), `PagePdfExportStatus`, skrytá akce „Přidat ikonu“ (`order: 1`), případně čip ověření a štítky. Která z nich mezeru dělá, ze zdroje nepoznám.
- Záměr autora byl „stav končí řádek“; v praxi to znamená, že se při prázdných položkách mezi nimi a čipem nic neukáže a čip zůstane osamocený.

### 1a.3 Návrh

- Nejdřív ověřit v prohlížeči, co mezeru tvoří (vyšetřit rozměry položek řádku v čtecím i editačním režimu, s dockem otevřeným i zavřeným).
- Cíl: čip stojí hned za poslední viditelnou položkou řádku (nebo na pevném, předvídatelném místě), ale **rezervuje si místo** pro „Ukládám…“, aby se řádek nehýbal. Pravidla z `AGENTS.md` platí: žádné přeskakování, žádné posunutí textu, plynulý příchod.
- **Rozhodnuto (vlastník, 2026-11-27): po uložení se stav ztlumí.** „Uloženo“ po chvíli zeslábne na tichou ikonu a plný čip zůstává jen u „Ukládám…“, „offline“, „pozastaveno“ a „chyba“, tedy u stavů, které čtenář potřebuje vidět. Zbývá určit: za jak dlouho ztlumit (z tokenů `motion.css`, ne z literálu), jestli ikona dostane `title` a popisek pro čtečky obrazovky (musí: `role="status"` zůstává) a že další změna čip hned vrátí. Zeslábnutí je přechod průhlednosti, ne skok; při omezeném pohybu nejvýš 160 ms.
- Stejný stav používá i tabule (`BoardPageEditor.vue`, `data-board-status`); změnu zkontrolovat na obou místech a v galerii (`SaveStateExample.vue`).

### 1a.4 Ověření

Šířky 390, 1280, 1920, 2560 a 3440 px, dock a postranní panel otevřené i zavřené, čtecí i editační režim, stavy Ukládám → Uloženo → Offline → Chyba, omezený pohyb; měření posunu rozložení (CLS) při příchodu stavu se nesmí zhoršit.

## 1b. Menu „⋯“ u stránky ve stromu se otevře a hned zavře (zásadní)

### 1b.1 Příznak

V postranním stromu stránek klik na „⋯“ u řádku (a podle popisu i pravé tlačítko) menu jen **problikne**: otevře se a okamžitě zmizí, takže se nedá použít. Menu má zůstat otevřené, dokud ho uživatel nezavře (výběr položky, Esc, klik mimo). Uživatel má podezření, že to souvisí se zásahem AI agenta do struktury stromu; zatím nejde o potvrzenou souvislost. Priorita: **vysoká**, funkce je nepoužitelná.

**Druhý příznak (2026-11-27, screenshot):** stejně se chová menu „⋯“ v horní liště stránky (vedle citace, odkazu, spolupracovníků, hvězdičky a zámku): po kliknutí se okamžitě zavře. Prohlížeč je Microsoft Edge, stránka má otevřený panel Obsah a byla právě upravena (Upraveno 27. 11. 2026 12:29, publikováno). **Třetí příznak (2026-11-27):** stejně se chová menu na úrovni prostoru v levém panelu nad stromem (`SidebarSpace.vue`, `ActionDisclosure`). Horní lišta a menu prostoru používají `ActionDisclosure`, menu řádku stromu `openRowActionsMenu`. Tři různá menu se stejnou chybou ukazují na **společnou příčinu** (sdílený kód menu nebo něco, co překresluje stránku i strom), ne na chybu jedné komponenty.

### 1b.2 Co je dohledáno (příčina neověřena)

Menu řádku je `PageTree.vue` → `openMenu()` → `openRowActionsMenu()` (menu editoru, `docs/ux-concepts.md`: „Actions menu“, jedno pro celý strom). Z kódu plynou kandidáti, žádný nelze potvrdit bez reprodukce:

1. **Překreslení stromu zruší hostitele menu.** Menu se zavěšuje na element řádku (`host = rowElement(page)`). Pokud se strom obnoví (živá změna struktury, např. agent přes MCP přesouvá nebo přejmenovává stránky, nebo periodické načtení stromu), řádek se vymění a menu zmizí s ním. To by odpovídalo podezření na agenta.
2. **Zavření ve stejném kliknutí.** Klik na „⋯“ otevře menu a stejná událost (kliknutí na řádek, který otevírá stránku, nebo zavření „klik mimo“ téhož kliknutí) ho hned zavře; po otevření se navíc `focusedId` mění a fokus se vrací na řádek (`focusItem`), což může menu zavřít jako ztrátu fokusu.
3. **Stav `busy`** nebo navigace po kliknutí (změna stránky mění strom).
4. **Společná příčina obou menu (nová, po druhém příznaku).** Menu v horní liště je `ActionDisclosure` (`App.vue`), menu stromu `openRowActionsMenu()`; oba sdílejí logiku zavírání (klik mimo, ztráta fokusu, scroll, resize) a oba se zavřou, když se překreslí hostitel. Kandidáti: stránka se při živé úpravě (spolupráce přes Yjs, agent přes MCP, ukládání) neustále překresluje; globální posluchač zavře menu při události, kterou Edge posílá jinak než dev prostředí (pořadí `pointerdown`/`mousedown`/`click`/`focus`, dotykový panel nebo pero); zavření přes `blur` kvůli přesunu fokusu na jiný prvek. Vlastník potvrdil (2026-11-27): menu nefungují **v Edge, na verzi 5.2** (neupřesněno 5.2.0 nebo 5.2.1); v obecném Chromiu se chyba nepodařilo zopakovat. Reprodukce musí běžet **ve skutečném Edge** (Playwright `channel: "msedge"`), na 5.2.0 i 5.2.1, a **s živou změnou stránky** (druhý klient nebo MCP zápis, zatímco je menu otevřené).

### 1b.2a Měření z Edge vlastníka (2026-11-27, přepisuje část kandidátů výše)

Logger událostí a sonda v konzoli na instanci vlastníka (`reducedMotion=false`, `dpr=1`, bez dotyku, okno asi 2270 px a víc) ukázaly:

- Menu se **samo nezavírá**. Po kliknutí na „⋯“ dostane první položka fokus do 14 ms a žádná událost (klik mimo, ztráta fokusu, scroll, resize) menu nezavře. Z DOM zmizí až asi 170 ms po **druhém** kliknutí na „⋯“ (běžné přepnutí).
- Obsah menu (`data-slot="dropdown-menu-content"`) je v DOM, uvnitř okna, nad ostatním (`z-index 1101`), nic ho neořezává a reaguje na klik, ale má **`opacity: 0`** po celou dobu (60, 200 i 450 ms po kliknutí). Animace `atlas-menu-in` (140 ms, bez `animation-fill-mode`) doběhne a opacity zůstane 0, takže nulu musí držet základní styl prvku, ne animace. Menu je tedy otevřené, ale **neviditelné**; vlastník klikne znovu a zavře ho.
- Hledá se zdroj základní průhlednosti 0 v produkčním buildu ve vlastníkově prohlížeči (pořadí a obsah produkčního CSS, utility `fade-in-0`/`animate-in` z komponent shadcn pod nenaložením vrstvy, nebo hodnota vzhledu). Původní kandidáti (hlavní příčina je překreslení, zavírání událostí) se tím nepotvrdili.

### 1b.2b Příčina nalezena a opravena (2026-11-27)

Rozbitá byla menu postavená na `ActionDisclosure` (menu u prostoru v postranním panelu, menu v liště stránky, hlavička asistenta). Panel měl třídu editorového popoveru bez atributu `data-positioned`, a styl editoru (`.md-context-popover:not([data-positioned]) { opacity: 0; }`, načtený na stránkách s editorem) ho po doběhnutí vstupní animace nechal při průhlednosti 0. Vlastník potvrdil, že menu řádků stromu fungují a první screenshot ukazoval menu u prostoru. Oprava: `data-positioned` na panelu `ActionDisclosure` (commit `57d4755bd` v `hotfix/5.2.2`), test hlídá atribut. Dřívější kandidáti níže (strom, překreslení, události zavírání) se nepotvrdili a jsou ponechaní jen jako záznam hledání.

### 1b.3 Návrh

- **Nejdřív reprodukce a příčina zavření.** Zjistit, kdo menu zavírá (`onClose` s důvodem), zda se děje bez živých změn stromu a zda se děje jen při aktivním agentovi; ověřit na čisté instalaci a na dev serveru.
- Podle příčiny: menu nesmí záviset na životnosti elementu řádku (hostitel je kontejner stromu, ne řádek) a přepis stromu (stejné `id`) nesmí otevřené menu zavřít; menu se zavře jen výběrem, Esc, klikem mimo, nebo když stránka, ke které patří, ze stromu zmizí.
- Test podle pravidla „každý nový test potřebuje důvod“: jeden test, který otevře menu a poté obnoví strom s týmiž stránkami (menu zůstane otevřené); a jeden pro klik na „⋯“ (menu zůstane otevřené po celé události kliknutí).
- Ověřit stejné chování v dalších řádkových menu stejného mechanismu (`RowActionsMenu`, tabule, seznamy), protože sdílejí `openRowActionsMenu`.

### 1b.4 Ověření

Klik, pravé tlačítko, Shift+F10 a klávesa menu; strom s otevřeným menu během živé změny (agent přes MCP: přesun, přejmenování, koš); šířky 390 a 1280 px; dotyk.

## 1c. Vyhledávání: text je příliš malý

### 1c.1 Příznak

V paletě vyhledávání (otevře se přes hledání; pole „co je ve FNUSA za objednáv…“, filtry Prostor, Upraveno, Obsah, Řazení, záložky Vše / Zeptat se / Příkazy, seznam výsledků a dole klávesové zkratky) je text, hlavně dotaz v poli a texty výsledků, „strašlivě maličký“ (vlastník, screenshot 2026-11-27).

### 1c.2 Cíl a návrh

- Zvětšit text vyhledávání: dotaz v poli, název, prostor, štítky, úryvek a datum výsledku, záložky, filtry i řádek zkratek. Hlavně pole dotazu a název výsledku musí být dobře čitelné; úryvek a meta zůstávají podřízené, ale ne drobné.
- Velikosti z existujících typografických tokenů (ne nové literály), stejné pravidlo pro všechny zobrazení palety (okno i rozšířené, telefon). Dotykový cíl a řádek se podle toho přizpůsobí, nic nepřetéká ani se nezalamuje jinak než dnes (`AGENTS.md`: žádný horizontální posuvník, dlouhé názvy se zkracují s `title`, hover nic nehýbe, paleta se otevírá a zavírá stejnými časy).
- Neměnit chování ani rozložení palety, jen čitelnost. Galerie (`/?gallery=1`) má příklad palety/hledání aktuální.
- Ověřit v šířkách 390, 1280, 1920, 2560 a 3440 px, ve světlém i tmavém tématu, se zvětšeným písmem prohlížeče a při omezeném pohybu.

## 1d. Konzole v Edge u nefunkčních menu: opakované 409 a 404, výpadky spojení (k prošetření)

Screenshot konzole Edge (vlastník, 2026-11-27), pořízený na instanci, kde se menu zavírají (§ 1b). Z něj plyne, bez ohledu na to, jestli to s menu souvisí:

- **Opakované `409` na `/api/pages/{id}` stejné stránky** (osmkrát za sebou na stránce, kterou měl vlastník otevřenou, další tři na jiné stránce). V serveru vrací `409` uložení stránky (`PUT /api/pages/{id}` v `PagesEndpoints.cs`, také `POST …/publish`). Metodu konzole neukazuje, takže je to **neověřené**; vypadá to na smyčku ukládání s konfliktem verze, tedy klient znovu a znovu posílá starou verzi, zatímco stránku mezitím mění někdo jiný (spolupráce, agent přes MCP). Smyčka by překreslovala stránku a mohla by zavírat menu (§ 1b, kandidát 4).
- **`404` na `/api/pages/{id}` i `/api/pages/{id}/pdf-export`** u dvou stránek (`9f25bca3…`, `700af016…`): klient se ptá na stránky, které server nevrací (smazané nebo bez přístupu). `PagePdfExportStatus` načítá `…/pdf-export` pro každou otevřenou stránku. Zjistit, odkud se ta id berou (poslední navštívené, odkazy, strom) a proč se na ně klient ptá znovu a znovu.
- **Výpadky živého spojení:** varování SignalR „The page is being frozen“ (Edge uspává karty) a `WebSocket … failed: Page entered Back-Forward Cache` pro `/hubs/live` a `/hubs/markdown`, pak „Connection disconnected … 1006“. Po probuzení karty se spojení obnovuje; zjistit, jestli obnovení resetuje stav stránky (a tím zavře otevřená menu) a jestli ukládání po probuzení pokračuje se starou verzí (zdroj `409`).

**Stav po 5.2.2 (2026-11-27):** lokálně se nezopakovalo. Vlastník v tu chvíli stránky přesouval a měnil, takže `404` jsou nejspíš dotazy na už neexistující stránky (5.2.2 se na `…/pdf-export` po `404` už znovu neptá) a `409` uložení se starší verzí po změně jinde. Sledovat: kdyby se `409` znovu opakovaly dokola, zachytit je záznamníkem `fetch` (metoda, tělo, odpověď).

Původní návrh: prošetřit v Edge se seznamem událostí `409` (metoda, tělo odpovědi, `version`), ověřit, zda se ukládání zastaví po prvním konfliktu a načte aktuální verzi, a podle toho opravit společně s § 1b. Nic z toho zatím není potvrzené.

## 2. MCP: syntaxe komponent musí být odkomunikovaná agentům

### 2.1 Příznak

Agent připojený přes MCP neví, jak zapsat informační box a další komponenty. Server jim to nesděluje, takže zápis buď nefunguje, nebo se syntaxe dohledává ve zdrojovém kódu.

### 2.2 Co je dohledáno

- Informační box je v Markdownu **GitHub alert** (`> [!NOTE]`, `[!TIP]`, `[!WARNING]`, `[!IMPORTANT]`, `[!CAUTION]`). Server všech pět čte (`MarkdownToRich`, `CalloutVariants`) i píše (`EditorMarkdownWriter`, případ `callout`). Popsáno je to jen v `docs/markdown/content-modules.md`, tedy v dokumentaci editoru, kterou agent nevidí.
- `McpModule.Instructions` (to, co server posílá agentům při připojení) zmiňuje jen `a-contact`, `a-chart` a `a-overview`, bez syntaxe, a značky `⟦c:N⟧`.
- `docs/mcp-claude-code.md`, oddíl „Pravidla pro text stránek“, popisuje vzorce, diagramy, kontakty, grafy a značky komponent. Informační boxy v něm nejsou. Doc je navíc pro člověka, který nastavuje Claude Code; agent ho za běhu nečte.

### 2.2a Skutečný případ: informační box skončil jako citace s textem `[!WARNING]`

Agent zapsal varování jako jeden řádek `> [!WARNING] Kdo soubor dostane, …`. Na stránce se objevil šedý blok citace, který obsahuje doslovný text „[!WARNING]“ a celou větu za ním (stránka o souboru VPN, nadpis „Odpovědnost za soubor“). Informační box to není.

Příčina je v `MarkdownToRich.Quote` (`AlertMarker`: `^\[!(?<kind>[A-Za-z]+)\][ \t]*$`): značka musí být **sama na prvním řádku citace**, text následuje až na dalším řádku:

```
> [!WARNING]
> Kdo soubor dostane, …
```

Tvar na jednom řádku je přirozený a agenti ho píšou; server ho **mlčky** převede na obyčejnou citaci a text `[!WARNING]` zůstane na stránce. To nesmí nastat. Dvě opravy, obě potřeba:

1. **Tolerantní čtení.** `> [!WARNING] text` se čte jako varování s tímto textem (značka na začátku prvního řádku a za ní text). Zápis editoru zůstává beze změny (značka a text na samostatných řádcích), takže zpětná kompatibilita se neláme. Pokud dnes někdo záměrně píše citaci začínající textem `[!NOTE] …`, je to výjimečné; rozhodnout a zapsat do testu.
2. **Žádné tiché selhání.** Cokoli, co vypadá jako komponenta a nepřevede se (`[!UNKNOWN]`, chybná značka, `a-` blok s chybou, vzorec s neplatným zdrojem), vrací zápis jako **upozornění** s řádkem, důvodem a opraveným tvarem (`warnings` v odpovědi MCP i REST). Dnes už se tak hlásí řada případů (např. obrázek mimo stránku, neplatný odkaz); informační box a podobné „skoro správné“ zápisy ne. Ověřit, které případy `MarkdownToRich` hlásí a které padají tiše, a doplnit chybějící.

Stejný rozbor udělat pro **všechny** komponenty: pro každou zapsat nejpravděpodobnější chybný tvar (jeden řádek, jiná velikost písmen, chybějící prázdný řádek, jiný jazyk bloku, `mermaid` s titulkem, graf bez `---`, …) a rozhodnout, jestli se toleruje, nebo hlásí. Seznam chybných tvarů se stane testovacím korpusem (§ 2.5).

### 2.3 Cíl

Agent má **z MCP samotného** zjistit, co smí do textu stránky napsat, **jak přesně** a s jakými možnostmi, a to i u komponent, které přibudou. Zápis, který komponentu nepozná, nikdy nezanechá na stránce zdrojový kód komponenty jako text.

### 2.4 Návrh

1. **Inventura komponent.** Pro každý druh obsahu, který editor umí (`EditorMarkdownWriter`: callout, tabulka a `a-table`, kontakt, graf, přehled `a-overview`, vzorec, diagram, obsah, odkaz na stránku, YouTrack, emoji, odkazy na hesla, skrytý obsah, požadavek, OpenAPI, příloha, obrázek, …) zapsat do jedné tabulky: (a) **lze psát** a jak, (b) **jen čtení** (značka `⟦c:N⟧`, nelze vytvořit), (c) **nepodporováno**. Tabulka je zdroj pravdy a vychází ze čtecího a zapisovacího kódu, ne z paměti.
2. **Dokumentace pro agenty přes MCP.** Server nemá žádné MCP resources ani prompts (ověřeno: v `src/Atlas.Server` se nepoužívají), jen nástroje. Návrh:
   - **Nový čtecí nástroj** (pracovně `atlas_get_syntax`, rozsah Čtení), který vrací referenci zápisu: pro každou komponentu **účel, přesnou syntaxi, všechny volby a jejich hodnoty, minimální a plný příklad, nejčastější chyby a co udělat místo nich**, a u komponent jen pro čtení řekne, že je nelze vytvořit. Parametr `component` vrátí jednu komponentu, bez parametru přehled. Nástroj, ne resource, protože nástroje jsou dostupné ve všech klientech a agent je vidí v seznamu hned; resources klienti často nenabízejí.
   - `McpModule.Instructions`: stručně (instrukce jdou při každém připojení), jen přehled komponent a pokyn „syntaxi si vyžádej `atlas_get_syntax`, než komponentu poprvé píšeš“.
   - Popis argumentu s textem v zápisových nástrojích (`atlas_create_note`, `atlas_create_draft`, `atlas_edit_note`, `atlas_replace_page`, `atlas_update_section`, `atlas_insert_section`) odkáže na tentýž nástroj a uvede nejčastější tvar (např. odstavec o boxu).
   - Odpověď zápisu s upozorněním (§ 2.2a) nese odkaz na odpovídající záznam reference (`atlas_get_syntax component=callout`) a opravený tvar.
   - Zdroj reference je **v kódu** (§ 2.4, bod 3); texty jsou v češtině jako ostatní odpovědi MCP.
3. **Jeden zdroj, ne dvě kopie.** Referenci držet na jednom místě v kódu (vedle `MarkdownToRich`, který je závazný) a z něj plnit instrukce i dokumentaci, aby se nerozešly. Nová komponenta v editoru ji musí rozšířit; hlídá to test (viz 2.5).
4. **Co agent dělá špatně dnes.** Z chybových odpovědí (`invalid_document`, `components_changed`, `components_removed`) má být vidět, jak to opravit, ne jen co je špatně. Projít zprávy a doplnit tam, kde chybí nápověda.
5. **Dokumentace.** Doplnit `docs/mcp-claude-code.md` o informační boxy a o celou tabulku; v `docs/markdown/content-modules.md` zůstává popis z pohledu editoru, bez duplicity (odkaz).

### 2.5 Test

Jeden test na serveru nad referencí: každá komponenta označená jako „lze psát“ projde cestou Markdown → editor → Markdown beze změny, příklad z reference je platný dokument (`Rules.ValidateDocument`) a výsledek obsahuje skutečnou komponentu (ne citaci nebo blok kódu). Tím příklady nezastarají a nová komponenta bez záznamu v reference neprojde.

Druhý test nad korpusem chybných tvarů (§ 2.2a): každý se buď přečte jako komponenta, nebo vrátí upozornění; žádný nezanechá na stránce text jako `[!WARNING]` nebo ```` ```a-chart ````. Jedna sada tabulkově řízených případů, ne test na každou komponentu zvlášť.

## 4. Co k vydání přibude (podle `AGENTS.md`)

Dělá se jednou, až jsou hotové a slité všechny části (ne v jednotlivých sessions):

- Verze 5.2.2 v `version.json`, `Directory.Build.props` a `src/Atlas.Web/package.json` (`node tooling/release-version.mjs bump patch version.json`), bez `VersionSuffix`.
- `CHANGELOG.md`: oddíl `### 5.2.2 — <datum>` na konci `## 5.2 · Unerring Ume`, jeho sekce `####`.
- Uživatelské poznámky „Co je nového“ v `src/Atlas.Web/release-notes/5.2.2.md` (+ `5.2.2.en.md`), napsané pro uživatele (`node tooling/release-notes.mjs` dá návrh z commitů). Patch nemá `codename`, `mark` ani `### Hlavně`.
- Test pro každou opravu; migrace se nečekají.
- Postup z `AGENTS.md`: release commit se pushuje jen na větev, CI musí projít, pak se `main` fast-forwarduje na stejný commit; hotfix se slije do `develop` (verze `5.3.0-dev` zůstává).

## 5. Otevřené otázky

1. ~~Kódové jméno~~ odpadá, patch ho nemá.
2. ~~„Oprava jedné chyby“~~ byla karta osoby (§ 1).
3. Jestli kartu osoby opravit jen v editoru (`style.css` pluginu kontaktů), nebo i ve Vue variantě; podle ověření v prohlížeči (§ 1.4).
4. Nástroj `atlas_get_syntax` (§ 2.4, bod 2): souhlas s názvem a s rozsahem Čtení; případně ještě odpovídající stránka v uživatelské dokumentaci.
5. Tolerantní čtení `> [!WARNING] text` (§ 2.2a): přijmout, nebo jen hlásit upozornění s opraveným tvarem? Doporučuji obojí: přečíst a upozornit.
6. ~~Čip „Uloženo“ (§ 1a)~~ rozhodnuto: po uložení ztlumit. Zbývá doba ztlumení a vzhled tiché ikony (§ 1a.3).
7. ~~Menu „⋯“ ve stromu (§ 1b)~~ rozhodnuto: součást 5.2.2 (spolu s MCP a vším ostatním).
8. Mají být do reference zahrnuty komponenty, které agent jen čte (`⟦c:N⟧`), nebo jen ty, které umí psát?
