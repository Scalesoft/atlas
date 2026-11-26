# Changelog

Změny Atlasu evidované od zavedení verzování.

Každá menší nebo větší verze má nadpis s kódovým jménem (`## 3.5 · Performant Pear`) a pod ním své opravné verze jako menší oddíly (`### 3.5.1 — datum`), od nejstarší.

## 5.2 · Unerring Ume

*5.2.0 — 2026-11-26*

### Ověřený obsah
- Stránka má garanta a datum, do kdy je ověřená; prostor ověřování zapíná v nastavení s intervalem 3, 6 nebo 12 měsíců, stránka může mít vlastní interval nebo se neověřovat. Výchozí stav: vypnuto, prostor vypadá jako v 5.1.
- Stav ověření (ověřeno, brzy vyprší, vypršelo, neověřeno) a „Změněno po ověření“, když je publikovaná novější verze; ověřit smí každý editor stránky, ověřuje se publikovaná verze.
- Nová stránka v prostoru s ověřováním má garantem autora; hromadně lze garanty nastavit podle autora poslední publikované verze.
- Garant dostane do Oznámení připomínku 14 dní před vypršením a v den vypršení, každou jen jednou; čtenář může stránku nahlásit jako neaktuální s poznámkou.
- Ikona stavu ověření ve výsledcích hledání a u odkazů na stránky; pořadí výsledků se nemění.

### Čtenost a Stav obsahu
- Nastavení prostoru → Stav obsahu: počty podle stavu ověření a seznam publikovaných stránek se stavem, garantem a čteností (zobrazení a různí čtenáři za 30, 90 nebo 365 dní, bez jmen), výchozí řazení nejdřív čtené a neověřené.
- Panel stránky ukazuje počet čtenářů za 90 dní, stejných kalendářních dní jako Stav obsahu.

### AI a API
- MCP `atlas_set_verification`, `atlas_list_content_health` a `atlas_report_outdated`; `atlas_get_page` a `atlas_search` nesou stav ověření (u hitů, položek stromu a odkazů na stránky pole `verificationState`, v seznamu prostorů `verificationOn`) a vypršelé ověření AI v odpovědi zmíní. Ověření přes MCP nebo API je označené „přes AI“.
- `/api/v1`: `GET|PUT /pages/{id}/verification`, `POST /pages/{id}/verification/report`, `GET /spaces/{id}/content-health`, `POST /spaces/{id}/content-health/bulk`; `GET /pages/{id}` nese blok `verification`.
- Diskuse pro AI: MCP `atlas_list_comments`, `atlas_post_comment`, `atlas_edit_comment` a `atlas_delete_comment` (odpověď, zmínka, úprava vlastního a smazání jako na webu: autor, nebo správce objektu), `/api/v1`: `GET|POST /objects/{id}/comments`, `PUT|DELETE /objects/{id}/comments/{commentId}`.

### Diskuse na stránce
- Diskuse stránky je stejné vlákno jako diskuse tabule (`CommentThread`) v panelu stránky; nový komentář se na chvíli ukáže pod avatary s rychlou odpovědí, po vysunutí panelu nahoře zmizí. Alt C otevře diskusi, na telefonu oddíl pod obsahem.
- Jedno odesílání komentářů na stránce, tabuli i u objektu: Enter odešle, Shift+Enter nový řádek, `SendButton` s vlaštovkou (i v asistentovi).

### Barvy spolupracovníků
- Jedna paleta dvanácti barev pro kurzory, avatary a ukazatele, ve světlé i tmavé variantě; Moje barva v profilu. Barvy přiděluje server za stránku i tabuli (`CollaboratorColors`), dva lidé nikdy nemají stejnou; `GET /api/collaborator-colors` (jen aktivní účty).

### Tabule
- Karta kontaktu se přizpůsobí velikosti (jméno, role, vizitka); vizitka .vcf a QR kód v panelu nástrojů tabule místo na kartě, QR se načte až při použití.
- Konec spojnice lze přetáhnout na jiný objekt (i dotykem), ostatní vidí náhled čáry; L na vybrané spojnici přepojí její konec, Shift+L začátek.

### Sjednocené ovládání (`docs/ux-concepts.md`)
- Jedno řádkové menu (`SettingsListRow`, `ActionMenu`) a menu záměrů stromu a seznamů s Delete a F2; záměry mohou být destruktivní, oddělené nebo vypnuté; „Přidat do stromu…“ v Obsahu, Použito na, Vazbách a Osnově.
- Hromadný výběr (select-all s pomlčkou při částečném výběru) v Obsahu, Schvalování a Stavu obsahu; Stav obsahu: klávesa S (G→S), řádkové menu.
- Zpětná vazba: výsledková lišta v nastavení a profilu, jinde toast; potvrzení publikace je toast (hlavička nechává jen čip stavu); smazání stránky či tabule jedním potvrzením se Zpět pro toho, kdo smí obnovit; Ctrl+Z vezme zpět toast, který je ještě vidět; sdílený dotaz na neuložené změny.
- Ctrl+K je jen hledání (i na tabuli); filtry omnisearch na `FilterMenu` (Upraveno, Obsah, Řazení), bez panelu filtrů, Ctrl+Shift+F a čtyř čipů.
- Jedno pravidlo data (`RelativeTime`), jedno `LoadMoreButton`, `OptionPills` jako přepínač zobrazení, `ErrorState` a `LoadingNote`, jedno pravidlo iniciál; avatary všech uživatelů v barvě uživatele (`SubjectAvatar userId`).
- Hranice telefonu 760 px pro dolní lištu stránky i shell; sekce nastavení na telefonu v levém panelu (spodní list Sekce zaniká).
- Karta kontaktu stejná na stránce i tabuli, vnitřní osoba v barvě uživatele; kontakt upravený ve stránce hlásí „Kontakt upraven“ se Zpět; účty v Lidé › Účty otevírají detail ve sdíleném doku.
- Tabule: kontakty v textech jako na stránce, kopie tabule jako Markdown na stránku, „Diskuse“ jedné položky, Ctrl+F v příkazovém panelu; stejné avatary spolupracovníků nahoře jako na stránce.

### Opravy
- Neuložená práce mimo stránku se před zavřením karty zeptá a při odhlášení se koncepty smažou.
- `ActionMenu` zůstává v dialogu a je ostylované i před načtením editoru.
- Hromadné „Nastavit garanta“ nabízí výběr osoby.
- „Co to blokuje“: dva čtenáři naráz už nezpůsobí deadlock (40P01); indexy stránek se nejdřív obnoví najednou.
- Nová stránka nebo tabule se ve stromu vlevo ukáže hned (jeden signál změny jako u přejmenování).
- Vzdálený kurzor ve složené sekci (zobrazení jen nadpisů) stojí na řádku nadpisu, na stránkách i tabulích.
- Odpověď omnisearch uvádí jen zdroje, které cituje, a začíná stručným přehledem.
- Asistent ukáže, které nástroje zapíná volba „nástroje“ (`GET /api/assistant/tools`).
- Pracující glyf AI střídá 24 náhodných rušných variant; při omezeném pohybu jemně dýchá.
- Blok kontaktu: vystředěné ikony sdílení a jeden rám.

### Profil osoby
- Jeden profil pro každý kontakt (dok kontaktu `ContentDetail`), editovatelný na místě přes stejné úložiště objektů; vizitky ve stránce a karta kontaktu na tabuli beze změny, proklik do profilu (`#/library/contacts?contact=<id>`).
- Kontakt účtu: kompetence jako hvězdicový `SkillGraph` (`GET /api/skills/graph?contactId=`, MCP `atlas_get_skill_graph` `contactId`), kalendář aktivity (`ActivityHeatmap`) a „Co se změnilo“ jedné osoby bez časového limitu s filtrem prostoru; `/api|/api/v1/me/changes?author=&space=`, `/me/changes/days?author=`, MCP `atlas_list_recent_changes` `authorId`, `spaceId`. Stejná práva jako všude (`RelationNodes`), AI vyloučení platí.
- Omnisearch: kolega je jeden výsledek z adresáře kontaktů, Enter otevře profil. Úryvky hledání a karty stránek na tabuli ukazují čitelný text místo zápisu bloků dialektu (`SearchText`).
- Správce wiki: část Účet v profilu (dřív `PersonPeek`), řádek ve Správa wiki → Lidé otevře profil; „Vytvořit účet“ z kontaktu (`CreateBuiltinUser.ContactId`, propojení v téže transakci, 409 pro kontakt jiné osoby, soukromý nebo v koši).

### Soukromé prostory
- Prostor vyloučený z AI a MCP je skrytý i osobním tokenům `/api/v1` (rozhodnuto ve sdílených přístupových branách; guard test projde každý nástroj MCP a každou routu `/api/v1`).
- Odkaz na stránku, kterou čtenář nesmí otevřít, ukazuje „Stránka není dostupná“ místo uloženého titulku; „Použito na“ a vazby objektu neprozradí počty skrytých použití ani hran.
- **Nekompatibilní změny `/api/v1`:** seznam použití objektu nemá `hidden` a `total` počítá jen viditelná použití; počet použití má `hidden` jako boolean a už nemá `visible`; inventář použití nemá `hidden`; `GET /objects/{id}/relations` nemá `hidden` a vynechá hrany k nečitelnému konci; REST hledání běží v rozsahu AI (bez „Mysleli jste“, bez hitů zdrojů a prostorů); vyloučené prostory dávají tokenům 404 nebo se vynechají.

### Závislosti
- Tiptap 3.35.0, ProseMirror (model 1.25.13, view 1.43.1), y-tiptap 3.3.1 bez vlastní záplaty; vzdálené změny mapují pozice podle toho, co se opravdu změnilo.
- Npgsql 11 (zůstává `AtlasNpgsqlDriver`), Scalar.AspNetCore 3, Wolverine 6.65, OpenIddict 7.9; pnpm 12.23, Vue 3.5.46, vitest 5.1.1, @scalar/api-reference 1.83; GitHub Actions na aktuálních hlavních verzích.

### Provoz
- Migrace 2026112400: tabulka `PageVerification` a sloupec `Space.VerificationIntervalMonths`; žádná konverze dat. Instalační záloha (Git, ZIP) ověření nese, wiki archiv ne.
- Úloha `content-verification` každý den v 5:15 UTC.

## 5.1 · Tandem Tangerine

*5.1.0 — 2026-11-24*

### Spolupráce
- Živé ukazatele spolupracovníků na stránce i tabuli, kurzor AI při zápisu asistenta nebo agenta přes MCP či API; text agenta se v otevřené stránce vepisuje živě.
- Server odebere přítomnost odcházejícího spojení (avatary se nezdvojují); rychlé znovupřipojení ukáže člověka hned; identifikátor agenta si spolupracovník nepřivlastní.
- Ukazatele nepřestavují kurzory editoru při každém pohybu myši; překreslení zapisuje jen to, co se změnilo.

### Kontakty a Mapa znalostí
- Karta osoby při najetí, vizitka .vcf a offline QR kód vizitky.
- Mapa znalostí: témata u lidí s úrovní 1–5, soukromé, anonymní a podepsané hodnocení (anonymní čísla po pásmech), přehled tématu, graf kdo–co, MCP nástroje a /api/v1.
- Vazbu kontaktu na účet (`directoryId`) mění jen správce wiki.

### Hledání
- Přesná shoda jména zůstává nahoře i po doběhnutí dalších výsledků; kontakty řadí přesnou shodu, pak začátek jména.

### Historie a vydání
- Historie stránky s grafem příspěvků po lidech, AI a publikacích; obrazovka vydání s ukázkami, v Co je nového přehratelná u každé pojmenované verze v dialogu.
- Dialogy se zavřou i kliknutím na pozadí.

### Provoz
- PostgreSQL 19 z oficiálního image `pgvector/pgvector` (kolace builtin C.UTF-8), převod z PostgreSQL 18 proběhne sám při prvním `docker compose up -d` (jednorázové služby `wiki-db-upgrade-dump` a `wiki-db-upgrade`, obecně pro každou další hlavní verzi; data 18 zůstanou pro návrat); vlastní databázový image zanikl.
- Při změně verze kolace Atlas při startu jednou přeindexuje databázi.

### 5.1.1 — 2026-11-24

#### Závislosti
- Aspire 13.6.5 → 17.0.0: AppHost na `Microsoft.NET.Sdk` s `Aspire.Hosting.Dotnet`, server přidaný cestou (`AddDotnetProject`); orchestrátor a dashboard z odpovídajícího Aspire CLI (`dotnet tool update -g Aspire.Cli`).
- Microsoft.Extensions.AI a Microsoft.Extensions.AI.OpenAI 11.0.1.
- Microsoft.Playwright 1.65.1; PDF export v image na Chromium headless shell 159.0.8154.6 (revize 1255).

#### Testy
- `McpRequestLimitTests`: příliš velký požadavek posílá `Expect: 100-continue` a čeká na 413 před tělem; na CI už nekončí přerušeným spojením (broken pipe).

## 5.0 · Shared Strawberry

*5.0.0 — 2026-11-15*

- Atlas je open source: veřejný repozitář s pull requesty do `develop`, vydání z `main` jako tag `v<verze>` a image v GHCR (`ghcr.io/scalesoft/atlas`).

## 4.1 · Radiant Raspberry

*4.1.0 — 2026-11-14*

### Přístupy
- Aktivní řádek katalogu modulů má souvislý vnitřní obrys místo levého stínu stáčejícího se do závorky.
- Přehled přístupů používá ikony a barvy prostorů, odděluje přístup celého prostoru od vlastních přístupů stránek a dalších zdrojů a umožňuje rozbalit jednotlivé zdroje oprávnění.
- Obnovovaná platnost ověření Hubu je odlišena od skutečného konce grantu; autorizační pravidla a jejich lhůty se nemění.
- „Přístup k API“ je přejmenován na „API a MCP“. Záložky účtu, tokenů, skupin a činnosti zůstávají zachované.
- Vlastní přístup stránky výslovně uvádí, že nepřebírá běžná práva prostoru. Odvozený přístup k YouTracku vysvětluje ID projektů a odkazuje na přehled integrace místo nepodporovaného detailu.

### Psaní
- Přepínač **Dokument | Zdroj** na stránce je jedno přepnutí: stojí na místě (už neposkočí a nenahradí ho hlavička s titulkem a „Zavřít“), Zdroj vyplní zbytek okna a kurzor jde rovnou do něj, zpět do textu dokumentu se vrátí klikem na Dokument nebo klávesou Esc. Neuložený návrh zdroje se před návratem i před zamknutím stránky zeptá (Pokračovat v úpravách / Zahodit / Použít a zavřít).
- Stránka otevřená přes nasazení (odmítnutí `SCHEMA_MISMATCH`) se sama načte znovu se stejnou pojistkou jednou za minutu jako u „novějších dat“, jen když není nic na cestě na server, žádný neodeslaný text ani návrh zdroje či komponenty. Po návratu ze Zdroje už panel dokumentu nedrží transformaci (pevné vrstvy editoru se počítají od okna).
- Tabule: výplň rámu, skupiny i cíle přetažení bere tón oříznutý do sRGB (`rgb(from …)` za `@supports`). Tóny blue-2 a violet-2 leží mimo sRGB a `color-mix` neořezává, takže průsvitná vrstva kreslila žluté body; změna prostoru míchání ve 4.0.1 to neodstranila.

### Provoz a závislosti
- WolverineFx 6.61.0 → 6.64.0; migrace `2026111300` přidá index naplánovaných zpráv (`idx_wolverine_incoming_envelopes_scheduled`), který 6.62+ čeká. Test A6 (schéma migrací = schéma Wolverine) je nově `Category=Guard`, takže ho `tests/changed.ps1` pustí i při změně projektového souboru.
- Atlas běží na .NET 11 (SDK 11.0.100, `net11.0`, obrazy `11.0-resolute` na Ubuntu 26.04); balíčky Microsoft (OpenApi, Extensions.AI, SignalR.Client, Mvc.Testing…) ve verzi 11.0.0. Popis API zůstává OpenAPI 3.1 (připnuto, balíček nově vydává 3.2); v dokumentu se změnilo jen `openapi` 3.1.2, `servers[0].url` bez koncového lomítka a `ContactWarning.message` je `string`.
- Web: pnpm 12.18.0, Vite 8.3.8, Vue 3.5.45, `@microsoft/signalr` 11.0.0, Mermaid 12.3.0, Tailwind 4.3.5, jsdom 31; `glob` 14 a `minimatch` 11 pod `js-beautify` (GHSA-3g3m-2fjr-6pw4, GHSA-g3hv-75wg-m249). Scalar.AspNetCore 2.21.0.
- Na dosavadní verzi zůstávají Tiptap 3.31.3 a y-tiptap 3.0.9 (novější `prosemirror-model` nesestaví uložený callout s variantou), Monaco 0.52.2 (+320 KiB gzip), vue-tsc 3.3.11, TypeScript 6, xunit 2.
- Testy křehké na vytíženém CI workeru (ředění zakrytí, `deploymentMap`, `yjsWriterFixtures`, koncept z průvodce) nezávisí na reálném čase.
- Dlaždice AI v přehledu nastavení nepočítá volitelný rerank mezi tři modely („4 ze 3“).

## 4.0 · Quiet Quince

*4.0.0 — 2026-11-12*

### Lokalizace
- Celé rozhraní (nástroj, ne obsah stránek) v češtině a angličtině, připravené na další jazyky: slovníky po oblastech v `src/Atlas.Web/src/i18n` (čeština v balíku, angličtina se stáhne jen pro angličtinu), editor přes `setMarkdownStrings`, serverové chyby přes `Platform/Localization` (česká zpráva je klíč i záloha). Jazyk podle prohlížeče, v profilu Automaticky / Čeština / English (migrace `2026111100`), přepnutí uloží a znovu načte stránku; cookie `atlas-language` platí i pro přihlášení, veřejné stránky a server. MCP a AI zůstávají česky. Guard `czechLiterals.guard.test.ts` hlídá nové české texty.

### Osobní API a MCP
- Tokeny mají jen dva rozsahy, `read` (Čtení) a `write` (Zápis); úrovně účtů jsou `none`, `read`, `write`. Migrace `2026110684` převede tokeny i úrovně (i jejich historii): `read` → `read`; `draft`, `publish`, `direct` → `write`. Token, který dřív smí jen koncepty, tak nově publikuje, přesouvá a maže stránky, vždy jen v právech vlastníka a mimo prostory vyloučené z AI (rozhodnutí vlastníka pro 4.0: zápis je živá úprava, viditelnost řídí publikace stránky).
- Klient, který při vydání tokenu nebo nastavení úrovně pošle dřívější hodnotu (`draft`, `publish`, `direct`), dostane 400; na `write` se nepřevádí, aby žádost o méně nedala víc.
- `atlas_publish` s rozsahem `write` publikuje hned (dřívější `direct`); `atlas_set_publication` a celá organizace stránek potřebují `write`. Politiky `PersonalApiWrite` a `McpWrite` nahrazují `*Draft`, `*Publish` a `*Direct`.
- Potvrzení publikace přes MCP ve webu (`#/mcp/publish/{id}`, `/api/mcp/publish-requests`, URL elicitace, `confirmationId`) zaniklo; migrace `2026110685` smaže tabulku `McpPublishRequest`. Starý odkaz vede na úvodní stránku.

### Požadavky
- `projector_compose_board` má volitelný parametr `arrange`: po poskládání uspořádá karty nasazení na tabuli do obrázku architektury, jako „Uspořádat jako architekturu“ ve webu (stejný výpočet, parita v `tests/fixtures/content/deployment-board.json`).
- Úroveň detailu požadavků má jen tři stupně: Bez (jen Přehled), S (jen nadpisy) a M (s poznámkami, výchozí). L a XL z 3.4 a 3.5 odpadly; uložená volba L nebo XL se čte jako M. Komentář je datovaný řádek v historii požadavku (Ctrl+Alt+M, nebo Komentář v nabídce Ctrl+.).

### 4.0.1 — 2026-11-12

Sestaveno a označeno (`v4.0.1`). Jeho obsah i opravy připravené jako 4.0.2 vycházejí společně ve 4.1.0.

#### Opravy
- Záložka předchozí verze, která otevře stránku už uloženou v novějším schématu pluginu (`registry.migrate`: „data is newer than the installed schema“), se sama znovu načte, nejvýš jednou za minutu; jinak ukáže hlášku s tlačítkem Obnovit stránku (`DocumentEditor.vue`).
- Jednořádkové textové editory tabule (`.board-text.is-inline`: organizace, oddělení a funkce v kartě kontaktu) neukazují úchyt bloku.
- Převod požadavků na nadpisy (`pages.requirements-headings`) už nenechá stránku v blocích kvůli tomu, co Markdown nikdy nerozlišoval: dva seznamy hned za sebou (řádek historie připsaný na webu za seznam poznámek) a odkaz na stránku (`pageReference`) v poznámce nebo v názvu požadavku. Takové stránky dosud zůstávaly ve starém tvaru s nouzovým vykreslením (`RequirementHeadingConverter.RoundTripDifferences`).
- Výplň rámu tabule se míchá v sRGB místo oklab; modrý a fialový tón ležely mimo sRGB a při ztlumení žloutly (`BoardNode.vue`).

#### Závislosti
- Aspire 13.6.4 → 13.6.5 (GHSA-f7vh-h5g3-7jrh).
- Opravné a menší verze: AngleSharp 1.10.2, Anthropic 13.8.0, Markdig 1.6.0, ModelContextProtocol.AspNetCore 2.4.0, OpenAI 2.15.0, OpenIddict 7.8.0, PuppeteerSharp 25.15.0, Scalar.AspNetCore 2.20.0, Microsoft.NET.Test.Sdk 18.12.0.

## 3.5 · Performant Pear

*3.5.0 — 2026-11-02*

### Výkon
- Přepnutí do úprav: dokument se vykreslí jednou rovnou editovatelný, kontakty neproblikávají textem, první render komponenty nečte layout. Najetí na Upravit editor předehřeje (po 200 ms), přechod maskuje vlna a FLIP.
- Psaní na stránce zakázky: čipy požadavků se cachují na uzel, méně `:has()`, akce menu se nedotazují při každé transakci; blok požadavku má v editaci o ~40 % méně prvků.
- Server: lehký seznam sad, rychlejší filtr strany a katalog, 304 u fotek kontaktů.

### Požadavky a zakázky
- Požadavek jako komponenta: klidný řádek, detail v popupu ve dvou sloupcích (vlevo pole, vpravo poznámky s editovatelným datem a formátováním), mazání celého bloku, úrovně NONE/S/M/L.
- Chybějící prerekvizity (čip, seznam v Přehledu, „Doplnit prerekvizity“, varování v MCP); Tabule nasazení v menu stránky a v paletě.
- Moduly produktu (strom produkt › kategorie › modul, vložení pod správné nadpisy se závislostmi) a stránka Sady a katalog v nastavení prostoru; MCP založí zakázku podle jiné i s moduly, kopie bez historie, vlastních stavů, blokací, hodnot tabulek a kontaktů.

