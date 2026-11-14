# Jak je editor poskládaný

Průvodce současným zdrojovým kódem pro vývojáře hostitelské aplikace i autora rozšíření. Galerie ukazuje výsledné chování; zde najdete vztahy mezi komponentami, tok změn a místa, do kterých patří nová funkce.

## Mapa od aplikace k dokumentu

```text
Hostitelská aplikace: hodnota, oprávnění, upload, zdroje dat
│
├─ MarkdownEditor (hotové Vue UI)
│  ├─ useMarkdownEditor → controller
│  └─ EditorSurface
│     ├─ MarkdownEditorRoot → sdílený kontext controlleru
│     ├─ MarkdownEditorContent → připojení editorového pohledu
│     ├─ EditorControls → formátování, tabulky, úchyt bloku
│     ├─ PluginToolbar / PluginPanels → příspěvky pluginů
│     └─ sloty, fullscreen, volitelná osnova a zdrojový náhled
│
├─ Vlastní UI → stejný controller a Vue headless komponenty
│
└─ MarkdownRenderer → statická validace a vykreslení obsahu

Controller
├─ registr pluginů → příkazy, akce, schéma, lifecycle
├─ engine → schéma, codec, projekce, Tiptap factory
├─ features → chování tabulek, obrázků, bloků a modulů
└─ volitelný collaboration adapter → Yjs / SignalR / server
```

Jde o mapu odpovědností, ne o úplný strom DOM. `MarkdownRenderer` nepřipojuje `EditorSurface` ani editační ovladače. Pro parsování používá krátce žijící headless runtime přes společnou factory; hooky pluginů následně mohou doplnit například graf.

## Tři způsoby sestavení

| Potřeba | Veřejná vrstva | Co vlastní aplikace |
| --- | --- | --- |
| Hotový editor | `MarkdownEditor` z `/vue` | Hodnotu nebo adaptér spolupráce, profil, pluginy, služby a oprávnění. |
| Vlastní rozložení | `useMarkdownEditor`, `MarkdownEditorRoot`, `MarkdownEditorContent` z `/vue` | Rozložení, tlačítka, dotazy na dostupnost příkazů a případné sloty. |
| UI mimo Vue | `createMarkdownEditorController` z `/core` | Připojení přes `attachView`, odběr stavu, odpojení a `destroy()`. |
| Čtecí náhled | `MarkdownRenderer` z `/vue` | Hodnotu, profil a stejnou sadu obsahových pluginů jako editor. Pro čtení stránky `variant="document"` a `outline`: stejný papír, tabulky a osnova jako v editoru bez sestavení editačního pohledu; zvýraznění kódu dodá `createStaticCodeHighlighting()`. |

`document` a `compact` mění povrch editoru. `markdown-safe` a `rich` určují datový kontrakt. Kompaktní vstup tedy může používat rich data a dokumentový povrch může ukládat Markdown. `readonly` mění oprávnění k editaci; není to jiný formát dat.

## Co se stane po kliknutí na akci

1. Toolbar nebo slash nabídka získá příkaz z registru pluginů. Vlastní UI se na dostupnost ptá přes `controller.query(call).enabled`.
2. `controller.dispatch(call)` ověří identitu příkazu a možnost úpravy. Pluginový příkaz pracuje nad transakcí editoru; nemá přímo přepisovat DOM.
3. Engine zkontroluje obsah a profil. Transakce mění ProseMirror dokument; NodeView a UI odrážejí nový stav.
4. Controller oznámí stav a zvýší `contentRevision` při změně obsahu. Pohyb kurzoru sám o sobě nevyžaduje uložení dokumentu.
5. Ve výchozím lokálním režimu `MarkdownEditor` předá novou hodnotu přes `v-model`. Rich režim předává obálku JSON, Markdown-safe řetězec.
6. Při spolupráci vede zápis přes adaptér do sdíleného dokumentu. Stav spojení, potvrzení dokumentu a stav textové projekce jsou samostatné údaje.

Režim `mode="revision"` místo průběžného exportu posílá `contentChange`. Počáteční obsah dostává přes `initialValue`; snapshot si aplikace vyžádá přes `getMarkdownContentCapability(controller).getSnapshot()`. Hodí se pro hosta, který potřebuje řídit frekvenci exportu. Nespojujte `v-model` se zdrojem spolupráce ani s režimem revision.

