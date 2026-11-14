# Přesuny bloků, řádků a sloupců

Společný kontrakt navazuje na [rozhodnutí o UI komponent](decision-editor-component-ui.md).

## Výběr a rozsah

- Kliknutí nebo Enter/Space na úchytu vybere jednotku. U běžného bloku jde o blok, u nadpisu o sekci až po další nadpis stejné nebo vyšší úrovně, u tabulky o celý řádek/sloupec.
- Shift+klik nebo Shift+Enter rozšíří souvislý výběr od původního úchytu. Další Shift+výběr dovoluje rozsah také zkrátit.
- Tažení kteréhokoli úchytu uvnitř označené skupiny přesune celou skupinu v původním pořadí. Úchyt mimo výběr bere svou jednotku. Označení textu samo neurčuje skupinu bloků.
- Výběr vlastní společnou ikonovou lištu nad skupinou. Obsahuje počet bloků, šipky přesunu a u nadpisu volbu rozsahu „Celá sekce / Pouze nadpis“ ve sdíleném menu bez záhlaví. Pravé tlačítko nebo šipka dolů na úchytu vybere skupinu a zaměří lištu. Nativní tooltip a samostatný hover štítek se nezobrazují.
- Shift+šipky přesouvají stejný rozsah jako tažení: nahoru/dolů pro bloky a řádky, vlevo/vpravo pro sloupce. Přesunutá skupina zůstane vybraná a klávesnicové přesuny lze opakovat.

## Umístění ovladačů

- Úchyt a boční „+“ tvoří jeden sloupec ve vnitřním levém okraji editoru: úchyt u prvního řádku bloku, „+“ (vložit blok pod) přímo pod ním. Platí stejně pro odstavce, nadpisy, seznamy, kód, informační boxy i komponenty; tabulky mají vlastní ovladače řádků a sloupců.
- Sloupec (24 px a 4 px k textu) se vejde do okraje editoru: 32 px, dokument 40 px, úzký dokument při úpravách 32 px. Když hostitel okraj zúží, ovladače se posunou dovnitř plochy editoru, nikdy za její levý okraj. Ověření: `block-inserter.spec.ts` (několik šířek ve všech prohlížečích) a `useBlockHandleMotion.test.ts`.

## Zpětná vazba

- Pouhý pohyb nad textem nezvýrazňuje sekce. Náhled rozsahu se objeví až nad úchytem (také při jeho zaměření klávesnicí); opuštění úchytu ho ztlumí. Výslovný výběr kliknutím zůstává označený.
- Dokumentová skupina má jeden společný zaoblený rám s velmi slabým podbarvením. Obrys se vykreslí od úchytu oběma směry za 420 ms, podbarvení nabíhá/zhasíná za 240 ms; jednotlivé bloky nedostávají samostatné pruhy. Rám nepřijímá pointer, nevstupuje do obsahu ani historie a respektuje scrollovací hranice. Tabulkové výběry zachovávají přechod 200 ms a dostupné označené úchyty.
- Dokumentové bloky i tabulkové osy sdílejí `createDragCard`: skutečný začátek obsahu, počet jednotek, čitelný text a vytracení při překročení výšky 190 px. Dlouhý dokument ukazuje nejvýše prvních šest bloků. Náhled nemountuje další editor ani pluginové runtime.
- Karta sleduje pointer bez interpolace polohy. Původní obsah zůstává zeslabený. Přesouvání sousedů má společné časování a easing z `features/drag/motion.ts`.
- Bloky otevřou mezeru o 40 px; tabulkové osy rezervují rozměr celé přesouvané skupiny. Cíl obsahuje čitelný popisek. Malé pohyby kolem přepínací hranice nemění cíl; tolerance se zmenšuje u krátkých bloků a nízkých řádků.
- Animace nesmí změnit geometrii, podle které se rozhoduje o cíli. Náhled a potvrzení používají stejnou pozici. Puštění usadí obsah; Escape zruší přesun, aniž zavře fullscreen. `prefers-reduced-motion` vypíná interpolaci.

## Data a životnost

- Jeden přesun tvoří samostatný krok undo, oddělený před i po operaci. Vlastnosti, značky a vnitřní pořadí přesouvaných uzlů se zachovají.
- Záhlaví tabulky zůstává nahoře. Přesun skupiny nesmí rozdělit sloučenou buňku; modelové operace dovolují přesun celého spojení uvnitř skupiny. Horní fyzické úchyty nad colspan záhlavím nadále samostatný přesun sloupce nenabízejí.
- Přesun blokuje jen režim čtení. Ve sdíleném Yjs dokumentu jde o běžnou editaci; hranice popisuje [Přesuny bloků ve spolupráci](collaborative-block-moves-and-review.md). Odpojení editoru uklidí kartu, portály, listenery a běžící animace.
- Ověření: `dragBehavior.test.ts`, `features/table/*test.ts` a `tests/e2e/playwright.drag.config.ts`; poslední obsahuje samostatný frontend bez závislosti na .NET serveru.

## Vkládání souborů

Po puštění souborů editor okamžitě ukáže dočasné komponenty v místě vložení: lokální náhled podporovaného obrázku, zašedlou přílohu nebo kartu importovaného textu či diagramu. Typ vychází ze stejného rozhodnutí jako samotný import, včetně fallbacku na přílohu. Náhledy používají společné ikony a motiv, jemný nástup, animaci nahrávání a usazení hotového obsahu; respektují `prefers-reduced-motion`.

Náhledy jsou pouze dekorace pohledu. Nevstupují do uloženého dokumentu, spolupráce ani undo historie a při psaní sledují původní cíl. Dokončená dávka zachová pořadí souborů a tvoří jeden krok undo. Dokončení, chyba, zrušení, odstranění cíle, readonly i zničení editoru uklidí náhledy a uvolní lokální URL. Regresní testy jsou v `features/images/fileDrop.test.ts` a `tests/e2e/file-drop-preview.spec.ts` (součást `test:editor`).

## Prohlížeče

- Firefox, Chromium i WebKit jsou součástí stejné sady, bez vynechávání přetahování v některém z nich. `pnpm --dir tests/e2e test:drag` ověřuje přesuny a tabulkové ovládání.
- `pnpm --dir tests/e2e test:editor` navíc ověřuje psaní, formátování, odkazy, úzký viewport, fullscreen, pluginové formuláře, obrázky, grafy, diagramy a readonly obsah ve všech třech prohlížečích. Vyžaduje nainstalované Playwright prohlížeče.
- Po přepočtu rozměrů se obnoví ovladače právě hoverované buňky i bez dalšího `mouseover`; Firefox při nehybném pointeru tuto událost neopakuje. Test focusu vstupuje na tlačítko klávesnicí, aby ověřoval skutečné `:focus-visible`.
