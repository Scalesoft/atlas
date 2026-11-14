# Skutečná browserová přejímka

Testy používají publikovaný Atlas, skutečný editor, SignalR a PostgreSQL. Nikdy je nespouštějte proti uživatelské instalaci. Potřebují vlastní databáze a adresáře klíčů/příloh/importů; test první instalace existující wiki odmítne a žádná data neresetuje.

## Příprava

Použijte .NET SDK podle `global.json` a checkout Atlasu; editor je jeho součástí (`src/Atlas.Markdown`, `pnpm run build:editor` sestaví `dist`, který frontend používá). Sestavte aplikaci přes `dotnet publish src/Atlas.Server/Atlas.Server.csproj --configuration Release --output <nový adresář>` (totéž dělá `e2e-podman.ps1`). Do běžícího výstupu další publish nesměrujte.

```powershell
npm --prefix tests/browser ci
npx --prefix tests/browser playwright install chromium
```

Host spusťte s novou prázdnou testovací PostgreSQL databází v `ConnectionStrings__atlas-db` a samostatnými hodnotami `Security__DataPath`, `DataProtection__KeysPath`, `Authentication__OpenIdConnect__CertificateDirectory`, `Attachments__DataPath`, `ContentImport__DataPath`, `ContentExport__DataPath` a `GitBackup__DataPath`. Connection pooling ponechte zapnutý: vypíná jej pouze helper dočasných schémat pro backendové testy, nikoli tento běžný webový host.

Hodnoty `Setup__BootstrapToken` a `ATLAS_E2E_PASSWORD` vygenerujte jako jednorázová tajemství v prostředí. `ATLAS_E2E_BOOTSTRAP_TOKEN` musí odpovídat bootstrap tokenu serveru. Test vytvoří pouze syntetický účet `browser-admin`. Tajemství neukládejte do verzovaných souborů ani nevypisujte do reportu.

`ATLAS_URL` nastavte na URL tohoto hostu, například `https://localhost:5226`. Použijte důvěryhodný HTTPS certifikát; testy mají `ignoreHTTPSErrors: false`. S Node.js 24 na Windows lze systémové kořeny zpřístupnit pomocí `NODE_USE_SYSTEM_CA=1` nebo `NODE_OPTIONS=--use-system-ca`.

## Pořadí průchodů

První instalaci spusťte samostatně. Abecední spuštění všech souborů nad prázdnou databází nemá správné pořadí.

```powershell
$env:ATLAS_E2E_ALLOW_SETUP = '1'
npm --prefix tests/browser test -- first-run.spec.ts
```

Po úspěšné instalaci pokračujte nad stejným hostem a databází:

```powershell
Remove-Item Env:ATLAS_E2E_ALLOW_SETUP
$env:ATLAS_E2E_ALLOW_COLLAB = '1'
$env:ATLAS_E2E_ALLOW_UPLOADS = '1'
$env:ATLAS_E2E_ALLOW_IMPORT = '1'
npm --prefix tests/browser test -- block-moves.spec.ts collaboration-access.spec.ts collaboration.spec.ts cross-space-move.spec.ts editor-integrations.spec.ts library-export.spec.ts live-readers.spec.ts product-finishing.spec.ts
# Před další dávkou ponechte ochrannému oknu přihlášení minutu na obnovení.
Start-Sleep -Seconds 60
npm --prefix tests/browser test -- search-filters.spec.ts space-creation.spec.ts space-image.spec.ts tree-navigation.spec.ts usability.spec.ts wiki-import.spec.ts workspace-consistency.spec.ts
```

Rychlá kontrola kritických cest: tři scénáře mají tag `@smoke` (hydratace spolupráce u stránky končící tabulkou, omnisearch s filtry a historií, záložka Přístup pro čtenáře). Po `first-run.spec.ts` je spusťte nad stejným hostem příkazem `npm --prefix tests/browser test -- --grep @smoke` s `ATLAS_E2E_ALLOW_COLLAB=1`. Úplnou přejímku nenahrazují; ta zůstává `e2e-podman.ps1` před vydáním do main. Playwright běží s jedním workerem, protože scénáře sdílejí jeden host, jeden syntetický účet a limit přihlášení.

Tyto průchody vytvářejí testovací obsah, účty, skupiny a přílohy. Každý používá vlastní browserovou relaci a společný syntetický účet; builtin ochrana dovoluje deset pokusů za minutu na účet. Proto jsou průchody rozdělené do dvou dávek. Velký počet rychlých opakování může správně narazit na ochranu proti zneužití; produkční limity kvůli testům nevypínejte.

`block-moves.spec.ts` je přejímka přesunů bloků za běhu sdílené relace: dvě okna nad stejnou stránkou, přesun prostředního bloku klávesnicí i tažením úchytu, shodné pořadí v obou oknech, automatické uložení a ověření pořadí v `contentJson` i po znovunačtení. Ověřuje také, že úchyt v přístupném názvu slibuje přesun a nehlásí dočasnou nedostupnost. Stejně jako `collaboration.spec.ts` běží jen s `ATLAS_E2E_ALLOW_COLLAB=1`.

## Přepínání dokumentu a zdroje nad importem

`page-source-switching.spec.ts` kontroluje opakované přepínání myší a šipkami, scroll dlouhého článku, zachování editoru a obsahu, zavření přes tlačítko/Esc a ukončení editace. `page-source-sync.spec.ts` ověřuje dokumentové i zdrojové úpravy, ukládání a konflikt s druhým oknem. Používají skutečný editor, Monaco a backend. Každý scénář vytvoří vlastní kopii importované stránky a po testu ji přesune do koše; originál nemění.

Nad izolovanou testovací wiki nastavte `ATLAS_URL`, `ATLAS_E2E_USER`, `ATLAS_E2E_PASSWORD`, `ATLAS_E2E_ALLOW_COLLAB=1` a `ATLAS_E2E_SOURCE_PAGE_ID` na ID dlouhého importovaného článku. Spuštění: `npm --prefix tests/browser test -- page-source-switching.spec.ts page-source-sync.spec.ts`. Bez těchto parametrů se scénáře přeskočí; nejsou náhradou obecné přejímky před vydáním.

