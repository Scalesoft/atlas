# Atlas 5.2 — plán a specifikace: Ověřený obsah a čtenost

Stav: **schváleno vlastníkem 2026-11-24** (rozhodnutí v § 9). Zatím nic není postavené.

Kódové jméno (návrh podle abecední řady): **Upright Ugli** (ugli je citrus; „upright“ = poctivý, spolehlivý obsah).

## 0. Cíl verze

Wiki má smysl, jen když se jí dá věřit. 5.2 přidává dvě věci, které spolu souvisí:

1. **Ověřený obsah:** každá důležitá stránka má garanta a datum, do kdy platí. Čtenář na první pohled vidí, jestli může
   stránce věřit, a garant dostane připomínku, když ověření dobíhá.
2. **Čtenost a stav obsahu:** v prostoru je vidět, co se čte, co nikdo neotevřel a co je čtené, ale už neověřené. Priority
   údržby určují data, ne odhad.

Inspirace: ověřování stránek v Notion wiki (ověřeno do data, vlastník), stav a vlastník stránky v Confluence a její
Analytics. Přebíráme model, ne vzhled (zásada „Notion model, not look“); Atlas zůstává u čipů, postranního panelu a klávesnice.

Minimalisticky: nic, co Atlas už má, se nestaví znovu. Nová je jedna tabulka, jeden plánovaný job, jedna stránka
v nastavení prostoru a malé rozšíření panelu stránky, hledání a MCP.

## 1. Co se znovu používá

| Potřeba | Existující část | Rozšíření |
|---|---|---|
| Nastavení stránky dědící z prostoru | `Page.PublicationMode` (null = podle prostoru) + `Space.PublicationMode` | stejný vzor pro interval ověření (§ 2.2) |
| Osoba | účet `UserAccount` zobrazený přes svůj kontakt (`AccountContacts`, karta osoby při najetí) | nic |
| Oznámení | `NotificationRequested` přes outbox, agregace podle klíče `kind:subject` (inbox spec §3–4) | dva nové druhy (§ 4) |
| Plánovaná práce | Wolverine joby, `JobSchedule.Cron(...)` (vzor `TrashCleanupRunner`) | job `content-verification` (§ 4) |
| Zobrazení stránek | tabulka `PageView` (retence 400 dní), `RecordPageVisit`, `AnalyticsHandler` | souhrn po stránkách prostoru (§ 5) |
| Čtenost jedné stránky | dialog **Čtenost stránky** (`PageReadership.vue`, `ActivityHeatmap`) | odkaz z panelu stránky (§ 5.1) |
| Oblíbené stránky | Profil → Aktivita (`/api/analytics/popular`) | nic |
| Údržba prostoru | Nastavení prostoru → Znalosti (Mezery v hledání, duplicity) | nová položka **Stav obsahu** (§ 6) |
| Panel stránky | `PageSidePanel.vue`: fakta (Verze, Upraveno), Publikace s `OptionPills` | fakta Garant / Ověřeno, volba intervalu (§ 3.2) |
| Stavový čip | čip stavu požadavku (galerie `ChipExample`) | stejný čip pro stav ověření (§ 3.1) |
| Seznamy a filtry | `SettingsList` + `SettingsListRow`, `FilterMenu`, `StatTile` | nic nového |
| AI | MCP nástroje + `[AssistantDescription]` (= i chat v aplikaci), `/api/v1` | rozšíření výstupu + 2 nástroje (§ 7) |
| Záloha | instalační záloha (Git, ZIP) nese nastavení jako sloupce/tabulky | nová tabulka do `InstallationModules` (§ 8) |

## 2. Model

### 2.1 Tabulka `PageVerification`

Nastavení, ne obsah: stejně jako `OwnAccess` nebo `PublicationMode` se neserializuje s entitou stránky (wiki archiv),
nese ho instalační záloha. Jeden řádek na stránku, vzniká až při prvním nastavení.