### Další
- Hledat ve stránce v pravém panelu (Ctrl+F), výsledky s náhledy a skákání, najde i sbalený text.
- Loga firem a fotky lidí v čipech kontaktů (tabulka `ContactPhoto`), rozlišení stejných jmen.
- Nová podstránka zdědí ikonu a barvu šablony nebo rodiče; MCP a asistent navrhnou vhodnou ikonu.
- Asistent nad stránkami zakázek: nástroje Projectoru mají přednost, stránkování místo zkracování, hledání slovy, strana i podle organizace kontaktu, brána pracovních verzí platí i pro Projector.
- Obnova zálohy do čisté instalace už nevyžaduje ruční mazání kontaktů účtů.

### Co je nového
- Stránka Co je nového (`#/whats-new`, paleta příkazů, Profil) ukazuje popis verzí od 3.0, nejnovější nahoře. Po přihlášení do nové verze se nahoře jednou ukáže hláška „Atlas 3.5 — co je nového“; Zobrazit, × i Esc ji uloží jako viděnou na účtu (ne v prohlížeči). Nový účet ani vývojová verze ji nedostanou.
- Každé vydání má svůj popis v `src/Atlas.Web/release-notes/<verze>.md`; kostru připraví `node tooling/release-notes.mjs` a `tooling/release-notes.test.mjs` bez něj na `main` selže.

### 3.5.1 — 2026-11-03

#### Požadavky a zakázky
- Údaje požadavku jsou tiché čipy místo podtržení; „kde uloženo“ se dělí podle `;` na cestu (název souboru, kopírování), odkaz na Atlas, URL, `PX2-…` a text (`requirementWhere.ts`). Úpravy kreslí údaje na stejném místě jako čtení.
- Řádek má dvě zóny: první řádek je text (kurzor), zbytek karta (výběr a detail); výběr přes `box-shadow`, výška řádku se nemění.
- Detail je karta (560 px, do 600 px spodní panel): velký stav, údaje upravované na místě, co chybí, poznámky s datem (Enter přidá, čip dne), akce PM s klávesami V/U/H/B a Delete.
- Úroveň XL: karta jako kontakt (strana s obrázkem, velký stav, co chybí, poznámky, kde uloženo, rychlá poznámka); blokovaná karta jemně červená. M a L ukazují důvod blokace, přepnutí úrovně drží řádek na místě.
- Backspace na začátku názvu blok nejdřív vybere; mazání odebere jen celé požadavky a ukáže „Odebráno · Zpět“; „+ Požadavek“ v nadpisu oddílu.
- NONE nechá souhrn zavřený; posuvník úrovní má XL.
- Tabule nasazení je tabule stránky (`Purpose = projector.board`): do stromu nepřibude, čte ji každý čtenář stránky, upravuje editor. Webové tlačítko i `projector_compose_board` doplní chybějící produkty do stávající tabule.

#### Data
- Záznamy historie „D. M. RRRR …“ se při čtení Markdownu (server i vložení v editoru) nerozpadnou na vnořené číslované seznamy. Pravidlo opravy `history_date_list` a migrace `2026110550` opraví uložené stránky stávající úlohou `pages.requirements-overview`.

#### Výkon
- Osnova stránky ignoruje editor stavěný skrytě pod stránkou, boční panel bere velikost řádku z observeru, pohyb čte časování jednou na vlnu (měření 3.5 proti 3.4.3).

#### Co je nového
- Klik na verzi ve Správě wiki otevře Co je nového; klik na kódové jméno větší verze v changelogu přehraje její splash.

### 3.5.2 — 2026-11-03


#### Data
- Text tabule nebo pole kontaktu přes limit se při vynuceném ukončení editoru (odmontování, `pagehide`) neuloží oříznutý; celý zůstane jako draft.
- Sloučení kontaktů, jehož sjednocení e-mailů, telefonů nebo aliasů přesáhne `ContactFields.MaxEntries`, server odmítne před změnou dat; webový i MCP náhled to oznámí.

#### Úlohy
- Pozastavení brzdí jen automatické běhy: čekající ruční běh nebo retry pozastavené úlohy se spustí po skončení předchozího běhu i při obnově fronty po startu.
- Oznámení o selhání úlohy dostávají a vidí efektivní správci wiki včetně oprávnění z Hubu.

#### Práva
- Tabule stránky (`projector.board`) je ve vztazích, použití a počtech čitelná pro čtenáře stránky stejně jako v `BoardAccess`.

## 3.4

*3.4.0 — 2026-11-02*

### Přehled (obecný blok)
- Nový blok Přehled shrne komponenty jednoho druhu na stránce nebo na tabuli: požadavky (Stav nasazení), kontakty a úkoly YouTrack. Vloží se lomítkem „Přehled: …“. Na stránce i na tabuli jich může být víc a všechny sdílejí jednu úroveň detailu.
- Úroveň detailu (u požadavků S–M–L–XL, u kontaktů Jména–Karty–Plné karty) mění plynule všechny komponenty svého druhu. Je to volba čtenáře, uložená v prohlížeči; stránka se nemění. Výchozí úroveň požadavků je M (řádek na požadavek).
- Přehled kontaktů stáhne všechny kontakty jako jeden soubor .vcf a zkopíruje jejich e-maily. Přehled úkolů zkopíruje klíče a stáhne CSV pro Excel.
- Karta Stav nasazení je krátká: pod pruhem je řada barevných čipů s počty (Blokuje, Čekáme na, Ještě jsme nežádali, Nedodané vstupy, Podle oddílů). Klik vysune jednu část, „Zobrazit vše“ všechny. Řazení a filtr stavu jsou v patě karty u požadavků.
- Přehled se tiskne celý, bez ovládacích prvků.

### Požadavky nasazení
- Historie požadavku je text: pole „Přidat komentář…“ u každého požadavku vloží do poznámky řádek s datem a časem (`- 2. 11. 2026 14:05 — text`), který jde přepsat. Editor datum jen hezky obarví. Úroveň L ukáže poslední záznam.
- Akce PM v nabídce požadavku: Vyzvali jsme, Urgovali jsme, Dodáno, Blokuje… — jedním klikem změní stav a zapíšou řádek do historie.
- Vlastní slovo stavu („ověřit“, „čeká na podpis“) nad šesti výchozími stavy; souhrn dál počítá podle výchozího stavu.
- Nový atribut Služba (ÚZIS, NIA, BankID…) u vstupu a kategorie modulu z katalogu. Výběr z katalogu seskupuje moduly podle kategorií a hledá i podle nich; vložit jde celý modul i s nadpisem.
- Na úrovních M a L procházejí šipky požadavky po řádcích a nic nepřeskočí.

### Tabule nasazení
- Tabule nasazení je obyčejná tabule: „Poskládat tabuli“ v přehledu požadavků ji založí pod stránkou z karet Produkt (kategorie a moduly se stavem) a Služba se spojnicemi „NIA · 3 pož.“, ve stylu Projectoru. Opakované poskládání jen doplní, co chybí. Karty berou data živě ze stránky.
- Pokusná tabule nasazení z 3.3 a její karty požadavků jsou smazané; text stránek zůstává.

### Editor
- Sekci jde sbalit podle nadpisu (šipka u nadpisu nebo Alt+↑/↓); je to volba čtenáře, tisk ukáže vše.
- Popovery polí se zavírají plynulou animací, klikem mimo i druhým klikem na čip, a fokus se vrátí na pole. Lišta formátování se nepřekrývá s poli bloku ani s úchytem.
- Tabulkám v režimu úprav už neskáčou posuvníky.
- Rychlejší úpravy ve více lidech: kurzory ostatních se kreslí jednou za snímek a kontrola změn po načtení i ukládání snímku počkají, až nepíšete.
- Lišta nad vybraným textem při čtení je pod výběrem, aby se nepřekrývala s nabídkou prohlížeče.

### Data a MCP
- Obsah stránek z 3.3 převede úloha na pozadí „Převod souhrnu nasazení“ (Správa wiki → Úlohy). Převede pracovní i publikovanou verzi, historii nemění, rozpracované stránky dodělá později. Obnova zálohy ji spustí znovu.
- AI přes MCP: `atlas_get_overview` (kontakty jako vCard, úkoly, souhrn požadavků ze stránky i tabule), `projector_compose_board` (poskládá tabuli nasazení) a blok přehledu jako Markdown ` ```a-overview `.

### 3.4.1 — 2026-11-02

#### Přihlášení
- Odmítnuté přihlášení přes Hub zapíše do logu serveru krok, ve kterém selhalo, a druh chyby (bez tokenů), aby šla příčina dohledat.

### 3.4.2 — 2026-11-02

#### Přihlášení
- Přihlášení přes JetBrains Hub znovu funguje i bez platného přihlášení (anonymní okno, nový prohlížeč). Hub posílá jméno a e-mail dvakrát a Atlas to dřív odmítl.
- Odmítnuté přihlášení přes Hub zapíše do logu i místo v kódu Atlasu, kde selhalo.

### 3.4.3 — 2026-11-02

#### Rozhraní
- Boční panel stránky (Obsah, Asistent, Historie, Přístup) stojí vždy u pravého okraje okna a vysouvá se z něj, i na velmi širokém displeji.

## 3.3

*3.3.0 — 2026-11-01*

### Úlohy na pozadí
- Nová stránka Správa wiki → Provoz → Úlohy na pozadí. Ukazuje všechno, co Atlas dělá sám: zálohy, export PDF, indexaci AI, propojování kontaktů, údržbu a retenci. U každé úlohy je plán, poslední běh a stav. Detail úlohy ukáže historii běhů, detail běhu průběh, souhrn a log.
- Úlohu jde spustit hned („Spustit teď“) a pozastavit. Běh jde zastavit a selhaný běh zkusit znovu, pokračuje od místa, kde skončil. Správce prostoru vidí běhy svých prostorů a smí je spustit, zopakovat i zastavit, ale ne pozastavit.
- Selhaný běh se sám opakuje. Když selže i poslední pokus, správci wiki dostanou oznámení a Přehled ukáže problém s odkazem na běh.
- Systémové služby ukazují stav toho, co běží mimo úlohy (databáze, Hub, fronta událostí), jen ke čtení.
- API `/api/v1/jobs` a MCP umí úlohy a běhy číst. Spustit a zastavit je umí s rozsahem Publikace. Pozastavení zůstává jen ve Správě.
- Běhy úloh se drží 30 dní, nejvýš 20 dokončených na úlohu a 500 řádků logu na běh.

### Zálohy
- Zálohy ZIP a do Gitu běží jako úlohy. Stránka Zálohy se vykreslí hned a u archivů ukazuje poslední běh s odkazem na jeho detail. Dřívější běhy záloh jsou převedené do historie úlohy.
- Záloha do Gitu zapíše publikaci hned. Ostatní změny zapíše nejpozději do 10 minut, dřív kontrolovala každých 15 s. Selhání opakuje úloha.
- Obnova instalace ze zálohy zachová úlohy a frontu běžícího serveru.

### Export PDF
- Automatický export PDF je úloha. PDF vznikne, když je stránka po uložení 30 s v klidu; uložení stránky na export nečeká. Uložené nastavení exportu ho jen naplánuje.

### Kontakty
- Stránka Propojit kontakty se vykreslí hned a ukazuje běh úlohy propojování s odkazem na jeho detail. Zastavení ukončí běh před dalším kolem. Dřívější běhy jsou převedené.
- Nový i znovu aktivovaný účet si hned vyžádá svůj kontakt.

### Výkon a spolehlivost
- Události (katalog entit, odkazy mezi obsahem, oznámení o zmínkách, záloha do Gitu, export PDF, index AI) doručuje trvalá fronta Wolverine, zapsaná ve stejné transakci jako změna. Import 1 000 stránek je o 11 % rychlejší a doručení skončí 7 s po importu místo 51 s.
- Události, které 3.2 nestihla doručit, se po aktualizaci doručí jednou úlohou „Převod nedoručených událostí“.
- Server, který při startu nedosáhne na databázi, skončí s jedním řádkem v logu a znovu ho spustí restart kontejneru.

### Opravy
- Po zrušení rozepsaného nového prostoru a volbě Zahodit se fokus vrátí na „Nový prostor“ v postranním panelu.
- Souhrn Projectoru při animaci na okamžik neukáže záporné procento.

## 3.2

*3.2.0 — 2026-10-31*

### Kontakty
- Stránky se propojují samy: celé jméno známého člověka (adresář a účty Atlasu) se po mezeře nebo interpunkci hned změní v kontakt v textu. Backspace hned potom vrátí napsané jméno bez té mezery, Ctrl+Z vrátí jen kontakt, mezera znovu ho udělá znovu; „Převést na text“ (Ctrl+Shift+Mezerník) ho kdykoli vrátí na jméno. V kódu, odkazech, nadpisech a v polích kontaktu samotného se jména nehledají.
- Jméno, které má víc lidí, se nikdy nepřiřadí samo: vznikne nerozhodnutý kontakt (čárkovaný, s „?“) s „nejspíš … · nebo …“ podle stránky (kdo už na ní je, slova kolem jména proti funkci a roli, třeba „PM“, kdo se používá v tomto prostoru, kdo nejčastěji). Klik, Enter nebo Ctrl+Mezerník na něm vybere osobu. Nerozhodnutý zůstane i po uložení a čtenáři ho vidí jako jméno s „?“; server ho sám nepropojí ani nezaloží.
- Už napsaná známá jména mají tečkované podtržení; klik, Ctrl+. nebo Ctrl+Mezerník nabídne „Je to …“ a „Není to on/ona“. Označené jméno udělá kontaktem „Udělat kontakt“ v bublině nad výběrem (nebo Ctrl+.): otevře výběr osoby s tím jménem jako hledáním a jedno Ctrl+Z vrátí text.
- Pole kontaktu (jméno, funkce, adresa…) nikdy neobsahují zmínku jiného kontaktu: psaní v nich nehledá lidi a vložená zmínka zůstane jen svým textem (editor i server).
- Kontakt ve stránce jde upravit kliknutím na vizitku nebo na zmínku v textu, bez lišty a menu. U kontaktu z adresáře se jméno, funkce, e-maily, telefony, web i adresa upravují přímo v panelu u vizitky (Enter uloží, Esc zruší) a změna se uloží do adresáře, takže platí všude, kde je kontakt použit; panel řekne, na kolika dalších místech. Roli, uživatelské jméno a zobrazení v textu má každá stránka svoje. Kdo nesmí kontakt v adresáři měnit, vidí jeho údaje jen ke čtení.
- Každý kontakt je ve stránce samostatný blok, jeden za druhým, a mezi ně jde psát text i nadpisy. „Přidat kontakt“ a „Přidat z adresáře“ vloží nový kontakt jako další blok hned za ten, ze kterého je spustíte; výběr několika lidí i přetažení několika kontaktů z tabule vloží každého zvlášť. Vizitky s několika kontakty server po nasazení sám jednou rozdělí na vizitky po jednom kontaktu za sebou ve stejném pořadí; propojení s adresářem, role i zobrazení zůstanou. Týká se pracovní i publikované verze živých stránek. Starší verze v historii zůstanou, jak byly. Stránku, kterou někdo právě upravuje a nemá uloženo, běh rozdělí později, takže se rozepsané úpravy neztratí.
- MCP a API: blok ```a-contact se seznamem několika lidí se uloží jako několik vizitek za sebou. Nástroje teď radí psát jeden kontakt do bloku.
- Lidi jde hledat podle iniciál ve všech výběrech osob (/kontakt, @, „čekáme na“): „MM“ najde Marka Musila i Michala Macka, „MMu“ jen Marka, „MMa“ jen Michala. V textu stránky se iniciály na kontakt samy nemění.
- Kontakt adresáře má „Zkratky a přezdívky“ (na kartě kontaktu, upravují se na místě). Když po hledání „MMu“ nebo „Mára“ vyberete Marka Musila, adresář si zkratku zapamatuje a příště ho najde jako první; zapamatuje ji jen pro toho, kdo smí kontakt měnit. Stejnou zkratku mohou mít dva lidé, první je ten, koho lidé pro ni vybírají častěji. MCP: atlas_get_contact a atlas_list_entities zkratky ukazují, atlas_upsert_contact s contactId je nastaví.
- Pole strany požadavku i „čekáme na“ v názvu naučí kontakt z adresáře napsanou zkratku („MMu“ → Marek Musil), až se strana použije. Zrušený panel, jméno ze stránky ani řádek ze staršího hledání nenaučí nic.
- Adresář se nenaučí zkratku, podle které kontakt najde i tak: začátek slova jména, funkce, organizace, oddělení nebo e-mailu, v libovolném pořadí.

### Editor
- Ctrl+Mezerník (nebo Alt+Enter) otevře u kurzoru nebo výběru nabídku „Co s tím jde dělat“: odkaz na stránku z označených slov, úkol YouTrack z napsaného klíče, emoji z „:smile“, „Převést na text“ u čipu, v názvu požadavku Čekáme na…, Termín…, Stav… a Závisí na…. S modulem Návrhy při psaní jsou v ní i jeho návrhy k odstavci a akce AI nad výběrem, jen když umí odpovědět; Alt+Enter je dává nahoru. První řádek je předvybraný, psaní filtruje, Enter použije, Esc zavře. Stejnou nabídku otevře pravé tlačítko, Ctrl+., Shift+F10 a klávesa nabídky jako na tabuli (Shift + pravé tlačítko nechá nabídku prohlížeče), i v názvu požadavku. Seznam návrhu u značky odstavce (Proč tento návrh?, Nenabízet) se otevírá kliknutím na značku nebo z Alt+F10.
- Čip jde vždy jednoduše vrátit na text, všude stejně: čip, který vznikl sám z napsaného textu (vzorec „$x^2$ “, úkol YouTrack nebo termín „do pátku “ v požadavku), vrátí Backspace hned potom na text, jako by smazal znak, který čip udělal (mezeru nebo dvojtečku): zůstane napsaný text bez něj a kurzor na jeho konci; ten znak napsaný znovu čip udělá znovu, nic jiného ho samo nepřevede. Ctrl+Z vrátí jen převod, ne psaní. Kdykoli později převede vybraný čip, nebo ten hned před kurzorem (úkol YouTrack, odkaz na stránku, emoji, vzorec v textu), na jeho viditelný text Ctrl+Shift+Mezerník nebo „Převést na text“ v jeho liště; skryté údaje (adresa, ID) nezůstanou. Zkratka je v přehledu klávesových zkratek. „Převést na běžný odkaz“ u odkazů zůstává.
- Čip v textu je jeden prvek se stejným vzhledem a stavy všude: úkol YouTrack, odkaz na stránku a údaje požadavku za názvem (čekáme na, termín, závislost, stav) mají vzhled čipu úkolu (jemné pozadí s rámečkem), osoba zmínku se jménem a avatarem. Vybraný čip má prstenec, nedostupná stránka a údaje hotového požadavku jsou ztlumené, popisek čipu v úpravách říká, co dělají klávesy. Úkol vypadá na pixel stejně jako dřív, odkaz na stránku je nově čip místo barevného textu a údaje požadavku mají rámeček místo oblé tabletky. Ukázka a klávesy jsou v galerii („Čip v textu“). Uložená data ani Markdown se nemění.
- Graf, vzorec, obsah, diagram, příloha, API a další komponenty se upravují tam, kde jsou: klik na komponentu rovnou otevře její úpravu (druhý klik ji zavře), z klávesnice Enter nebo F2 na vybrané komponentě. Kurzor stojí v prvním poli, Esc vrátí výběr na komponentu. Tlačítko „Upravit“ v liště zůstává.
- Vzorce se upravují tam, kde jsou vidět: pole LaTeX se otevře pod blokovým vzorcem nebo u vzorce ve větě (Enter, klik), vzorec se mění za psaní, chybu v zápisu vypíše pod polem a poslední platný vzorec zůstane, Esc pole zavře a Ctrl+Z vrací změny. Napsání „$x^2$ “ udělá vzorec ve větě (Backspace hned po něm vrátí text), „$$“ + Enter v prázdném odstavci blokový vzorec. Částky („$5 a $10“), kód a odkazy se za vzorec nepovažují. Zápis v Markdownu se nemění.
- Obsah dokumentu se upravuje tam, kde je vidět: název přepíšete kliknutím (nebo Tabem a Enterem), Enter ho uloží, Esc zruší a prázdný název vrátí výchozí. Rozsah nadpisů je čip vedle názvu („H1–H3“) s nabídkou voleb (jedna úroveň, běžné rozsahy, Všechny nadpisy), která se ovládá šipkami a Enterem a použije se hned. Formulář s Uložit zmizel; obsah dál vede na nadpisy.
- Příloha se upravuje přímo na kartě: název a popis se přepíšou na místě (Enter uloží, Esc zruší, v popisu Ctrl+Enter), prázdná karta má tlačítko „Nahrát soubor“ a přijme přetažený soubor, karta se souborem malé „Nahradit“. Chyba nahrávání se ukáže na kartě a odkaz na soubor je schovaný pod „Další“. Příklad je v galerii („Příloha“).
- Graf se upravuje na místě: název přepíšete kliknutím nebo Enterem, typ grafu změníte štítkem přímo na grafu a klik na tabulku dat pod grafem otevře úpravu dat (Ctrl+Enter použije). U grafu propojeného s tabulkou vede „Upravit data“ rovnou do zdrojové tabulky.
- Karta API (OpenAPI) má v úpravách přímo na sobě „Upravit specifikaci“ a „Nahrát soubor“ (nebo přetažení souboru YAML či JSON), název souboru a režim zobrazení se mění na místě a hned.

### Hesla z trezoru
- Odkaz na heslo jde vyměnit za jiné heslo bez mazání: „Vyměnit heslo“ na kartě, v liště odkazu (i v textu) nebo v nabídce „Co s tím jde dělat“ otevře výběr hesla ve stejné skupině KeePassu s označeným současným heslem; Enter na vybraném odkazu otevře jeho úpravu, ne výměnu. Zobrazení odkazu zůstane, Ctrl+Z výměnu vrátí. Do stránky se dál ukládá jen odkaz.
- Karta hesla ukazuje v úpravách přímo na sobě, která pole zobrazí a zda se hodnoty samy skryjí (Skrýt po 60 s). Zbytek (Kopírovat, Detail, počet sekund) je pod „Další…“. Při čtení se karta nemění.