## AI — dávka `ai`

`ai-search.spec.ts` ověřuje AI funkce 2.0 proti publikovanému Atlasu a **falešnému OpenAI-kompatibilnímu serveru** `fake-ai/server.mjs` (Node, bez závislostí; žádný skutečný model se nevolá). Dávka běží ve vlastním podu, protože si AI sama nastaví a nesmí ovlivnit ostatní dávky. `e2e-podman.ps1` do podu přidá kontejner `fake-ai` (Node z obrazu Playwright, adresář `fake-ai` připojený jen pro čtení); Atlas ho zná jako `http://fake-ai:1234/v1`, scénáře ho řídí na `http://localhost:1234`.

```powershell
# jen tato dávka (první instalace + ai-search.spec.ts), nebo v rámci celé přejímky (pwsh tests/full.ps1, pwsh tests/browser/e2e-podman.ps1)
pwsh tests/browser/e2e-podman.ps1 -Batch ai
# bez Podmanu: vlastní falešný server a hostitel podle sekce Příprava; první instalace musí proběhnout dřív
node tests/browser/fake-ai/server.mjs   # PORT=1234
$env:ATLAS_E2E_FAKE_AI_URL = 'http://localhost:1234/v1'; $env:ATLAS_E2E_FAKE_AI_CONTROL = 'http://localhost:1234'
$env:ATLAS_E2E_ALLOW_COLLAB = '1'
npm --prefix tests/browser test -- ai-search.spec.ts
```

Scénáře (v pořadí běhu; sdílejí jeden wiki a jednu databázi): 1 bez nastavení AI (v DOM žádné `data-ai-*`, žádné volání `/api/assist` ani `/api/admin/ai`, capabilities samé `false`), 2 nastavení Embeddingů, vypínače a indexu přes Administraci → AI včetně StepFlow Indexu, 3 významová shoda se štítkem a pasáží, 4 Související v patičce stránky, 5 výpadek (`timeout`): funkce zmizí bez obnovení stránky, hledání zůstane lexikální, publikace nezpomalena, 7 přehled ukazuje Nedostupné s časem a kódem, běžný uživatel nic, 6 obnova do 60 s a fronta indexace na 0. Scénáře 1, 3 a 4 běží i s `reducedMotion: 'reduce'`.

Řízení fake serveru (stejný port): `POST /__mode { "mode": "healthy|slow|timeout|error|invalid-json", "delayMs": 2000 }` (nebo po rozsazích `{ "embeddings": "timeout", "chat": "healthy" }`), `POST /__meaning { "rules": [{ "contains": "…", "as": "…", "near": false }] }` (parafráze a pasáž sdílejí vektor bez společného slova), `POST /__reset`, `GET /__requests` (jen počty). Vektory jsou stejné trigramy jako `FakeAi` v C#; shodu obou stran hlídá `tests/fixtures/fake-ai-vectors.json` (`FakeAiVectorParityTests`, `npm --prefix tests/browser run test:fake-ai`).

## Restart a obnova potvrzené práce

`host-restart.spec.ts` potřebuje druhou novou prázdnou databázi a druhou sadu soukromých datových adresářů. Do prostředí testu předejte jejich běžnou serverovou konfiguraci, stejné jednorázové bootstrap/testovací tajemství a:

```powershell
$env:ATLAS_URL = 'https://localhost:5224'
$env:ATLAS_E2E_ALLOW_HOST_RESTART = '1'
$env:ATLAS_E2E_HOST_DIRECTORY = (Resolve-Path 'artifacts/publish').Path
npm --prefix tests/browser test -- host-restart.spec.ts
```

Na portu 5224 nespouštějte host ručně: test sám spustí, násilně ukončí a znovu spustí pouze svůj dětský proces. Ověří přihlášení po restartu, obnovení potvrzených dosud nepublikovaných společných změn a jejich následnou publikaci. Existující instalaci odmítne.

## Důkazy a výkon

Výstupy jsou v ignorovaném `artifacts/browser-tests` a záměrné screenshoty v `artifacts`. Nastavením `PLAYWRIGHT_JSON_OUTPUT_NAME` a přidáním `--reporter=list,json` lze uchovat strukturovaný report včetně příloh s časováním. Testy obsahující přihlášení mají vypnuté automatické screenshoty/trace; konfigurace navíc vypíná automatickou kopii DOM, která by mohla zachytit heslo či jednorázový token.

Měření odezvy spouštějte samostatně, bez souběžných instrumentovaných integračních testů nebo benchmarku. Browserový p95 zahrnuje vstup, debounce, HTTP a použitelné výsledky; backendový benchmark s 10 000 stránkami je oddělené měření.

## Sjednocení UI — přejímka

Dva soubory ověřují sjednocení UI proti spuštěné instanci wiki.

### `ui-unification.spec.ts` — chování

Šest scénářů z Task 4.2: zavírání překryvů (menu „…“ i dialog Historie) při navigaci ve stromu, mobilní dokument se širokou (7sloupcovou) tabulkou bez vodorovného přetečení stránky a bez viditelné osnovy, právě jedno hlášení o spojení a zakázané Uložit/Publikovat při `context.setOffline(true)`, 40s nečinnosti v editaci bez chyby „Server timeout“ v konzoli, žádný ze zakázaných termínů (`Reader`, `Writer`, `Admin`, `WikiAdmin`, `Fullscreen`, `FILE`, `Choose File` — jako celé slovo) v Knihovně, na dokumentu a ve Správě wiki a potvrzovací dialog „Zahodit“/„Zůstat“ při odchodu z rozepsané sekce profilu.

Test se přeskočí (`test.skip`), pokud nejsou nastavené `ATLAS_E2E_USER` a `ATLAS_E2E_PASSWORD` — na rozdíl od ostatních souborů v tomto adresáři nevyžaduje syntetický účet `browser-admin`, funguje i s vývojovým účtem `test`/`test`. Celý soubor se přihlásí jen jednou (`test.beforeAll` uloží `storageState` do `artifacts/ui-audit-2026-09-07/ui-unification-state.json`, který sdílí každý test), aby nenarazil na limit deseti přihlášení za minutu. Heslo se nikam nevypisuje.

