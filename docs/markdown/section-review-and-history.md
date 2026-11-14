# Sekční schvalování a historie dokumentu

Galerie `?story=proposals` používá `MarkdownSectionReview` z veřejného balíčku
`@atlas/markdown/vue`. Původní celé návrhy zůstávají dostupné s `&legacy`;
jejich existující data se nemigrují automaticky ani nezahazují.

## Chování

- Sekce je explicitní celek dokumentu se stabilním ID. Může obsahovat více bloků,
  nadpisy, tabulky, vodorovné čáry i pluginový obsah.
- Psaní se agreguje do checkpointu po 600 ms klidu. Opuštění sekce nebo přechod
  do další vyvolá okamžité uložení se stavem `pending`. Pouhé otevření a opuštění
  nezměněné schválené sekce nevytváří návrh ani událost.
- Schválená podoba zůstává publikovaná během úprav. Stav `draft` je rozepsaný;
  `pending` lze schválit. Další psaní vrací sekci do `draft` do jejího opuštění.
- Schválit lze výhradně revizi zobrazenou při kliknutí. Každá úprava, přijetí,
  odmítnutí či stažení revizi mění. Zastaralá akce vrací HTTP 409.
- Odmítnutí/stažení obnoví poslední schválenou podobu této sekce. U dosud
  nepublikované nové sekce ji odstraní z aktuálního dokumentu. Historie zůstává.
- Smazání je návrh smazání: publikovaná sekce zmizí až po přijetí. Její text
  se neposílá jako prázdný dokument a neztrácí se před rozhodnutím.
- Schvalování má ovládání vlevo. Historie je pouze jedna pro celý dokument,
  přístupná horní pilulkou. Přepínač neschválených změn v historii ukazuje stav
  **v tehdejším okamžiku**, nikoliv aktuální publikační stav.

## Oprávnění

Host implementuje `ISectionReviewPolicy`. `CanRead` je nutné pro všechny endpointy.
`CanContribute` dovolí změnit publikovanou sekci a pokračovat ve vlastním návrhu.
`CanEdit` dovolí upravit také cizí neschválenou sekci. `CanReview` je samostatné
oprávnění pro schválení/odmítnutí. Autor s platným příspěvkovým právem nebo editor
může změny stáhnout. Samotné čtecí právo dostane jen publikovaný obsah i v historii.

Referenční policy mapuje stávající `DocAccess.Write` na obě editační práva;
produkční host si může role rozdělit. Galerie používá dosavadní vývojovou identitu
z query parametru. Nejde o produkční autentizaci.

## Úložiště a API

Implementujte `ISectionDocumentStore` v
`src/Atlas.Markdown.Server/Sections/SectionModels.cs` a registrujte jej před
`AddSectionReview()`. Součástí kontraktu je konzistentní načtení, atomický CAS commit,
stránkovaná metadata historie a snímek podle ID. Stav a událost historie musí být
durabilní v **jedné transakci**. Git provider je referenční implementace; SQL a
souborové providery nepotřebují Git ani Gitové identifikátory.

```csharp
builder.Services.AddSingleton<ISectionDocumentStore, MySectionStore>();
builder.Services.AddSingleton<ISectionReviewPolicy, MySectionPolicy>();
builder.Services.AddSectionReview();
```

`/api/section-documents/{documentId}` nabízí `GET`, inicializační `POST`,
`GET /permissions`, `POST /sections`, `PUT /sections/{id}` a
`POST /sections/{id}/{approve|reject|withdraw|delete}`. Zápis a rozhodnutí obsahují
`expectedRevision`; server nepřijímá klientem zadaného autora ani oprávnění.
`GET /history?limit=100&cursor=...` vrací metadata; `GET /history/{id}` teprve načítá
celý dokument. Limit stránky je 1–200. Cursor je stabilní při nových zápisech.

Vue komponentě se dodá `MarkdownSectionReviewSource`: `read`, `save`, `add`,
`decide`, `history`. Součástí kontraktu je `watch(onChange, signal)`: host po každém
commitu zavolá `onChange(revision)` a po každém úspěšném přihlášení ke skupině,
včetně prvního připojení i každého reconnectu, `onChange(null)`; komponenta
sama nikdy nepolluje. Transport není součástí editorového jádra. Při změně dokumentu
nebo přihlášeného uživatele komponentu znovu vytvořte pomocí `:key`; cache se nesmí
sdílet mezi autentizačními kontexty. Pluginy předané komponentě se použijí i pro
historický renderer.

Samostatně lze použít `MarkdownDocumentHistory` s `MarkdownHistorySource`.
Má jednu osu, logaritmický zoom, seskupení značek podle pixelů, časový výběr
binárním vyhledáváním, 90ms debounce síťového čtení a LRU cache 12 snímků.
Metadata se načítají po stránkách na vyžádání; velikost počtu SVG značek je omezená
i pro 100 000 událostí. Starší období se zatím načítá tlačítkem, nikoliv pomocí
serverové agregace miliard událostí. Probíhající žádosti se ruší při opuštění historie.

