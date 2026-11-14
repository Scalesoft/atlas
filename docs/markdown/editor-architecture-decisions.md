# Architektonická rozhodnutí

Tento přehled vysvětluje rozhodnutí zachycená v současném kódu a existujících kontraktech. Není novým návrhem přestavby editoru ani tvrzením, že všechny starší komponenty prošly úplným auditem. Závazná pravidla UI a jejich známé mezery uvádí [společné rozhodnutí UI/UX](decision-editor-component-ui.md).

## 01 · Controller je společná hranice

**Kontext:** hotový Vue editor, vlastní headless UI a původní `PoolEditor` potřebují stejné příkazy a dokumentové chování.

**Rozhodnutí:** stav, příkazy a životnost řídí controller. Konstrukce Tiptap instance je soustředěná v `engine/editorFactory.ts`; Vue skládá povrch a váže jeho životnost.

**Důsledek:** oprava příkazu se projeví ve více površích. Nový toolbar se napojí na controller. Za tuto jednotnost platíme nutností udržovat veřejné typy a oddělit připojení DOM od existence relace. Headless zde znamená bez předepsaného UI; interaktivní pohled stále potřebuje DOM.

**Ověření:** `tests/contracts/architecture.mjs` kontroluje konstrukční hranici a importy. `/core` nesmí transitivně přitáhnout Vue, Yjs nebo SignalR.

## 02 · Profil určuje autoritativní data

**Kontext:** Markdown neumí bezeztrátově vyjádřit každý graf, kontakt, atribut tabulky nebo další rich obsah.

**Rozhodnutí:** `markdown-safe` používá Markdown řetězec a odmítá ztrátové pluginové změny. `rich` používá verzovanou obálku `MarkdownRichValue`; její JSON dokument je autoritativní. Markdown a prostý text jsou projekce a mohou být ztrátové.

**Důsledek:** aplikace volí formát podle požadované věrnosti. Nestačí ukládat `getProjection().markdown`, pokud potřebuje zachovat rich data. UI varianta `compact` nebo `document` je na profilu nezávislá. Projekce se počítají lazy; čtení rich hodnoty samo nevyžaduje serializaci Markdownu.

**Ověření:** stejný codec kontroluje počáteční hodnotu, náhradu dokumentu a výstup; kontroly transakcí brání neplatným změnám. Plugin se svým obsahem potřebuje serializer, textový fallback a pro Markdown-safe výslovný kontrakt `markdownLossy`.

## 03 · Pluginy jsou aditivní a schéma se verzovává

**Kontext:** pořadí importů nesmí měnit význam uloženého dokumentu. Starý obsah se nesmí tiše ztratit při chybějícím pluginu.

**Rozhodnutí:** plugin deklaruje stabilní `id`, `apiVersion: 1`, závislosti `requires` a pro nové nodes, marks nebo atributy také `schemaVersion`. Registr odmítá kolize a nepovolené zásahy do cizího schématu. Příkaz se volá pomocí registrované factory, nestačí stejné string ID.

**Důsledek:** plugin pouze s akcí nad běžným textem verzi schématu nepotřebuje. Obsahový plugin vyžaduje migrace po jednotlivých verzích; neznámý plugin nebo novější dokument se odmítne. Sada pluginů a profil jsou konfigurace relace: pro změnu vytvořte nový editor. Změna označení tlačítka není změna schématu.

**Ověření:** registr, schema regrese a migrace se testují v `packages/markdown/src/core/`. Ve spolupráci musí stejnou identitu schématu přijmout také server před hydratací; nasazení nové verze obsahu je koordinovaná migrace.

## 04 · Ovládání patří výběru, obsah dokumentu zůstává čistý

**Kontext:** vlastní tlačítka pod každým blokem a různé dialogy komplikují čtení, klávesnici, fullscreen i export.

**Rozhodnutí:** výběr komponenty zobrazí její lištu nad obsahem. Jednoduché akce a volby používají společné menu bez viditelného titulku a křížku; vlastnosti používají ukotvený formulář. Oba mají přístupný název. Tabulky jsou interakční reference, další komponenty sdílejí tokeny, toolbar, popover a form primitives.