```powershell
$env:ATLAS_URL = 'https://localhost:7101'
$env:ATLAS_E2E_USER = 'test'
$env:ATLAS_E2E_PASSWORD = 'test'
npm --prefix tests/browser test -- ui-unification.spec.ts
```

Scénáře mohou na rozpracované instanci selhávat na částech, které ještě nejsou hotové (jiná dávka plánu) — to je očekávané; test nikdy neupravuje `src/Atlas.Web/src`.

### `census.spec.ts` — census s prahy

Spustí `artifacts/ui-audit-2026-09-07/scripts/census-assert.cjs` (samostatný Node/Playwright skript, ne fixture `page` tohoto testu) proti `ATLAS_URL` a přiloží jeho výstup k výsledku testu. Skript znovu projde stejných 30 stavů jako původní census (`census.md` §6) a ověří prahy z Task 4.1 / spec §6:

- ≤ 1 `pathsHash` na ikonový pojem, `stroke-width` pouze `1.75`, `viewBox` pouze `0 0 24 24`, rendrované velikosti jen `{14,16,18,22,26}`;
- písmo ovládacích prvků jen `DM Sans`; `button text` ≤ 4 tuple, `button icon-only` ≤ 2, `menu item` přesně 1, `input` ≤ 2, `dialog surface` přesně 1, `popover surface` ≤ 2;
- přesně 1 definice focus prstence a 1 definice opacity pro zakázaný stav;
- žádný `<span>` s textem jako jediným obsahem `.md-plugin-action-icon`;
- žádný ze zakázaných termínů v `innerText` auditovaných stavů.

Výsledky se zapíšou vedle původního `census.json`/`census.md` jako `census-after.json`/`census-after.md`, se stavy, které se nepodařilo otevřít (UI se mezitím změnilo). Test se přeskočí, pokud není `ATLAS_E2E_ALLOW_CENSUS=1` — běh trvá řádově minuty a otevírá vlastní headless prohlížeč nezávislý na Playwright fixture.

```powershell
$env:ATLAS_URL = 'https://localhost:7101'
$env:ATLAS_E2E_ALLOW_CENSUS = '1'
npm --prefix tests/browser test -- census.spec.ts
```

Skript samotný lze spustit i přímo mimo Playwright (rychlejší iterace, čitelnější výstup v terminálu):

```powershell
$env:NODE_PATH = (Resolve-Path 'tests/browser/node_modules').Path
node artifacts/ui-audit-2026-09-07/scripts/census-assert.cjs --url https://localhost:7101
```

Nenulový návratový kód vypíše každé porušení prahu i každý nedosažitelný stav; skript nikdy neupravuje `src/Atlas.Web/src` — pouze měří a hlásí.

## Osnova a prostor pro obsah — `outline-and-reading-space.spec.ts`

Dvě věci nad jedním dokumentem: osnovu nadpisů „Na této stránce“ (`src/Atlas.Web/src/components/PageOutline.vue`: řádek nad článkem, nebo seznam v bočním panelu stránky) a to, kolik z obrazovky skutečně nese text článku. `ui-unification.spec.ts` k tomu kontroluje, že na telefonu zůstává osnova sbalená v řádku.

Test si zakládá vlastní publikovanou stránku s dvanácti nadpisy `##` a třemi odstavci v každé sekci (přes API, ne přes editor) a v `afterAll` ji přesouvá do koše. Nezávisí tedy na obsahu instance; potřebuje pouze alespoň jeden prostor, kam ji smí založit.

Soubor má dvě části:

- **Osnova „Na této stránce“** (`PageOutline`, od 2.5 místo plovoucí osnovy editoru). Řádek nad článkem se rozbalí se všemi nadpisy, kliknutí na položku pod okrajem obrazovky odroluje na daný nadpis, `aria-current` v bočním panelu sleduje pozici čtení (očekávaná položka se dopočítává ze živého DOM podle stejného pravidla jako `activeHeadingIndex()`) a otevřený boční panel osnovu převezme, zatímco řádek nad článkem ustoupí.
- **Prostor pro obsah.** Relativní invarianty bez „magických“ pixelů: „Široký dokument“ využije širší obrazovku, boční panel vedle článku ubírá nejdřív volný okraj a teprve pak šířku textu, otevřený panel vydrží otevření další stránky, panel vedle článku neleží na textu (překryv pod 600 px článku je záměrný) a na telefonu je nad článkem nejvýš třetina výšky. Hodnoty naměřené 2026-09-09 se starou osnovou jsou v komentářích.

Stejně jako `ui-unification.spec.ts` se přeskočí bez `ATLAS_E2E_USER`/`ATLAS_E2E_PASSWORD`, přihlásí se jednou a session sdílí přes `storageState`; funguje s vývojovým účtem `test`/`test` a nepotřebuje syntetický `browser-admin`.

```powershell
$env:ATLAS_URL = 'http://127.0.0.1:5101'   # nebo https://localhost:7101
$env:ATLAS_E2E_USER = 'test'
$env:ATLAS_E2E_PASSWORD = 'test'
npm --prefix tests/browser test -- outline-and-reading-space.spec.ts
```

## Čtenářský audit — `wiki-reading-audit.spec.ts`

Třiadvacet regresních scénářů pro přímé odkazy, historii prohlížeče, osnovu klávesnicí,
hledání a command paletu, mobilní menu včetně změny velikosti na tablet, kopírování
bez schránky, roztažení navigace s uložením šířky, Knihovnu s nedávno otevřenými stránkami a prostory,
široký režim, osnovu „Na této stránce“ v bočním panelu stránky i v řádku nad textem a geometrii
při 390/1024/1280/1440/1920 px. Dvě další
kritéria `@proposal` měří přijaté rozpočty prostoru; jsou přeskočená, pokud není
`ATLAS_E2E_READING_PROPOSALS=1`. Samotný tag nezajišťuje jejich vynechání.