| Sloupec | Typ | Význam |
|---|---|---|
| `PageId` | uuid PK, FK `Page` | stránka |
| `StewardId` | uuid null, FK `UserAccount` | **garant**; null = bez garanta |
| `IntervalMonths` | smallint null | vlastní interval; null = podle prostoru; 0 = stránka se neověřuje |
| `VerifiedById` | uuid null, FK `UserAccount` | kdo naposledy ověřil |
| `VerifiedAt` | timestamptz null | kdy |
| `VerifiedUntil` | timestamptz null | do kdy platí |
| `VerifiedVersion` | int null | verze publikované revize, kterou ověřil |
| `NoticeStage` | smallint not null default 0 | poslední odeslané upozornění (0 žádné, 1 brzy vyprší, 2 vypršelo); job podle něj neposílá dvakrát |

`Space.VerificationIntervalMonths` (smallint null): výchozí interval prostoru. **Null = ověřování v prostoru vypnuté**
(výchozí stav, nic se nemění, dokud ho správce prostoru nezapne). Volby: 3, 6, 12 měsíců.

`Page.OwnerId` se **nepoužije**: dnes znamená autora a rozhoduje o viditelnosti nepublikovaného konceptu
(`AnalyticsHandler.VisiblePages`, přístupová pravidla). Garant je jiná role, proto vlastní sloupec.

### 2.2 Stav ověření (odvozený, neukládá se)

Interval stránky = `IntervalMonths ?? Space.VerificationIntervalMonths`.

| Stav | Podmínka | cs / en |
|---|---|---|
| `off` | interval je null nebo 0 | (nic se nezobrazuje) |
| `unverified` | interval je zapnutý, `VerifiedUntil` null | Neověřeno / Not verified |
| `verified` | `VerifiedUntil` > now + 14 dní | Ověřeno / Verified |
| `expiring` | now < `VerifiedUntil` ≤ now + 14 dní | Brzy vyprší / Expires soon |
| `expired` | `VerifiedUntil` ≤ now | Ověření vypršelo / Verification expired |

Doplňující informace (ne stav): **„Změněno po ověření“**, když publikovaná verze > `VerifiedVersion`. Ověření tím
nekončí, jen to čtenář i garant vidí. Úprava bez publikace s ověřením nic nedělá.

Stav se počítá v C# (jedna statická funkce, jeden test pro každou hranici), ne v SQL ani triggerem
(zásada „No logic in the database“). Seznamy v § 6 filtrují podle `VerifiedUntil` přímo v dotazu.

### 2.3 Pravidla

- **Ověřit** smí každý, kdo smí stránku upravovat (role ≥ editor), nejen garant. Ověření nastaví `VerifiedById`,
  `VerifiedAt`, `VerifiedVersion` = aktuální publikovaná verze, `VerifiedUntil` = now + interval (v dialogu lze zvolit
  jiné datum) a `NoticeStage` = 0.
- Ověřuje se **publikovaná** verze. Stránka, která publikovaná není, ověřit nejde (tlačítko vysvětlí proč).
- **Garanta** nastavuje editor stránky nebo správce prostoru. Garant musí mít ke stránce aspoň čtení; garant bez práva
  editace dostane upozornění, ale ověřit nemůže (rozhodnutí v § 9, O4).
- Nová stránka v prostoru se zapnutým ověřováním: garant = autor, stav `unverified`. První publikace se ověřením
  **nepočítá** automaticky (ověření je vědomý úkon).
- Deaktivovaný účet garanta → stránka se zobrazuje jako „bez garanta“ a v Stavu obsahu ji to zařadí mezi „Bez garanta“;
  oznámení dostanou správci prostoru (jedno souhrnné, ne za každou stránku).
- Koš, obnovení, přesun do jiného prostoru: řádek zůstává se stránkou; po přesunu platí interval cílového prostoru,
  pokud stránka nemá vlastní.
- Šablony (`IsTemplate`) se neověřují.

## 3. Uživatelské rozhraní stránky

### 3.1 Čip u titulku

Jen pokud stav není `off`. Stejný stavový čip jako stav požadavku (galerie `ChipExample`), bez nové komponenty:

