# Živá spolupráce bez schvalování

**Historický dokument.** Rozhodnutí z 8. 9. 2026 ([přesuny bloků](collaborative-block-moves-and-review.md)) ponechává přesuny jako běžné editace bez stabilní identity bloků. Níže popsaný nový binding se nebude stavět; pravidla technického logu a lokálního Undo zůstávají platná. Dřívější poznámka: uživatel následně navrhl konkurenční [proposaly celého dokumentu](document-proposals-design.md).

Návrh z 5. 9. 2026 po upřesnění zadání: první verze má umožnit současné psaní a bezpečné přesouvání bloků, bez návrhů a schvalování. Šlo o návrh chování; nový binding nebyl zapojen a tehdejší ochrana před přesuny byla 8. 9. 2026 odstraněna.

## Chování pro uživatele

- Psaní, formátování a přesuny se ihned projeví lokálně a synchronizují ostatním. Na schválení se nečeká.
- Alice přesune blok a Bob do něj píše: Bobův text zůstane ve stejném bloku na novém místě. Platí to také pro opožděně doručené offline psaní.
- Dva souběžné přesuny stejného bloku mají jednoznačný deterministický výsledek na všech replikách. Nevytvoří kopii bloku a nezasáhnou jeho obsah.
- Smazání bloku a přesun nejsou totéž. Přesun ani opožděný zápis nesmí smazaný blok automaticky oživit.
- Nadpis se sekcí přesouvá přesnou sadu bloků vybranou při zahájení operace.
- Nejsou potřeba zámky pro běžné psaní ani přesouvání.

## Co znamená historie

### Technický záznam

Již nyní Yjs vytváří aktualizace dokumentu. Provider slučuje dosud neodeslané aktualizace stejného klienta; server ukládá přijaté dávky s pořadovým číslem, ověřeným autorem a časem přijetí. Potvrzení znamená trvalé uložení dávky. Snapshot následně umožní starší log zkrátit.

Počet technických záznamů proto není počet stisků kláves ani počet uživatelských akcí. Změny ve sloučené dávce se neztrácejí. Současný obnovovací log není neomezená historie verzí; po kompakci z něj nelze automaticky prohlížet každý dřívější stav.

### Undo a Redo

Undo patří konkrétnímu uživateli a aktuální relaci. Nemá vracet celý dokument do starého snapshotu. Dosavadní Yjs UndoManager seskupuje po sobě jdoucí lokální transakce s odstupem kratším než 500 ms.

Pro nový binding navrhujeme ponechat tento krátký interval, ale skupinu ukončit při změně bloku nebo druhu akce:

| Akce | Jeden krok historie |
| --- | --- |
| Plynulé psaní či mazání v témže bloku | Skupina změn do pauzy; nemusí vznikat krok na každé písmeno. |
| Vložení ze schránky | Jedna samostatná akce. |
| Formátování výběru | Jedna samostatná akce. |
| Přesun bloku nebo vybrané sekce | Jeden samostatný krok, který mění pouze pořadí. |
| Přidání nebo smazání bloku | Samostatná strukturální akce. |

Undo Alicina přesunu má ponechat Bobovy pozdější úpravy textu. Novější cizí přesun stejného bloku nesmí přepsat. Nový binding musí toto chování ověřit na skutečných nezávislých Y.Doc, nikoli pouze na dvou pohledech do jednoho objektu.

### Čitelná dlouhodobá historie

Časová osa „Alice upravila odstavec“ / „Bob přesunul sekci“ je samostatná funkce, kterou současné UI nemá. Pokud ji přidáme, bude seskupovat související psaní stejného autora v jednom bloku a přesuny ukazovat jednotlivě. Má mít vlastní pravidla uchování; technická kompakce ani lokální Undo ji nesmějí náhodně smazat. Její implementace není předpokladem bezpečné živé spolupráce.

## Minimální technické jádro

Jeden editor zůstane souvislou plochou. Každý blok dostane stabilní sdílenou identitu; jeho obsah se bude synchronizovat nezávisle na umístění. Přesun změní pořadí identit a nebude mazat a znovu vkládat sdílený text. Pouhé doplnění ID atributu do staré delete/insert operace nestačí.

Nový formát musí mít vlastní identitu schématu, kontrolu kompatibility klientů a výslovnou migraci starých dokumentů. Zachovat je potřeba také výběry, kurzory spoluautorů, formátování, skupiny bloků, čtenáře bez práva zápisu a ukládání po obnovení spojení. Schvalovací model se do této verze nepřidává.

## Testy před zapnutím přesunů

Přesun proti souběžnému psaní a formátování; dvě současná přemístění; Undo/Redo při cizím psaní a pozdějším přesunu; smazání během přesunu; rozdělení a spojení odstavců; výběry a kurzory po vzdálených změnách; různé pořadí a duplicity doručení; obnovení z uloženého snapshotu a logu; odmítnutí nekompatibilního klienta. Kontrola musí prokázat zachovaný obsah, jednu identitu každého bloku a shodné výsledné pořadí.