Tento soubor neprovádí zápisy do obsahu. Jeden scénář otevře editor a zkontroluje
tlačítka bez změny textu, uložení nebo publikování. Použijte testovací účet s právem
upravit fixture stránku a existující
článek s krátkým názvem, více nadpisy a cílovou sekcí pod prvním viewportem.
Stačí publikovaný článek z nadpisů a odstavců; nadpisy musí být aspoň tři, jinak se
řádek „Na této stránce“ nad textem nezobrazí.
Přihlašování a běžné zaznamenání návštěvy stránky stále zapisuje aplikace.
Článek musí být dohledatelný v uvedeném rozsahu; pro koncept potřebuje účet
právo zápisu. Bez všech povinných proměnných se testy explicitně přeskočí.

```powershell
# ATLAS_E2E_USER a ATLAS_E2E_PASSWORD dodat prostředím.
$env:ATLAS_URL = 'https://localhost:7101'
$env:NODE_USE_SYSTEM_CA = '1'
$env:ATLAS_E2E_READING_PAGE_ID = '<UUID testovací stránky>'
$env:ATLAS_E2E_READING_TITLE = '<přesný název stránky>'
$env:ATLAS_E2E_READING_SECTION = '<jedinečný hlubší nadpis>'
$env:ATLAS_E2E_READING_SCOPE = 'published' # nebo drafts
npm --prefix tests/browser test -- wiki-reading-audit.spec.ts --grep-invert @proposal

# Návrhové rozpočty se zapínají výslovně:
$env:ATLAS_E2E_READING_PROPOSALS = '1'
npm --prefix tests/browser test -- wiki-reading-audit.spec.ts --grep @proposal
```

Celý audit včetně návrhových kritérií spustíte s poslední proměnnou a bez
`--grep`. Pro JSON report nastavte `PLAYWRIGHT_JSON_OUTPUT_NAME` na cestu mimo
výstupní adresář jednotlivých testů a přidejte `--reporter=list,json`.
Použijte např. `--output=../../artifacts/wiki-reading-audit-results`,
aby se nesmazaly důkazy z jiných sad.

Session cookies jsou jen v paměti; TLS ověřování se nevypíná. Pokud sandboxový
prohlížeč nevidí důvěryhodný vývojový certifikát, spusťte testy v běžném
uživatelském terminálu s příslušným trust store. Nejde o důvod měnit
`ignoreHTTPSErrors`.




## Tagování a široká osnova

`page-tags.spec.ts` ověřuje přidání více tagů, uložení bez nové revize článku,
vyhledání tagu, URL filtru po reloadu, odebrání, mobilní rozložení a konflikt
dvou úprav. Vyžaduje `ATLAS_E2E_USER`, `ATLAS_E2E_PASSWORD` a výslovné
`ATLAS_E2E_ALLOW_TAGS=1`. Účet musí mít alespoň jeden zapisovatelný prostor.
Test zakládá syntetické stránky a v `afterEach` je přesouvá do koše.

`wiki-wide-outline.spec.ts` používá stejné proměnné fixture jako čtenářský audit
a ověřuje 1920/2560 px s navigací širokou 272/480 px: zachování šířky článku
s otevřeným bočním panelem stránky, zarovnání názvu, panel vedle článku a jeho vzhled.
Obsah stránky nemění. Další dva scénáře po odscrollování kontrolují, že osnova
v bočním panelu zůstane pod horní lištou a její položky jsou kliknutelné při 1024/1920 px.

```powershell
# Přihlašovací údaje a případnou reading fixture dodat prostředím.
$env:ATLAS_URL = 'https://localhost:7101'
$env:NODE_USE_SYSTEM_CA = '1'
$env:ATLAS_E2E_ALLOW_TAGS = '1'
npm --prefix tests/browser test -- page-tags.spec.ts wiki-wide-outline.spec.ts --output=../../artifacts/page-tags-results
```


## Volitelné ikony stránek

`page-icons.spec.ts` potřebuje `ATLAS_E2E_USER`, `ATLAS_E2E_PASSWORD`,
`ATLAS_E2E_ALLOW_ICONS=1` a alespoň jeden zapisovatelný prostor. Zakládá vlastní
rodičovskou a podřízenou stránku a po testu je přesune do koše. Ověřuje výběr
ikony/barvy, zobrazení v nadpisu a stromu, rozbalování rodiče, zachování po reloadu,
změnu barvy a odebrání na mobilu i konflikt souběžných změn.

```powershell
$env:ATLAS_E2E_ALLOW_ICONS = '1'
npm --prefix tests/browser test -- page-icons.spec.ts --output=../../artifacts/page-icons-results
```


## Projector — dávka `projector`

Sedm příběhů požadavků ve stránkách (Projector v1), jeden soubor na příběh, společné pomocné funkce v `projector-helpers.ts`. Každý příběh si zakládá vlastní prostor, takže katalog a přehledy jednoho nevidí stránky druhého. Tvrdí současně, co uživatel vidí, i uložený stav na serveru (`GET /api/projector/pages/{id}/requirements`, `contentJson`). Potřebují modul v hostiteli (`Modules__Projector__Enabled=true`; `e2e-podman.ps1` ho nastavuje), `ATLAS_E2E_ALLOW_COLLAB=1` a oba účty z `first-run.spec.ts` (`browser-admin`, `browser-reader`).

```powershell
pwsh tests/browser/e2e-podman.ps1 -Batch projector
# nad hotovým hostem po first-run.spec.ts:
npm --prefix tests/browser test -- projector-kbn-from-scratch.spec.ts projector-copy-deployment.spec.ts projector-waiting.spec.ts projector-collaboration.spec.ts projector-rights.spec.ts projector-odd-cases.spec.ts projector-keyboard.spec.ts
```