- `verified`: tichý, ikona štítu s fajfkou, text „Ověřeno“, tooltip „do 3. 5. 2027 · garant Jana Malá“.
- `expiring`: tichý, ve tvaru odpočtu „Vyprší za 9 dní“.
- `expired` a `unverified`: tón upozornění, „Ověření vypršelo“ / „Neověřeno“. **Neblokuje čtení** a nepřidává pruh nad
  obsahem (žádná blokující hlášení).

Klik nebo Enter otevře malý popover (vzor `BoardSettingsPopover` / stubbed popover v galerii):

- garant (kontakt s kartou osoby), kdo a kdy ověřil, „Změněno po ověření“ s odkazem do historie (porovnání verzí),
- editor: **Ověřit** (primární, zkratka v popoveru `V`), **Ověřit do…** (datum), **Změnit garanta** (vyhledávač osob,
  první výsledek předvybraný, Enter vybere — zásada „Search: first item + Enter“),
- čtenář: **Nahlásit neaktuální**. Volitelná poznámka, odešle oznámení garantovi (§ 4). Nic na stránce nemění.

Čip se vykreslí hned z dat stránky (verifikace přijde v bundle stránky `GetPageWithBundle`, žádný další dotaz) —
zásada „Show fast, signal loading“.

### 3.2 Panel stránky (záložka Osnova, fakta)

Do `page-side-facts` přibudou řádky (jen při zapnutém ověřování):

- **Garant:** jméno (kontakt), nebo „bez garanta“ + akce Nastavit pro editora,
- **Ověřeno:** „3. 11. 2026 · do 3. 5. 2027“, případně „Změněno po ověření (v. 14 → 17)“,
- **Čtenost:** „12 čtenářů za 90 dní“, odkaz otevře existující dialog Čtenost stránky (§ 5.1).

Pod Publikací volba **Ověřování** (`OptionPills`, jako Publikace): Podle prostoru (6 měsíců) / 3 / 6 / 12 měsíců /
Neověřovat. Jen pro editory; ostatní vidí jen fakt.

### 3.3 Hledání a odkazy

- Výsledek hledání a omnisearche: ikonka štítu u `verified`, upozornění u `expired`. Pořadí se v 5.2 **nemění**
  (o posílení rozhodne měření nad benchmarky hledání, § 9 O5).
- Čip odkazu na stránku (náhled při najetí) ukáže stav stejně jako čip u titulku.

## 4. Upozornění a job

Nové druhy oznámení v `NotificationKinds`:

- `page.verification_due`: garantovi; stupně „brzy vyprší“ (14 dní před) a „vypršelo“. Klíč `page.verification_due:{pageId}`
  → jedna položka na stránku, druhý stupeň nahradí první.
- `page.reported_outdated`: garantovi (bez garanta → editorům, kteří stránku naposledy publikovali, nejvýš 3) s poznámkou
  čtenáře. Opakované nahlášení se agreguje do jedné položky.

Oba druhy jdou do `NotificationKinds.All` (uživatel je může vypnout v preferencích Inboxu).

Job **`content-verification`**, `JobSchedule.Cron("15 5 * * *")` (UTC, ráno před pracovní dobou):

1. stránky se zapnutým ověřováním a garantem, kde stav přešel do `expiring` a `NoticeStage` < 1 → oznámení, `NoticeStage` = 1,
2. totéž pro `expired` a `NoticeStage` < 2,
3. v jedné transakci s outboxem (`outbox.Notify`), dávkově po 500, idempotentní: opakovaný běh neposlal nic navíc.

Žádný týdenní e-mailový souhrn v 5.2: Atlas dnes e-maily neposílá a oznámení patří do Inboxu.

## 5. Čtenost

Data už existují: `PageView` (jeden záznam na zobrazení stránky uživatelem, retence 400 dní), dialog Čtenost stránky
a oblíbené stránky v profilu. 5.2 je jen zpřístupní tam, kde se rozhoduje o údržbě.

### 5.1 Na stránce

Řádek **Čtenost** ve faktech panelu (§ 3.2): počet různých čtenářů za 90 dní. Práva stejná jako dnešní dialog Čtenost
stránky (`PersonalPageGuard.RequirePageAsync`); dialog zůstává beze změny.

### 5.2 V prostoru

