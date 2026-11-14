# Nástroje editoru

Volitelný entrypoint `@atlas/markdown/plugins/editor-tools` používá upstream FindAndReplace, InvisibleCharacters a CodeBlockLowlight. Nezavádí nový formát dokumentu ani pluginové schéma. Hledání a neviditelné znaky jsou stav konkrétního pohledu; běžný uzel `codeBlock`, atribut `language` a obsah zůstávají stejné.

```ts
import { createMarkdownEditorController } from '@atlas/markdown/core'
import {
  createEditorToolsPlugin,
  createCodeBlockHighlighting,
} from '@atlas/markdown/plugins/editor-tools'

const controller = createMarkdownEditorController({
  profile: 'markdown-safe',
  source: { kind: 'local', value: '```typescript\nconst x = 1\n```' },
  plugins: [createEditorToolsPlugin()],
  codeBlock: await createCodeBlockHighlighting(),
})
```

Stejné `codeBlock` přijímají `MarkdownEditor` a legacy `PoolEditor`. `MarkdownRenderer` zvýraznění nevykresluje dekoracemi: do jeho `plugins` přidejte `createStaticCodeHighlighting(options)`. Má stejné jazyky, limit délky i třídy `hljs-*` a bloky obarvuje až ve chvíli, kdy se blíží viewportu. Továrnu zavolejte před vytvořením editoru. Konfigurace schématu je pro instanci neměnná; zvýrazňovací uzel nepřidávejte do obecných `extensions`, jinak by vznikly dva uzly stejného názvu. Obě funkce lze samostatně vypnout pomocí `createEditorToolsPlugin({ findAndReplace: false, invisibleCharacters: false })`.

## Hledání a nahrazování

- Ctrl/Cmd+F obsluhuje pouze editor, do kterého směřuje klávesová událost. V readonly lze hledat, měnit volby a přecházet mezi výsledky. Přepnutí readonly skryje náhradu a zahodí její návrh.
- Tlačítko „Hledat a nahradit“ je v běžné liště doplňků. Panel je Vue načítané až při otevření a používá společný ukotvený panel, tokeny, focus a koordinaci popupů. Escape a kliknutí mimo panel/editor zavřou hledání a odstraní dekorace.
- Hledání probíhá v textových blocích, také přes rozdílné marks a v tabulkách, ale nepřekračuje buňky/bloky. Nehledá ve skrytých atributech kontaktů, odkazů či jiných uzlů.
- RE2 režim nepodporuje lookaround ani zpětné reference. Chybný výraz se oznámí a vyčistí předchozí shody. Nulové shody se vynechají; náhrada je doslovný text, bez substituce `$1`.
- `findInEditor(editor, term, { caseSensitive, wholeWord, useRegex })`, `editorSearchState(editor)`, `replaceInEditor(editor, replacement, all)` a `openEditorSearch(editor, anchor?)` jsou headless integrační API pro držitele Tiptap editoru. Příkazy upstream pro další/předchozí výsledek zůstávají dostupné.
- Zápisy kontrolují readonly i u přímých upstream příkazů `replace` a `replaceAll`. Každá náhrada je ohraničena existující lokální/kolaborativní historií před i po zápisu; samotné hledání se do undo nezapisuje.

## Neviditelné znaky

Výchozí stav je vypnuto. Akce „Zobrazit neviditelné znaky“ přepíná upstream dekorace mezer, nezalomitelných mezer, konců odstavce a ručních zalomení. Značky se nevkládají do JSON, Markdownu, textového exportu ani undo. Styly jsou součástí běžného stylesheetu editoru; upstream nevkládá globální `<style>`.

## Kód

Asynchronní továrna načítá Lowlight a jen zvolenou sadu definic: prostý text, C#, SQL, JSON, XML/HTML, JavaScript, TypeScript a shell. `languages` může tuto sadu omezit. Lowlight je při knihovním buildu zahrnuto jako pojmenovaný export, aby se jeho kompletní katalog odstranil při tree shakingu; knihovny zůstávají mimo core cestu.

Neznámý jazyk, nenačtená definice nebo chyba zvýraznění ponechá původní čitelný text. Automatické hádání jazyka je vypnuté. Blok delší než `maxHighlightLength` (výchozích 100 000 znaků) se zobrazí bez zvýraznění; obsah se nezkracuje.

Lišta s jazykem a kopírováním se objeví pouze u kódu s aktivním výběrem/focusem. Volba jazyka používá společnou nabídku a jeden samostatný undo krok. `controls: false` vypne vlastní NodeView pro hostitele s vlastním UI. Kopírování používá clipboard aktuálního dokumentu a případné selhání oznámí přístupným názvem tlačítka.

## Ověření

`editor-tools.test.ts` pokrývá češtinu, marks, tabulky, readonly, regex, dvě instance, Ctrl+F, životnost panelu, NBSP, historii náhrad i jazyků, dlouhý kód, zachování fenced jazyka a vybranou lištu. `contextToolbar.test.ts` ověřuje readonly panel jako výslovný opt-in při zachování dosavadního editačního defaultu. Úplné browserové ověření galerie a všech kolaborativních transportů je samostatná integrační kontrola.