### Tabule
- Úpravu každého prvku (text, název rámu a skupiny, popisek spojnice, komponenta, pole karty kontaktu) začne Enter, F2 nebo dvojklik; karta stránky otevře úpravu stránky nad tabulí.
- Enter už nic neotevírá. Kartu stránky, tabule a požadavku (i uzel v grafu vazeb) otevře Ctrl+Enter, odkaz „Otevřít“ na kartě nebo „Otevřít…“ v nabídce akcí; detail objektu je položka „Otevřít detail“. Nápověda lišty to tak i říká.
- Nabídka „Co s tím jde dělat“ editoru na každém prvku: pravé tlačítko, Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 a klávesa nabídky. Nabízí Upravit, Otevřít, Otevřít detail, Kopírovat odkaz a akce výběru; první řádek je předvybraný, psaní filtruje, Alt+Enter dá položky AI nahoru. Otevře se hned i na tabuli jen ke čtení a v grafu vazeb, dvě rychlá gesta otevřou jen jednu nabídku a otevřená nabídka si vezme Esc i Enter dřív než okolí. Shift + pravé tlačítko, odkaz a obrázek uvnitř prvku a místa, kde tabule nic nenabízí, nechají nabídku prohlížeče.
- Karta stránky, tabule a kontaktu se „Odebrat z tabule“ (objekt zůstane) v nabídce, liště i oznámení; vlastní prvek tabule se dál „Smazat“.
- Úprava pole karty kontaktu je krok Zpět tabule jako každá jiná změna; Ctrl+Z ji vrátí přes kontakt a pole, které mezitím změnil někdo jiný, nepřepíše. Odmítnuté Zpět se ohlásí.
- Přejmenování tabule a převod poznámky na stránku (stránka v prostoru zůstane) ukážou oznámení se „Zpět“; „Zpět“ vrátí jen svou změnu a po přejmenování je to krok zpět (Ctrl+Y ho vrátí). Enter a Esc v názvu tabule uloží a vrátí klávesnici na tabuli, i v seznamu tabulí nasazení.
- Po úpravě popisku spojnice zůstane klávesnice na spojnici, po Shift+kliku, který zruší výběr, na tabuli.
- První úprava textu ukáže editor hned ve stejné úloze jako dvojklik: tabule ho předehřeje ve volné chvíli, nejpozději do 2 s.
- Ctrl+Z a Ctrl+Y (Ctrl+Shift+Z) fungují i v liště úprav výběru (Alt+F10), bez návratu na prvek.
- Výběry „Přidat úkoly z YouTrack…“ a „Přidat kontakty…“ hned po otevření ukážou naposledy přidané úkoly prostoru tabule a naposledy přidané kontakty (nejvýše 8); jdou zaškrtnout a přidat bez hledání.
- Přehled klávesových zkratek odpovídá novému ovládání: Enter, F2 nebo dvojklik upraví, Ctrl+Enter otevře kartu, Ctrl+mezerník / Alt+Enter / Ctrl+. / Shift+F10 nabídne „Co s tím jde dělat“, Delete u karet „Odebrat z tabule“.

### Hledání a výběry
- Všechna hledání a výběry mají tytéž klávesy: první Esc smaže napsané a další zavře, Enter nebo Tab vezme předvybraný první výsledek, Enter stisknutý před odpovědí počká a vezme první výsledek odpovědi, Enter v prázdném poli nic nevezme. Platí i pro hledání sekce v nastavení, filtr stromu, příkazy a výběr stránky na tabuli, výběr obrázku prostoru a hledání ikon.
- Výběry bez napsaného textu nezvýrazňují řádek, který Enter nevezme: první šipka (nebo najetí) ukáže první řádek a Enter ho pak vezme (katalog požadavků, příkazy tabule, kontakt v textu, tagy stránky).
- První Esc ve hledání uvnitř panelu editoru (katalog požadavků, výběr nasazení, kontakt, hesla) smaže napsané a panel nechá otevřený; zaškrtnuté řádky zůstanou, až druhý Esc panel zavře.
- Hledání v adresáři kontaktů, v přehledu kontaktů a v Mém prostoru po napsání předvybere první řádek: Enter upraví první kontakt na místě, otevře první výskyt osoby nebo první poznámku. Enter v hledání Mého prostoru už nezavře poznámku otevřenou jen ke čtení.
- Hledání ve wiki: Ctrl+Shift+F skočí na rozsah a filtry (Tab v napsaném hledání bere první výsledek). Nedávné stránky v prázdném hledání se ukážou hned z paměti a síť je jen zpřesní; prázdné hledání nevybírá nedávnou stránku, dokud na ni nepřejdou šipky, a vybraný řádek zůstane u stejné stránky, i když se seznam obnoví.
- Stejná jména osob ve výběrech, ve výsledcích na tabuli a ve filtru Kdo se rozliší popisem (e-mail), jinak pořadím (1. z 2).
- Tab v hledání obrázku prostoru už nenahradí nahraný obrázek: zůstane, dokud výběr nepotvrdíte klávesou Enter.
- Řádek, který vezme Enter ve výběrech a hledáních, má všude stejný vzhled (i ve výběru kontaktu a strany požadavku); jedno zvýraznění fokusu (včetně polí a editoru) místo pěti.
- V historii stránky první Esc smaže filtr autora a teprve druhý zavře panel; stejně v „Hledat a nahradit“ v editoru.
- Výběr kontaktu v textu ukáže známé lidi stránky hned při otevření.
- Nabídka „Co s tím jde dělat“ (Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10, pravé tlačítko) je teď i na řádcích koše, přístupů, kontaktů a Mého prostoru; v seznamech a ve stromu stránek vypadá jako v editoru a na tabuli (nadpis, dlaždice ikon, zkratky, neutrální smazání).

### Tagy stránky
- Tagy se ukládají hned při každé změně, bez tlačítka Uložit; Esc dialog zavře a nic nezahodí, Ctrl+Z vrátí poslední změnu a odmítnuté uložení vrátí uložené tagy i se zprávou. Po zavření dialogu s odebranými tagy nabídne oznámení „Zpět“.
- Nabídka tagů se ukáže hned i na jiné stránce (z paměti, nebo kostra před první odpovědí); opětovné otevření dialogu během ukládání neodebere právě přidaný tag a odchod ze stránky uložení dokončí.
- Tagy na stránce se ukážou hned po změně, ne až po odpovědi serveru.

### Požadavky
- Pole požadavku (datum, strana) uloží i klik mimo nebo odchod fokusu, panel požadavku i zvolený směr a stranu vybranou ze seznamu; neplatné datum nechá pole otevřené s chybou. Koncept kontaktu vrácený z otázky na propojení se kliknutím mimo použije, neztratí se.
- Vybraný požadavek má svou lištu a její akce v „Co s tím jde dělat“; po uložení pole je klávesnice na novém údaji (bez časovače) a Ctrl+Z tam změnu vrátí.
- Panel požadavku, detail OpenAPI a úprava komentáře mají „Použít“ místo „Uložit“; Esc v upravovaném komentáři úpravu zahodí. Oznámení u požadavku říkají „Zpět“ a „smazán“ a jeho × na dotykové obrazovce nepřekrývá čip.

### AI
- Při úpravách stránky jsou k dispozici i Zeptat se, Vysvětlit a Shrnout (do asistenta) a nově Doplnit; Přepsat, Přeložit a Doplnit jsou vždy návrh k potvrzení a jeden krok zpět. Každý použitý krok AI a asistence (Přepsat, Přeložit, Doplnit, Začít s osnovou, návrhy při psaní, Přesunout do trezoru) potvrdí oznámení se „Zpět“; nápovědy „· Ctrl+Z vrátí“ zmizely.
- AI má jeden limit (1 600 znaků, takže v otázce zbývá místo na vlastní slova) a jedno pravidlo dostupnosti; dlouhý název sekce se v pokynu zkrátí a příliš dlouhý text už neotevře prázdnou konverzaci; co AI nezvládne, se nezobrazí, bez hlášky. Tlačítko AI ve výběrové liště editoru se ukáže nebo skryje hned, jakmile asistent začne nebo přestane umět odpovídat.
- Lišta nad výběrem při čtení: výběr v nadpisu (i trojklik v Chromu) shrne či vysvětlí celou sekci; když je sekce delší než limit, AI vezme jen vybraný text a tlačítka nezmizí. Nad samotným prvkem nebo tajemstvím zůstanou akce bez AI (odkaz na sekci, hledání).
- Vysvětlit a Shrnout říkají asistentovi, nad čím se ptáte (např. „sekce „Úvod““, „vzorec“, „prvek tabule“); Zeptat se nad prvkem tabule nebo sekcí to v otázce pojmenuje také („Prvek tabule:“, „Sekce „Úvod“:“) místo „Výběr ze stránky“. Čip přiloženého textu v asistentovi jmenuje cíl (Sekce „…“, Prvek tabule, Tabule) a lišta nad výběrem při čtení ukáže sekci, na kterou výběr rozšířila.
- „Zeptat se na tuto stránku“ (dřív „Zeptat se na stránku“), „Pokračovat v asistentovi“ u shrnutí stránky a odpovědi hledání a Zeptat se na mobilní liště se ukážou jen tehdy, když asistent umí hned odpovědět; samotné otevření panelu Asistent zůstává.
- Tabule: na vybraných prvcích s textem nabízí „Co s tím jde dělat“ Zeptat se, Vysvětlit a Shrnout, jen když AI umí odpovědět. Pravé tlačítko na prvku, ke kterému AI ani tabule nic nenabízí (např. prázdná poznámka čtenáře), nechá menu prohlížeče i se zapnutou AI. Shrnout a Vysvětlit nad rámem nebo skupinou berou i texty jejích prvků; bez výběru nabídne Ctrl+mezerník Zeptat se, Vysvětlit a Shrnout nad celou tabulí. Výběr se spojnicemi nebo kartami AI neztratí; vezme text prvků, které nějaký mají.
- V upravovaném textu tabule nabídne „Co s tím jde dělat“ Zeptat se, Vysvětlit a Shrnout nad výběrem nebo nad celým textem.
- Návrh asistenta s nástroji se potvrzuje „Použít vybrané“ a zamítá „Zahodit“.
- Modul asistence editoru se na stránce, kterou smíte upravovat, stáhne ve volné chvíli, takže řádky AI jsou tu od první úpravy.

### Oznámení, Zpět a načítání
- Jeden systém oznámení: potvrzení (odkaz zkopírován, export připraven, prostor vytvořen, šablona, přesun do jiného prostoru, obnovení historické verze, uložení zdroje stránky) se ukazují jen jako oznámení vpravo dole; starý pruh nad obsahem zanikl. V záhlaví stránky zůstává jen krátký stav (Koncept uložen., Stránka byla publikována.). Varování v oznámení zůstává na obrazovce stejně dlouho jako chyba.
- Přejmenování a přesun stránky ve stromu (myší i Alt+šipkami), odebrání odkazu ze stromu, kontakt v koši a prostor v koši („Prostor je v koši.“, správce wiki ho vrátí hned) mají v oznámení „Zpět“. Řada kroků Alt+šipkami ukazuje jediné oznámení, které se nahrazuje; selhání „Zpět“ se ohlásí.
- Strom stránek: přejmenování se ukáže hned (druhá přejmenovaná stránka se neztratí, ani když se první ještě ukládá), také dvojklikem na řádek; odmítnuté se ohlásí oznámením a vrátí původní název, „Zpět“ řekne, že původní název je zpět v rozpracované verzi. Po přejmenování a kliku nebo Tabu jinam zůstane klávesnice tam, kam člověk šel, jinak se bez čekání vrátí na řádek. Nabídka řádku se otevře i Ctrl+mezerníkem, Alt+Enter a Ctrl+. a nápověda klávesových zkratek to uvádí; dvojklik už stránku nenačítá dvakrát.
- Kontakty: po přesunu kontaktu do koše i po „Zpět“ zůstane klávesnice v seznamu (další kontakt, jinak hledání). Poznámka v Mém prostoru jde do koše hned, bez potvrzení, se „Zpět“ a klávesnicí na dalším řádku.
- Přejmenování a přesun stránky v Nastavení stránky ukážou stejné oznámení se „Zpět“ jako ve stromu; „Uložit název“ a „Uložit ikonu“ jsou „Použít“.
- Místo holého „Načítání…“ je kostra nebo řádek načítání ve správné velikosti, bez skoku rozložení: katalog požadavků, stránky v postranním panelu, nedávné stránky v hledání, skupiny při zakládání prostoru, historie stránky, aktivita v profilu, koncepty ke schválení a jejich náhled, přihlášení, obnova přístupu správce, sdílená stránka, seznam požadavků nasazení a konverzace asistenta.
- Po přechodu do jiného zobrazení se klávesnice usadí na nadpis, jakmile ho zobrazení vykreslí, bez opakování na časovačích.
- Oznámení se vejdou na 375 px bez vodorovného posouvání; po výběru šablony se kurzor přesune na název bez časovače.

### Výkon a galerie
- Hlídání velikosti balíčku nedovolí, aby se panel asistenta nebo modul asistence editoru dostal do úvodního načtení.
- Přihlašovací karta není nikdy širší než obrazovka telefonu (375 px).
- Panel kontaktu ve stránce se načítá jako ostatní líné části: po nasazení nové verze se zkusí znovu a nabídne obnovení stránky.
- Galerie: ukázky odkazu na heslo, čipu v textu, grafu a editoru z klávesnice popisují úpravu na místě; „Pravidla ovládání“ ukazují sdílenou nabídku akcí i klávesy hledání.
- Galerie: „Pravidla ovládání“ jmenují skutečné tokeny stavů a po smazání vzorku nechají klávesnici na tlačítku ukázky; ukázky stromu ukazují „Zpět“ přes useToast; ukázky klávesnice, kontaktů, stavu AI a publikace se na 375 px neposouvají do stran.
- Kontraktní testy ovládání chtějí od kontroly v prohlížeči dotykové cíle 44 × 44 px (D5): žádná poznámka k dotyku neschválí menší cíl a testy selžou, když ho některá uvede.
- Pravidla ovládání: P16 říká, že řádek „Ovládání“ dostane změněná ukázka galerie, místo slibu, že ho má každá ukázka.

### Provazování znalostní báze
- Při psaní se poznávají lidé, úkoly YouTrack (podle zkratek projektů z nastavení YouTrack) a názvy existujících stránek. Jednoznačné jméno a ověřený úkol se hned změní v čip, Backspace ho vrátí na text; názvy stránek, zkratky a iniciály se jen podtrhnou a nabídnou. Stejně ve stránce, v textu na tabuli i v komentářích.
- Výběry lidí a stránek ukážou známé řádky hned ze slovníku prostoru; řazení podle stránky, prostoru a slov kolem kurzoru, s AI navíc podle podobnosti.
- Prostor má záložku „Nepropojené zmínky“: jména a názvy stránek, které ještě nejsou propojené, s jedním potvrzením na položku. Co je propojené, nese stránka: propojené je čip, odmítnuté zůstane textem a nabídne se znovu.
- Při zakládání stránky se nabídne „Podobná stránka už existuje: …“ (↓ do nabídky, Enter otevře, „Přesto založit“). Šablona „Projekt“ má místa pro PM a „Čekáme na“, z nichž vyplnění rovnou udělá kontakt.
- Tabule mají seznamy „Lidé projektu“, „Na co čekáme“ a „Kde se používá“ složené z existujících vazeb (komponenta K).
- Každý účet má svůj kontakt v adresáři: účet se stejným e-mailem se propojí sám, nejisté páry nabídnou Kontakty → Možné duplicity („Propojit účty s kontakty“, „Není to on“).
- MCP a API umí provázat celou wiki: `atlas_list_unlinked`, `atlas_link_page`, `atlas_resolve_reference` a `atlas_recognize`. Změna, která jen propojí (text zůstane stejný), jde rovnou do publikované verze a vrátí ji historie; zápisy hlásí, co propojily.

### Správa a velký objem
- Lidé a členové skupin se hledají na serveru (bez ohledu na diakritiku) a načítají po stránkách („Načíst další“), takže i při 10 000 účtech jde najít a otevřít každého; počty v nastavení odpovídají celé wiki.
- „Kdo má přístup“ k prostoru jde prohledat za limitem 200 a odpovídá zhruba 10× rychleji; obsah velkého prostoru se načítá zhruba 7× rychleji.

### Zálohy a zabezpečení
- Záloha ZIP nabízí jako výchozí „Celá instalace“ (tabule, kontakty, komentáře, poznámky i nastavení, ne jen stránky) a obnovuje se přímo v Nastavení → Obnovení ze zálohy, stejně jako Git; klíč zálohy se ukáže jednou.
- Vytvoření klíče zálohy a vydání osobního API tokenu vyžadují nové přihlášení a zapíšou se do auditu.
- Poznámka v Mém prostoru se při otevření během ukládání už nevrátí ke starší verzi a nehlásí falešný konflikt; tabule po odchodu doposílá čekající změny.
- Úplná obnova ze ZIP i z Gitu už nespadne na prázdném chráněném nastavení (nevyplněný token YouTrack, AI profil bez klíče); obnoví se i zálohy z verze 3.1, nastavení pak jen bez tajemství.

### 3.2.1 — 2026-10-31

#### Kontakty
- Vizitka s více lidmi se rozdělí na jednotlivé kontakty i na stránce, kterou server vedl jako rozepsanou, ačkoli živé úpravy nic neměnily a editor žádnou změnu neukazoval (třeba Zaměstnanci). Stránka se skutečně neuloženými změnami dál počká, až je někdo uloží.

#### Provazování
- Stránku z importu 2.x s odkazem vnořeným do odkazu jde provázat přes MCP; dřív validace odmítla každý zápis vazby.
- Lidi z vizitky jde přes MCP určit: vizitka si nechá telefony a e-maily a doplní se jen, o koho jde. Dřív skončila každá volba chybou.

### 3.2.2 — 2026-11-01

#### Kontakty
- Vizitku s více lidmi už nerozděluje úloha na pozadí. Rozdělí se tam, kde se obsah čte: při otevření stránky v editoru i jen ke čtení, ve všem, co server čte (MCP, API, provazování, obnova verze, import). Živou stránku, kterou mají lidé otevřenou, rozdělí jednou ten prohlížeč, který za ni ukládá snapshoty; do historie Ctrl+Z se to nezapíše. Uloží se to s nejbližším uložením stránky. Nic už nečeká na uložení rozepsaných úprav ani na desetiminutový interval (třeba Zaměstnanci).
- Úloha rozdělení, její tabulka a časovač jsou pryč.

## 3.1

*3.1.0 — 2026-10-30*

### Požadavky (Projector)
- Na koho čekáme, termín, závislosti a stav jsou v editoru čipy přímo v názvu požadavku. Dvojí Backspace čip odebere (odebrání osoby ukončí čekání), Ctrl+Z nebo „Vrátit“ ho vrátí. Tlačítka a menu zůstávají.
- Stačí napsat „čekáme na “, „do pátku “, „do 15. 11. “ nebo „po #“; každý zapsaný termín jde vrátit a čísla verzí („do 3.1“) zůstanou textem. Klíč úkolu YouTrack (#PX2-1235) se v prostoru s YouTrackem hned změní v úkol i se stavem a v názvu požadavku zůstane. Ctrl+. v názvu otevře nabídku Čekáme na…, Termín…, Stav…, Závisí na….
- Poslední položkou výběru „čekáme na “ je „Nový kontakt „jméno““: Enter založí kontakt stejnou cestou jako „/Kontakt“ (včetně otázky „Použít existujícího / Vytvořit nového“) a vloží ho jako stranu požadavku.
- Má-li úkol YouTrack v názvu požadavku termín (pole „Due Date“) a požadavek žádný vlastní, nabídne to oznámení po vložení klíče i pole Termín: „Převzít termín“. Sám se nikdy nenastaví a existující termín nepřepíše.
- „Na koho čekáme“ jde smazat křížkem u jména nebo klávesou Backspace či Delete na něm. Když požadavek přestane čekat na vstup, jméno zmizí a řádek ho chvíli nabízí vrátit („Vrátit“).
- Nad souhrnem nasazení se přepíná úroveň detailu (plný detail, kompaktní, řádek), řazení (pořadí v dokumentu, stav, termín, na koho čekáme, klíč) a filtr stavu. Volba je osobní a platí pro danou stránku v prohlížeči; dokument se nemění.
- „/Požadavek z nasazení“ vloží vybrané bloky nebo moduly z jiného nasazení i s tím, na čem závisí, s vynulovaným stavem, termínem a stranou.
- Předvolby nasazení jsou šablony prostoru s požadavky: nabízí je „/Předvolba nasazení“ i nové nasazení a stránka ze šablony začne s vynulovanými požadavky.
- Přehled nasazení ukáže i nasazení × produkty: co které nasazení má celé, zčásti a co mu chybí. Produkt je modul katalogu se stabilním klíčem.
- „Na koho čekáme“ může být osoba z adresáře. Pole strany ji nabídne mezi kontakty a řádek pak ukazuje, jak se kontakt jmenuje teď. Volný text zůstává dovolený a přepsání textem kontakt odpojí. „Na co čekáme“ seskupí požadavky podle kontaktu a kontakt je má v „Použito na“.
- U požadavku je vidět, kdo ho naposledy upravil a kdy („Upravil Jana Nováková, před 2 hodinami“). V kompaktním a řádkovém zobrazení to řekne čip stavu po najetí myší. Editor stránky vidí uložení, čtenář publikace.
- Požadavek může záviset na požadavku jiného nasazení. V „Závisí na“ se hledá i v ostatních nasazeních prostoru a kruh přes jiné stránky se odmítne. „Čeká na N prerekvizit“ počítá i ty, které čtenář nesmí číst, jako nedostupné.
- Detail požadavku ukazuje „Co to blokuje“: požadavky této i jiných stránek, které na něm závisí. Na katalogové stránce ukazuje i nasazení, která katalogový požadavek používají.
- „Závisí na“ hledá požadavky ostatních stránek na serveru podle napsaného textu (nejvýš 50), místo aby načetlo celý prostor. Přes MCP nejsou vidět závislosti v prostorech vyřazených z AI.
- Vložení z jiného nasazení hlásí chybějící závislost jako „na zdrojové stránce není“ místo „v katalogu není“.
- MCP: nové `projector_create_requirement`, `projector_query`, `projector_insert_from_deployment`, `projector_list_presets` a `projector_get_requirement_history`. Zápisy přijmou stranu jako kontakt (`partyContactId` nebo identitu jako `atlas_upsert_contact`), `projector_query` filtruje podle `partyContactId`, `projector_get_requirements` vrací `changed`. Asistent v aplikaci smí `projector_query` a historii požadavku.

### Použito na
- Jeden panel „Použito na“ ukazuje u kontaktu, tabule a stránky, kde se objekt používá: po prostorech, u každého místa jak (zmínka, odkaz, karta na tabuli, odkaz ve stromu) a proč (úryvek textu kolem). Je v doku Obsahu, v detailu karty na tabuli, v informacích o stránce a v potvrzení koše.
- Počítají se i rozpracované koncepty stránek (označené „koncept“, vidí je jen ten, kdo smí stránku upravovat). Místa, která čtenář nesmí vidět, panel jen spočítá.
- V Obsahu se kliknutím na počet použití (nebo Alt+↓ na názvu) rozbalí místa přímo pod řádkem. Sloupec Použito, filtr Nepoužité a `atlas_list_usage` počítají místa stejně jako panel i úklid sirotků: i odkazy, koncepty a místa, která čtenář nevidí. Tabule s několika kartami téhož objektu je jedno místo. Vizitka se ukazuje jako „vizitka“ se jménem a rolí. Prázdný filtr Nepoužité hlásí „Všechno se někde používá.“ V seznamech se počty načítají až při zobrazení řádku. Panel i Obsah posílají databázi jeden dotaz místo šesti a požadavek čekající na sloučený kontakt se počítá jako použití kontaktu, který zůstal.
- Katalogový požadavek, který nasazení používá, je v „Použito na“ jako „požadavek z katalogu“ (`GET /api/usage/{id}?part=klíč`).
- API: `GET /api/usage/{id}`, `GET /api/usage/{id}/count` a `GET /api/spaces/{id}/usage`. MCP: `atlas_get_relations` umí `includeDrafts`, důvod s úryvkem a počet skrytých vazeb; nový `atlas_list_usage` vypíše použití komponent prostoru s filtrem nepoužitých.
- Šablona ukáže stránky, které z ní vznikly: v Nastavení prostoru › Šablony stránek i v informacích o šabloně. Stránka si od 3.1 pamatuje, ze které šablony vznikla. Záloha Git i ZIP to obnoví bez klíče. U starších stránek se původ nezná.
- Přehled kontaktů ukazuje u kontaktu z adresáře „Použito na“. Výskyty podle identity zůstávají jen u osob, které v adresáři nejsou.