Souhrn po stránkách prostoru za zvolené období (30 / 90 / 365 dní): zobrazení, různí čtenáři, poslední zobrazení,
poslední publikace. **Jen počty, bez jmen**: kdo konkrétně četl, ukazuje jen dialog jedné stránky.

Nový dotaz `GetSpaceReadership(spaceId, from, to, filter, cursor)` v `AnalyticsHandler`, nad indexem
`IX_PageView_PageTime`, s viditelností stránek podle `EffectivePageAccess` (stejný `VisiblePages` jako dnes).
Bez denních agregací: nejdřív změřit na produkční záloze (perf guard). Agregační tabulku přidat, jen když dotaz
nad prostorem s 5 000 stránkami a 365 dny přesáhne 300 ms.

Čtení přes MCP/API se do `PageView` nepočítá (dnes také ne). Samostatný čítač „čte AI“ je kandidát na později (§ 9 O6).

## 6. Nastavení prostoru → Znalosti → **Stav obsahu**

Nová položka vedle Mezer v hledání (`SpaceSettings.vue`, skupina `knowledge`). Vidí ji správce prostoru a každý, kdo
v prostoru smí upravovat. Složená z existujících komponent, žádná nová sdílená komponenta.

Nahoře čtyři `StatTile`: Ověřeno · Brzy vyprší · Vypršelo / neověřeno · Bez garanta. Klik na dlaždici nastaví filtr.

Pod nimi jeden seznam (`SettingsList`) s hledáním a `FilterMenu`:

- **Filtr stavu:** vše / k ověření (expiring + expired + unverified) / bez garanta / nečtené (0 zobrazení v období),
- **Jen moje** (kde jsem garant),
- **Řazení:** výchozí **„Nejdřív čtené a neověřené“** = stav k ověření, pak počet čtenářů sestupně. To je hlavní přínos
  spojení obou částí: nejdřív se ověřuje to, co lidé opravdu čtou. Dále podle data vypršení, čtenosti, poslední publikace.

Řádek: titulek s cestou, čip stavu, garant, čtenáři / období, poslední publikace. Akce (řádek + hromadně s výběrem):
**Ověřit**, **Nastavit garanta**, **Otevřít**. Hromadné „Nastavit garanta podle posledního autora“ pro výchozí naplnění
po zapnutí ověřování (autor poslední publikované revize, `PageRevision.AuthorId`).

Nečtené stránky se **nenavrhují ke smazání** automaticky. Řádek nabídne jen „Přesunout do koše“ přes existující
koš (obnovitelné).

Nastavení obecné prostoru (`SpaceGeneralPage.vue`): přepínač **Ověřování obsahu** s intervalem (Vypnuto / 3 / 6 / 12
měsíců), vedle Publikace.

Klávesnice: seznam se ovládá šipkami, mezerník vybírá, `V` ověří vybrané, `G` otevře výběr garanta
(zásada „Keyboard-first everywhere“).

## 7. API a MCP

Všechno, co člověk udělá s ověřením, udělá i AI pod tokenem uživatele (zásada „AI parity via MCP/API“); práva jsou stejná.

- `atlas_get_page` a `GET /api/v1/pages/{id}`: nový blok `verification { state, stewardId, stewardName, verifiedAt,
  verifiedUntil, verifiedVersion, changedSinceVerification }`.
- `atlas_search`: u výsledku `verification.state`.
- **`atlas_set_verification`** (Zápis): `pageId`, volitelně `verify: true`, `until`, `stewardId`, `intervalMonths`;
  `PUT /api/v1/pages/{id}/verification`.
- **`atlas_list_content_health`** (Čtení): stejné filtry a řazení jako Stav obsahu, s čteností;
  `GET /api/v1/spaces/{id}/content-health`.
- `[AssistantDescription]` asistenta: u stránky ve stavu `expired` to v odpovědi řekne („zdroj nebyl ověřen od …“).
- Pokrytí ověří existující test pokrytí MCP.

Ověření přes MCP se zapíše jako ověření vlastníkem tokenu; v popoveru se u něj zobrazí „přes AI“, pokud šlo o token
agenta (stejné rozlišení jako kurzor AI v 5.1).

## 8. Provoz, migrace, záloha

