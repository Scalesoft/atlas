# Zdroje obrázků, importní limity a kompozice

`resourcePolicy` je volitelná konfigurace `createMarkdownEditorController`, moderního `MarkdownEditor`, kompatibilního `PoolEditor` a samostatného `MarkdownRenderer`. Politika se kopíruje při vytvoření kitu. Změna politiky controlleru vyžaduje nový controller; renderer reaguje na změnu svého propu.

```ts
const resourcePolicy = {
  maxFiles: 20,
  maxTotalFileBytes: 32 * 1024 * 1024,
  maxImagePixels: 20_000_000,
  maxCanvasPixels: 20_000_000,
  imageSources: {
    allowRelative: true,
    allowedOrigins: ['https://files.example.com'],
    allowDataImages: false,
    allowBlobImages: false,
  },
} satisfies MarkdownResourcePolicy
```

Neuvedené počty, bajty a pixely nemají nový implicitní limit, aby se nezměnily stávající hostitelské integrace. Textový import zachovává dosavadní maximum 5 MiB na soubor. Nyní se však velikost a počet ověří pro celou dávku před spuštěním resolverů; překročení limitu nedělá tichý částečný import. Importy, uploady a exporty jednoho editoru sdílejí frontu se dvěma aktivními operacemi. Čekající zrušené operace se nespouštějí, výsledky importu drží pořadí souborů. Dřívější chování při běžné chybě jednotlivého resolveru zůstává: editor ohlásí chybu souboru a může vložit ostatní úspěšné výsledky.

Hostitelský resolver musí respektovat `AbortSignal` a ukončit svou práci. Dokud jeho promise skutečně neskončí, stále obsazuje jeden ze dvou slotů; fronta neprohlašuje práci za ukončenou pouze proto, že požadavek byl zrušen.

Výchozí politika připouští HTTP(S), relativní cesty, rasterové `data:image/{png,jpeg,gif,webp,avif};base64,…` a blob adresy s HTTP(S) původem. Odmítá aktivní schémata, SVG data URI, přihlašovací údaje v URL, řídicí znaky a zpětná lomítka. Seznam `allowedOrigins` omezuje absolutní HTTP(S) a blob adresy; relativní adresy ovládá samostatný přepínač. Při nastaveném seznamu původů se adresy `//host/path` odmítají: jejich skutečné schéma závisí na hostitelské stránce, kterou čistý Node převod nezná. Politika neprovádí síťový požadavek a není serverovou ochranou proti SSRF ani náhradou CSP, ověřování redirectů a oprávnění úložiště.

Stejné pravidlo `src` je na vlastním atributu Image/RichImage schématu. Používají je validace rich JSON, převod Markdownu/HTML, přímé transakce, upload odpovědi, panel nahrazení obrázku a samostatný readonly renderer. `prepareMarkdownValue` a `renderMarkdownValue` přijímají politiku jako poslední volitelný argument. Renderer ověří kandidáta před výměnou dříve zobrazeného obsahu. Důvěryhodné hostitelské pluginy mohou definovat vlastní jiné typy zdrojů; jejich síťové chování tato politika nesandboxuje.

Tiptap parser má zapnuté čtení base64 obrázků, aby platný rasterový obrázek nezmizel při rich → Markdown → readonly roundtripu. O přijetí následně rozhoduje validátor politiky. `allowDataImages: false` tedy vrací chybu při validaci; nespoléhá na upstream přepínač, který obrázek pouze vynechá.

Pokud je zapnutý `maxImagePixels`, souborový import/upload čte nejvýše 256 KiB hlavičky PNG, JPEG, GIF nebo WebP bez spuštění image decoderu. Nerozpoznatelná či neúplná hlavička se odmítne. U dávky proběhne tento preflight před vzdálenými uploady. To je kontrola metadat podporovaných formátů, nikoliv validátor úplného obrazového souboru. Rozšířený obrázkový node view navíc ověří rozměry po nativním načtení. Samostatný statický renderer a jednoduchý markdown-safe obrázek aplikují pravidla URL, nikoliv pixelový dohled nad síťovým decoderem. Nastavení nemůže omezit vnitřní CPU či paměť prohlížeče při načítání externí URL; pro tuto hranici host používá ověřené zdroje a předzpracování na serveru. Upravovaný export ověří skutečné rozměry načteného obrázku a spočtený počet výstupních pixelů před alokací canvasu. Po úspěchu, chybě či zrušení uvolní zdroj a rozměry canvasu.

Obrázkové node views nepřepisují atributy při pouhé změně výběru. Editability sleduje jeden sdílený observer na editor, který notifyuje pouze změnu režimu. Ovladače řádků/sloupců zachovávají DOM a focus při stejné struktuře tabulky, aktualizují geometrii/výběr a používají aktuální callbacky. Po změně struktury se obnoví. Moduly sdílejí jeden oddělený, hluboce zmrazený JSON snapshot na editor state; PM atributy se nezmrazují. Staré asynchronní renderery drží vlastní původní snapshot, `watchDocument` zůstává konzervativní.

Sdílený IME guard respektuje `isComposing`, historický `keyCode = 229` a kompozici editoru. Formuláře navíc sledují `compositionstart`/`compositionend`, protože událost `submit` příznak kompozice nemá. Escape, Enter a navigace během kompozice nezavírají panel ani neaplikují rozepsanou hodnotu. Nativní přejímka používá Chromium CDP `Input.imeSetComposition`; neprohlašuje pokrytí všech systémových IME a všech prohlížečů.
