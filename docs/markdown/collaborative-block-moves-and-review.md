# Přesuny bloků ve spolupráci

**Rozhodnutí 8. 9. 2026:** přesun bloku je běžná editace. Ve sdíleném Yjs dokumentu se provede stejně jako Ctrl+X a Ctrl+V: smazání na původním místě a vložení na cílovém. Žádný zámek, žádné schvalování přesunu, žádný nový model identity bloků. Přesun se všem projeví okamžitě a nikoho neblokuje.

## Co se změnilo

- `canMoveBlocks` už nekontroluje přítomnost Collaboration extension. Přesuny klávesnicí, tažením, přesuny řádků a sloupců tabulek i řazení fungují ve všech režimech.
- `CollaborationDragGuard` byl odstraněn. Nativní drop ProseMirroru se nezachytává.
- Zmizelo hlášení „Přesouvání je ve sdíleném dokumentu dočasně nedostupné“ z gripu i z lišty vybraných bloků; tlačítka Posunout nahoru/dolů jsou vypnutá jen na okrajích dokumentu a v režimu čtení.
- Dřívější zákaz byl nekonzistentní: chránil pohodlnou cestu (grip) a nechával otevřenou stejnou operaci přes schránku.

## Přijaté hranice

| Situace | Chování |
| --- | --- |
| Někdo současně píše do jiného bloku | Text zůstane, repliky konvergují. |
| Někdo upraví přesunutý blok po doručení přesunu | Text zůstane v přesunutém bloku. |
| Někdo má nesloučenou (typicky offline) úpravu uvnitř právě přesouvaného bloku | Úprava se nepřenese do kopie. Stejné chování jako u vyjmutí a vložení. Repliky konvergují. |
| Přesun a smazání téhož bloku současně | Blok se nezduplikuje. |
| Undo přesunu poté, co do přesunutého bloku někdo psal | Původní blok se vrátí, cizí text zůstane samostatně na konci dokumentu. Známá vada, řeší se až na základě zpětné vazby. |

Okno pro ztrátu je při online spolupráci v řádu milisekund. Při offline editaci trvá po dobu výpadku; offline změny dnes nepřežijí zavření karty, takže jde o okno do znovupřipojení.

## Ověření

- `packages/markdown/src/collab/block-concurrency.test.ts`: nezávislé repliky přes veřejný `useCollab`, konvergence po přesunu klávesnicí i po dropu gripu, zachování cizích úprav, zdokumentovaný přijatý scénář ztráty, souběžné smazání, Undo a chybějící drag guard.
- `packages/markdown/src/features/table/rowOrder.test.ts`: přesun řádku a řazení sloupce nad sdíleným fragmentem.
- `tests/e2e/collaborative-block-moves.spec.ts`: klávesnice, tažení a lišta vybraných bloků pod skutečným Yjs bindingem.
- Samostatné přesuny dále ověřují `dragBehavior.test.ts`, `block-motion.spec.ts` a `block-drop.spec.ts`.

## Historie

Předchozí návrhy (schvalování jednotlivých přesunů, stabilní identita bloků, sekční Yjs kanály) jsou zachyceny v historii tohoto souboru a v `live-collaboration-design.md`. Nejsou plánované.