## Přesná hranice současné spolupráce

Tato sekční implementace **není Yjs realtime režim**. Změny ostatních klientů
přicházejí okamžitě přes SignalR hub `/hubs/sections`: klient volá
`Subscribe(documentId)` a dostává `SectionDocumentChanged(documentId, revision)`.
Zpráva nese jen identitu a revizi; obsah se vždy čte autorizovaným REST voláním,
takže čtenář nezíská nic navíc. Host dodává kanál přes
`MarkdownSectionReviewSource.watch`; komponenta nikdy nepolluje. Různé sekce se
ukládají nezávisle a atomický retry
zachová všechny změny. Při souběžném psaní do téže sekce vyhraje jeden zápis a
druhý dostane konflikt. Jeho místní obsah zůstává viditelný a dostupný ke
zkopírování, včetně případu, kdy druhý klient sekci mezitím odstraní. Reload se
neuloženými změnami vyvolá browserové potvrzení; trvalý offline outbox není hotový.

Toto omezení je záměrně vidět i pod demem. Stávající .NET Yjs relay obsah pouze
přenáší a neumí vynucovat sekční oprávnění. Připojit tento editor na celý sdílený
Yjs dokument a schovávat akce jen v UI by bylo chybné. Pro bezkonfliktní psaní do
téže sekce je stále potřeba navázat obsah každé stabilní sekce na vlastní
autorizovaný CRDT kanál a atomicky spojit revizi posuzovaného snímku s jeho sekvencí.
Přesuny uvnitř sekce jsou součást jejího obsahu; přesun celé sekce mezi ostatními
zatím nové API nenabízí.

## Rider a demo přes Aspire

V `Atlas.Markdown.slnx` je nyní frontendový projekt
`web/playground/Atlas.Markdown.Gallery.esproj`. Používá Microsoft JavaScript SDK;
instalace balíčků zůstává řízena kořenovým pnpm lockfilem. Projekt nespouští npm install
a běžný MSBuild nespouští nekonečný vývojový server.

Sdílená konfigurace **Markdown Demo · Aspire** spouští AppHost s HTTP profilem.
Aspire spustí .NET server, počká na jeho health endpoint a spustí Vite galerii;
`ATLAS_MARKDOWN_OPEN_GALLERY=true` otevře prohlížeč, až bude Vite připravené.
Konfigurace **Editor Gallery** spustí samotné Vite pro již běžící server.
Samotný `dotnet build` sestavuje aplikaci; procesy a prohlížeč startují až při Run.

Konfigurace prostředí Rideru nebyla nastavována přes MCP: v této relaci nástroje
Rider MCP nejsou dostupné. Sdílené soubory jsou připravené v `.run/`.

## Ověření

`SectionReviewTests` spouští shodné kontraktní scénáře nad Git i paměťovým providerem:
souběžné zápisy/rozhodnutí, zastaralé revize, autor/editor/reviewer, smazání,
odmítnutí, vkládání, pořadí publikovaných sekcí, pluginový JSON a historické snímky.
`SectionEndpointTests` ověřuje HTTP autorizaci a nezpřístupnění pracovního obsahu čtenáři.
Vitest ověřuje cache, zrušení žádostí, pozdní odpovědi, debounce, recovery a práva.
Playwright `section-review.spec.ts` ověřuje skutečný server v Chromiu i WebKitu,
klávesnici, dotyk, kompaktní timeline a přesnost přijaté revize.

## Přesouvání a řazení tabulek

Řádky lze přesouvat levým úchytem nebo Shift + ↑ / ↓; sloupce horním úchytem nebo Shift + ← / →. Záhlaví řádků zůstává nahoře. Výběr jednoho celého sloupce zpřístupní v nabídce sloupce vzestupné a sestupné řazení; veřejný příkaz je `{ type: 'sort-table', direction: 'ascending' | 'descending' }`. Řadí se celé řádky včetně formátování. Prázdné hodnoty zůstávají na konci v obou směrech, stejné hodnoty zachovávají původní pořadí. Čísla podporují desetinnou čárku i tečku; text používá české přirozené řazení. Každý přesun nebo řazení je jeden krok Undo.

Svisle sloučené buňky nepovolují samostatné přesuny řádků ani řazení. Ve sdíleném Yjs režimu jsou strukturální přesuny a řazení dostupné stejně jako v lokálním editoru; jde o běžnou editaci typu smazat a vložit, jejíž přijaté hranice popisuje [Přesuny bloků ve spolupráci](collaborative-block-moves-and-review.md).