**Důsledek:** autor modulu doplní specializovaná pole, nikoli nový systém ovládání. Návrh se zapisuje až potvrzením. Každá dokončená operace má samostatnou undo hranici; jedna transakce sama o sobě oddělení od okolního psaní nezaručuje. Při readonly zůstávají obsahové akce, například odkaz nebo stažení, dostupné, ale změny dokumentu jsou blokované.

**Ověření:** review zahrnuje klávesnici, focus, Escape, zrušení návrhu, dvě rychlé operace undo, scroll, druhý editor a readonly. Podrobná mapa současných implementací a mezery zůstávají ve [společném UI/UX kontraktu](decision-editor-component-ui.md).

## 05 · Malé schéma, lazy runtime, explicitní služby

**Kontext:** dokument musí jít validovat a uložit, i když se ještě nenačetla knihovna grafu nebo selhalo vzdálené API.

**Rozhodnutí:** schema definice a synchronní export jsou dostupné při vytvoření editoru. Náročný renderer, formuláře a styly modulu se načítají až při použití. Hostitel dodává upload, adresář, klienta wiki či jinou službu.

**Důsledek:** základní import netahá všechny integrace a těžké knihovny. Plugin ale musí nabídnout čitelný fallback, loading, chybu a případný retry. Serializer nesmí stahovat data. Uložený obsah obsahuje JSON hodnoty, nikoli DOM, funkce, requesty nebo přístupové tokeny.

**Ověření:** balíčkové smoke testy hlídají veřejné exporty; runtime testy ověřují načítání a úklid. `defineMarkdownBlockModule` a `createMarkdownModuleLoader` poskytují společnou infrastrukturu. Detaily jsou v [obsahových modulech](content-modules.md).

## 06 · Async práce má vlastníka a konec životnosti

**Kontext:** upload může skončit po smazání cíle, uživatel může přepnout readonly nebo zavřít editor během hledání.

**Rozhodnutí:** operace používají abort signal, kontrolu aktuální generace a mapování cíle přes transakce. Hooky `onCreate`, `onAttach` a `onRender` vracejí synchronní cleanup. Mutable stav patří do `create()` konkrétní instance.

**Důsledek:** opožděný výsledek nesmí obnovit smazaný blok, ukrást focus ani otevřít zrušený panel. Samotné předání signalu nestačí, když ho služba ignoruje. Portál se vytváří ve správném `ownerDocument`, registruje přes `registerMarkdownOverlay` a uklidí před odstraněním DOM.

**Ověření:** testujte zničení během requestu, změnu výběru, readonly, selhání cleanupu a dvě současné instance. Statický renderer má vlastní lifecycle; zobrazení dokumentu není důvodem připojit plný editorový povrch.

## 07 · Spolupráce je volitelná a uložení není stav spojení

**Kontext:** lokální editor nemá potřebovat SignalR a online spojení není důkaz trvalého uložení.

**Rozhodnutí:** controller přijímá lokální zdroj nebo adaptér spolupráce. Synchronizační implementace je za samostatným exportem `/collaboration/signalr`. Stav rozlišuje `connection`, `documentSave`, `projectionSave` a `pendingChanges`.

**Důsledek:** hostitel dodává identitu, oprávnění a úložiště. Nemá současně přepisovat sdílený dokument lokálním `v-model`. Server ověřuje schéma a oprávnění; pouhé skrytí tlačítka nenahrazuje autorizaci. Garance obnovy a potvrzení závisí na zvoleném protokolu a úložišti, nelze je odvodit z označení „realtime“.

**Ověření:** frontendové a serverové testy jsou samostatné. Realtime ukázka vyžaduje backend, základní dokumentace a pluginový příklad fungují pouze s frontendem.

## Jak posuzovat další změnu

Nejdříve určete vlastníka dat, vlastníka UI a konec životnosti operace. Potom zvolte veřejnou vrstvu podle [mapy komponent](editor-composition.md). Při změně schématu popište kompatibilitu a migraci; při změně UI ověřte společný akceptační seznam. Nové architektonické rozhodnutí má uvést konkrétní problém, zvolenou variantu, náklady a způsob ověření.

Pro první rozšíření pokračujte na [vlastní plugin krok za krokem](writing-a-plugin.md).