## Kde hledat ve zdrojácích

Všechny následující cesty jsou relativní k `packages/markdown/src/`.

| Soubor nebo adresář | Odpovědnost a důvod ke změně |
| --- | --- |
| `vue/MarkdownEditor.vue`, `vue/useMarkdownEditor.ts` | Veřejné props, vazba hodnoty a životnost controlleru ve Vue. |
| `ui/EditorSurface.vue` | Kompozice povrchu, sloty, fullscreen, drop, lazy osnova. |
| `ui/EditorControls.vue` | Propojení společných ovladačů s výběrem. |
| `core/controller.ts`, `core/types.ts` | Stav, příkazy, připojení pohledu, veřejný kontrakt. |
| `core/plugins/` | Registrace, závislosti, identita příkazů, příspěvky UI a lifecycle. |
| `engine/editorFactory.ts` | Společná konstrukce Tiptap instance. Nové UI nevytváří další vlastní factory. |
| `engine/documentCodec.ts`, `engine/staticRenderer.ts` | Validace, schéma, import a samostatný náhled. |
| `features/table/`, `features/drag/`, `features/images/` | Doménové příkazy, výběr, geometrie a async operace. |
| `features/modules/`, `features/suggestions/` | Sdílené primitivy modulů, lišt, formulářů a našeptávání. |
| `plugins/*` | Volitelné schopnosti s vlastní registrací a případným lazy runtime. |
| `collaboration/`, `collab/` | Veřejný vstup spolupráce a implementace synchronizace. |
| `legacy/`, `PoolEditor.vue` | Kompatibilita původního API nad společným controllerem. |
| `styles/` | Sdílený vzhled a témata; ovládací styly nepatří do uloženého dokumentu. |

Hostitel i samostatný plugin importují veřejné exporty `@atlas/markdown/...`. Interní soubory jsou mapa pro vývoj tohoto repozitáře, nikoli stabilní API pro konzumenta.

## Kdo vlastní stav a úklid

| Stav | Vlastník | Konec životnosti |
| --- | --- | --- |
| Uložený obsah | Lokální hodnota hosta nebo sdílený dokument | Řídí aplikace a úložiště. |
| Controller a pluginová instance | Jedna relace editoru | `destroy()`; Vue composable uklízí při konci scope. |
| Výběr, toolbar, otevřený panel | Připojený pohled | Změna výběru, uzavření nebo odpojení podle typu UI. |
| Návrh vlastností bloku | Otevřený formulář | Použít zapíše; Zrušit, Escape a kliknutí mimo návrh zahodí. |
| Renderer a async request | Konkrétní pluginový lifecycle | Abort signal, kontrola generace a cleanup. |
| API klient a autentizace | Hostitelská aplikace | Podle životnosti aplikace; tajné údaje se neukládají do dokumentu. |

Definici pluginu lze sdílet mezi editory. Jeho proměnlivý stav vzniká uvnitř `create()`. `onCreate` patří relaci, `onAttach` připojenému pohledu a `onRender` statickému výstupu. Odpojení pohledu není totéž jako zničení controlleru; externě dodaný Y.Doc vlastní hostitel.

## Kam patří nová funkce

- Pouze jiné rozložení nebo vlastní stavový řádek: Vue sloty `toolbar`, `before-content`, `inspector`, `status`. Sloty dostávají `{ controller, state }`.
- Nová akce nad existujícím obsahem: plugin s typovaným příkazem a příspěvkem do toolbaru nebo slash nabídky.
- Nový uložený typ obsahu: schema plugin, validace, verze, export a případná migrace. Pro samostatný blok použijte `defineMarkdownBlockModule`.
- Sdílená interakce více komponent: společná primitiva v `features/` nebo `ui/`, se zachováním veřejných exportů a lazy hranic.
- Integrace služby: klient dodaný hostitelem do factory pluginu; síťové požadavky mají zrušení a ošetření opožděného výsledku.

Pokračujte na [architektonická rozhodnutí](editor-architecture-decisions.md) a [návod na vlastní plugin](writing-a-plugin.md). Podrobný kontrakt je v [referenci pluginového API](editor-plugin-api.md).
