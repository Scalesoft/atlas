# Vlastní plugin krok za krokem

Vytvoříme plugin „Vložit poznámku“. Přidá akci do toolbaru i slash nabídky a vloží běžný odstavec. Příklad používá pouze veřejné API a běží bez backendu. Záměrně začíná bez nového typu uzlu: uložený text lze znovu otevřít i bez pluginu.

## 1 · Vyberte správný druh rozšíření

| Chci přidat | Použiji | Potřebuji schemaVersion? |
| --- | --- | --- |
| Tlačítko nebo příkaz nad existujícím obsahem | `commands`, `actions` | Ne. |
| Pouze chování, zkratky nebo dekorace | Tiptap extension v `extensions` | Ne, pokud nepřidává obsahové schéma. |
| Nový node, mark nebo atribut | Obsahový plugin s `extensions` | Ano. |
| Samostatný obsahový blok s vlastním renderem | `defineMarkdownBlockModule` uvnitř pluginu | Ano. |
| Stavový nebo inspekční panel | `panels`, případně hostitelský Vue slot | Ne, pokud nemění schéma. |

Nepotřebujete měnit registraci v jádře knihovny. Soubor pluginu může být součástí vaší aplikace; seznam `plugins` předáte při vytvoření editoru. Instalace editoru do samostatné aplikace a peer dependencies jsou popsané v README balíčku. V tomto workspace jsou závislosti připravené přes `pnpm install`.

## 2 · Definujte typovaný příkaz

Příkaz vytvořte přes `defineMarkdownCommand` z `@atlas/markdown/core`. Naše factory přijímá `{ text: string }`, odmítne prázdný text a vloží JSON odstavec s prefixem `Poznámka:`. Text předáváme jako textový uzel, takže například `<b>text</b>` zůstane doslovným textem.

Kompletní implementace (`notesPlugin.ts`) byla součástí galerie dřívějšího samostatného repozitáře editoru.

Vlastní ovládání pak může použít:

```ts
import { insertNote } from './notesPlugin'

// controller získáte například z Vue slotu nebo useMarkdownEditor.
const call = insertNote({ text: 'Prověřit přístupnost formuláře.' })
if (controller.query(call).enabled) {
  controller.dispatch(call)
}
```

Předání jiného objektu se stejným ID nestačí: registr ověřuje identitu factory. Argumenty se při vytváření volání kopírují pomocí `structuredClone`; callbacky, DOM a služby patří do closure pluginu.

`query` musí být bez vedlejších efektů. Tiptap příkaz může běžet i při kontrole dostupnosti bez `dispatch`. Ukázka deleguje na `commands.insertContent`, který tento kontrakt dodržuje. Pokud píšete transakci sami, podmínky kontrolujte vždy, zápis a vedlejší efekty provádějte pouze při skutečném provedení. Síťové požadavky nepatří do dotazu na dostupnost.

Vložení také potřebuje vlastní undo krok. Příkaz přes `closeMarkdownHistory(tr)` označí začátek a přidá vlastní metadata; hook `onCreate` po commitu uzavře konec prázdnou transakcí mimo historii. Dvě rychlá vložení ani následující psaní se tak nesloučí. Cleanup odregistruje listener. Značku nastavujeme pouze při `dispatch`, takže kontrola dostupnosti historii nemění.

## 3 · Zaregistrujte plugin a akce

Factory `createNotesPlugin()` vrátí `MarkdownPlugin` se stabilním `id: 'example.notes'` a `apiVersion: 1`. V `create()` zaregistruje definici příkazu v `commands`, volání s výchozím textem v `actions.slash` a stejné volání v `actions.toolbar`.