### Kontakty
- Smazání kontaktu přímo v adresáři: použitý kontakt se nejdřív ukáže s varováním, na kolika místech je, a seznamem „Použito na“; do koše jde až po potvrzení. Nepoužitý jde do koše hned. Trvale smazat jde jen v koši.
- Kontakt je jedna osoba napříč wiki a prostory. Vizitka, zmínka i karta tabule na kontakt v adresáři odkazují a ukazují jeho aktuální údaje. Uložená kopie slouží jen jako záloha a pro čtenáře, kteří kontakt vidět nesmí. Smazaný kontakt se ukáže jako „nedostupný kontakt“.
- U odkazu na kontakt jde zapsat místní roli nebo poznámku, například „PM projektu“. Údaje kontaktu se mění jen v adresáři. Úprava propojeného kontaktu na stránce proto mění jen roli a zobrazení a odkaz do adresáře zůstane. Nedostupný kontakt je ztlumený a nenabízí vizitku.
- Přidání osoby, která už v adresáři je, vloží odkaz na existující kontakt a ukáže, kde se používá. U podobné osoby Atlas nabídne „Použít existujícího / Vytvořit nového“. U rozdílných údajů ukáže rozdíl a nechá vybrat, co platí. Stejný kontakt podruhé na stránce nebo tabuli jen upozorní. Platí to pro /Kontakt, `@`, formulář i „Přidat z adresáře“.
- Odebrání posledního použití kontaktu nabídne „Přesunout do koše“ (trvale smazat jde jen z koše) nebo „Ponechat jako nepoužitý“.
- V adresáři jsou „Možné duplicity“ se sloučením. Stejnojmenné lidi rozliší organizace, e-mail nebo telefon. Sloučený kontakt nejde trvale smazat, dokud ho uvádějí stránky nebo tabule. Obnovený z koše je zase samostatný; když jeho e-mail nebo účet mezitím drží jiný kontakt, obnova se odmítne s vysvětlením. Otevřená tabule uvidí sloučený kontakt hned, bez obnovení. Odkazy ve stromu a použití se po sloučení počítají u kontaktu, který zůstal.
- V Nastavení › Kontakty správce propojí kontakty zapsané ve stránkách s adresářem: nejdřív report, potom „Propojit kontakty“; potvrzení ukáže, kolik se propojí a založí. Provedení běží na serveru na pozadí, stránka ukazuje postup a po restartu běh pokračuje. Text stránek se nemění a nic se nemaže. Ze stránek, které čtou všichni, zakládá kontakty wiki; ze stránky v prostoru, který nečtou všichni, kontakt toho prostoru (vidí ho, kdo prostor čte, jinde stránky ukazují svou kopii). Stránky s vlastním přístupem, koncepty a šablony report uvádí jako „nepropojeno“. Běh jde zastavit, po opakované chybě skončí sám a souběžné uložení stránky ho už nepřepíše.
- Přetažení vizitky ze stránky na tabuli a karty z tabule do stránky vytvoří odkaz. Několik kontaktů najednou vytvoří několik karet.
- MCP a API: zápis stránky propojí kontakty s adresářem a vrátí `contacts` (`used`, `created`, `warnings`). Nové nástroje jsou `atlas_upsert_contact` a `atlas_merge_contacts` (sloučení jen po potvrzení člověka). `atlas_edit_board` přijme kartu kontaktu přes `contactId` nebo `entity` a `atlas_list_entities` vrací `contactId`. Nové nástroje `atlas_get_contact` (údaje kontaktu podle id) a `atlas_list_contact_duplicates` (možné duplicity).
- Hledání shody a varování neukazují údaje kontaktů, které uživatel nesmí číst. Zápisy kontaktů přes MCP respektují prostory vyřazené z AI.

### Tabule
- Karta na tabuli mění velikost plynule už během tažení za roh, ne až po puštění myši. Kartu kontaktu nejde zmenšit pod čitelnou velikost.
- „Zpět“ po smazání karty kontaktu vrátí i kontakt z koše. Úchyt pro přetažení karty je vidět i na dotykových zařízeních a při výběru klávesnicí.

### Editor
- Editace stránky už nemá rámeček a podsvícení kolem celého editoru. Fokus z klávesnice ukáže tenký pruh u okraje. Rámeček modulu při najetí myší a obrys vybrané komponenty zůstávají.

### 3.1.1 — 2026-10-30

#### Audit
- Záznam asistenta, MCP a API říká, co nástroj udělal se kterou stránkou („upravil(a) sekci stránky Nabídka přes MCP“, „dostal(a) od asistenta návrh změny“, „potvrdil(a) návrh asistenta“). Na stránku jde kliknout.
- Čekající, potvrzený nebo odmítnutý návrh asistenta už nehlásí „Zamítnuto“. Výsledek má vlastní štítek (Čeká na potvrzení, Odmítnuto uživatelem, Konflikt verzí, Stránku někdo upravuje, Neplatný vstup, Nenalezeno, Bez oprávnění…) a v Podrobnostech větu „Co se stalo“.
- Stejný záznam opakovaný do 10 minut je jeden řádek s „3×“. Časy opakování jsou v Podrobnostech.
- Nad auditem je přehled za zvolené období (Kdy, bez filtru 30 dní): úpravy stránek a kolik s pomocí AI, kdo nejvíc upravuje, volání MCP, API a asistenta podle kanálu, nejpoužívanější nástroje a jejich selhání, kdo nástroje používá a otevření stránek podle kanálu. Neúspěšná volání a jména uživatelů nástrojů vedou do filtrovaného auditu.

### 3.1.2 — 2026-10-30

#### Audit
- Přehled nad auditem je sbalený na čtyři dlaždice s malými grafy: podíl úprav s pomocí AI, nejaktivnější lidé, volání podle kanálu (MCP, asistent, API) a podíl neúspěšných volání. Pořadí lidí a nástrojů se rozbalí pod „Podrobnosti“, takže log začíná hned pod dlaždicemi.

## 3.0

*3.0.0 — 2026-10-29*

Atlas 3 dělá z wiki pracovní prostor s více druhy obsahu. Číslo 3.0 je produktová etapa.

### Tabule
- Tabule je nekonečné plátno a plnohodnotný uzel stromu prostoru: má vlastní práva, koš, zálohu a hledání.
- Na plátno patří poznámky, text, tvary, rámy, skupiny, spojnice s trasováním, karty stránek a objektů a komponenty editoru, volitelně s pozadím.
- Stránka může mít vlastní tabuli.
- Velké tabule kreslí jen to, co je vidět a čitelné.
- Ovládá se celá z klávesnice.
- Spolupráce probíhá živě.
- Práce kolegů je vidět plynule: cizí tažení nezamrazí plátno a cizí přesun karty klouže na nové místo.
- Uložení dávky nečeká, až se změna rozešle ostatním.
- Karty se živě aktualizují, když se zdrojová stránka nebo kontakt přejmenuje, přesune do koše, obnoví nebo trvale smaže.
- Stránku lze upravit přímo z karty na tabuli, ve stejné živé relaci jako ve wiki. Souběžné úpravy poznámky se slučují místo přepsání.

### Projector
- Bloky požadavků ve stránkách, katalog požadavků a tabule nasazení stránky se živými závislostmi.
- Pohledy Přehled nasazení a Na co čekáme.
- Modul se zapíná při startu (`Modules:projector:Enabled`).

### Objekty, kontakty a komentáře
- Společné jádro objektů s živými relacemi a adresář kontaktů. Příkaz `/kontakt` v editoru.
- Komentáře jsou na každém objektu nejvyšší úrovně.
- **Kontakty napříč wiki:** záložka Kontakty v prostoru a Knihovna › Kontakty ukazují každou osobu z vizitek, zmínek ve stránkách a adresáře jen jednou.
  - U osoby jsou odkazy na místa výskytu a export do CSV a vCard.
  - Kontakty se počítají se stejnými právy i přes `/api/v1` a nástroj MCP `atlas_list_entities`.
  - Údaje z adresáře mají přednost před údaji ve stránkách.

### Můj prostor a sdílení
- Soukromé poznámky (Markdown) v Knihovně › Můj prostor.
- Sdílení jedné poznámky s člověkem nebo skupinou probíhá v jednom krátkém formuláři: hledání, Enter, úroveň (čtení nebo úpravy), Enter. Platnost lze volitelně omezit.
- Sdílené položky najdete v Knihovně › Sdíleno se mnou.
- Správci wiki k osobním položkám nemají automatický přístup.
- Osobní tabule byly z 3.0 vyřazeny. Ty, které vznikly na vývojových instancích, se převedly na poznámky.

### Oznámení
- Jedna schránka pro zmínky v komentářích a stránkách a pro sdílení.
- Počet nepřečtených je vidět v Knihovně i na tlačítku menu na telefonu a živě se aktualizuje.
- Oznámení lze nastavit podle druhu.
- Schránka je dostupná i přes `/api/v1` a MCP.
- Oznámení o položce, ke které už nemáte přístup, neprozradí její název.

### Koš a trvalé smazání
- Jeden koš pro kontakty, poznámky, stránky, tabule v prostoru i celé prostory, se stejným seznamem a ovládáním.
- **Smazat trvale** (s potvrzením) odstraní obsah, historii textu, přílohy, komentáře, odkazy, projekce hledání a AI i sdílení.
  - Stránky a tabule trvale maže správce prostoru.
  - Prostor trvale maže jen správce wiki, a to po opsání názvu. Smažou se i jeho trezory.
  - Každé trvalé smazání je v auditu Centra zabezpečení.
  - Položka zůstává v historii Git zálohy.
- Seznam koše a obnova jsou dostupné i přes `/api/v1` a MCP. Trvalé smazání jde jen z webu.

### Zálohy
- Formát v7 zálohuje do Gitu všechny moduly a obnoví je bez klíče zálohy.
- Obnovu celé instalace z Gitu provede správce v Nastavení › Zálohy › Obnovení ze zálohy. Postup: náhled repozitáře, bezpečnostní archiv, potvrzení opsáním slova OBNOVIT a nakonec znovunačtení.
- Změna cíle zálohy (repozitář, klíč, složka, úložiště) a stažení úplné zálohy vyžadují nové přihlášení a zapíšou se do auditu.

### Klávesnice, hledání a vzhled
- Jeden model zkratek s nápovědou `Ctrl+/` a příkazy palety. Fokus padá vždy na logické místo.
- Každé hledání a výběr předvybere první výsledek a Enter ho potvrdí. Slash menu v editoru bere argumenty.
- Hledání na celou stránku s nekonečnými výsledky, bez limitu 1000 a s rychlejším tolerantním hledáním.
- Při načítání se ukazuje kostra místo prázdného místa.
- Řada záložek na telefonu bledne k okraji.

### AI a MCP
- Tabule, Projector, koš, osobní poznámky, oznámení a kontakty mají paritu přes MCP a `/api/v1`.
- Asistent používá nástroje jen po zapnutí.
- `atlas_arrange_pages` navrhne levé menu prostoru.

### Účty a spojení
- Aktivní relace se udržují a účty z Hubu si mohou nastavit místní heslo.
- Když relace skončí, živé spojení vrátí uživatele na přihlášení místo trvalého „Offline“.

### Provoz
- Trvalý outbox doručuje události po uložení alespoň jednou, zvlášť každému příjemci, s opakováním a slučováním.
- Nezpracované zprávy na pozadí ukáže Přehled a nabídne „Zkusit znovu“.
- Provozní logy mají jednu retenci podle stáří i počtu (sekce `Retention`). Volitelné logy se při velkém objemu přestanou zapisovat. Bezpečnostní audit zůstává.

### Společné úpravy dokumentu
- Ani při mnoha pisatelích na velké stránce se neztrácí text: když prohlížeč nestihne uložit snapshot, uloží si ho server sám.
- Editor zůstává zapisovatelný i během znovupřipojení a napsané se odešle po něm.
- Server zvládá víc změn za sekundu, protože už u každé změny nepřepočítává celý log.
- Velká stránka zůstává plynulá i při psaní mnoha lidí najednou. Druhý autor může uložit i poté, co první stránku uložil nebo publikoval.

### Editor a výkon
- Editor (Atlas.Markdown) je nově součástí Atlasu a nemá samostatná vydání.
- První obrazovka nenačítá editor (572 → 297 KiB gzip).
- Sada ikon se stahuje po částech.
- Snapshot kolaborace se posílá komprimovaný.

### Migrace databáze
- 42 nových migrací (`2026102600`–`2026103035`).
- Povýšení z 2.15 na reálných datech proběhlo bez chyby, z `2026102880` rovnou na finální schéma (29. 10. 2026).

### 3.0.1 — 2026-10-29

- Tabule a Projector jsou v nasazené aplikaci zapnuté. Ve 3.0.0 byly ve vydaném image vypnuté, protože je zapínala jen vývojová konfigurace. Nasazení může modul vypnout proměnnou `Modules__<id>__Enabled=false`.
- Nová tabule se zakládá z nabídky „Nová ▾“ vedle nové stránky (v hlavičce prostoru i v postranním panelu).
- Zálohy upozorňují, jen když není zapnutá ani Git, ani ZIP záloha. Nenastavený ZIP vedle zapnuté Git zálohy už varování nehlásí.

## 2.15

*2.15.0 — 2026-10-20*

- Psaní stránky je jednodušší: **Hotovo** stránku uloží, zkontroluje před publikací a publikuje (i novou stránku); s ničím novým k publikaci jen ukončí úpravy. Nepublikované změny totiž čtenáři, hledání ani AI nevidí. „Uložit koncept“ zůstává jako vědomá volba a „Zahodit změny“ je v nabídce ⋯ (s potvrzením). Při čtení vidí autor s právem psát stav stránky a tlačítko **Publikovat** vedle Upravit, kdykoli uložená verze není publikovaná.
- MCP může stránku přepsat a rovnou publikovat s novým právem **Přímá publikace** (úroveň a rozsah tokenu `direct` nad Publikací). Úroveň přiděluje jen správce wiki, rozsah volí vlastník tokenu; nikdo ho nemá automaticky, takže stávající tokeny dál publikují přes potvrzení na webu. REST API se nemění. Migrace databáze `2026102020` rozšíří kontroly úrovní a rozsahů; stávající řádky se nemění.
- Hledání a AI vidí obsah komponent **OpenAPI**: cesty, operace (`operationId`, popisy, parametry, odpovědi) a schémata. Dosud se z nich do hledání dostal jen řádek s názvem, verzí a počty, takže například `freeBusy` ze specifikace nešlo najít. Fulltext prohledává celou specifikaci, AI index má pro každou operaci a schéma vlastní úsek s nadpisem (např. „GET /v1/calendar/freebusy“). Stránka, která má všechna slova dotazu v názvu, je nad stránkami, které je mají jen v textu (dotaz „API“ najde nejdřív stránky s API v názvu). Stránky bez textu (jen nadpis) se už neobjevují jako výsledky hledání podle významu s prázdným obsahem. Asistent hledá podle klíčových slov otázky („Co je to freebusy“ → „freebusy“) a panel Asistent se otevírá s rozsahem prostoru místo jediné stránky. Migrace databáze `2026102010` přepočítá vyhledávací řádky stránek s OpenAPI a zařadí je k přeindexování AI.
- Vyhledávání rozumí českým tvarům slov a názvům z kódu. Slovo dotazu (mimo frázi v uvozovkách) najde i jiné pádové tvary: „smlouva“ najde „smlouvy“ a „smlouvou“, „tiskárna“ najde „tiskárnu“; stránka s přesným tvarem je výš. Slova do čtyř písmen zůstávají beze změny. Identifikátor v camelCase, PascalCase, snake_case nebo kebab-case se dá najít po částech i vcelku: `getFreeBusy` najde dotaz „freebusy“, „free busy“ i „busy“, `export_page_pdf` dotaz „exportpagepdf“. Platí pro web, osobní API i MCP (`atlas_search`, `atlas_find_mentions`) nad publikovanými stránkami i koncepty, včetně nadpisů a dřívějších názvů. Migrace databáze `2026102030` přidá ke slovům vyhledávacích vektorů kmeny a části identifikátorů a přepočítá všechny řádky `SearchDocument` a `WorkingSearchDocument` (na 10 000 stránkách 7 s); na skutečné wiki jsou vektory zhruba o 60 % a index GIN asi dvakrát větší (z 1,4 na 2,6 MB na 300 stránek), rychlost dotazů se nemění.
- Asistent s nástroji odpovídá na otázku o wiki rychleji: Atlas před prvním voláním modelu sám spustí `atlas_search` s otázkou (jako krok tahu, s právy uživatele a vyloučením prostorů z AI) a model dostane výsledky rovnou v kontextu (prompt `agent@5`). Na testovací sadě 43 otázek (Gateway s Qwen 3.8 Flash) klesl počet volání nástrojů, o která si model řekne, ze 4,1 na 2,1 na tah a medián tahu z 6,8 s na 5,6 s (p95 z 24,0 s na 22,1 s) při stejném pokrytí zdrojů. Úkoly se zápisem (štítky, koncepty, přesuny, publikace) začínají jako dosud. `atlas_search` vrací u hybridního výsledku text pasáže (celý úsek až 1 500 znaků podle pořadí, ne jen 320znakový úryvek) v mezích 12 000 znaků výstupu nástroje; strukturovaná data MCP jsou beze změny. Nový volitelný profil AI **Reranker** (Administrace → AI, úloha `search.rerank` ve Funkcích): cross-encoder (např. bge-reranker-v2-m3) na OpenAI-kompatibilním serveru s `POST /rerank` (llama.cpp `--reranking`, Infinity, vLLM) vybere z 30 kandidátů podklady odpovědi z wiki (omnisearch i panel bez nástrojů); nastavuje se, testuje a hlídá jako ostatní profily. Na reálné wiki zvýšil podíl odpovědí, mezi jejichž 6 podklady je očekávaná stránka, ze 77,9 % na 89,4 %. Hledání a `atlas_search` nepřeřazuje: tam nepomohl. Bez profilu, s vypnutým profilem, po překročení 1,5 s, při chybě nebo nesmyslné odpovědi zůstává dnešní výběr podkladů bez chyby pro uživatele; opakovaně selhávající profil vypne jistič. Migrace databáze `2026102040` povolí druh profilu `rerank` a záznam využití `rerank`; stávající profily a záznamy se nemění.

## 2.14

*2.14.0 — 2026-10-19*

- Export PDF ukládá do metadat dokumentu (Vlastnosti → Název, titulek okna prohlížeče PDF) název stránky místo „Atlas – tisk dokumentu“. Platí pro stažení PDF, PDF z Historie i automatický export do úložiště; už vyexportované soubory se změní až dalším exportem. Bez migrace databáze.

## 2.13

*2.13.0 — 2026-10-17*

- Export PDF už nenese značku Atlasu: titulní strana nezačíná řádkem se značkou a záhlaví stránek ukazuje vlevo název dokumentu a vpravo jen číslo revize, když ho titulní strana uvádí (dříve vždy „ATLAS“, případně „ATLAS · Revize N“). Platí pro stažení PDF, PDF z Historie i automatický export do úložiště; miniatura titulní strany v dialozích exportu odpovídá. Bez migrace databáze.

## 2.12

*2.12.0 — 2026-10-16*

- Automatický export PDF příručky vybírá cíl procházením úložiště: dialog **Automatický export PDF** ukáže složky zvoleného úložiště, otevřená složka je cílová (včetně kořene úložiště, který je pro nový export výchozí) a novou podsložku lze založit na místě. Před uložením dialog ukáže celou výslednou cestu (`\\server\sdílení\složka\název.pdf`, u místního úložiště cestu na serveru). Procházení nikdy nezakládá složky, cesta zůstává uvnitř kořene úložiště (`..`, absolutní cesty a symbolické odkazy se odmítají) a platí oprávnění k úpravě stránky i povolení úložiště pro prostor. Když v cíli už leží soubor, který export nezapsal, uložení se zeptá, zda ho export má převzít a přepisovat (`409 pdf_target_exists`, pole `confirmTakeover` v `PUT /api/pages/{id}/pdf-export`); ostatní soubory ve složce, například Wordy, export nemění. Nastavení → Obecné → Úložiště nově ukazuje přehled všech automatických exportů (stránka, cílová cesta, režim exportu, poslední úspěšná aktualizace a chyba) s tlačítky Otevřít nastavení a Zopakovat export. API: `GET`/`POST /api/pages/{id}/pdf-export/folders`, `GET /api/storage-profiles/pdf-exports`, `POST /api/storage-profiles/pdf-exports/{pageId}/run`; profily v `GET /api/pages/{id}/pdf-export` nesou `location`. Bez migrace databáze.

## 2.11

*2.11.0 — 2026-10-16*