- Migrace `2026112xxx_page_verification.sql`: tabulka `PageVerification`, sloupec `Space.VerificationIntervalMonths`.
  Žádná konverze dat, žádný job na pozadí. Server test nad existujícími řádky: existující stránky mají stav `off`,
  existující prostory mají ověřování vypnuté.
- Kontrola integrity: FK na `Page` s `ON DELETE RESTRICT` jako ostatní tabulky stránek; `PagePurge` řádek smaže.
- Instalační záloha (Git, ZIP): tabulka do `InstallationModules`; obnova bez klíčů (zásada „Git backup is the backup“).
  Wiki archiv ověření nenese (nastavení, ne obsah).
- Lokalizace cs + en pro všechny texty (§ 2.2 tabulka, popover, Stav obsahu, oznámení).
- Changelog: oddíl 5.2 „Ověřený obsah“ a „Čtenost“.

## 9. Rozhodnutí vlastníka (schváleno 2026-11-24)

| # | Otázka | Rozhodnutí |
|---|---|---|
| O1 | Název role v UI | **Garant** (en *Owner*); kód `Steward` |
| O2 | Výchozí interval po zapnutí | 6 měsíců |
| O3 | Upozornit předem | 14 dní před a v den vypršení; nic dalšího |
| O4 | Smí ověřit jen garant? | ne, každý editor; garant je hlavně adresát připomínek |
| O5 | Ovlivní stav pořadí hledání? | v 5.2 ne, jen ikona; posílení až po měření |
| O6 | Počítat čtení přes AI/MCP | v 5.2 ne; kandidát na 5.3 jako samostatný čítač |
| O7 | Smí AI ověřit? | ano, pod tokenem uživatele, označené „přes AI“ |
| O8 | Úprava po ověření | ověření neruší, jen ukáže „Změněno po ověření“ |
| O9 | Historie ověření | v 5.2 jen poslední ověření; úplný log až na požádání |

## 10. Pořadí práce a přejímka

1. **Model + API** — migrace, `PageVerification`, výpočet stavu, příkazy Ověřit / Garant / Interval, rozšíření
   `GetPageWithBundle`, `/api/v1`, MCP. Testy: hranice stavů, práva (matice role × akce), migrace nad existujícími řádky.
2. **Stránka** — čip, popover, fakta a volba v panelu, Nahlásit neaktuální. Vitest komponenty, gallery příklad čipu
   ve stavech ověření (rozšíření `ChipExample`).
3. **Job + oznámení** — dva druhy oznámení, job s `NoticeStage`. Test idempotence (dva běhy = jedno oznámení).
4. **Čtenost + Stav obsahu** — `GetSpaceReadership`, stránka nastavení, hromadné akce. Perf měření nad produkční
   zálohou (limit v § 5.2).
5. **Hledání** — ikona stavu ve výsledcích a čipu odkazu.

Přejímka:

- Prostor s vypnutým ověřováním vypadá a chová se přesně jako v 5.1.
- Po zapnutí: hromadně nastavit garanty, ověřit stránku, čtenář vidí čip, po posunu času job pošle právě jedno
  oznámení na stupeň.
- Stav obsahu seřadí čtené neověřené stránky nahoru; vše jde ovládat bez myši; tmavý motiv čitelný.
- AI přes MCP přečte stav, ověří stránku a vypíše stav obsahu se stejnými výsledky jako UI.
- Testy podle AGENTS.md: při vývoji `tests/changed.ps1`, plné sady na CI z `main`.

## 11. Další kandidáti 5.2 (nespecifikováno)

Rozhodnout po schválení této části; každý by dostal vlastní specifikaci.

- **Import z Confluence a Notion** (dnes Wiki.js a Obsidian v `ContentImport`) — hlavní překážka přechodu pro open source.
- **Webhooky / oznámení do Teams a Slacku** — malé, otevírá integrace.
- **Tabule pro workshopy** — hlasování tečkami, prezentace po rámech.
- **Vložení sekce jiné stránky** (výňatek) — náročnější kvůli Yjs.

Na 5.3+: strukturovaná data / typované objekty, kalendáře a rezervace (intranetový směr 5.x).
