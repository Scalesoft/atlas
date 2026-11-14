# Navigace dokumentem

`MarkdownEditor` i kompatibilní `PoolEditor` podporují volitelný prop `outline`. Výchozí hodnota je `hidden`. Navigace je lokální stav pohledu: neukládá se do Markdownu, rich JSON ani spolupráce. Kompaktní editor navigaci nezobrazuje.

```vue
<MarkdownEditor v-model="text" outline="floating" ariaLabel="Dokument" />
<MarkdownEditor v-model="text" outline="panel" ariaLabel="Dokument" />
<MarkdownEditor v-model="text"
  :outline="{ mode: 'panel', collapsed: true, preview: true }"
  ariaLabel="Dokument" />
```

Typy `MarkdownOutlineMode` a `MarkdownOutlineOptions` jsou exportované z `@atlas/markdown/vue` i hlavního balíčku.

| Volba | Význam |
| --- | --- |
| `mode: 'hidden'` | Bez navigace a jejího runtime. |
| `mode: 'floating'` | Husté pravostranné značky nadpisů s plynulou vlnou a popiskem úrovně; přejetí otevře obsah. Špendlík připne panel vpravo nahoře a skryje značky. |
| `mode: 'panel'` | Panel vpravo nahoře bez vlnek. Lze ho sbalit do ikonky a znovu otevřít. |
| `pinned?: boolean` | Počáteční připnutí plovoucího režimu; výchozí `false`. |
| `collapsed?: boolean` | Počáteční sbalení; výchozí `false`. |
| `preview?: boolean` | Pasivní živý výřez; výchozí `true`. |

Změna konfiguračních voleb resetuje příslušný lokální režim. Běžné editace jej nemění. Navigace nevytváří další editor, transport ani účastníka spolupráce. Přepínání režimů nemění instanci editoru nebo jeho historii.

## Ovládání

Přejetí nebo klávesnicový focus zvýrazní skutečný nadpis v dokumentu. Barva se rychle naznačí zprava a jedním nepřerušovaným přechodem rozlije přes nadpis. Prohlížení neposouvá dokument ani kurzor. Kliknutí na celou viditelnou značku nebo řádek, Enter či mezerník přejdou na nadpis bez přepsání aktuálního textového výběru; první kliknutí funguje i bez předchozího náhledu. Aktuální sekci značí barva textu, prohlížený nadpis podklad. Dlouhé názvy se v seznamu zalamují. Šipky a Home/End procházejí seznam. Escape zavře nebo sbalí obsah z vlnek i seznamu, vrátí focus na tlačítko obsahu a předchází ukončení fullscreenu. Navigace funguje také v readonly.

Seznam nadpisů v panelu je posuvný. Lišta vlnek se neposouvá: všechny značky se zahustí podle počtu nadpisů a dostupné výšky, nejvýše po 12 px. Zvětšuje se pouze kresba kolem ukazatele; pozice tlačítek zůstávají stabilní. Viditelné přesahy patří ke svému nadpisu a při pohybu přes ně vlna zůstává stát. Na dotyku jsou tlačítko obsahu a položky seznamu velké 44 px. Panel používá prostor vpravo vedle dokumentu, na úzkém editoru nad ním. Ovládání zůstává v hranicích konkrétní editorové instance, včetně fullscreenu. `prefers-reduced-motion` vypíná animace.

## Živý náhled a hranice

Náhled zrcadlí již vykreslený obsah vybrané sekce, včetně textových změn, viditelných značek cizích kurzorů a výběrů, tabulek, obrázků a vykreslených komponent. Používá existující DOM a neinstancuje další editovatelný pohled. Zůstává `inert` a mimo přístupnostní strom, aby čtečka nečetla stejný text dvakrát. Ovládací prvky, zdrojové editory, aktivní média a skripty se nepřenášejí.

Běžný text se zalamuje do šířky panelu; náhled používá 14px nadpisy a 12px text. Široké tabulky a moduly zachovávají původní šířku a výřez se ořízne. Náhled nemá posuvník ani jinou interakci. DOM změny jsou sloučeny do renderovacího snímku; canvas se kopíruje pouze při otevřeném náhledu. Zavření nebo zničení komponenty uvolní observer, časovače a kopie. Rozsah je omezen na nejvýše osm bloků, 1600 uzlů a hloubku 32.

Nejde o obecný záznam obrazovky: video, iframe, uzavřený shadow DOM a lokální neodeslané formulářové návrhy se nezrcadlí. Canvas, který prohlížeč nedovolí kopírovat, má dostupný textový fallback. Navigace podle avatarů není součástí této iterace; může později použít stejnou infrastrukturu náhledu.

Galerie: `/?story=outline`. Testy: `packages/markdown/src/ui/outline.test.ts`, `tests/e2e/document-outline.spec.ts`; samostatná browserová konfigurace `playwright.outline.config.ts`.