| Soubor | Příběh |
|---|---|
| `projector-kbn-from-scratch` | katalog napsaný v editoru (Portál s předpoklady NIS a Notifikace, vstupy SSL a SMS), označení jako katalog, publikace; nasazení „KBN — Production“ z katalogu včetně „Přidá se také“, poznámky pod bloky, splnění jeden po druhém do 100 % |
| `projector-copy-deployment` | kopie KBN → Kostelec z menu souhrnu: průběh se vynuluje, klíče, závislosti a poznámky zůstanou, původní stránka se nezmění |
| `projector-waiting` | PX2 čeká na SMS bránu od „IT PX2“: stránka, přehled „Co nám nebylo dodáno“ a „Na co čekáme“ podle strany (obojí jen přes API, web pro ně zatím nemá obrazovku) |
| `projector-collaboration` | dvě okna: jedno splní blok, druhé jiný zanoří (Tab); shoda souhrnu, nic neztraceno po uložení a znovuotevření |
| `projector-rights` | čtenář prostoru (publikovaný stav, 403 při zápisu) a host s právem na jednu stránku (žádný katalog ani jiná nasazení) |
| `projector-odd-cases` | cyklus v katalogu, předpoklad „netýká se“, duplikát, smazání bloku s závislými a Zpět, omezený pohyb (`test.fixme`: oznámení „netýká se“, viz komentář v souboru) |
| `projector-keyboard` | vložení z katalogu a splnění všech bloků jen klávesnicí |

Čtení stránky (bez úprav) vykresluje bloky jako statický text („☑ hotovo 10. 9. 2026 · vstup · od: … · klíč: …“, souhrn jako řádek „Stav nasazení: …“); živé bloky a souhrn s ovládáním jsou jen v editaci.

## Podman: jednorázová instance jedním příkazem

`e2e-podman.ps1` spustí celou přejímku v jednom Podman podu a po skončení vše odstraní:
produkční image databáze z `deploy/postgres/Dockerfile` (PostgreSQL 18.6 Alpine s pgvectorem, sestavený pro `linux/amd64`; data v tmpfs), publikovaný server v obrazu nad stupněm `runtime` z Dockerfile (`aspnet:11.0-resolute`
s `git` a `openssh-client`; publish ve vrstvě obrazu, adresáře dat v tmpfs), jednorázový SSH git server (`tests/browser/git-server`)
a testy v oficiálním `mcr.microsoft.com/playwright:v<verze>-noble`, jehož tag
odpovídá nainstalovanému `@playwright/test` z `package-lock.json`. Browser, fonty a OS jsou tedy
nezávislé na hostu.

```powershell
npm --prefix tests/browser ci                            # jednou; balíčky jsou čistý JavaScript a použijí se i v kontejneru
pwsh tests/browser/e2e-podman.ps1                        # publish + první instalace + dávky z „Pořadí průchodů“ a záloha do Gitu
pwsh tests/browser/e2e-podman.ps1 -EditorMode source     # frontend s editorem ze zdrojů (src/Atlas.Markdown)
pwsh tests/browser/e2e-podman.ps1 -PublishDir artifacts/e2e-podman/publish-package -Spec shell-surfaces.spec.ts,usability.spec.ts
pwsh tests/browser/e2e-podman.ps1 -InstallOnly -Keep     # jen první instalace; pod zůstane běžet na vypsané adrese
```

Průběh:

1. Bez `-PublishDir`/`-AppImage` spustí na hostu `dotnet publish` (včetně `pnpm install` a buildu frontendu) do
   `artifacts/e2e-podman/publish-<package|source>`. `-EditorMode source` nastaví `ATLAS_EDITOR_MODE=source`
   (cesta k editoru: `-EditorSource`, výchozí `src/Atlas.Markdown`) a předá `-p:AllowEditorSourcePublish=true`,
   protože běžný publish zdrojový editor odmítne (`Directory.Build.targets`); takový výstup nikdy nenasazujte. Editor je součástí
   repozitáře, takže žádné další zdroje ani přístupy nejsou potřeba.
2. Sestaví stupeň `runtime` z Dockerfile (`podman build --target runtime` s prázdným kontextem, bez zdrojů) jako `localhost/atlas-e2e-runtime` a obraz git serveru `localhost/atlas-e2e-git-server`.
   Publish pak zkopíruje do vlastního obrazu `localhost/atlas-e2e-app:<hash>` nad připnutým `runtime` (`WORKDIR /app`,
   `ENTRYPOINT ["dotnet", "Atlas.Server.dll"]` jako produkční obraz), takže server nečte sestavení přes bind mount:
   Windows adresář je ve WSL stroji připojený přes 9p a při pěti souběžných podech tam čtení referenčních sestavení
   pro runtime kompilaci handlerů Wolverine selhalo s `Cannot allocate memory` (`JasperFx.dll`, `NHibernate.dll` …)
   a `Compilation failures!`. Tag je hash obsahu publish a obrazu `runtime`: souběžné dávky i další běhy se stejným
   publish sdílejí jeden build a skript smaže obrazy dřívějších publish téhož adresáře (kromě obrazu, který ještě
   používá kontejner). Z cache vrstev to trvá sekundy a obrazy po běhu zůstávají. `-AspNetImage` a `-GitServerImage`
   místo toho použijí hotové obrazy (publish se kopíruje nad `-AspNetImage`); `-AppImage` spustí hotový obraz serveru
   beze změny. Obraz serveru (i z `-AppImage`) musí spustit `git --version`, `ssh -V` a `ssh-keygen`,
   jinak běh skončí dřív, než vznikne pod.
3. Vygeneruje bootstrap token, heslo `browser-admin` a heslo databáze jako Podman secrets
   (do souborů ani výpisů se nezapisují) a počká na `/api/setup/status` s prázdnou databází.
