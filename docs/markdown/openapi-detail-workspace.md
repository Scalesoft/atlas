# Detail komponenty na místě dokumentu — prototyp galerie

> **Atlas 3.6 (2026-11-05):** Atlas už OpenAPI detail na místě dokumentu neotevírá. V úzkém sloupci článku Scalar sbalil svůj
> panel a vedle posuvníku stránky měl vlastní (dva posuvníky vedle sebe). `openDetail` v Atlasu teď otevírá
> `OpenApiDetailPanels.vue`: panel přes celou výšku okna (`AppDialog size="side"`, na telefonu celá obrazovka) se záložkami
> Dokumentace (Scalar) a YAML (Monaco, jen pro autora), v němž se posouvá jen obsah; stránka za ním stojí. Stav návrhu, Použít
> a ochrany zůstávají v `openApiDetail.ts`.
>
> **Atlas 4.1:** ani Zdroj stránky už `MarkdownDetailWorkspace` nepoužívá. Hlavička pracovní plochy nahrazovala přepínač stránky
> (poskočení o pár pixelů, třetí stav „otevřeno se záložkou Dokument“, zavírání na dva kroky); Atlas má jeden přepínač
> Dokument | Zdroj nad dvěma pohledy ([Přepínač v Atlasu](page-source-editor.md#přepínač-v-atlasu-41)). Obal zůstává v balíčku
> editoru beze změny a používá ho jen jeho galerie; text níže popisuje jeho kontrakt, ne chování Atlasu.

Stav: schválený směr z konverzace 2026-09-25, implementovaný prototyp OpenAPI. Nejde ještě o integraci do Atlasu.

## Pravidla

- Dokument má nejvýše jeden aktivní detail komponenty. Žádné souběžné editory ani panely vedle sebe.
- V běžném dokumentu nejsou pracovní záložky vidět.
- Otevření Scalaru nahradí obsah dokumentu detailem ve stejné šířce a místě a přidá nahoře Dokument + API. Přímá úprava zdroje přidá Dokument + YAML. Nevzniká dialog, backdrop ani přesun do body; okolní stránka zůstává dostupná.
- Druhý pohled stejného API se přidá až na vyžádání. Pořadí je Dokument, API, YAML; přechody přejíždějí do strany.
- Dokument zůstává stejnou instancí editoru ve stejném DOM rodiči. Přepínání nezapisuje obsah, zachovává lokální návrh a nemění undo.
- Návrat na záložku Dokument ponechá detail otevřený. Zavřít detail / Escape vrátí Dokument a zasune pracovní záložky. Potom zůstane pouze záložka Dokument, která se krátce zvýrazní, smrští a zmizí. Obsah dokumentu se nezeslabuje ani neskrývá. Celá sekvence trvá přibližně 940 ms.
- Neuložené změny při zavírání nebo otevření jiného API vyžadují Použít, Zahodit nebo Pokračovat v úpravách. Při současné změně zdroje na stránce se starý návrh nesmí automaticky zapsat přes nový.
- Scalar drží svůj stav mezi přepnutími; při změně uložené definice se obnoví. V této iteraci zobrazuje uložený zdroj, ne neplatný či nepotvrzený návrh.
- Neaktivní panely jsou inertní, klávesnice záložek podporuje šipky/Home/End; focus není uzamčen a Escape působí jen uvnitř pracovní plochy. Reduced motion vypíná přechody.
- Režim pouze stažení neumožňuje otevřít Scalar. Readonly neotevírá editaci YAML; odebrání oprávnění ukončí rozpracovaný YAML (návrh se zahodí, záložka YAML zmizí), čtenářský pohled API zůstává otevřený. Zmizí-li samotný blok, zavře se celý detail.
- Zdroj stránky je stejný druh detailu (viz níže): na stránce je vždy nejvýše jeden detail, buď komponenty (Dokument/API/YAML), nebo zdroje stránky (Dokument/Zdroj), nikdy oba současně.

## Součásti

`MarkdownDetailWorkspace` z `@atlas/markdown/vue` poskytuje obecný obal: `open`, `active`, `tabs`, `title`, volitelný `label` (přístupný název oblasti, výchozí „Detail komponenty“, pro zdroj stránky „Zdroj stránky“), pojmenované sloty podle ID pohledů a události `update:active`, `request-close`, `closed` a `tab-intent` (najetí ukazatelem nebo fokus na záložku ještě před aktivací; host jím může předem načíst líný pohled, např. `prefetchOpenApiSourceEditor()` pro YAML nebo `prefetchSourceEditor()` z `@atlas/markdown/source` pro zdroj stránky). Host vlastní životnost návrhu a nastaví `open=false` až po jeho vyřešení. První pohled musí mít ID `document` a zůstat přítomen i během zavírání. Slot `confirmation` dovoluje hostiteli nabídnout vyřešení návrhu; `MarkdownDraftConfirmation` je hotová modální volba Pokračovat v úpravách / Zahodit / Použít (bez `applyLabel` jen Pokračovat / Zahodit), kterou sdílí YAML i zdroj stránky.

Téma: obal leží mimo `.md-editor`, kde motivy definují tokeny (`.md-editor.md-theme-midnight`). Při otevření proto převezme tokeny editoru ze slotu `document` (stejně jako portály přes `copyMarkdownOverlayTheme`) a sleduje změnu jeho třídy. Záložky, plocha stránek i hostitelské pohledy tak používají jen tokeny (`--bg-2`, `--accent-soft`, `--green`, `--amber`, `--paper`, `--text-dim`, `--line`, `--shadow-1`, `--focus-ring`); pevný světlý přechod je pryč. Monaco volí světlý/tmavý základ při připojení podle `color-scheme`.

### Záložka „Zdroj“ (zdroj stránky)

Pro správce prostoru nabízí hostitel vstup (položka menu, tlačítko) otevírající workspace se záložkami **Dokument** a **Zdroj** (ID `source`, `label="Zdroj stránky"`). Obsah záložky je `MarkdownSourcePanel` nad `useMarkdownSourceDraft` ([návrh](page-source-editor.md)). Ne-admini vstup ani záložku nedostanou a Monaco se jim nenačte; `tab-intent`/hover vstupu volá `prefetchSourceEditor()`.

- Před otevřením hostitel odešle vlastní rozpracované živé úpravy; základem je živý dokument.
- **Dokument jen pro čtení během návrhu je odpovědnost hostitele** (KISS, bez další prop): `<MarkdownEditor :readonly="readonly || (open && draft.dirty.value)">` a krátká poznámka v záložce Dokument.
- Zavření s neuloženým návrhem: `MarkdownDraftConfirmation` (Použít a pokračovat / Zahodit a pokračovat / Pokračovat v úpravách); Použít je asynchronní uložení, při neúspěchu zůstane Zdroj otevřený.
- Ztráta oprávnění: hostitel nastaví `readonly` a zavře detail (`open=false`), po `closed` zavolá `draft.close()` (přeruší i běžící uložení přes `signal`).


OpenAPI nabízí runtime volbu `openDetail(context, 'api' | 'yaml')`. Vrácení `true` předává detail hostiteli; jinak zůstává samostatný dialog. Callback ani stav záložek nejsou serializované.

Kontext je `MarkdownModuleNodeContext` vázaný na **uzel bloku** (`MarkdownModuleContext.nodeContext()`), ne na jedno vykreslení karty: `context.signal` se přeruší jen při odstranění či nahrazení bloku nebo zničení editoru, nikoli při přepnutí readonly a zpět (dřív hostitel blok považoval za ztracený). `context.anchor` je stabilní prvek uzlu jen pro určení a umístění (host do něj nikdy nevykresluje, jeho obsah patří rendereru modulu), `context.update` zapisuje i po překreslení (v readonly se odmítne) a `context.data` je snímek; aktuální data čte host přes `getPos()`. Změny oprávnění hlásí vždy přítomné `context.onEditableChange(editable => …)` (odhlášení vrácenou funkcí nebo s `signal`): host při `false` ukončí YAML a ponechá API. Před zápisem dál ověří `editor.isEditable` a pozici uzlu.

Galerie zapojuje tuto možnost pomocí `OpenApiTabsPrototype.vue`. Přepínač „Prototyp horních záložek“ umožňuje porovnání se samostatnými dialogy. Soubor, import a režimy komponenty jsou zatím v původním dialogu dostupném po vypnutí prototypu; prototyp nového pracovního prostoru nyní ověřuje přechody, zdroj a Scalar.

## Historie a schvalování

Původní Dokument / Změny / Historie v sekčním review zůstávají beze změny. Jejich propojení s detailem je následující integrační krok: detail musí nést konkrétní historickou revizi a její readonly stav; schvalování musí nadále pojmenovat a potvrdit celou existující jednotku schvalování (sekci) a přesnou revizi. Tato galerie nepředstírá vlastní historii API ani kolaborativní YAML. Kolaborativní objekty a zanořování patří do plánu 3.0.

## Ověření

Finální in-place korekce: 4/4 cílených scénářů Chromium/WebKit prošly, včetně stavu s poslední záložkou Dokument před zmizením. Typecheck a build balíčku prošly; umístění nad původním dokumentem bylo také vizuálně ověřeno v galerii.

Následná korekce uživatele: detail je in-place, nikoli celoobrazovkový dialog. Browserové testy navíc ověřují zachování původní šířky/pozice, viditelnost záložek po otevření, dostupnost okolní stránky a stav s jedinou poslední záložkou Dokument před zmizením. Předchozí výsledky níže popisují původní iteraci.

2026-09-25: celá sada OpenAPI prošla 22/22 scénářů (Chromium + WebKit). Po finální úpravě focusu a veřejného exportu znovu prošly oba scénáře pracovního prostoru v obou prohlížečích; první běh Chromium přerušil reload během souběžného buildu, samostatné opakování prošlo. Package build včetně deklarací, typecheck galerie a produkční build galerie prošly. Vizuálně ověřeno otevření Scalar → YAML, centrované barevné záložky a návrat focusu na kartu po zmizení dialogu. Build nadále hlásí velikost velkých lazy balíků Monaco/Scalar; nejde o chybu sestavení.

Browserové scénáře v `tests/e2e/openapi.spec.ts` kontrolují otevření z karty, dynamické záložky, zachování návrhu, potvrzení/zahození, návrat a undo, přímé otevření YAML, druhou definici po zavření, klávesnici a mobilní rozměry. Původní scénáře nadále ověřují samostatné dialogy, import, autora, režimy a skutečné lokální GET/POST požadavky.