- `label` je viditelný a přístupný název akce. `keywords` rozšiřují vyhledávání; v příkladu funguje `/poznamka` i `/note`.
- `variants` omezuje povrchy. Toolbar příkladu povoluje `document` i `compact`; ostatní akce mohou mít jiné omezení.
- ID příkazu a jednotlivých akcí jsou v příkladu rozlišená a mají vlastní prefix. Duplicitní registrace nebo chybějící `requires` selže při inicializaci.
- `profiles` výslovně uvádí oba podporované profily. Protože plugin nevytváří vlastní schéma, nepotřebuje `schemaVersion`, serializer ani migraci.
- Sdílená definice příkazu neobsahuje proměnlivý stav editoru. Případný stav konkrétní instance alokujte uvnitř `create()`.

Výchozí Vue toolbar vykreslí akci automaticky. Když host nahradí slot `toolbar`, vykreslení `controller.actions.toolbar` přebírá sám. Akce v `actions.selection` se nabídnou nad výběrem: v plovoucí liště formátování za Citací a Blokem kódu a v liště vybraných bloků. Tlačítko svítí podle `active` z `defineMarkdownCommand` (například plugin Skrytý obsah přepíná obalení výběru). Slash menu používá tentýž příkaz a předává rozsah dotazu: odstranění `/poznamka` a synchronní vložení patří do jedné operace.

## 4 · Zapojte plugin do Vue

Úplná kopírovatelná Vue komponenta (`NotesExample.vue`) dává oba soubory vedle sebe. Používá `ref` s Markdown řetězcem, stabilní seznam `plugins` a běžný `MarkdownEditor`:

```vue
<MarkdownEditor v-model="value" :plugins="plugins"
  :readonly="readonly" ariaLabel="Ukázka vlastního pluginu" />
```

Výchozí profil je `markdown-safe`. Při použití `profile="rich"` musí být hodnota místo řetězce `MarkdownRichValue`. Není potřeba ručně přidávat metadata našeho pluginu: pouze vkládá vestavěné odstavce.

Profil a sada pluginů jsou pro vytvořenou relaci neměnné. Při přepnutí na jinou sadu vytvořte nový controller nebo remountujte Vue komponentu s novým `key`. Přepínač `readonly` se naopak může měnit za běhu.

## 5 · Vyzkoušejte skutečné chování

V galerii otevřete „Dokumentace → Vlastní plugin“, pod textem najdete živý editor a oba zdrojové soubory.

1. Umístěte kurzor do dokumentu a zvolte „Vložit poznámku“. Objeví se běžný editovatelný odstavec.
2. Na nový řádek napište `/poznamka` a potvrďte nabídku. Dotaz zmizí a nahradí jej poznámka.
3. Použijte Ctrl/Cmd+Z. Vložení vraťte jedním krokem; ověřte také dvě rychlá vložení a okolní psaní.
4. Zapněte „Pouze čtení“. Text zůstane čitelný, editační akce nejsou dostupné.
5. Rozbalte „Ukládaný Markdown“. Výsledek obsahuje pouze odstavec, žádné tlačítko ani pluginové UI.

Samostatný frontend spustíte z kořene repozitáře příkazem `pnpm --dir web/playground dev`. Příklad nepotřebuje připojení k SignalR ani vzdálenou službu.

## Texty rozhraní

Každý viditelný text knihovny (popisky, `aria-label`, tooltipy, stavy, chyby zobrazené uživateli) se čte za běhu ze sdíleného slovníku `markdownStrings` z `@atlas/markdown/core`. Host ho přepíše jednou při startu přes `setMarkdownStrings`, ještě před vytvořením editorů a pluginů. Výchozí texty jsou české.