4. Spustí `first-run.spec.ts` s `ATLAS_E2E_ALLOW_SETUP=1`. Pokud je wiki nainstalovaná, pokračuje i při
   pozdějším selhání tohoto testu. Pak bez pauzy (ochrana přihlášení počítá jen neúspěšné pokusy; `-ThrottlePauseSeconds`)
   běží dávky s `ATLAS_E2E_ALLOW_COLLAB/UPLOADS/IMPORT=1` (druhá dávka navíc s `csp.spec.ts`, třetí je `git-backup.spec.ts`, čtvrtá `ai-search.spec.ts`, pátá `storage-export.spec.ts`, šestá `projector-*.spec.ts`), nebo místo nich specifikace z `-Spec`.
   Výchozí dávky běží souběžně, každá ve vlastním podu s vlastní databází a první instalací (`-Sequential` je spustí za sebou v jednom podu).
5. Uloží výsledky a odstraní pod, kontejnery, svazek s klíči a secrets (kromě `-Keep`).

Server běží s `ASPNETCORE_ENVIRONMENT=Development` (`-AspNetEnvironment`): mimo Development je auth
cookie `Secure` a zapnuté HTTPS přesměrování, takže přihlášení přes `http://localhost:8080` uvnitř podu
neprojde. Prostředí se týká jen cookie/HSTS; frontend i databáze jsou produkční build.
Předpřipravený obraz serveru lze použít přes `-AppImage`, např. build z Dockerfile (celý běh s ním neověřený; git a ssh skript ověří):
`podman build -t localhost/atlas:e2e .`

Nespouští se `host-restart.spec.ts` (spouští vlastní proces hostu), `deployment-setup.spec.ts`
(Production za HTTPS proxy), `*-live`/`census` a specifikace s fixture proměnnými čtenářského auditu;
ty zůstávají pro běh na hostu popsaný výše. Specifikace s `ATLAS_E2E_USER`/`ATLAS_E2E_PASSWORD`
(`ui-unification`, `shell-surfaces` ...) lze přidat přes `-Spec`; skript předává účet `browser-admin`.

### Výstupy

`artifacts/e2e-podman/<run>/` (ignorováno gitem):

- `<fáze>/report/index.html` – HTML report, `<fáze>/results.json` – JSON report,
- `<fáze>/test-results/` – trace a screenshoty selhání (testy s přihlášením je mají vypnuté),
- `app.log`, `db.log`, `git.log` – logy serveru, PostgreSQL a sshd git serveru; jednorázové tokeny a hesla jsou nahrazené `***`,
- `<fáze>/git-backup.json` – důkaz z `git-backup.spec.ts` pro kontrolu repozitáře.

Fáze jsou `install`, `batch-1` až `batch-5` (souběžné dávky mají každá vlastní `<run>` s fází `batch-1`). Záměrné screenshoty specifikací jdou jako dosud do `artifacts/`.

### Konfigurace

`playwright.config.ts` bere `ATLAS_URL`; `PLAYWRIGHT_BASE_URL` nebo `BASE_URL` ho doplní, pokud chybí.
`PLAYWRIGHT_REDUCED_MOTION` (`reduce`/`no-preference`) nastaví `contextOptions.reducedMotion` kontextům fixture `page`/`context` (top-level `reducedMotion` Playwright nezná; `test.use({ contextOptions })` ve specifikaci celý objekt nahradí, `browser.newContext()` ho nedědí);
Podman běh ho explicitně nastavuje na `no-preference` (`-ReducedMotion`). Windows hostu hlásí `reduce`,
specifikace, které pohyb testují, si hodnotu emulují samy (`motion.spec.ts`, `wiki-motion.spec.ts` ...).
Vizuální reference (`toHaveScreenshot`) sada nemá; kdyby vznikly, generujte je jen v kontejneru.

### Řešení potíží

- **Paměť stroje.** U WSL poskytovatele `podman machine set --memory/--cpus` neplatí; paměť a CPU určuje
  `%UserProfile%\.wslconfig` (`[wsl2] memory=8GB`, `processors=6`) a `wsl --shutdown`. Změřeno 2026-09-26 (`podman stats`,
  stroj 6 GB / 4 CPU): PostgreSQL ~150 MB, server 2,7–3 GB (server GC, tmpfs data), Playwright s Chromium
  ~0,65 GB; obsazeno ve stroji až ~4 GB. 2 GB nestačí, 6 GB je minimum, doporučeno 8 GB. Výchozí souběžné dávky
  spouštějí pět podů; na menším stroji použijte `-Sequential`.
- **Port 5101/7101** vývojového Aspire se nepoužívá; pod vystavuje server jen na volném portu `127.0.0.1`.
- **Zbylý pod po přerušení** – `podman pod ls`, `podman pod rm -f atlas-e2e-<run>`, `podman volume rm atlas-e2e-<run>-git-keys`,
  `podman secret ls` a `podman secret rm atlas-e2e-<run>-...`.
- **Obrazy serveru** – skript maže jen starší obrazy stejného adresáře publish; poslední obraz smazaného worktree
  zůstane (`podman images localhost/atlas-e2e-app`, `podman rmi <id>`).
- **`Installed @playwright/test ... differs`** – `npm --prefix tests/browser ci`.

## Automatické PDF do SMB — `storage-export.spec.ts`

Samostatná pátá dávka používá skutečný Samba server (`docker.io/dperson/samba:latest`, Linux AMD64).
Skript ho spustí pouze pro tuto dávku. Účet služby předá přes Podman secrets; server Atlas dostane SMB adresu,
nikoli připojený svazek. Tato dávka zachová `/data` v jednorázovém svazku přes restart aplikace,
včetně klíčů DataProtection nutných pro dešifrování hesla SMB; svazek se na konci odstraní.
Playwright čte svazek sdílení jen pro čtení a ověřuje skutečné PDF bajty:

- první export po 30sekundovém zpoždění po uložení nastavení,
- sloučení dvou uložení a přepsání jediného aktuálního souboru při vypnuté historii,
- volitelné historické soubory se shodnými bajty a zachováním starších kopií,
- restart aplikace během čekání na export: příprava uloží revizi, skript restartuje server před uplynutím 30 sekund
  a `storage-export-restart.spec.ts` ověří dokončení stejné revize na SMB.

```powershell
pwsh tests/browser/e2e-podman.ps1 -Spec storage-export.spec.ts
```