- Nový nástroj MCP `atlas_apply_labels` (rozsah Koncepty) změní ikony, barvy a tagy až 100 stránek jedním voláním, v jedné transakci a vše nebo nic: položka `changes` má `pageId`, volitelně `icon` + `color` nebo `clearDecoration` (s `expectedRevisionId`), `addTags`, `removeTags` a `expectedTagIds`. Každou položku zapíší tytéž sdílené handlery jako `atlas_set_decoration` a `atlas_set_tags` (práva, vyloučení prostoru z AI a MCP, katalog ikon a barev, souběžnost); zámky se berou v pevném pořadí (prostory, nové tagy, stránky podle id), takže se souběžné dávky nezablokují. Když některá položka neprojde, neuloží se nic a chyba vypíše v `items` index, `pageId` a kód každé chybné položky (`decoration_changed`, `tags_changed`, `invalid_argument`, `not_found`; nečitelná či vyloučená stránka jako neexistující). Revize stránek nevznikají; audit zapíše řádek za každou stránku (u neúspěchu ostatní položky `rolled_back`). `atlas_get_space_tree` nově u stránky s ikonou vrací i její `revisionId`, takže dávka nepotřebuje čtení každé stránky zvlášť. Bez migrace databáze.
- `atlas_find_mentions` rozumí českým tvarům slov: každé slovo výrazu (mimo frázi v uvozovkách) zkrátí o běžnou pádovou koncovku a hledá jako předponu bez ohledu na diakritiku, takže „tiskárna“ najde i „tiskárnu“, „tiskárny“ a „tiskárně“; slova do čtyř písmen zůstávají beze změny. `atlas_search` ani vyhledávání ve webu se nemění. Bez migrace databáze.
- Nový obsahový kanál `assistant` pro asistenta v Atlasu, který jedná jako přihlášený uživatel bez osobního tokenu: změny, publikace, otevření stránek i auditované zápisy jeho nástrojů nesou kanál `assistant` a Historie, Poslední publikace, Kdo má přístup › Historie i nástroje MCP je označí „přes asistenta“; Security centrum › Činnost ukáže `assistant.write` a `assistant.publish` jako „upravil(a) / publikoval(a) přes asistenta“ (bez tokenu). Kanál určuje jen server značkou relace nástrojů asistenta, nikdy nic z požadavku klienta; osobní API token se značkou asistenta je 401. Strop asistenta tvoří obsahová práva uživatele ve webu, ne úroveň osobního API. Jako MCP asistent odmítne uložit, obnovit, nahradit zdroj, přesunout do koše i publikovat stránku s neuloženými živými úpravami kohokoli, i svého uživatele (`409 collaboration_unsaved`). Migrace databáze `2026101610` rozšíří sloupce `Channel` (`PageRevision`, `PagePublication`, `PageAccessEvent`, `ApiActivity`) na 16 znaků, jejich CHECK přijme `assistant` jen bez tokenu, `ApiActivity` dovolí řádek bez tokenu právě pro `assistant` a pohled auditu se znovu vytvoří beze změny; stávající řádky se nemění.
- MCP má tři nové nástroje ke čtení, které odpovídají jedním voláním (server iteruje sám až do limitu a vrátí `truncated` s `nextCursor`), aby malý lokální model nemusel procházet výsledky po stránkách: `atlas_get_space_tree` (celý strom prostoru s cestou, hloubkou, ikonou, tagy a stavem publikace, nejvýše 500 stránek), `atlas_find_mentions` (všechny publikované stránky, které výraz doslova zmiňují, s cestou a úryvkem, nejvýše 200; limit hledání jako `atlas_search`) a `atlas_explain_access` (kdo má přístup ke stránce či prostoru a proč; podrobnosti jen tomu, komu je ukáže i web, tedy správci přístupů stránky a správci prostoru). Všechny respektují práva a vyloučení prostoru z AI a MCP. Bez migrace databáze.
- Vzorce a diagramy v Markdownu osobního API a MCP: server čte `$…$` (a `$$…$$` v textu) jako vložený vzorec, řádky `$$ … $$` a ```` ```math ```` jako blokový vzorec a ```` ```mermaid ```` jako diagram, stejně jako editor (pravidla Pandocu: `$5 a $10` ani `$HOME` vzorcem nejsou, neuzavřené `$$` nebo `$$` přes prázdný řádek zůstává textem, zdroj, který validátor editoru odmítne, zůstává blokem kódu). Čtení stránky (`GET /api/v1/pages/{id}?format=markdown`, MCP) proto vrací vzorce a diagramy bez titulku jako text místo značek `⟦c:N⟧`; nezměněný vzorec či diagram si při uložení ponechá uložený uzel a nový vzorec nebo diagram lze zapsat. Diagram s titulkem a vzorec, který Markdown nepřenese, zůstávají značkou. Konce řádků CRLF se čtou jako LF i v blocích kódu. Bez migrace databáze.
- Kontakty a grafy v Markdownu osobního API a MCP (dialekt editoru, jmenný prostor `a-`): kontakt je blok ```` ```a-contact ```` se seznamem kontaktů v YAML nebo v textu odkaz `[Jméno](a-contact:?name=…&phone=…&email=…)`, graf blok ```` ```a-chart ```` s hlavičkou (`title`, `kind`), řádkem `---` a mřížkou jako tabulkou Markdownu; graf napojený na tabulku uvede `source` (`table`, `rows`, `columns` od 1) a tabulka nese odstavec `{#id}`. Server je zapisuje i čte stejně jako editor (hodnoty textových polí jako `2026` nebo `+420…` zůstanou textem). Čtení stránky (`GET /api/v1/pages/{id}?format=markdown`, MCP) proto vrací kontakty a grafy bez zdroje jako text místo značek `⟦c:N⟧`; nezměněný kontakt či graf si při uložení ponechá uložený uzel. Graf napojený na tabulku a jeho tabulka zůstávají značkou. Zápis je striktní: neznámá nebo neplatná komponenta (neznámý klíč, chybný YAML, chybějící tabulka, hodnota, kterou editor odmítne, jiný blok ```` ```a-… ```` či odkaz `(a-…:…)`) je `400 invalid_document` s řádkem a sloupcem každé chyby (`line`, `column`, `errors`) a nic se neuloží. Blok kódu v jazyce začínajícím `a-` se píše ```` ```a-code a-… ````. Uložená Markdown projekce stránek s kontakty a grafy se tím mění (obnoví se při dalším uložení). Bez migrace databáze.
- AI smí vytvořit kontakt a graf: úprava textu v editoru (`editor.transform`) i asistent s nástroji v konceptech (prompt `agent@4`) dostanou přesné tvary ```` ```a-contact ````, `[Jméno](a-contact:?name=…)` a ```` ```a-chart ```` a smějí je napsat jen z údajů v textu či zadání; jiné komponenty dál ne. Návrh úpravy textu, který obsahuje jinou nebo neplatnou komponentu `a-` (například ```` ```a-toc ````), server odmítne jako dosud (`502 ai_failed`); návrh konceptu asistenta ověří zápis jako každý zápis MCP (`invalid_document`). Evaluace asistenta má dva nové případy (graf z čísel, kontakt ze zadání) a u návrhů kontroluje i text navrženého Markdownu (`facts`). Bez migrace databáze.
- Administrace → AI → Funkce má dva nové řádky pro budoucího asistenta s nástroji: Asistent s nástroji (`assistant.agent`, vypínač a denní limit tahů na uživatele, nevyžaduje index vyhledávání) a Asistent čte rozepsané verze (`assistant.working-text`, na cloudovém profilu jen s povolením správce, na lokálním a firemním vždy). `GET /api/ai/capabilities` vrací nový klíč `agent`. Bez migrace databáze.
- Nové právo „AI s nástroji“ rozhoduje, zda asistent smí používat nástroje (hledat a číst napříč wiki, navrhovat změny k potvrzení); přepínač modulu MCP ani úroveň API ho neovlivňují. Nastavuje se u grantu skupiny v Administrace → AI → Přístup (přepínač pod Upravit) a osobně v detailu účtu pod „Přístup k modelovým funkcím“ (Výchozí / Osobně povolit / Osobně zakázat, ukáže se, jen dokud AI není osobně zakázaná). Platí `povolená AI && (osobní volba ?? (má povolený grant skupiny ? některý grant s nástroji : ano))`, takže účet s AI povolenou jen osobně má nástroje výchozí zapnuté. Přibyl i denní limit asistenta s nástroji (`assistant.agent`) mezi limity skupin a účtu. Capability `agent` je pravdivá jen s tímto právem a tah s nástroji bez něj server odmítne 403 `ai_tools_forbidden`. Migrace databáze `2026101605` přidá k politikám AI sloupce `ToolsEnabled` a `AgentDailyLimit`: dosavadní i nové granty skupin mají nástroje zapnuté, osobní výjimky dědí ze skupin; vypnout lze po skupinách i osobně.
- Asistent s nástroji v panelu: s přepínačem „S nástroji“ (výchozí zapnuto, jen s právem „AI s nástroji“) je otázka tahem úlohy `assistant.agent` v běžné konverzaci. Asistent si podle otázky, předchozích otázek a otevřeného prostoru či stránky vybere skupiny nástrojů, jako přihlášený uživatel hledá, čte stránky a prochází strukturu wiki (nejvýše 8 kol a 120 s na tah), kroky ukazuje živě („Čtu stránku …“) a po odpovědi je složí do „Použité kroky ▸ n“. Zdroji odpovědi jsou jen stránky, které nástroje v tahu vrátily (přečtené stránky a výsledky hledání, které odpověď jmenuje); věta cituje stránky, jejichž název uvádí. Zápis zatím neprovede: popíše, co by změnil (potvrzování přijde v dalších změnách). API: `POST /api/ai/conversations` a `…/{id}/messages` přijímají `task` (`chat.answer` výchozí, `assistant.agent`; bez práva 403 `ai_tools_forbidden`, denní limit 429 `ai_limit`), `POST /api/ai/messages/{id}/complete` s `Accept: application/x-ndjson` proudí řádky `step` a závěrečný `message` (nebo `error`), bez hlavičky vrací JSON jako dosud; zpráva má volitelné `steps` (nástroj a výsledek, nikdy argumenty ani výstup). Prompt `agent@1` se ukládá u zprávy. Prototypní `POST /api/ai/agent` a nastavení `Atlas:AssistantAgentPrototype` zanikly. Migrace databáze `2026101620` přidá ke zprávě AI sloupec `Steps` (jen u zodpovězené zprávy); stávající zprávy zůstávají beze kroků.
- Návrhy změn asistenta s nástroji: zápisový nástroj, který model v tahu zavolá, se neprovede, ale uloží jako návrh k potvrzení (zatím hromadné ikony, barvy a tagy přes `atlas_apply_labels`; ostatní zápisy asistent dál jen popíše). Náhled skládá server pod právy uživatele (název a cesta stránky, dnešní ikona, barva a tagy → navržené); neznámá ikona či barva, nečitelná stránka nebo víc než 100 změn návrh odmítne a model to přečte. Na konverzaci je nejvýš jeden otevřený návrh, nová zpráva ho nahradí (`superseded`), po 15 minutách propadne. Proud tahu má před zprávou řádek `proposal` (id, stav, `expiresAt`, položky `index`, `pageId`, `title`, `path`, `kind`, `before`, `after`), zpráva nese `proposalId`. `GET /api/ai/proposals/{id}` vrátí návrh vlastníkovi, `POST /api/ai/proposals/{id}/decision` `{ approve, items }` ho zamítne, nebo provede uložené argumenty jen zvolených položek (nic z těla požadavku) v nové relaci nástrojů na kanálu `assistant`, znovu ověří právo „AI s nástroji“ a výsledek uloží po položkách (`applied`, `failed` s kódem, `skipped`); cizí návrh 404, propadlý 409 `proposal_expired`, nahrazený 409 `proposal_superseded`, rozhodnutý 409 `proposal_decided`. Další tah ví, co se provedlo. Návrh i rozhodnutí jdou do auditu (`assistant.proposal`, `assistant.decision`). Migrace databáze `2026101630` přidá tabulku `AiToolProposal` (maže se s konverzací); stávající data se nemění.
- Asistent s nástroji čte i rozepsanou pracovní verzi stránky (`atlas_get_page` s `version: "working"`), ale jen editorovi stránky (čtenář dostane 404 jako ve webu) a jen když úloha Asistent čte rozepsané verze (`assistant.working-text`) smí poslat rozepsaný text profilu, na kterém asistent běží: lokálnímu a firemnímu vždy, cloudovému jen s povolením správce v Administrace → AI → Funkce. Rozhoduje se při každém volání, takže změna nastavení platí i uprostřed konverzace; jinak model dostane chybu nástroje 403 `working_version_unavailable` s vysvětlením (cloud bez povolení, nebo funkce vypnutá) a publikovanou verzi čte dál. Bez migrace databáze.
- Asistent s nástroji navrhuje i koncepty: založení stránky (`atlas_create_draft`), úpravu a vložení sekce (`atlas_update_section`, `atlas_insert_section`), nahrazení textu stránky (`atlas_replace_page`, tak i „přeformátuj tuto stránku podle stránky X“ jedním návrhem) a přejmenování (`atlas_rename_page`); jedno volání je jedna položka karty. Náhled skládá server pod právy uživatele z pracovní verze: u úpravy jen změněné sekce před a po (komponenty jako „⟦komponenta: …⟧“, karta je ukáže slovním rozdílem jako Historie), u přejmenování starý a nový název, u nového konceptu umístění a první řádky. Co by nástroj odmítl (zastaralá verze, chybějící nadpis sekce, neplatný Markdown, cizí značka komponenty, chybějící právo) odmítne už návrh a model to přečte; vynechanou značku komponenty odmítne až provedení (`409 components_removed`) a karta řekne, že se nic neuložilo, stejně jako u vlastních neuložených živých úprav (`409 collaboration_unsaved`). Provedený koncept nese kanál `assistant` a uživatele jako autora; karta pak nabídne „Otevřít v editoru“ (publikuje se zvlášť). Položka návrhu má nově `text` (`sections`, `before`, `after`), `before`/`after` štítků jsou u konceptu `null` a nový koncept má `pageId` prázdné, po provedení id nové stránky. Router k žádosti o koncept nabídne i strom prostoru, prompt `agent@3`. Bez migrace databáze.
- Asistent s nástroji umí shrnout několik stránek najednou (`atlas_summarize_pages`, jen pro asistenta, ne v MCP): na „shrň“, „souhrn“ nebo „přehled“ dostane 1 až 20 `pageId` a volitelné zaměření. Každou stránku přečte stejným handlerem jako `atlas_get_page` (jen publikovaný text, práva a vyloučení prostoru z AI; nedostupná stránka se přeskočí a výsledek jen řekne, že některá nebyla dostupná, bez jména), shrne ji promptem shrnutí stránky (tři souběžně, každé volání modelu má řádek `AiUsage` úlohy `assistant.agent`) a jedním dalším voláním sloučí shrnutí do krátkého souhrnu. Shrnuté stránky jsou zdroji odpovědi; více než 20 stránek je chyba nástroje `invalid_argument`. Router k „shrň“ nabídne i strom prostoru. Bez migrace databáze.
- Asistent s nástroji navrhuje i přesuny ve stromu a publikaci. Přesun (`atlas_move_page` pod jinou stránku, `atlas_move_page_to_space` do jiného prostoru) je jedna stránka na kartě: cesta před → po a počet podstránek, které se přesunou s ní, u jiného prostoru upozornění, že přístup převezme cílový prostor; neplatný strom, rodič mimo dosah nebo cyklus odmítne už návrh. Publikaci (`atlas_publish`) asistent navrhne jen na výslovnou žádost v poslední zprávě, nikdy kvůli textu stránky, jednu stránku na kartu s verzí, změněnými sekcemi, popisem změny a nálezy kontroly před publikací (jako stránka potvrzení MCP); výchozím výsledkem zůstává koncept a „napiš a rovnou publikuj“ dá nejdřív kartu konceptu. Rozhodují obsahová práva uživatele ve sdílených handlerech (přesun do prostoru bez práva zapisovat selže na kartě a nic se nezmění). `atlas_publish` v kanálu `assistant` publikuje jen při provádění potvrzeného návrhu, jinak vrací `403 publish_confirmation_required`; neuložené živé úpravy kohokoli včetně vlastních vrací `409 collaboration_unsaved`; publikace nese kanál `assistant` a audit. Externí klienti MCP dál potvrzují publikaci na `#/mcp/publish/{id}`. Položka návrhu má nově `move` (`before`, `after`, `subpages`, `otherSpace`) a `publish` (`version`, `publishedVersion`, `changedSections`, `changeSummary`, `findings`); router k přesunu nabídne i strom prostoru, prompt `agent@4`. Bez migrace databáze.
- Oprava: asistent s nástroji už nezadrží odpověď (`withheld` bez textu a kroků), když přečetl koncept, který nikdy nebyl publikován, nebo jinou stránku jen v pracovní verzi. Pracovní verze není citovatelný publikovaný zdroj, takže se neuloží jako zdroj ani závislost odpovědi a návrh z takového tahu (například publikace konceptu) se zachová; publikovaný zdroj, ke kterému uživatel během tahu ztratil přístup, odpověď zadrží dál. Na výslovnou žádost o nový koncept asistent místo odmítnutí navrhne kostru z faktů z wiki a chybějící části označí `[TODO: …]` (prompt `agent@4`). Bez migrace databáze.
- Profil AI typu Gateway (brána kompatibilní s OpenAI, která obsluhuje jeden načtený model) přežije výměnu modelu na serveru: Gateway obsluhuje jediný načtený model a pole `model` požadavku ignoruje, takže když profil uvádí jiný model, než server vypisuje (a server vypisuje aspoň jeden), zůstane profil dostupný a volání pokračují místo `model_not_found` po dvou kontrolách. Administrace → AI to ukáže jako varování jako u záloh: sekce „AI potřebuje pozornost“ jmenuje nastavený i skutečný model a vede na úpravu modelu, stav profilu je „Vyžaduje pozornost“ (i na dlaždici AI v Přehledu Správy wiki) a varování zmizí, jakmile se jména shodují. „Otestovat uložené spojení“ hlásí totéž jako varování, ne selhání. Využití a provenience dál zaznamenávají model, který skutečně odpověděl. OpenAI-kompatibilní servery a Anthropic zůstávají přísné.
- Automatický export PDF: do jedné složky může exportovat více stránek, každá pod vlastním názvem PDF (dosud jedna stránka na složku). Od každé stránky zůstává ve složce jen poslední PDF: nový export nahradí její předchozí soubor (dočasný soubor ve stejné složce a přejmenování) a po změně názvu nebo složky smaže soubor, který stránka zapsala naposledy. Název, který v téže složce používá nebo naposledy zapsal export jiné stránky, se při uložení odmítne (409 „Soubor s tímto názvem PDF v této složce již vytváří export jiné stránky…“, bez ohledu na velká a malá písmena) a export soubor jiné stránky nikdy nepřepíše ani nesmaže. Migrace databáze `2026101640` změní jedinečný index cíle na profil, složku a název PDF a přidá sloupce `LastStorageProfileId`, `LastFolder` a `LastFileName`; dosavadní exporty, které už PDF zapsaly, je mají vyplněné svým současným cílem.

## 2.10

*2.10.0 — 2026-10-13*

- Export do PDF se před stažením zeptá, co titulní strana uvede o revizi: číslo revize (i v záhlaví stránek), stav, datum revize, datum exportu a ID stránky. Předvolby Všechny údaje, Formální (číslo a datum revize) a Bez údajů, jednotlivé údaje zaškrtávátky a vedle živý náhled titulní strany s ukázkovými údaji; volba se pamatuje v prohlížeči a platí i pro PDF z Historie. Automatický export má stejnou volbu v oddílu Titulní strana. API: `details` u `GET …/export?format=pdf` (bez parametru všechny údaje jako dosud), `coverDetails` u `PUT /api/pages/{id}/pdf-export`. Nová komponenta `PdfCoverDetailsField` a dialog `PagePdfDownloadDialog` mají ukázku v galerii. Migrace databáze `2026101300` přidá k automatickému exportu sloupec `CoverDetails`; dosavadní exporty dál uvádějí všechny údaje.
- Exportované soubory se jmenují podle názvu stránky (ZIP prostoru podle prostoru) místo `atlas-page-<ID>`, například `Acme Care - příručka.pdf`; PDF z Historie s `– revize N`. Nový automatický export navrhne název PDF podle stránky místo `document.pdf`.

## 2.9

*2.9.0 — 2026-10-13*

- Export dlouhé příručky do PDF už nezahltí server: obrázky zmenšené pro tisk se ukládají (`<ContentExport:DataPath>/print-images`, pojmenované SHA-256 originálu a cílovou velikostí, nepoužité 30 dní nebo nad 1 GiB se mažou), takže se každý obrázek zmenšuje jen jednou. Další export téže stránky (i automatický) zmenšování přeskočí a export, který vypršel, předá hotové obrázky dalšímu pokusu; hláška `pdf_timeout` to říká. Chromium exportu i jeho ovladač běží v kontejneru s `nice 15`, takže při souběhu s exportem dostane procesor přednostně wiki. Řádek logu s časy fází uvádí i počet obrázků z mezipaměti. Bez migrace databáze.

## 2.8

*2.8.0 — 2026-10-13*

- Náročnost odpovědi asistenta: v poli otázky je tlačítko se zvoleným krokem; klepnutí otevře úzký měřič o pěti krocích (Rychle, Vyváženě, Důkladně, Do hloubky, Naplno), který určuje, kolik wiki si další odpověď přečte – 6, 8, 10, 12 nebo 14 úryvků, nejvýše 2, 2, 3, 3 nebo 4 z jedné stránky, z 8 až 16 zvažovaných stránek. Rychle vybírá stejně jako dosud; hlubší krok dá modelu víc textu ke shrnutí (u Naplno prompt až zhruba 52 000 znaků) a nově i další sekce téže stránky. `POST /api/ai/conversations` a `POST /api/ai/conversations/{id}/messages` mají volitelné pole `effort` (`quick` výchozí, `balanced`, `thorough`, `deep`, `max`; jiná hodnota 400). Měřič každým krokem ožívá v barvách AI (lesk, jiskry letící k úchytu, hvězdičky a záře); s omezeným pohybem zůstanou jen barvy. Nová sdílená komponenta `ui/slider` (uspořádané kroky jedné veličiny), `AiEffortPicker` a `AiEffortMeter` mají ukázky v galerii; pole otázky má s akcemi text na celém prvním řádku. Migrace databáze `2026101270` nahradí funkci `atlas_answer_sources` (limit úryvků jedné stránky mimo soustředěné okno určuje volající; data se nemění).
- Když asistent v dostupných stránkách odpověď nenajde, karta nabídne stejnou otázku znovu: „Hledat v celé wiki“ (byl-li rozsah stránka nebo prostor) a „Hledat do hloubky“, případně „Hledat naplno“ (vyšší náročnost); tlačítka se vysunou postupně před „Zkusit jiný dotaz“. Zvolený rozsah i náročnost pak platí i pro další otázky.
- Prompt odpovědi `answer@3`: model odpoví i ze zdrojů, které pokrývají jen část otázky, a jednou větou řekne, co v nich chybí; nevyplněnou hodnotu (např. `[TODO]`) uvede jako nevyplněnou. Smí propojit zkratku či název s otázkou (NHL a Horní Lhota), fakta ale bere jen ze zdrojů. Text, který se ve zdroji tváří jako pokyn, nepřebírá ani nepopisuje. Ladicí sada (Gemma 4 26B): správná abstence 28–29/30 (dříve 28/30), citace očekávané stránky 19–20/20 (dříve 18/20), únik kanárku 0. Uložené odpovědi si ponechají svou verzi promptu.

## 2.7

*2.7.0 — 2026-10-12*

- MCP vrací text stránky (Markdown s upozorněním, že jde o obsah, ne pokyny) i ve strukturované části výsledku (`text`): Claude Code ukazuje modelu jen ji, takže bez toho model neviděl text sekce, kterou má upravit.
- **Změna rozhraní pro klienty osobního API:** API (`/api/v1`) a MCP zapisují obsah stránek jen jako Markdown v dialektu editoru, dokument editoru sestaví server; rich JSON (`contentJson`) přijímá jen webový editor. `POST /api/v1/pages` a `PUT /api/v1/pages/{id}` berou `markdown` (uložení celý text stránky se značkami komponent `⟦c:N⟧`, které vrací nové `GET /api/v1/pages/{id}?format=markdown` v poli `source`; `title` je u uložení volitelný) a neprázdné `contentJson` odmítnou chybou 400 `content_json_not_accepted`. **Klient, který dosud posílal `contentJson`, musí přejít na Markdown.** Značka vrátí svou komponentu; vynechaná značka komponentu odebere, jen když je její číslo v novém `removeComponents` (odpověď ji uvede ve `warnings`), jinak zápis skončí `409 components_removed` a nic se neuloží. Nezměněné bloky (i formátované) si ponechají uložený uzel beze změny, nezměněné nadpisy ID; zarovnání, barvy a šířky buněk, výšky řádků, styl číslování a atributy odkazů text nezakrývají a upravený blok si je ponechá, kde je jasné, kam patří (řádky, sloupce a položky podle nezměněného obsahu, zbylé podle pořadí jen při stejném počtu; rozdělený odstavec na obou částech); úprava, která by je přesto zahodila, skončí `409 formatting_lost` (pole `blocks`) a nic se neuloží, dokud požadavek nepošle `resetFormatting: true` (podrobně v `docs/mcp-claude-code.md`, „Pravidla pro text stránek“). REST převádí Markdown mimo zápisovou transakci jako MCP. Stejně se nově chová `atlas_replace_page`, který stránku s komponentami dřív odmítal (`page_has_components`). Obrázky a odkazy na přílohy se píšou Markdownem: `![popis](/api/attachments/{id}/content)` je obrázek editoru, `[text](/api/attachments/{id}/download)` odkaz ke stažení, jen pro aktivní přílohy téže stránky (`attachment_not_found`) a u obrázku jen pro přílohu nahranou jako obrázek (`attachment_not_image`); jiné obrázky zůstávají odkazem. Nezměněný obrázek si ponechá šířku, zarovnání i ostatní vzhled. Vzhled a náhradu obrázku či karty přílohy mění bokem `PATCH /api/v1/pages/{id}/embeds/{attachmentId}` a `atlas_update_embed` (jen pracovní verze, `expectedVersion`, ostatní obsah beze změny, jinak `embed_invariant_failed`); samostatné vkládání a odebírání (`POST`/`DELETE …/embeds`, `atlas_insert_image`, `atlas_insert_attachment`, `atlas_remove_embed`) zaniklo dřív, než bylo vydáno. Bez migrace databáze.
- Bezpečnost zápisů Markdownu a MCP: `POST /api/v1/pages` (i ze šablony) a `atlas_create_draft` nejdřív zkontrolují délku textu, pak krátkým čtením práva a odkazy, Markdown převedou mimo transakci i zámky a teprve potom stránku založí; token bez práva v prostoru tak už nedrží zámek prostoru po dobu převodu. Text připojený za šablonu se slučuje se šablonou přečtenou předem a stránka vznikne jednou revizí; změní-li se šablona mezitím, je výsledek `409 version_conflict`. `PUT /api/v1/pages/{id}` kontroluje délku před čtením stránky. `removeComponents` má nejvýše 1 000 čísel (jinak 400 `invalid_argument` před sloučením) a chybové hlášky mají nejvýše 2 000 znaků. Příliš hluboce vnořený Markdown je 400 `invalid_markdown` místo chyby 500. Limit požadavku na `/mcp` je opět 3 000 000 B pro každé volání a `atlas_upload_attachment` proto přijme nejvýše 2 MiB (větší soubor `413 attachment_too_large` s odkazem na REST multipart; Claude Code spolehlivě pošle jen jednotky kB base64). Jeden token zpracovává současně nejvýše 4 požadavky na `/mcp` (účet napříč tokeny 8) a 2 zápisy Markdownu přes REST (účet 4), další dostane 429 s `Retry-After`. Odmítnuté založení stránky přes REST se v auditu API znovu zapisuje s prostorem. `atlas_get_labels` rozhoduje vyloučení prostoru z MCP až pod zámkem prostoru. Bez migrace databáze.
- Zápis Markdownu přes REST a MCP už potichu neztrácí formátování ani adresy sekcí: obrázek s novým popisem či titulkem si ponechá velikost a vzhled (titulek obrázku, ořez, otočení); blok přesunutý beze změny (prohozené sekce) si ponechá `headingId`, zarovnání i atributy odkazů; nadpis v citaci, boxu či seznamu si ponechá `headingId`; odkazy na stejnou adresu si nevymění `target`/`class`. Formátovaný blok přesunutý a zároveň upravený, změněný na jiný druh bloku, obrázek se vzhledem nahrazený jiným a odkaz s vlastními atributy bez protějšku jsou `409 formatting_lost` (`resetFormatting: true` je zahodí). Upozornění (`warnings`) uvedou ID nadpisů, která zápis odstranil nebo přidělil jen podle úrovně a pořadí. Server zapisuje rich JSON jako web (čeština bez `\u0159`) a nezměněný obsah porovnává podle hodnoty, takže uložení beze změny už nevytvoří revizi. `removeComponents` a značky `⟦c:N⟧` se kontrolují v lineárním čase a hlášky jich vypíší nejvýše 20. Přidání či odebrání tagu zachová ostatní tagy podle ID (i se starým názvem, který dnešní pravidla odmítnou). Kontrola zálohy před obnovou používá stejná pravidla jako obnova (i limit Markdownu); postup pro archiv s neplatnými verzemi popisuje [zálohování a obnova](docs/backup-restore.md#archiv-wiki-s-verzemi-které-dnešní-editor-neotevře). Přejmenování ukládá Markdown napsaný serverem. Snímek společné úpravy, který server jako obsah stránky odmítne, se dál neukládá, ale uložení stránky z editoru místo nekonečného „Společné změny se ještě ukládají“ ohlásí skutečnou chybu s cestou (`invalid_document`, `document_too_large`); relay ho neopakuje každou sekundu. Migrace databáze `2026101250` přidá k živému dokumentu sloupce `RefusedSequence` a `Refusal` (stávající řádky bez odmítnutí).
- Přílohy přes REST API a MCP: nový výpis příloh stránky (`GET /api/v1/pages/{pageId}/attachments`) a údaje o jedné příloze (`GET /api/v1/attachments/{id}`), obojí i jako `/api/…` pro web. Vrací název, MIME typ, velikost, SHA-256, čas nahrání, kanonické odkazy, odkaz ke stažení tokenem a zda přílohu používá publikovaná a pracovní verze. Přístup rozhoduje stejně jako stažení: čtenář vidí jen přílohy publikované verze, nikdy přílohy konceptu. MCP má nové nástroje `atlas_list_attachments`, `atlas_get_attachment`, `atlas_download_attachment` (původní bajty jako binární zdroj po částech do 4 MiB se SHA-256) a `atlas_upload_attachment` (rozsah Koncepty, base64 do 2 MiB a povinný `sha256` původních bajtů, neshoda skončí `hash_mismatch` bez uložení; model spolehlivě pošle jen jednotky kB; stránku nemění ani nepublikuje). Limit požadavku na `/mcp` zůstává 3 000 000 B; větší požadavek dostane srozumitelnou odpověď 413. Soubory nad 2 MiB se nahrávají přes REST multipart.
- Ikony a tagy stránek přes osobní REST API (`/api/v1`) a MCP. REST: čtení a nastavení ikony a barvy (`/pages/{id}/decoration`, `expectedRevisionId`, `decoration_changed`, vymazání), katalog identifikátorů ikon a barev (`/appearance`), čtení a nastavení tagů (`/pages/{id}/tags`, `expectedTagIds`, `tags_changed`), přidání a odebrání tagů se zachováním ostatních pod zámkem stránky (`POST /pages/{id}/tags/changes`), nabídka tagů a stránky podle tagu (`/tags`, `/tags/{id}/pages?spaceId=`). MCP: `atlas_get_labels`, `atlas_list_appearance`, `atlas_suggest_tags`, `atlas_find_pages_by_tag` (rozsah Čtení) a `atlas_set_decoration`, `atlas_set_tags` (rozsah Koncepty). Změna se čtenářům ukáže ihned, nepublikuje se a nevytváří revizi dokumentu, proto stačí rozsah Koncepty; zápisy se auditují. Přes MCP nabídka a hledání tagů nevyzradí tagy ani stránky vyloučených prostorů. Webové (cookie) cesty se nemění.
- MCP: organizace stránek. Nástroje `atlas_move_page` (pod rodiče, na kořen, řazení mezi sourozenci), `atlas_move_page_to_space`, `atlas_trash_page`, `atlas_list_trash` a `atlas_restore_page` volají stejné operace jako web a REST (verze stromu, verze stránky, výslovný rozsah podstromu, potvrzení změny přístupu, neuložené živé úpravy); zápisy vyžadují rozsah Publikace jako v REST a mění strom pro čtenáře hned. Prostor vyloučený z MCP je nerozeznatelný od neexistujícího. `atlas_list_pages` navíc vrací `treeVersion`, `version`, `position` a `canEdit`. Bez migrace databáze.

- Validace obsahu editoru na serveru: každý zápis rich dokumentu (osobní API, MCP, import, zdroj stránky, šablony, obnova z historie i web) se ověřuje proti schématu připnutého editoru – typy uzlů a značek, pole, atributy a jejich hodnoty (prezentace obrázku, adresy obrázků a odkazů – u odkazů jen skriptovací schémata `javascript:`, `vbscript:` a `data:`, data komponent), obsah uzlů, geometrie tabulek a deklarace pluginů. Dokument, který by editor neotevřel („Dokument se nepodařilo otevřít“), server odmítne chybou 400 `invalid_document` s cestou k chybě (`document.content[12].attrs.width`, i v poli `path`). Dříve uložené vadné revize zůstávají čitelné a správce prostoru je najde přes `GET /api/spaces/{spaceId}/content-validation`; nic se automaticky nepřepisuje. Schéma se generuje z nainstalovaného editoru a testy hlídají, že každý nový atribut má pravidlo.
- Validaci dokumentů editoru přebírá balíček editoru: server ověřuje rich obsah validátorem `Atlas.Markdown.Sync` 0.16.0 (manifest stejného vydání `@atlas/markdown`) místo vlastního portu pravidel; vlastní schéma, generátor a korpus verdiktů v Atlasu zanikly a povýšení editoru přináší pravidla samo. Rozhraní zůstává: 400 `invalid_document` s cestou v hlášce i v poli `path`; důvod je český podle kódu porušení a za ním je v závorce anglický detail validátoru. Kolaborační ochrana nově ověřuje i hodnoty atributů a strukturu dokumentu stejným manifestem. Bez migrace databáze.
- Uložený Markdown stránky píše server: z ověřeného obsahu editoru při uložení, vytvoření (i ze šablony), úpravě zdroje, flushi společné úpravy i obnově zálohy. Markdown poslaný klientem (pole `markdown` v osobním API a webu, nyní volitelné) se ignoruje, takže vyhledávání, export, zálohy, AI index ani MCP nečtou text, který v dokumentu není. Uložení lišící se jen Markdownem je beze změny. Obnova a přidání zálohy ověří každou verzi stránky jako ostatní zápisy; archiv s verzemi, které by editor neotevřel, odmítne celý se seznamem (stránka, verze, cesta, důvod) dřív, než cokoli změní; opraví se v archivu, nebo obnovou verzí Atlasu, která zálohu vytvořila, a běžnou aktualizací. Obnova z historie píše Markdown také znovu z JSON. Nové a změněné názvy stránek, prostorů, skupin, uživatelů, tokenů, odkazů pro sdílení a trezorů odmítnou zalomení řádku, řídicí znaky a řídicí znaky obousměrného textu i ve webu a osobním API (MCP řídicí znaky odmítal už dřív); dříve uložený název poslaný beze změny se dál uloží a kontrola obsahu prostoru ho vypíše. Popis prostoru a poznámka k hodnocení AI smějí mít zalomení řádků, jiné řídicí znaky ne. Jména a názvy převzaté z OIDC, KDBX, Hubu a importu se od řídicích znaků očistí. Názvy příloh ztratí všechny neviditelné formátovací znaky. Spolupráce ukládá jen ověřenou rich projekci a známé schéma; prostý text klienta se neukládá.
- Nadpisy úrovní 5 a 6. Markdown z osobního API, MCP i importu wiki čte `#####` a `######` jako H5 a H6 (dosud je snížil na H4 s upozorněním, import wiki s nálezem `heading-level`, který zanikl) a zápis je vrací beze změny, takže uložené H5/H6 už v Markdownu pro MCP nejsou značkou komponenty `⟦c:N⟧`. Obsah stránky v doku zahrnuje H1–H6. S editorem 0.16.0 se H5 a H6 správně vykreslují ve čtení, úpravách i PDF (dosud jako H1). Bez migrace databáze.
- Číslovaný seznam s velkým počátečním číslem (vložené `<ol start="3000000000">`, editor připouští každé bezpečné celé číslo JavaScriptu) už nezastaví ukládání: serverový Markdown počítal čísla položek v rozsahu `int`, flush společné úpravy proto opakovaně selhával („Uložit“ i „Publikovat“ zůstaly neaktivní) a zápis přes API by skončil chybou 500. Čísla se nyní píší jako v editoru (i se zaokrouhlením nad 2^53). Bez migrace databáze.

- Export PDF je předvídatelný a nezdržuje ostatní stránky: najednou se tiskne jediné PDF a další požadavek hned dostane 503 `pdf_busy` s `Retry-After: 30` místo čekání do 504 reverzní proxy; ruční stažení počká nejvýše 30 s jen na právě tištěný automatický export, který mezitím další položku nezačne, a automatický export obsazení nevykazuje jako chybu. Celá příprava v prohlížeči má limit 100 s a chyba `pdf_timeout` říká, ve které fázi skončila; prohlížeč exportu se po skončení, chybě i přerušení vždy zavře, případně ukončí. Rozlišení obrázků klesá podle potřeby až na 150 DPI a o tom, zda se obrázky vejdou do 40 milionů pixelů, se rozhodne z hlaviček souborů před spuštěním prohlížeče: příliš velký dokument se odmítne hned (413 `pdf_images_too_large`) se zprávou, kolik pixelů by potřeboval a co změnit. Tisková sestava už obrázky nerasterizuje ve 300 DPI kvůli neutrálnímu CSS filtru rendereru (u syntetické příručky s 64 obrázky 5× méně pixelů v PDF, tisk 3,4× rychleji) a dává všem šesti úrovním nadpisů vlastní velikost. Podrobnosti v [exportu do PDF](docs/pdf-export.md#souběh-časové-limity-a-ukončení-prohlížeče).
- Export PDF delších příruček se screenshoty: obrázky z příloh se pro PDF zmenší na velikost, kterou na stránce skutečně zabírají (300 DPI, u dlouhých dokumentů méně, nejméně 150 DPI, aby text screenshotů zůstal čitelný). Limit 40 milionů pixelů celkem se teď počítá po tomto zmenšení, takže příručka s desítkami screenshotů Full HD už nekončí chybou 413. Limit 16 milionů pixelů na jeden originál zůstává a chybová hláška uvádí název souboru. Původní přílohy a jejich stažení se nemění; zmenšená kopie existuje jen v paměti exportu. Podrobnosti v [exportu do PDF](docs/pdf-export.md#obrázky-v-pdf-a-jejich-limity).

- Omezené animace: když formulář po krocích nahradí závěrečný krok (například nově vydaný API token), formulář nejdřív vybledne a výsledek se objeví až na jeho místě. Dosud se výsledek ukázal pod formulářem, stránka se o něj na okamžik prodloužila (o 314 px) a pak se v jednom snímku zkrátila o celý formulář, takže napůl viditelný výsledek poskočil nahoru. Stejně se teď střídá krok, který z formuláře odchází, s krokem, který se místo něj otevírá (trezor: „Nové umístění“). Běžných animací se změna netýká.
- Asistent: tlačítko Odeslat nese papírové letadélko. Odeslaná otázka s ním odletí: letadélko se odlepí z tlačítka, udělá nad polem lehkou kličku a odplachtí do ztracena; po hotové odpovědi do tlačítka přiletí nové. Bublina otázky do vlákna přistane z rohu u pole, uvítání po první otázce plynule odejde, návrhy otázek a znovu otevřená konverzace naběhnou postupně, čip s výběrem textu a upozornění pod polem se objeví i zmizí plynule. Při omezených animacích nic nelétá.
- Odstraněno „Nahlásit chybějící obsah“ z odpovědí AI, ze zpětné vazby (palec dolů → Chybí informace) a z hledání bez výsledků. Mezery v hledání v nastavení prostoru dál vznikají z hledání bez výsledků.
- Mapa souvislostí pod článkem se už nestránkuje: po rozbalení zabere celou šířku patičky a ukáže všechny vazby najednou. Štítky souvisejících článků nad ní jsou o odstín klidnější.
- UI polish napříč rozhraním: chybějící a nejednotné odsazení (vnořené karty nastavení, například Spojení v průvodci AI, zase drží vnitřní okraj a sahají k oběma okrajům karty), sbalené části bez prázdných pruhů, viditelné rámečky fokusu i uvnitř ořezaných prvků, plynulé objevování a mizení poznámek a výsledků, chybějící stavy stisku. Index znalostí: výsledek kroku Ověření je zase vidět.
- Vyhledávací paleta: příkaz (například Kopírovat odkaz) se spustí až po zavření palety. Dosud ruční kopie odkazu, když prohlížeč schránku odmítl, otevřela okno bez fokusu a na telefonu ho vrácený fokus hned zase zavřel.
- Telefon: po zavření ruční kopie odkazu se fokus vrátí tam, odkud kopírování začalo (nabídka akcí stránky), místo aby se ztratil.
- Široký dokument: název stránky při úpravách, shrnutí a „Odtud dál“ lícují s okraji textu. Dosud název v úpravách přesahoval text o 40 px (při 1920 px až pod otevřený boční panel). „Přidat ikonu“, tagy a řádek stavu při úpravách začínají u levého okraje textu jako v pohodlné šířce, ne uprostřed nad ním. Odstraněna mrtvá pravidla staré osnovy editoru.

## 2.6

*2.6.0 — 2026-10-11*

- Rozložení článku, knihovny a nastavení se přizpůsobuje skutečné šířce dostupného prostoru. Boční panel při nedostatku místa přechází do překryvu a zachovává zvolenou šířku.
- Sjednocené hovery, fokus a zakázané stavy ovládacích prvků; plynulé otevírání a zavírání panelů, dialogů a formulářů zvládá i rychlé obrácení animace. AI ikony výrazněji ukazují průběh práce a respektují omezený pohyb.
- Hledání odkrývá vedlejší filtry na vyžádání, zachovává viditelné aktivní filtry a před zadáním dotazu nevyhrazuje místo prázdné odpovědi AI.
- AI nastavení a osobní předvolby postupně odkrývají související volby, zachovávají kompatibilní rozepsané hodnoty a při chybě otevřou příslušnou pokročilou sekci. Skryté neplatné hodnoty jiné větve neblokují uložení.
- Výběr vzhledu funguje i v úzkém panelu; hledání ikon se ukáže až po rozbalení „Další ikony“. Návrhy ve filtrech se zavírají při odchodu fokusu nebo klávesou Escape.
- Nabídka akcí neblokuje okolní navigaci, publikační panel lze znovu otevřít během zavírání a kliknutí na konkrétní historickou verzi článku otevře právě tuto verzi.
- Boční panel článku se otevírá a zavírá posunem, text se při tom nepřelamuje. Na velmi širokém monitoru stojí hned vedle článku místo u vzdáleného okraje a zapamatovaný panel už po načtení stránkou nepohne. Obsah v panelu drží aktivní nadpis v dohledu.
- Štítky článku navazují na datum a zalamují se; mnoho štítků už nezpůsobí vodorovné rolování. Široké tabulky naznačují stínem, že pokračují do strany.
- Začátek a konec úprav stránky je plynulý a zachová místo čtení; text při přechodu nezmizí a stránka neodskočí na začátek.
- Knihovna a prostor: záložky i přepnutí Karty / Seznam se prolnou bez skoku, obálky karet nenaskakují, hlavička tabulky stránek zůstává vidět a filtrování velkého prostoru je svižnější. Nový prostor je na širokém monitoru uprostřed.
- Postranní panel: větve stromu se rozbalují plynule, otevřená stránka se ve stromu vždy ukáže a oříznuté názvy mají nápovědu. Drobečková navigace se při zúžení nerozpadá, nabídka „…“ obsahuje všechny nadřazené stránky a horní lišta zůstává na jednom řádku i při úpravách.
- Nastavení: náhled člověka se řídí místem v seznamu (na notebooku se dřív otevřel až pod tabulkou), záložky mají posuvný indikátor, lišta neuložených změn přijíždí zespodu a po uložení odjede dolů, chybové hlášky se rozbalují místo vyskočení.
- Ovládací prvky mají jednotný stisk, jeden fokusový rámeček a odezvu na najetí, která ničím nepohne. Dialogy i hledání se otevírají za 200 ms a zavírají za 160 ms; po zavření dialogu otevřeného z nabídky se fokus vrátí na její tlačítko.
- Rychlost: článek je čitelný dřív (stránka se začne načítat zároveň s ověřením přihlášení a nečeká na zvýrazňovač kódu), přechod mezi články a návrat z nastavení jsou kratší, odlesk loga už při čtení nezatěžuje procesor a tažení šířky postranního panelu je plynulé.
- Rychlejší otevření článku: sestavené soubory aplikace (`/assets` s otiskem v názvu) server vydává ještě před ověřením přihlášení, takže jejich načtení už nestojí dotaz na odvolání relace za každý soubor. Dotaz na odvolání relace se přeskakuje i u `index.html` a u ostatních existujících statických souborů (u požadavku s platnou cookie 0 SQL dotazů místo 1); ověření přihlášení u nich dál probíhá a dotaz si ponechávají API, huby a požadavek na soubor, který neexistuje. Při nedostupné databázi se soubory aplikace a úvodní stránka obslouží i s cookie relace, dříve skončily chybou 500.
- Obrázky v článcích si prohlížeč ponechá a při dalším zobrazení se jen zeptá serveru (`Cache-Control: private, no-cache` s ETagem). Server pokaždé znovu ověří oprávnění a beze změny odpoví 304 bez těla; po odebrání přístupu vrací 404, takže uložená kopie se nezobrazí. Stažení souborů, osobní API a veřejné sdílení zůstávají `no-store`. Požadavek na přílohu, na přílohu přes sdílený odkaz i na stažení exportu obsahu ověřuje oprávnění před začátkem přenosu jednou místo dvakrát (příloha u čtenáře 17 SQL dotazů místo 33, příloha přes sdílený odkaz 15 místo 30, export stránky u čtenáře 22 místo 31).
- Strom stránek se načte i uživateli, který vidí víc než 65 535 stránek; dosud selhal chybou „A statement cannot have more than 65535 parameters“. Dotaz na strom má nyní pro každý počet stránek stejný tvar. Stejně lze přesunout podstrom do koše a obnovit ho z koše, i když má jedna jeho úroveň víc než 65 535 stránek.
- Nastavení: globální nastavení a nastavení prostoru se stahují zvlášť a až při otevření; dosud se při otevření kteréhokoli stáhlo obojí. Část nastavení, kterou se nepodaří načíst, ukáže hlášku s tlačítkem Obnovit stránku a navigace nastavení zůstane použitelná.
- Editor: obsah článku se při psaní nepřepočítává při každém snímku (dosud 40 stisků kláves v odstavci znamenalo 40 dotazů na nadpisy a 40 měření). Přidaný nebo odebraný nadpis se projeví v dalším snímku, přejmenovaný nadpis a aktivní položka po změně, která posunula nadpisy, nejpozději 100 ms po odmlce v psaní, při nepřetržitém psaní nejpozději po 300 ms.
- Odkazy na související stránky pod článkem a v bočním panelu se po otevření stránky ani po označení hvězdičkou nenačítají znovu a na chvíli nezmizí.
- Rychlost: kontrola záloh pro správce už u adres stránek a knihovny nezdržuje první zobrazení; varovný odznak se ukáže, až když odpověď dorazí.
- Záloha do Gitu: klidná wiki už každých 15 sekund nesestavuje celý snímek stránek, ale nejdřív jedním dotazem přečte pět počítadel a úplné porovnání provede jen při jejich změně, po startu, při ruční synchronizaci, po chybě, při čtení stavu a při každé čtyřicáté klidné kontrole (po deseti minutách). Klidná kontrola stojí 5 SQL dotazů místo 12, při 10 000 stránkách 5 až 7 ms místo 368 ms. Ručně upravený nebo ztracený záznam stavu zálohy, zápis mimo triggery či obnovená databáze se tak zjistí do deseti minut místo do 15 sekund.
- YouTrack: hledání, rozpoznání úkolu a test spojení volají YouTrack mimo databázovou transakci. Dosud každé pomalé volání (až 8 sekund) drželo spojení do databáze v nečinné transakci; při měření s pomalou odpovědí YouTrack jsou nečinné relace v transakci 0 místo 1 u každé ze tří operací. Odpovědi se nemění.
- Opraveno: potvrzení publikace konceptů se ptalo „Publikovat 5 stránky?“ a shrnutí důvěryhodných proxy psalo „nejvýš 5 hopy“ a „0 důvěryhodné proxy“. Nyní je tam „5 stránek“, „5 hopů“ a „0 důvěryhodných proxy“.
- Opraveno: adresa nastavení s názvem sekce, který se shoduje s vlastností každého objektu (například `#/settings/constructor`), už správce nepřesměruje na nesmyslnou adresu a ukáže se jako každá jiná neznámá sekce. Adresy, které jen začínají jako adresa stránky (například `#/pages/<id>/x`), vedou do Knihovny a při startu už nespouštějí zbytečné načtení stránky.
- Obrázek prostoru: Atlas ho vybírá sám z vestavěného katalogu 97 kreseb podle názvu, popisu a ikony prostoru a ukazuje ho na kartě v Knihovně, v hlavě prostoru, v Nastavení prostoru i při zakládání prostoru. V Nastavení prostoru → Obecné lze v galerii vybrat jinou kresbu, nahrát vlastní obrázek, zvolit prostor bez obrázku, nebo se vrátit k automatickému výběru. Aktualizace provede migraci 2026101142: dříve nahrané obrázky zůstávají, ostatní prostory dostanou automatický výběr. Vybraná kresba a volba „bez obrázku“ se zálohují ve formátu ZIP 6; archiv bez nich zůstává čitelný pro Atlas 2.5.
- Ikony prostorů a stránek: pod „Další ikony (1 864)“ je celá sada Lucide 1.55.0, tedy dosavadních 87 ikon a 1 777 dalších ve stejné kresbě; volba nahrazuje „Vlastní…“. Hledá se česky i anglicky bez ohledu na diakritiku, výsledky jsou seřazené od nejlepší shody a každá ikona má pod sebou český název. Ikony lze zúžit na doporučené, naposledy použité nebo jednu ze 14 kategorií; mřížkou se prochází šipkami a volí se Enterem, mezerníkem nebo kliknutím. Balík ikon se stahuje až při otevření „Další ikony“ nebo při zobrazení takové ikony, úvodní načtení aplikace ho neobsahuje. Název ikony stránky může mít až 40 znaků a databáze u něj hlídá jen tvar, platné názvy ověřuje aplikace (migrace 2026101141). Opraveno: nastavení prostoru vytvořeného importem z Wiki.js (ikona `archive`) jde znovu uložit.
- Nastavení: místo jedné lišty s přepínačem Já · Prostor · Wiki jsou tři místa, každé s vlastním vstupem, nadpisem, drobečkovou navigací a cestou zpět: Profil (avatar), Nastavení prostoru (správci prostoru) a Správa wiki (správci wiki, dříve Nastavení wiki). Profil už nepřekrývá otevřené nastavení. V Nastavení prostoru lze mezi spravovanými prostory přepínat v liště: otevře se stejná sekce vybraného prostoru a při rozepsané změně se Atlas zeptá, zda ji zahodit. Každý, kdo smí prostor otevřít, má v něm záložky Stránky a O prostoru (popis, vlastní přístup a šablony; správce tu navíc najde odkazy do sekcí nastavení, seznam lidí nevidí nikdo). Správa wiki je seskupená do částí Lidé, Prostory, Trezory a hesla, AI a integrace a Provoz a filtr v liště hledá jen sekce (Hledat sekci). Dosavadní adresy fungují beze změny; přibyla jen adresa O prostoru (`#/spaces/<id>/about`).
- Úprava postupu vydání; aplikace beze změny.

### 2.6.1 — 2026-10-11

- Doladění rozhraní po 2.6.0: zaoblení, zarovnání a animace, které nebyly dotažené. Beze změny chování a dat.
- Postranní panel: rozbalený prostor se rozbaluje a sbaluje plynule i se svým rámečkem. Řádek prostoru se při tom neposune do strany a řádky pod ním neposkočí (dosud 4 px do strany a 12 až 16 px svisle v jednom snímku). Zaoblení řádku odpovídá kartě kolem něj. Obrácení uprostřed pohybu naváže tam, kde je, „Sbalit vše“ a „Rozbalit vše“ mění výšku stromu plynule, dlouhé stromy se pohybují jen ve viditelné části a připnutí či odepnutí prostoru otevře nebo zavře jeho místo v seznamu.
- Postranní panel: název prostoru se pod nabídkou „…“ plynule ztrácí, bez tmavšího obdélníku; vybraný prostor si při najetí drží barvu výběru a fokusové rámečky končí v rozích řádků. Při sbalení celé navigace panel odjíždí a mizí zároveň, drobečková navigace neposkočí a fokus z klávesnice se neztratí. Při změně šířky zůstává kurzor i za krajní šířkou. Po návratu z nastavení nebo profilu se navigace wiki objeví plynule.
- Telefon: lišta akcí stránky drží u spodního okraje i pod krátkou stránkou, položky vysouvacího panelu mají výšku 40 px, v drobečkové navigaci zůstává čitelné aktuální místo a zkrácené části mají nápovědu s celým textem.
- Článek: tlačítko Kopírovat u bloku kódu po zkopírování neukazuje druhý text a neuskočí o 98 px; zaškrtávátka úkolů sedí na středu prvního řádku; štítek stavu má výšku štítků tagů; obsah „Na této stránce“ má rovnou lištu, aktivní nadpis přejíždí jedna značka a řádek obsahu nad článkem při otevření bočního panelu zeslábne. Ikonová tlačítka horní lišty mají nápovědu.
- Boční panel článku se čtyřmi záložkami se vždy vejde do své hlavičky (nejmenší šířka 340 px). Historie: potvrzení „Obnovit tuto verzi…“ se rozbalí na místě a oba přepínače jsou stejně vysoké.
- Segmentové přepínače (historie, Uspořádat stránku, schvalování konceptů, Dokument | Zdroj, Karty / Seznam, přepínače v nastavení) posouvají jedno zvýraznění, místo aby se prohodila dvě políčka.
- Drobná potvrzení tam, kam se člověk dívá: hvězdička oblíbených se vybarví a po výběru jednou poskočí, zkopírování odkazu a kódu potvrdí ikona, stav exportu PDF po dokončení krátce vyznačí potvrzení, palec hodnocení odpovědi AI se po stisku potvrdí a zaškrtávací políčko fajfku nakreslí.
- Úpravy stránky: při zahájení a ukončení úprav text neposkočí o výšku přepínače Dokument | Zdroj (36 px), název stránky s ikonou neuskočí do strany a tlačítka horní lišty plynule odjedou a vrátí se (dosud skok o 278 px ve dvou snímcích). Text vedle „Upraveno …“ při ukládání neposkakuje. Nově vytvořená stránka přináší lištu formátování spolu s hlavičkou a textem.
- Tagy: vybrané tagy v okně úprav jsou opět vidět a okno při otevření ani při psaní neskáče. Umístění stránky: názvy řádků začínají na jedné linii a šipka rozbalení se otáčí. Schvalování konceptů: dialogy jsou zarovnané s nadpisem a tlačítka jsou v patičce.
- Opraveno: v panelu „Před publikací“ končily Zobrazit, Zpět k úpravám a Esc chybou a místo ve stránce se nevybralo. V nabídce akcí stránky fungují šipky, Home a End i po otevření myší.
- Knihovna: obrázek karty prostoru plynule přechází do karty; řádky Nedávných, Konceptů a Publikací mají jednu výšku; prázdné a chybové stavy částí Knihovny jsou jednotné a patička sedí hned pod seznamem; hledání bez výsledku seznam prolne s prázdným stavem; přepnutí záložky nekolísá výškou; čas na úzké kartě se neutne uprostřed slova (ustoupí mu role, která se ukáže ikonou).
- Prostor: při otevření neproblikne „0 stránek“, štítek konceptů přijede na své místo, vstup do výběru konceptů a odchod z něj nic neposune (dosud 36 px v jednom snímku) a připínání ukazuje během čekání kolečko.
- Hledání: tagy a aktivní filtry se nad výsledky rozbalují (dosud skok o 87 px); najetí myší se liší od výběru klávesami; nedávné a oblíbené stránky vyjedou bez bliknutí „Načítání…“; počet výsledků a filtry rozsahu se rozbalují; vybraný řádek se zastaví s odstupem i nad patičkou; hotová odpověď AI sklouzne na místo a panel odpovědi se zavírá bez stlačení textu do úzkého sloupce.
- Vyhledávací paleta: stín přijde s rozbalením, ne skokem po něm. Z vyhledávacího pole se rozbaluje, když je pole poblíž; na širokých obrazovkách se rozbalí na místě.
- Asistent a AI: popisek v postranním panelu začíná vedle ikony, vlákno pod štítky kontextu zeslábne místo useknutí řádku, odesílací tlačítko je souosé s polem, karta kvóty vyroste ze svých kroužků a shrnutí stránky se rozbaluje bez úvodního skoku.
- Nastavení: oznámení o výsledku se rozbalí a sbalí (dosud posun stránky o 68 px v jednom snímku); sekce Správy wiki, stránky nastavení prostoru a sekce profilu se prolínají, nová začíná nahoře a stará zůstane po dobu prolnutí tam, kde byla; lišta neuložených změn neposune stránku o mezeru; při filtrování seznamu mizející řádky blednou na místě a karta plynule mění výšku; kroky průvodce se pohybují ve směru cesty a poslední krok už nenahradí formulář skokem; šipky rozbalovacích sekcí se otáčejí stejně dlouho jako jejich panel.
- Nastavení: nadpis karty a názvy jejích řádků začínají na jedné čáře; dlaždice a tlačítka v záhlaví s dlouhým popisem drží u prvního řádku; boční lišta nastavení lícuje s navigací wiki a aktuální sekce je v ní vidět i na nízkém okně; všechny stránky nastavení prostoru mají v hlavičce ikonu; poznámky s odkazem se zalamují jako jedna věta; řádky „Kdo má přístup“ drží odsazení karty; karty voleb mají stejný odstup nad textem i pod ním. Při změně motivu nebo akcentu na profilu se karty přebarvují společně s okolím.
- Moduly: vnořené karty sahají k okraji karty; karty s doplňujícími částmi nemají prázdný pruh pod posledním řádkem; pruh s testem spojení YouTracku doléhá na okraj karty; tlačítka Kopírovat nemění šířku a jsou souosá s polem; průvodce poskytovatelem přihlášení, části záloh a migrace z Hubu se prolínají s předchozím obsahem; chyby a hlášení se rozbalují. Klepnutí na „Otevřeno“ u rozevřeného kroku nastavení AI už formulář nezavře a nenačte znovu.
- Lidé a přístupy: náhled člověka se zavírá prolnutím a načtené údaje se rozbalují; části karty člověka a skupiny se prolínají; řádky tabulky lidí zavírají mezeru posunem i při rychlém psaní; hlášky průvodců se rozbalují. Opraveno skloňování: „2 čekají na aktivaci“, „3 stránky“.
- Profil a přihlášení: přepnutí mezi přihlášením, aktivací účtu a obnovou správce plynule mění výšku karty; po přihlášení, odhlášení i vypršení relace se obrazovky prolnou; informace o verzi má čitelný kontrast; změna hesla plynule vymění řádek za formulář; kalendář aktivity se na telefonu otevře u dneška; osobní tokeny a dokumentace API se rozbalují. Veřejná sdílená stránka je vystředěná.
- Sdílené prvky: fajfka zaškrtávacího políčka je uprostřed; přepínač má celopixelovou výšku; záložky sedí v liště rovnoměrně; tabulky mají linky mezi řádky; v dialogu vyšším než okno obsah neprosvítá nad nadpisem a pod tlačítky; oznámení (toast) se v tmavém motivu odliší od stránky; nabídka otevřená nad tlačítkem nebo vedle něj vyjede směrem od něj; načtený obsah plynule nahradí zástupné řádky; čárkovaný rámeček prázdného stavu je v tmavém motivu vidět.
- Stránka nebo seznam, který při prolnutí odchází, není po dobu mizení dosažitelný klávesnicí ani čtečkou obrazovky.
- Bez migrace databáze a beze změny konfigurace. Editor zůstává na verzi 0.15.0.

### 2.6.2 — 2026-10-12

- Strom v postranním panelu wiki: dotažené rozbalování, zarovnání a najetí myší. Beze změny chování a dat.
- Řádek prostoru při rozbalení a sbalení stojí na místě a šipka zůstává pod kurzorem. Dosud řádek při rozbalování sjel o 6 px a při sbalení se vracel, protože místo pro kartu kolem prostoru vznikalo až se stromem; teď ho má každý řádek stále (rozteč řádků prostorů je 38 px místo 36).
- Rozbalení a sbalení prostoru i větve vychází z klidu a trvá podle vzdálenosti (220 až 400 ms). Dosud první snímek urazil čtvrtinu cesty, u stromu vysokého 400 px zhruba 100 px. Šipka se otáčí stejně dlouho jako strom, který otevírá. Dlouhý strom dojede a zpomalí u spodního okraje seznamu prostorů, ne až za ním.
- Prostor, jehož strom se teprve načítá, počká na stránky nejvýše 150 ms a rozbalí se jedním pohybem. Dosud se rozbalil pro „Načítání stránek…“ a stránky pak doháněl ve zbylém čase, při pomalejší odpovědi skokem. Strom se začne načítat už při stisku šipky, o okamžik dřív než dosud.
- Šipka prostoru a větve se při najetí zvýrazní malým zaobleným polem uprostřed svého sloupce. Dosud to byl blok přes výšku řádku přilepený k jeho levému okraji, v barvě samotného řádku. Klikací zůstává celý sloupec šipky na výšku řádku a fokus z klávesnice obkresluje totéž pole.
- Tlačítko nabídky „…“ na řádku prostoru odpovídá stejně jako šipka: malým polem uvnitř řádku. Dosud tvořilo blok na konci řádku a na rozbalené kartě mělo barvu řádku, takže na najetí nereagovalo vůbec.
- Zarovnání: první úroveň stromu stojí ve sloupcích řádku prostoru. Šipka je pod šipkou a název začíná tam, kde ikona prostoru; u stromu s ikonami stránek je ikona pod ikonou a název pod názvem prostoru. Zvýraznění řádku při najetí i výběru má na všech úrovních šířku karty; úroveň je odsazení uvnitř řádku a vodicí linka vede od šipky rodiče. Stránku otevře klepnutí kamkoli do jejího řádku, nejen na název.
- Bez migrace databáze a beze změny konfigurace. Editor zůstává na verzi 0.15.0.

### 2.6.3 — 2026-10-12

- Úprava postupu vydání.
- Aplikace je shodná s 2.6.2. Bez migrace databáze a beze změny konfigurace.

### 2.6.4 — 2026-10-12

- Úprava postupu vydání: push do `main` je vydání a tag `v<verze>` označuje vydané verze; větev `release` v postupu není.
- Aplikace je shodná s 2.6.3. Bez migrace databáze a beze změny konfigurace.

## 2.5

*2.5.0 — 2026-10-09*

UI refresh „Ohnisko“: celé rozhraní je uhlazené, zarovnané a sjednocené podle schváleného návrhu. Žádné nastavení neubylo; staré odkazy fungují nebo se přesměrují.

### Rozhraní

- Nový postranní panel: hledání a nová stránka, vstup Asistent (Ctrl J), Knihovna, Nedávné, Oblíbené a Moje koncepty, připnuté prostory s kartou aktuálního prostoru. Hlavička se sbalující drobečkovou navigací.
- Knihovna s kartami prostorů s úvodním obrázkem sladěným do barev prostoru (nebo v původních barvách), přepínač Karty / Seznam. Prostor má novou hlavičku a zarovnanou tabulku stránek.
- Článek ve sloupci 720 px, kód s tlačítkem Kopírovat, boční panel Obsah · Asistent · Historie · Přístup, lišta formátování a tlačítko Hotovo při úpravách, spodní lišta na telefonu.
- Jednotný AI znak se stavy a velikostmi, jeden stavový řádek „něco se děje“ se Zastavit, nový panel asistenta, hledání Ctrl K s čistými výňatky a odpovědí vedle výsledků, lišta Zeptat se · Vysvětlit · Shrnout nad vybraným textem.
- Nastavení v postranním panelu s přepínačem Já · Prostor · Wiki a hledáním v nastavení; zarovnané řádky a sekce, Lidé jako tabulka s náhledem člověka, šablona stránky modulu, AI s přehledem postupu a logy poskytovatelů.
- Vzhled prostoru: 10 rychlých barev, galerie 60 kombinací po rozbalení, běžné ikony a „Vlastní…“, úvodní obrázek karty.
- Já: stránka Účet a vzhled, motivy s náhledem, osm barev zvýraznění (nově indigová, fialová a jantarová), tmavé schéma tónované do barvy zvýraznění, Omezit animace. Výchozí barva nových účtů je tyrkysová.
- Pohyb všude přes tokeny motion systému; omezený pohyb jen krátce prolíná.

### Opraveno

- Název wiki zvolený v průvodci instalací se ukáže hned, bez obnovení stránky.
- Výpadek spojení se ukazuje česky místo hlášky prohlížeče.
- Počty lidí a skupin v nastavení se po pozvání nebo nové skupině přepočítají.

### Data a provoz

- Úvodní obrázek prostoru (PNG, JPEG, WebP do 5 MB, ověřený podle obsahu) s historií změn; migrace 2026100910. ZIP záloha (formát 5) a obnova obrázky obsahují.
- Barvy zvýraznění indigo/violet/amber a předvolba pohybu účtu; migrace 2026100960.

### Závislosti a vydání

- Editor `@atlas/markdown` a serverový `Atlas.Markdown.Sync` přecházejí na **0.15.0**: úchyt bloku a „+“ tvoří jeden sloupec v levém okraji editoru, „+“ pod úchytem, nikdy mimo plochu editoru; nabídka lomítka má nadpis.

### 2.5.1 — 2026-10-09

- Úprava postupu vydání a ověření identity image aplikace i AMD64 databáze.

### 2.5.2 — 2026-10-10

- Označení stránky jako šablony už nevydá cizí nepublikovaný koncept. Správce prostoru, který znal ID dosud nepublikovaného konceptu jiného autora, dostal v odpovědi na toto označení název a obsah konceptu; příznak se navíc uložil a autor tím přišel o možnost koncept upravovat. Server teď rozhoduje o přístupu ke konkrétní stránce dřív, než cokoli změní, a odpoví stejně jako při otevření stránky, kterou účet nesmí číst (404 „Stránka nebyla nalezena.“). Správce prostoru dál označuje stránky, které smí číst; pro správce wiki se nic nemění. Koncept, který se takto šablonou už stal, jí zůstává; označení může zrušit správce wiki.
- Stejný požadavek už neprozradí, zda stránka s daným ID existuje. Přihlášený účet, který prostor nespravuje, dostával pro existující stránku kteréhokoli prostoru jiný text chyby 404 než pro neznámé ID. Obě odpovědi jsou teď shodné.
- Text, jehož značky končí v jiném pořadí, než začaly (například tučný, kurzívní a zvýrazněný text, za kterým pokračuje jen zvýraznění, v Markdownu `==***a***b==`), už neshodí převod stránky na Markdown. Jediná taková stránka v importované wiki způsobila, že náhled celého importu skončil chybou 500. U takové stránky v Atlasu odpovídaly nástroje MCP pro koncepty a `atlas_get_page` chybou `internal_error` a AI psaní nad ní nebo nad takovou šablonou končilo chybou 500. Server teď zapíše stejný Markdown jako editor; uložený obsah stránek se nemění.
- Bez migrace databáze a beze změny konfigurace. Editor zůstává na verzi 0.15.0.

## 2.1

*2.1.0 — 2026-10-08*

### Použitelnost

- Omnisearch zachovává režimy běžného hledání, `?` pro AI a `>` pro příkazy. Výsledky lze rychle procházet šipkami a potvrdit Enterem; průběžné odpovědi AI respektují oprávnění a osobní preference.
- Související stránky zobrazuje kompaktní klikací graf skutečných odkazů. Sjednocené zarovnání článku, souvislostí a ovládacích prvků doplňují srozumitelnější popisky.
- Obsah dokumentu nabízí spolehlivé klikání na celou vlnu, čitelné dlouhé názvy a lepší ovládání dotykem i klávesnicí.

### Opraveno

- Přepnutí zdrojového editoru zpět na dokument obnoví běžné rolování stránky bez zbytkového rámečku a zachová rozepsaný obsah.
- Uložení zdrojového návrhu po souběžných úpravách správně rozlišuje zamčení dokumentu od připravenosti synchronizace. Konflikty zachovávají návrh a umožňují bezpečnou obnovu.
- Hledání obnovuje pozici po návratu z historie; volitelné části editoru se načítají podle potřeby.

### Závislosti a vydání

- Editor `@atlas/markdown` a serverový `Atlas.Markdown.Sync` přecházejí na **0.14.0**. Oprava obsahu dokumentu je součástí balíčku a nahrazuje dočasný patch 0.13.0.
- Větší redesign zůstává odložený. Vydání zachovává stávající rozložení aplikace a oddělení publikace image od nasazení ověřeného digestu.

### 2.1.1 — 2026-10-09

- Panel asistenta má čistší vstup s vlaštovkou, která při odeslání vyletí po oblouku. Čekání signalizuje animovaná ikona AI; klávesové ovládání a omezený pohyb zůstávají dostupné.
- Přehledy kvót zmizely z asistenta, hledání a shrnutí stránky. Konečné limity ukazují nenápadné kroužky u profilu s rozbalitelným detailem; neomezené účty indikátor nemají.
- Odkazy pod článkem tvoří jediný řádek malých tagů bez nadpisů; na úzké obrazovce se zalomí. Mapa souvislostí se rozbalí až na vyžádání a zůstává úzká; prázdná patička se nezobrazuje.
- Podklady odpovědi mají jeden společný vstup. Ctrl/Cmd+klik na zdroj otevírá právě jednu novou kartu.
- Akce Upravit a Nová stránka mají v hlavičce kompaktní textovou podobu s ikonou; zvýrazní se při najetí nebo zaměření klávesnicí.
- Editor zůstává na verzi 0.14.0.

## 2.0

*2.0.0 — 2026-10-07*

### Přidáno

- Atlas 2.0 sjednocuje rozpracované funkce: asistenta, odpovědi nad oprávněným obsahem wiki, sémantické hledání, návrhy při psaní a správu AI profilů, indexu, kvót a dostupnosti. AI se zapíná výslovnou konfigurací správce.
- Osobní API a MCP respektují úroveň účtu, rozsah tokenu a aktuální oprávnění k obsahu; změny přístupu se promítají také do běžících operací.
- Profily lokálních a SMB úložišť pro zálohy a PDF, automatické odložené exporty publikovaných stránek, volitelná historie exportů a export historických revizí.
- Formální HTML/PDF export stránek, rozšířené editorové reference, vizitky kontaktů s uživatelským jménem a společné stavy živé spolupráce.

### Opraveno

- Odhlášení trvale odvolává relaci i pro živou spolupráci a rozběhnuté vydávání API tokenů. Citlivé administrátorské operace vyžadují čerstvé ověření podle nastavené bezpečnostní politiky.
- Ověření indexu respektuje vypnutou AI, zahrnuté prostory a aktuální publikované revize. Změna SMB serveru či účtu vyžaduje nové zadání hesla.
- Vymazání historie asistenta odstraňuje i klientský stav; vyhledávání zachovává běžné pořadí fokusu a lokální akce nad výběrem fungují i bez AI.

### Změněno

- Editor `@atlas/markdown` a serverový `Atlas.Markdown.Sync` jsou připnuté na stabilní obecnou verzi **0.13.0**. Editor neobsahuje aplikačně specifickou příponu verze ani jazyk chráněných bloků.
- Návrhy editoru a referenční prvky se načítají podle potřeby; společné motion tokeny respektují omezený pohyb. Zachovány jsou rychlejší vstup do úprav, klávesová práce s bloky a slash menu bez závislosti na diakritice.

### Provoz a upgrade

- PostgreSQL 18 musí před startem nové aplikace obsahovat **pgvector alespoň 0.8.1**. Použijte ověřený AMD64 databázový image z `deploy/postgres`; výměna databázového image a obnova zálohy jsou součástí release postupu.
- Image aplikace je **Linux AMD64 / linux-x64**. Nativní ARM testy nenahrazují AMD64 acceptance.
- Publikace image a nasazení jsou oddělené. Nasazení používá pouze explicitně potvrzený neměnný digest po acceptance a testu upgradu ze skutečně nasazené verze.

## 1.5

*1.5.0 — 2026-10-02*

### Přidáno

- **Oprávnění přes skupiny JetBrains Hub:** propojení podle stabilních externích ID, ověření úplného transitivního členství při přihlášení a pravidelná synchronizace. Platnost ověření je pět minut; chyba adresáře ji neprodlužuje.
- **Správa integrace a migrace:** režimy Disabled, Preview a Enforced, diagnostika synchronizace, audit změn a průvodce převodem místních skupin s kontrolou aktuálnosti náhledu. Profil zobrazuje původ a platnost přístupů.
- Odebrání či vypršení členství se promítne do hledání, navigace, živé spolupráce a probíhajících stahování. Nezávislá místní oprávnění zůstávají zachovaná.

### Provoz

- Nasazení automaticky nemigruje firemní skupiny ani nezapíná vynucování Hubu. Konfigurace a přechod probíhají ručně v průvodci převodem ve správě wiki.
- Upgrade zachovává existující data a granty; akceptace doložila kompletní testy, živý Hub a 93 kontrol upgradu.

### 1.5.1 — 2026-10-05

#### Opraveno

- **Cizí neuložené živé úpravy se už potichu neztratí.** Uložení bez generace spolupráce (osobní API `PUT /api/v1/pages/{id}`), obnova z historie a přesun do koše zakládaly novou relaci a mazaly neuložené živé změny ostatních autorů, aniž by to volající poznal. Teď odpoví 409 `collaboration_unsaved` bez jména toho, kdo stránku upravuje; přesun do koše kontroluje každou stránku vybraného podstromu. Vlastní neuložené úpravy se nahrazují jako dosud a publikace se nemění. Historie stránky ukáže hlášku serveru místo konfliktu verzí a obnovu lze zopakovat bez načtení historie.
- **Tablety:** do šířky 1024 px je navigace vysouvací panel jako na telefonu, takže na iPadu na výšku nezabírá třetinu šířky (článek se rozšíří z 484 na 740 px). Ukotvený panel od 1025 px na dotykovém zařízení nechá tah přes strom posouvat stránku a zobrazení aktuální stránky ve stromu posouvá jen seznam v panelu, ne celou stránku.

## 1.4

### Změněno

- **Zálohy na jedné stránce:** Nastavení wiki → Obecné → Zálohy spojuje archivy ZIP i zálohu do Gitu. Samostatná položka Záloha do Gitu zmizela.
  - Nahoře jsou oba způsoby vedle sebe se stavem, poslední a další zálohou a tlačítky Zálohovat teď a Synchronizovat teď.
  - Pod nimi jsou záložky Archivy ZIP, Git a Obnovení ze zálohy. Každá ukládá své nastavení zvlášť.
  - Starý odkaz `#/settings/general/git-backup` vede na záložku Git.
- **Připnuté prostory vždy nahoře:** Knihovna → Prostory přesune dlaždici hned po připnutí mezi připnuté a po odepnutí zpět mezi ostatní. Fokus přitom zůstane na tlačítku.
  - Výběr prostoru ve filtrech hledání a v přesunu stránky do jiného prostoru řadí stejně jako postranní panel: nejdřív prostory připnuté správcem, pak vlastní připnuté, pak ostatní.

## 1.3

### Přidáno

- **Čtenost stránky v nabídce „…“:** vysune se zprava jako Historie. Kalendář zobrazení za rok (stejný jako v Profil → Aktivita) a přehled, kdo a kdy stránku četl, za celý rok nebo vybraný den.
  - Zobrazení nebo zkopírování hesla na stránce je v kalendáři i v přehledu zvýrazněné. Vidí ho jen správci daného trezoru.
  - Audit hesel nově zná stránku, ze které se heslo zobrazilo; Atlas ji uloží jen tehdy, když uživatel stránku smí číst a stránka na heslo odkazuje (migrace `2026092910`).
- **Bezpečnostní centrum s přístupy na jednom místě:**
  - Nastavení wiki má skupiny Kdo (Lidé, Skupiny, API tokeny), Co (Prostory, Trezory a druhy z pluginů), Pravidla a Činnost. Přehled práv je nahrazený.
  - Karta osoby ukáže všechno, k čemu člověk má přístup, podle prostorů: řádek pro celý prostor a pod ním jen výjimky (stránky s vlastním přístupem, trezory, hesla, API tokeny, YouTrack). Má záložky Účet, Skupiny, Tokeny a Činnost, akce Přidat přístup a Odebrat vše….
  - „Kdo má přístup“ je jeden panel pro prostor, stránku, trezor i heslo: automatičtí správci, skupiny, lidé, Efektivně / Jen přidělené a u prostoru záložka Výjimky.
  - Profil má novou sekci Moje přístupy.
  - Pluginy a další služby přidávají vlastní druhy přístupů (`docs/access-model.md`).
- **Průběžná záloha do Gitu:** Nastavení wiki → Obecné → Záloha do Gitu. Po každé uložené změně zapíše Atlas commit do vzdáleného repozitáře; autorem commitu je uživatel, který změnu provedl.
  - Každá revize je jeden commit. Commit vzniká i při přejmenování, přesunu, smazání do koše, obnově a publikaci.
  - Stránky se ukládají jako čitelný Markdown strom s obrázky a přílohami, nativní obsah editoru je v `.atlas/`.
  - Přihlášení k repozitáři zajišťuje SSH deploy klíč, který Atlas sám vygeneruje. První synchronizace přehraje celou historii.
  - Obraz kontejneru nově obsahuje `git` a `openssh-client`. Popis je v [docs/git-backup.md](docs/git-backup.md).

### Změněno

- **Knihovna je jediný přehled prostorů:** odkaz Všechny prostory v postranním panelu zmizel, protože duplikoval Knihovna → Prostory. Prostory jsou první záložkou Knihovny a úvodní stránkou. Správci prostoru najdou Připnutí pro všechny… v nabídce „…“ prostoru; přehled prostorů ukazuje, kde je prostor připnutý pro všechny.
- **Jedna stupnice úrovní všude:** Vidí → Čte → Upravuje → Spravuje, každá úroveň má svou barvu. Místo rolí Čtenář / Autor / Správce prostoru i stránky. Změnit a Odebrat se v seznamech přístupů ukáže po najetí na řádek.
- **Nové přihlášení i pro další změny přístupů:** udělení úrovně Spravuje na prostor nebo stránku, zapnutí či vypnutí správce wiki a Odebrat vše teď vyžadují nové přihlášení (platí 10minutové okno jako u ostatních bezpečnostních změn).
- **Jedno tlačítko pro úpravy:** položka „Upravit zdroj stránky“ v nabídce stránky je pryč. Zdroj se otevírá v režimu Upravit přepínačem Dokument | Zdroj nad dokumentem (správci prostoru a wiki jako dosud).
- **Dokumentace API přímo v profilu:** tlačítko „Dokumentace API“ v Osobním API zobrazí popis rozhraní `/api/v1` ve Scalaru na stránce, místo odkazu na samostatnou stránku. Scalar se načte až po otevření; stažení OpenAPI JSON zůstává.

### Opraveno

- Profil se po přihlášení otevře hned prvním kliknutím; dřív se kliknutí během načítání po přihlášení ztratilo.
- Rámečky zaměření a okraje ovládacích prvků se už neořezávají na okrajích rozbalených bloků (nastavení, profil, výběr ikony, postranní panel).
- Jezdec přepínače zase dosedá ke krajům dráhy.
- Správce prostoru už při přidávání přístupu nedostane celý seznam účtů wiki včetně neaktivních. Lidé a skupiny se hledají na serveru (nejvýš 20 výsledků, jen aktivní účty).
- Zdroj stránky už nejde otevřít mimo úpravy. Dřív šlo otevřít zdroj při čtení, začít psát a pak kliknout na Upravit: společné úpravy se spustily nad rozepsaným zdrojem. Ukončení úprav nebo publikování teď zdroj zavře; neuložený zdroj se před tím potvrzuje.
- **Druhý autor se nepřipojil ke společným úpravám (od 1.2.1).** Když novou nebo mimo společné úpravy uloženou stránku začal upravovat druhý autor dřív, než první cokoli napsal, založila se mu nová relace: autoři neviděli změny ani sebe navzájem. Relace, ve které ještě nikdo nic nezměnil, se už znovu nezakládá.
- **Editor 0.7.3:**
  - Kolečko myši nad širokou tabulkou zase posouvá stránkou; tabulka si vodorovně posouvá jen sama sebe.
  - Tlačítko pro vložení sloupce už nepřekrývá smazání sloupce.
  - Příloha se ve Firefoxu stáhne jedním kliknutím a stránka neodskočí na začátek.
  - Server spolehlivěji uvolňuje ukončené pracovní procesy editoru.
- **Soubory trezorů „Na serveru wiki“ v Dockeru mimo trvalý svazek (od 1.1.0).** Image nenastavoval `Vaults__DataPath`, soubory .kdbx proto ležely v `/app/.data/vaults` uvnitř kontejneru a každé nové nasazení je smazalo. Teď leží v `/data/vaults` na svazku `atlas-data`.

### 1.3.1

#### Opraveno

- Kontrola gitu a OpenSSH v publikovaném obrazu už obraz nespouští (linux/amd64 obraz na arm64 stroji), ověří jeho soubory; spuštění pokrývá Podman e2e. Aplikace se oproti 1.3.0 nemění.

## 1.2

### Změněno

- **Sjednocené a přehlednější rozhraní (obsah na prvním místě):**
  - Filtry jsou v jednom řádku (hledání, rozbalovací volby, počet) a nahoře jsou kompaktní hlavičky bez opakovaných nadpisů.
  - Stránky, koncepty, trezory, hesla i aktivita mají stejné řádky seznamu a jeden štítek platnosti.
  - Stavy jsou jednotné (prázdný stav, chyba, načítání) a přechody mají kontextové animace. Při omezeném pohybu zůstává jemné prolnutí.
- **Méně dialogů:**
  - Nový prostor a Kdo má přístup ke stránce jsou celoobrazovkové průvodce s vlastní adresou (`#/new-space`, `#/pages/<id>/access`).
  - Šablony stránek jsou v Nastavení prostoru.
  - Ikona stránky se nastavuje v panelu přímo u stránky.
  - Profil používá stejné komponenty jako nastavení.
- **Vzhled prostoru a ikony stránek:** k výběru je všech 88 ikon s hledáním (česky, bez diakritiky, podle synonym) a 60 barev (12 odstínů × 5 sytostí) s náhledem. Server přijímá stejný katalog; původní barvy zůstávají platné.
- **Historie stránky:** Změny i Celá verze se zobrazují vykreslené, jak se stránka čte.
  - Upravené odstavce mají zvýrazněná přidaná a smazaná slova a nezměněné části jsou sbalené.
  - Přepínač Náhled | Markdown ukáže přesné porovnání Markdownu; stejně funguje i schvalování konceptů.
- **Knihovna místo Domů:** stránka Domů je zrušená, protože opakovala Knihovnu. `#/` otevře Knihovnu. Knihovna má novou záložku Prostory: dlaždice s hledáním, filtrem připnutých, počtem stránek a poslední změnou.
- **Připnuté prostory:** připnuté prostory jsou trvale v postranním panelu, každý se svým stromem stránek. Panel si pamatuje, které jsou rozbalené.
  - Každý si prostor připne nebo odepne sám.
  - Správce prostoru může v Nastavení prostoru → Obecné určit, že je prostor připnutý všem, nebo povinně připnutý.
  - Přepínač jednoho prostoru v sidebaru je pryč.
- **Knihovna** ukazuje u stránek cestu „prostor › nadřazená stránka“.
- **Nové přihlášení platí 10 minut:** po jednom ověření další bezpečnostní změny ve stejné relaci znovu nevyžadují heslo.
  - Vydání hesla v režimech Mimo důvěryhodné sítě a Vždy dál vyžaduje ověření pokaždé.
  - Do limitu přihlášení se počítají jen neúspěšné pokusy.
  - Odmítnutí nového přihlášení vrací vždy 403 s kódem `reauth_required` nebo `fresh_auth_unsupported`.
- **Pozvánka** (kód pro nastavení hesla nového účtu) platí 7 dní; kód pro obnovu hesla platí dál 30 minut.
- Editor `@atlas/markdown` a `Atlas.Markdown.Sync` jsou ve verzi 0.7.2:
  - nadpis H4, callouty GitHubu včetně nového „Pozor“ (`> [!CAUTION]`), `==zvýraznění==`, Mermaid a vzorce i v zobrazení Zdroj;
  - viditelné Požádat o přístup u hesla;
  - ve čtení mají callouty popisek a diagramy přirozenou velikost;
  - plynulejší neviditelný inkoust.
- **Revize schématu dokumentů 3:** stávající dokumenty přejdou na novou generaci a otevřená záložka starší verze se místo úprav vyzve k obnovení.
- **Import wiki** zapisuje stejný Markdown jako editor (callouty, zvýraznění, H4, diagramy, vzorce) a caution/danger převádí na „Pozor“.

### Opraveno

- Blok s heslem ukazuje uživateli s úrovní Vidí tlačítko Požádat o přístup. Stav čekající nebo zamítnuté žádosti přetrvá i po obnovení stránky, protože API reference vrací `requestPending` a `requestRefused`.
- Přístup s už uplynulým koncem platnosti nejde uložit; formulář to ukáže rovnou u data.
- U sdíleného odkazu je limit 30 dní vidět ve formuláři.
- Příliš velká stránka hlásí, který limit překročila a o kolik.
- Nedostupná stránka ukáže vysvětlení s cestou zpět místo prázdné plochy.
- Otevřená záložka ze starší verze po nasazení nabídne Obnovit stránku, místo aby žádala správce o sjednocení verzí.
- Úvodní krok modulů má stejnou hlavičku a stavy jako ostatní nastavení.

### 1.2.1

#### Opraveno

- **Blok API mizel po přechodu na 1.2.0.** Pokud stránku upravoval editor předchozí verze (0.6.0) a ve 1.2.0 ji pak otevřel jiný editor, blok OpenAPI se v úpravách nezobrazil a další uložení ho odstranilo.
  - Když stránka nemá neuložené změny, relace úprav se teď vždy založí z uložené verze. Týká se to stavu ze starší verze editoru i stavu, který se od uložené verze liší.
  - Neuložené změny ve společné relaci zůstávají.
  - Ztracený blok vrátí obnovení předchozí verze v Historii stránky.

### 1.2.2

#### Opraveno

- **Uložení ikony a barvy stránky:** databázová kontrola nyní přijímá všech 88 ikon a 60 barev nabízených v aplikaci. Původní hodnoty i historie obsahu zůstávají zachované.

## 1.1

První stabilní vydání. Produktové číslo 1.1 zahrnuje dokončenou wiki 1.0 i etapu 1.1 (práva, trezory a Security centrum); proti provozní 0.1.1 jde o kompatibilní rozšíření, osobní API `/api/v1` zůstává. Upgrade z 0.1.1 je ověřen testem migrace (`tests/upgrade/upgrade-podman.ps1`); migrace jsou pouze přidávající.

### Přidáno

- **Přístupy ke stránkám (WP3):** vlastní přístup stránky (zděděný / jen vybraní), přidělení uživatelům a skupinám s platností, vysvětlení „proč má přístup“ včetně toho, kdo a kdy naposledy zobrazil a upravil; odebrání přístupu se okamžitě projeví i v živé spolupráci.
- **Trezory KeePass (WP4):** soubory KDBX 4 na lokální nebo SMB složce, automatické odemknutí po restartu (hlavní heslo šifrované přes Data Protection), průběžná synchronizace záznamů se stálými ID, založení nové databáze s jednorázově zobrazeným heslem.
- **Úrovně přístupu k heslům Vidí → Čte → Spravuje:** správci prostoru a správci wiki mají automaticky Spravuje, ostatní přes přidělení na trezor nebo heslo; žádost o přístup a schválení s platností (výchozí 30 dní), živé změny přes SignalR.
- **Ochrana hesel:** režimy nevyžadovat / mimo důvěryhodné sítě / vždy, dědění instance → trezor → heslo, výchozí „vždy“. Nové přihlášení lokálním heslem nebo přes Hub (ověřeno `auth_time`), jednorázový důkaz vázaný na heslo a operaci; tlačítko „Otestovat nové přihlášení“.
- **Důvěryhodné sítě a proxy:** pojmenované IPv4/IPv6 rozsahy (LAN/VPN), důvěryhodné proxy a počet hopů, diagnostika „Vaše připojení“, tester adres; existující `Http:TrustedProxies` se jednorázově převezme.
- **Vydání hesla s auditem:** zobrazení a kopírování jen s úrovní Čte, audit před vydáním hodnoty (kdo, kdy, co, režim, síť, výsledek; nikdy hodnota), limit počtu vydání, okamžité skrytí po odebrání přístupu.
- **Hesla v editoru:** reference na heslo (blok i v textu) přes `/`, výběr jen z hesel, která autor smí vidět; server v dokumentu, Yjs, historii, exportu, vyhledávání i veřejném sdílení nikdy nedrží hodnotu ani nepovolené reference.
- **Security centrum a nové nastavení:** dvě úrovně (nastavení wiki a nastavení prostoru) se stejným vzhledem, průvodci po krocích, klikací volby místo výběrů, přehledy „kdo co kdy“, profil ve stejném shellu, galerie komponent.
- Zdrojový editor stránky („Zdroj“) pro správce prostoru, import Markdownu z cizích wiki, šablony stránek, OpenAPI s detailem, vkládání bloků, stav offline.

### Změněno

- Editor `@atlas/markdown` a `Atlas.Markdown.Sync` 0.6.0 (reference na hesla v Markdownu jako `::secret{…}`, obnovení práv bez znovunačtení, důvod žádosti).
- Webové API stránky (`/api/pages/{id}`) nevrací Markdown; osobní API `/api/v1` beze změny.
- Rychlé načítání: těžké knihovny (Monaco, Scalar, Mermaid) jen na vyžádání, hlídaný rozpočet hlavního balíku.
- Testy: plná serverová sada za ~40 s (šablonová databáze, paralelismus podle CPU), smoke sada `tests/smoke.ps1`.

### Provoz

- Adresář `/data/keys` (Data Protection) musí přežít výměnu kontejneru; bez něj se trezory po startu zeptají na hlavní heslo znovu.
- Pro trezory na SMB musí mít server přístup ke sdílené složce; umístění se nastavuje v Nastavení wiki → Umístění souborů.
- Po nasazení ověřit nové přihlášení přes Hub tlačítkem „Otestovat nové přihlášení“ v přehledu nastavení wiki.

### Dřívější položky vývoje

### Přidáno

- Základ trezorů KeePass (WP4 V1, zatím bez UI a bez zapnutí v provozu): vlastní čtečka KDBX 4.0/4.1 s limity `Vaults:*` a zápis nové prázdné databáze (závislost BouncyCastle.Cryptography, MIT, viz [NOTICES.md](NOTICES.md)); úložiště záloh přesunuto do sdíleného `Platform/Storage` s ohraničeným čtením, metadaty souboru a čtením, které neblokuje uložení v KeePassu.

### Změněno

- Editor `@atlas/markdown` a `Atlas.Markdown.Sync` 0.6.0: odkazy na hesla mají v Markdownu tvar `::secret{…}`/`:secret{…}`, takže je Zdroj stránky zachová. Server je při každém uložení ověřuje jako rich JSON (bez hodnot, jen odkazy, které JSON drží); API, historie a export je odstraní čtenářům, kteří heslo nesmějí vidět, vyhledávání je nikdy neindexuje a veřejné sdílení Markdown nemá. Odkazy se po 403, vyřízené žádosti nebo odebrání přístupu znovu vyhodnotí na místě; žádost o přístup posílá důvod. Kolaborační strážce kontroluje u přepsání staré i nové id.
- Nastavení: zálohy se nedotazují každou minutu (obnova při návratu do okna a po vlastní akci); konflikt 409 na stránkách s konceptem koncept ponechá a nabídne „Načíst znovu“; platnost přidělení má jednu komponentu (nepovinné Od a Do, 7 / 30 / 90 dní, bez omezení); ikony nastavení jsou z registru editoru.

- Sjednocena vývojová verze serveru a webu na `1.0.0-dev`.
- Zavedena pravidla verzování, changelog a postup vydání.
- Aktuální vývoj patří do 1.0; roadmapa nově řadí práva, trezory, vlastní 2FA a externí sdílení do 1.1 před RAG/chat 2.0 a obsahový model/tabule 3.0. Jde o dokumentaci budoucího rozsahu, nikoli již implementované funkce.

### 1.1.1

#### Opraveno

- Kontejner v nasazení přes Compose při nedostupné databázi během startu skončí s chybou, aby jej restart policy spustila znovu s uloženými poskytovateli OIDC. Nemůže tak po souběžném restartu PostgreSQL zůstat běžet bez firemního přihlášení. Zapíná to `Authentication__OpenIdConnect__RequireDatabaseAtStartup=true` v konfiguraci nasazení s předem nastavenou databází; samostatná instalace a její průvodce zachovávají dosavadní chování.
- Docker Compose čeká na připravený PostgreSQL před startem aplikace. Schéma databáze se nemění; výpadek přihlášení po upgradu na 1.1.0 způsobovalo pořadí startu, nikoli smazání konfigurace migrací.