- Opakovaná slova (`apply`, `cancel`, `close`, `remove`, `loading`, `retry`, `insert` …) jsou na nejvyšší úrovni. Vlastní plugin je používá místo vlastních kopií.
- Jádro i pluginy dodávané s balíčkem mají každý svou plochou sekci (`markdownStrings.table.*`, `markdownStrings.charts.*`). Klíče jsou typované v `packages/markdown/src/ui/strings/<sekce>.ts`. Výchozí hodnoty leží u kódu, který je vykresluje (u pluginu `strings.ts`, text jen líného runtime v `runtimeStrings.ts`), a do slovníku se přidají přes `registerMarkdownStrings` při načtení modulu. Stránka tak nese jen texty toho, co načte. Text s číslem nebo jménem je malá funkce, která vrací celou větu.
- Text čtěte až při použití (render, handler, factory pluginu), nikdy do konstanty při importu modulu: importy proběhnou dřív než hostitelské `setMarkdownStrings`. Seznamy definované na úrovni modulu používají gettery.
- Plugin mimo tento balíček slovník rozšířit nemůže. Vlastní texty přijme volbou `labels` s výchozími hodnotami (jako `createSecretsPlugin({ labels })`), sdílená slova bere z `markdownStrings`.
- Klíčová slova pro vyhledávání ve slash nabídce, ukázkový obsah vkládaných bloků a vývojářské chyby do slovníku nepatří.

## 6 · Když potřebujete vlastní obsahový blok

Vlastní uložený typ už mění datový kontrakt. Zde začíná práce navíc oproti poznámce:

1. Deklarujte `schemaVersion` a validujte pouze JSON data s rozumnými limity. Nové nodes, marks a atributy musí mít jednoznačného vlastníka.
2. Přidejte synchronní Markdown serializer a textový fallback. Pro `markdown-safe` výslovně deklarujte podporu profilu a přesné `markdownLossy(document)`; jinak vlastní obsah používejte v `rich`.
3. Pro blok využijte `defineMarkdownBlockModule` a `createMarkdownModuleLoader`. Schéma a export zůstávají malé; renderer, formulář a náročné knihovny načítejte lazy. Volitelné `markdown(data)` vrátí Markdown, který dialekt editoru načte zpět beze ztráty; `undefined` ponechá textový fallback a blok zůstane `markdownLossy` ([obsahové moduly](content-modules.md#ukládání-spolupráce-a-ověření)).
4. Lištu a formulář stavte ze sdílených primitiv. `runtime.edit` předá `context.anchor` do `openMarkdownModuleForm`; panel pracuje s návrhem a potvrzení provede jednu samostatnou undo operaci. Menu nemá viditelný titul ani křížek. Obsah nemá trvalá editační tlačítka.
5. Síťovou službu dodává hostitel. Respektujte `signal`, readonly, odstranění cíle a opožděné výsledky. Hooky lifecycle vracejí synchronní cleanup; u statického výstupu využijte `onRender`.
6. Zaregistrujte stejný plugin u `MarkdownRenderer`. Ověřte export bez načteného runtime a čtecí akce při readonly.
7. Při změně dat přidejte souvislé migrace `from → to` vždy o jednu verzi. U sdíleného dokumentu změnu koordinujte se serverovým schématem před připojením nových klientů.

Podrobný příklad modulu, lazy runtime a jeho aktualizace najdete v [obsahových modulech](content-modules.md). Přesné typy, migrace a lifecycle popisuje [reference pluginového API](editor-plugin-api.md); ovládání a akceptaci stanoví [společná pravidla UI/UX](decision-editor-component-ui.md).

## Co ověřit před začleněním

Otestujte dostupnost příkazu bez změny dokumentu, toolbar i slash, readonly, undo/redo a nezávislost dvou editorů. U obsahového pluginu přidejte validní i neplatný vstup, rich round-trip, přiznanou ztrátovost Markdownu, statické vykreslení a migrace. U async pluginu simulujte zrušení, selhání a pozdní výsledek po odstranění cíle.

Pro změnu knihovny spusťte `pnpm check`; browserové interakce mají samostatné Playwright scénáře v `tests/e2e`. Při vývoji externího pluginu ověřte také zabalený veřejný balíček, aby source aliasy galerie neschovaly interní import nebo chybějící export.

Související: [skladba editoru](editor-composition.md) a [architektonická rozhodnutí](editor-architecture-decisions.md).