`-SmbImage` dovoluje použít předem připravený obraz. `smb.log` a `storage-restart.json` jsou součástí reportu;
`storage-restart-prepare` a `storage-restart-verify` obsahují výsledky před restartem a po něm.
Výchozí 60sekundový limit jednotlivého browserového testu se nemění.

Svazek sdílení používá `nocopy` a před prvním browserovým připojením obsahuje kořenový marker `.atlas-test-share`.
Podman totiž při připojení prázdného pojmenovaného svazku do dalšího kontejneru mění jeho vlastníka i při `ro,nocopy`;
přejímka tento problém reprodukovala změnou `smbuser:smb` na `root:root` a odmítnutým síťovým zápisem.
Marker pouze dokončí inicializaci jednorázového testovacího svazku. Složky jednotlivých stránek začínají neexistující
a vlastní oprávnění Samby se nemění.

Na Windows s checkoutem pod junction použijte `-BrowserStageDir` s fyzickým adresářem (například pod `$env:TEMP`).
Musí už obsahovat `tests/browser/node_modules` instalované podle `package-lock.json`; skript do něj kopíruje
aktuální browserové zdroje a uloží tam reporty. `-PublishDir` se nepřipojuje; skript ho kopíruje do obrazu serveru.

## Záloha do Gitu — `git-backup.spec.ts`

Přejímka [průběžné zálohy do Gitu](../../docs/git-backup.md) proti skutečnému SSH serveru. Běží v `e2e-podman.ps1`
jako třetí dávka ve vlastním podu, v repozitáři jsou pak jen její stránky a stránky první instalace. Bez
`ATLAS_E2E_GIT_REMOTE`, `ATLAS_E2E_GIT_KEYS` a `ATLAS_E2E_ALLOW_UPLOADS=1` se přeskočí.

- **Git server** (`tests/browser/git-server/Containerfile`): Alpine s OpenSSH a gitem, v podu na `localhost:2222`.
  Uživatel `git` má `git-shell`, repozitář `/srv/git/atlas.git` je prázdný bare s výchozí větví `main`. Klíč serveru
  vzniká při každém startu. Povolené klíče čte sshd z `/git-keys/authorized_keys` ve svazku `atlas-e2e-<run>-git-keys`,
  který sdílí s Playwright kontejnerem.
- **Proměnné**: `ATLAS_E2E_GIT_REMOTE=ssh://git@localhost:2222/srv/git/atlas.git`,
  `ATLAS_E2E_GIT_KEYS=/git-keys/authorized_keys`, `ATLAS_E2E_GIT_EVIDENCE=<fáze>/git-backup.json`.
  Server dostane `GitBackup__DataPath=/data/git-backup`.
- **Scénář**: jako `browser-admin` otevře Správa wiki → Provoz → Zálohy → Git a vygeneruje deploy klíč. Jeho veřejnou část
  zapíše do `authorized_keys`, vyplní adresu a větev `main` a otestuje přístup (uloží se otisk klíče serveru). Pak zálohu
  zapne a uloží. Založí stránku se značkou `git-backup-e2e-marker`, v editoru do ní nahraje jedinečný obrázek PNG,
  uloží koncept a spustí „Synchronizovat teď“. Čeká stav `ok` a commit z odpovědi synchronizace. Stránku nemaže,
  protože přesun do koše by její Markdown z repozitáře odstranil.
- **Kontrola repozitáře**: po dávce se skript podívá přímo do bare repozitáře
  (`podman exec --user git <run>-git git -C /srv/git/atlas.git …`). Ověří větev `main` s aspoň dvěma commity,
  `.atlas/repository.json` a právě jeden `*.md` se značkou, který začíná hlavičkou `---` s `title:`. Dál commit
  `docs: create|update <cesta>` od `Test správce <browser-admin@atlas.invalid>` (účet bez e-mailu; kontaktní e-mail, který mu nastavila
  dřívější dávka, spec zapíše do `git-backup.json` jako `authorEmail`) a obrázek
  v `<stránka>.assets/` se stejným git blobem a velikostí, na který Markdown odkazuje relativní cestou. Nakonec, že commit
  hlášený v UI je na `main`. Na zálohu, která ještě běží na pozadí, čeká až 90 s. `git log --stat -5` vypíše do logu dávky.

```powershell
pwsh tests/browser/e2e-podman.ps1 -Spec git-backup.spec.ts
```

## Content-Security-Policy — `csp.spec.ts`

Nad publikovaným serverem sbírá v každém rámci události `securitypolicyviolation` a zprávy konzole
o Content-Security-Policy a selže při jakémkoli porušení, ať server posílá politiku vynucenou
(výchozí), nebo jako Report-Only (`Http__ContentSecurityPolicy__Enforce=false`). Projde shell aplikace
s přihlášením, stránku s blokem OpenAPI (záložka API se Scalarem v `srcdoc` iframu, YAML v Monacu
s workery a vyhledávacím panelem, který načte font ikon) a diagramem Mermaid a veřejnou stránku
`/s/*` v anonymním kontextu. Ověřuje také, že dokument hlavičku politiky skutečně nese.

Zakládá a publikuje jednu stránku, případně zapne externí sdílení prostoru a vydá odkaz; po testu
odkaz zruší, sdílení vrátí do původního stavu a stránku přesune do koše. Běží se stejnými
proměnnými jako první dávka (`ATLAS_E2E_PASSWORD`, `ATLAS_E2E_USER`, výchozí `browser-admin`,
a `ATLAS_E2E_ALLOW_COLLAB=1`), v Podmanu tedy přes `-Spec csp.spec.ts`.

```powershell
npx --prefix tests/browser playwright install chromium firefox webkit
$env:ATLAS_E2E_ALLOW_COLLAB = '1'
foreach ($browser in 'chromium', 'firefox', 'webkit') {
  npm --prefix tests/browser test -- csp.spec.ts --browser=$browser --repeat-each=5
}
```

Politika je ve výchozím stavu vynucená ([README](../../README.md) u Content-Security-Policy); spec musí být zelený
na každém z těchto prohlížečů ve výchozím stavu i s `Enforce=false`.

## Souběžná spolupráce — `collaboration-board-live.spec.ts`, `collaboration-convergence.spec.ts`

Revizní cyklus 2 (COL2). Běží proti lokální instanci ve `Development` s vývojářským přihlášením a modulem Boards (každý kontext se přihlašuje sám), obsah zakládají v prostoru „COL2 spolupráce“:

- `COL2-B1` dvě okna na jedné tabuli: souběžné přesuny různých karet i téže karty a přesun udělaný offline skončí na obou obrazovkách i na serveru (`GET /api/boards/{id}`) stejně;
- `COL2-B2` cizí přesun karty na mé obrazovce klouže na místo (několik mezipoloh během `--atlas-motion-base`), nedrží a neskočí;
- `COL2-B3` kolega táhne kartu na tabuli s 300 položkami: moje plátno nemá dlouhé snímky a jeho upuštění vidím do 1,5 s;
- `COL2-D1` tři autoři na jedné stránce: stejný odstavec, jiný odstavec a buňka tabulky, vlastní Zpět, jeden offline a zpět; všechny editory skončí se stejným dokumentem, nic potvrzeného se neztratí ani nezdvojí a uložený koncept je ten dokument.

`collaboration-board-document.spec.ts` (COL2-9, přejímka 3.0-D „jeden ve wiki a druhý v tabuli“) za stejných podmínek:

- stránka upravovaná ve wiki a zároveň z karty na tabuli (zobrazení Karta: dvojklik otevře editor stránky v panelu přes tabuli; zobrazení Náhled: druhý klik do textu vybrané karty ho upravuje na místě, bez tlačítka Upravit): oba píší současně do svých odstavců, oba editory skončí se stejným dokumentem, každé slovo jednou, změna fokusu v panelu nezaloží novou relaci a koncept uložený z tabule obsahuje i slova z wiki;
- poznámka, kterou dva upravují zároveň: kdo ukládá druhý, dostane obě verze textu a upozornění, na serveru i na obou obrazovkách zůstanou obě.

`COL2-B2` a `COL2-B3` popisují otevřená zjištění COL2-2 a COL2-1 a jsou označené `test.fail(…)`: dokud chyba trvá, hlásí se jako očekávané selhání; po opravě začnou „neočekávaně procházet“ a řádek s `test.fail` se smaže.

```powershell
$env:ATLAS_URL = 'http://127.0.0.1:5141'
npm --prefix tests/browser test -- collaboration-board-live.spec.ts collaboration-convergence.spec.ts collaboration-board-document.spec.ts --repeat-each 5
```

## Zátěž více uživatelů (ruční, mimo CI) — `load/`

Ruční zátěžový test spolupráce: několik skutečných prohlížečů (Playwright) a desítky protokolových botů, kteří mluví přesně tím, čím web: dávkové API tabule (`POST /api/boards/{id}/operations`) a pohledová relace `board:{id}` na `/hubs/live` (`JoinView`, `SetViewport`, `SendObjectPresence`), u dokumentu V1 relace editoru na `/hubs/markdown` (`JoinDocumentWithSchema`, `SendUpdate`, `ReceiveUpdate`) se skutečným Yjs. Nikdy jej nespouštějte proti skutečné wiki: zakládá účty, tabule, stránky a kontakty a mění oprávnění. Patří na kopii databáze (např. obnovený dump) a na lokální produkční build (`dotnet publish`, SPA ze `wwwroot`, `ASPNETCORE_ENVIRONMENT=Development` kvůli vývojářskému přihlášení správce).

```powershell
npm --prefix tests/browser/load ci
$env:ATLAS_URL = 'http://127.0.0.1:5141'
$env:ATLAS_LOAD_PASSWORD = '<jednorázové heslo, 12+ znaků>'   # neukládat, nevypisovat
$env:ATLAS_LOAD_PREFIX = 'c2-load-'                             # jiný prefix, pokud účty s tímto prefixem už mají jiné heslo
$env:ATLAS_LOAD_SPACES = '<id prostoru>'; $env:ATLAS_LOAD_SPACE = $env:ATLAS_LOAD_SPACES
$env:ATLAS_SERVER_PID = '<pid procesu serveru>'                 # CPU a paměť serveru; bez něj se neměří
$env:PGPORT = '5432'; $env:PGDATABASE = '<databáze kopie>'      # psql: pg_stat_activity, outbox, BoardOperation, log aktualizací
node tests/browser/load/setup-users.mjs                         # 34 účtů s rolí Writer v prostoru
node tests/browser/load/board-load.mjs                          # 30 botů + 4 prohlížeče, 5 minut na jedné nové tabuli
$env:ATLAS_LOAD_PAGE = '<id stránky>'; node tests/browser/load/document-load.mjs   # 26 botů + 4 editory, 5 minut na jedné stránce
node tests/browser/load/sources-load.mjs                        # „Zdroje pod tabulí“: 30 botů, změny kontaktů a stránek, odebrání práv
$env:ATLAS_LOAD_BOARD = '<id tabule>'; node tests/browser/load/motion-probe.mjs    # plynulost: snímky, cizí přesun, posun a zoom
```

Parametry: `BOTS`, `BROWSERS`, `SECONDS`; u dokumentu `OFFLINE` (počet botů, kteří vypadnou), `OFFLINE_SECONDS`, `UNDO_BOTS` a `RESTART_AT` s `RESTART_CMD` (restart serveru uprostřed běhu); u tabule `PRESENCE=0` vypne náhledy tažení (k oddělení jejich ceny od dávek); `OUT` uloží výsledek jako JSON. Každý skript na konci porovná všechny klienty se serverem (tabule s `GET /api/boards/{id}`, dokument s novým připojením k relaci) a spočte ztracené a zdvojené unikátní tokeny. Bot nerenderuje projekci: když jej server zvolí snapshotterem, odpojí se a znovu připojí, aby roli převzal editor v prohlížeči, proto se prohlížeče připojují první.
