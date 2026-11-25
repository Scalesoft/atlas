# Changelog

All notable changes to `@atlas/markdown`. The package follows [Semantic Versioning](https://semver.org/);
while it is below 1.0.0, a minor release may contain breaking changes, which are listed first.

## Unreleased

### y-tiptap 3.3 bez patche (Atlas 5.2)

- **Breaking:** `@tiptap/y-tiptap` 3.3.1 a bez pnpm patche. Kurzory spolupracovníků kreslí a vlastní kurzor publikuje
  `CollaborationCarets` (`@atlas/markdown/collaboration/signalr`, jméno rozšíření `collaborationCaret` a volba `provider` zůstaly):
  kurzor, který předběhne svou editaci, zůstane na posledním místě, kam šel vyhodnotit; překreslují jen cizí změny, nejvýš jednou za
  snímek, a nejdřív se přečte výběr, který prohlížeč ještě nenahlásil; vlastní kurzor se zveřejní až po první klávese nebo kliknutí.
  Peer `@tiptap/extension-collaboration-caret` odpadl.
- Vzdálená změna nahrazuje jen bloky, které změnila (upstream). `KeepingCollaboration` (místo `Collaboration` v `useCollab`) zajistí,
  že plugin registrovaný později (bublinové menu, úchyt, živý zápis agenta) dokument znovu nepostaví z Yjs.
- `CollaborationRemoteSelection` odstraněno: pád „Position N out of range“ opravil y-tiptap 3.3; nahrané stavy zůstaly jako regresní test.
- Materializer corpus: hook, Y.Map a holá hodnota mezi potomky odstavce y-tiptap 3.3 přeskočí; editor je nikdy nezapíše a server je dál
  odmítá (`refused`).

### Tiptap 3.35 a ProseMirror (Atlas 5.2)

- Tiptap 3.35.0 (peer `^3.35.0`), `prosemirror-model` 1.25.13 a `prosemirror-view` 1.43.1 přes `@tiptap/pm`.
- Pravidla psaní (input rules) vkládají napsaný znak do dokumentu dřív, než pravidlo běží: `range` pravidla sahá až za něj. Ctrl+Z po automatické konverzi (`:smile:`, `$x$ `) vrací text tak, jak byl napsán, včetně spouštěcího znaku.
- Manifest schématu: atribut s vlastním validátorem nese `validated: true`. ProseMirror 1.25.12 ho spouští při každém vytvoření uzlu s explicitní hodnotou, takže editor takový uzel z neplatné hodnoty nepostaví a y-tiptap ho smaže ze sdíleného dokumentu; `YjsDocumentMaterializer` ji odmítne stejně (`InvalidAttributeValue`).
- První Enter v hledání vybere zvýrazněný první výsledek, přeskakovat ho přestalo (Tiptap 3.33).

### Neutrální názvy (Atlas 5.0)

- **Breaking:** formát rich dokumentu je `atlas-rich` (`RICH_FORMAT` z `./core`); vše, co píše, ho zapisuje. Čtení, validace, migrace a koncepty dál přijímají `scalesoft-rich` (`LEGACY_RICH_FORMAT`, `isRichFormat`, vstupní typy `MarkdownRichInput` a `MarkdownValueInput`).
- Výchozí databáze IndexedDB jsou `atlas-markdown-drafts`, `atlas-markdown-journal-v2` a `atlas-markdown-recovery`; při otevření se záznamy staré databáze `scalesoft-markdown-*` jednou přesunou a stará se smaže.
- Build: `dist/atlas-markdown.js` a `dist/atlas-markdown.css`, manifest schématu má `format: "atlas-markdown-schema"`, proměnné testů `ATLAS_UPDATE_SCHEMA`, `ATLAS_UPDATE_EMOJI`, `ATLAS_YJS_FUZZ_*`.

### Zdroj stránky: Esc a fokus pro hostitele s vlastním přepínačem (Atlas 4.1)

- `mountSourceEditor` má volitelné `onEscape`: Esc, který editor sám nevyužil (hledání, návrhy, výběr, další kurzory, zpráva, navigace chyb, snippet, běžící formátování). `MarkdownSourcePanel` ho předává událostí `escape`; hostitel jím může pohled opustit. Bez posluchače se nic nemění.
- `MarkdownSourcePanel` po připojení editoru převezme fokus i od stisknutého tlačítka segmentového přepínače (`aria-pressed="true"`), nejen od záložky (`role="tab"`).

### Zdroj stránky zamyká méně (Atlas 4.0)

- Blok ve zdroji (`createMarkdownSourceSession`) už nezamyká obrázek s vlastní prezentací ani odkaz na stránku: session je přenáší sama. Obrázek se stejnou adresou dostane při `apply` zpět svou šířku, ořez a popisek, odkaz se stejnou adresou a názvem je zase `pageReference`. Seznam kroků se snímky obrazovky je tak obyčejný upravitelný Markdown.
- Zdroj čte text doslovně: holá adresa nebo doména zůstává textem (odkaz je `<adresa>` nebo `[text](adresa)`) a víc mezer za sebou se neslučuje. Dřív obojí zamklo celý blok, protože by se čtením změnil.
- Blok kódu s jazykem `""`, a seznam, jehož uložená těsnost neodpovídá obsahu, už chráněné nejsou.
- `MarkdownLossyOptions.ignoreIdentity` nově přeskočí i prezentaci obrázku a odkaz na stránku; `parseMarkdownContent` a `MarkdownDocumentCodec.parseMarkdown` berou volitelné `ParseOptions`.
- **Chráněný blok ukazuje celý svůj text.** Fence `md-protected` má pod popiskem Markdown bloku (čitelný a hledatelný, nikdy nečtený zpět); `MarkdownSourceBlock.text`, `MarkdownSourceProtectedRange.text` a `SourceProtectedBlock.text` ho nesou a blok má proměnný počet řádků (`endLine` už není `startLine + 2`). `protectedFence.lines(id, label, text?)` píše vnitřní fence s vlnovkami.
- Seznamy stejného druhu za sebou drží od sebe řádek `<!-- -->`, tabulka mimo GFM se upravuje jako ```` ```a-table ````. Nadpis, který nese víc než text (aliasy; v Atlasu stav požadavku), se ve zdroji jmenuje, `## Název {#id …}`: aliasy se mu vrátí podle ID, ID mimo stránku nebo dvakrát je chyba na řádku a obyčejný nadpis nikdy nepřevezme ID pojmenovaného. Čtečku zápisu dodává hostitel (Atlas: plugin požadavků); bez ní zůstává takový nadpis chráněný.
- Hostitelské rozšíření smí psát řádek nadpisu samo: `markdown.heading(state, node, parent, index)` ve svém storage. `reportMarkdownSyntaxError(issue)` (`./core`) hlásí zápis, který čtečka pluginu odmítla; zdroj stránky ho ukáže jako chybu na řádku. `<!-- -->` je oddělovač jen tehdy, když za seznamem následuje seznam, a jen první takový řádek.
- Pojmenovaný je nadpis, kterému čtení dalo ID (ne podle vzhledu řádku). `{#id}` samotné vrací nadpisu vše, co nesl; hostitel smí přečtený nadpis upravit proti původnímu (`markdown.headingRead(attrs, original)` ve storage rozšíření). Řádek nadpisu, kterému zmizelo jméno, je chyba; nadpis, který nese víc, než text říká, a ve zdroji se nejmenuje, zůstává chráněný. `readMarkdownLiterally` nastaví parseru `pageSource`, podle kterého čtečky hostitele poznají zdroj stránky.
- Mezery se drží jen uvnitř řádku (seznam úkolů zůstává upravitelný, kolem obrázku nevznikají mezery navíc).
- **Oprava writeru (i na serveru, `EditorMarkdownWriter`):** text adresy zapsané jako `<url>` se neescapuje; `<https://example.com/~a/*>` se dřív zapsal se zpětnými lomítky, která se čtením stala součástí adresy. Adresa s mezerou, `<` nebo `>` se píše `[text](url)`, protože `<…>` by ji uťalo.
- **Oprava čtení:** alternativní text obrázku si ponechá escapované znaky (`![a\_b\[1\]](…)` se četlo jako „ab1“).

### Obnovení vybraných dokumentů (Sync, Atlas 4.0)

- `DocumentAccessCoordinator.RefreshReadersAsync(Func<string, bool> documents)` znovu ověří jen účastníky bez práva zápisu v živých dokumentech, jejichž ID filtr vybere. Hostitel tak po změně, která dokument nahradí novějším (nová generace stránky), hned odpojí jeho čtenáře místo čekání na periodické obnovení; čtenář nepíše, takže `[SUPERSEDED]` by ho nikdy nezastihlo. Zapisující se o nahrazení dozví jako dřív při dalším zápisu.

### Plynulý kurzor (Atlas 4.0)

- `MarkdownEditor` má volbu `smoothCaret` (výchozí `false`): kurzor kreslí editor sám (`.md-smooth-caret-mark` vedle dokumentu, nativní kurzor průhledný) a při psaní a pohybu plynule doklouže na nové místo v `--md-motion-fast`, po skoku přes víc řádků nebo po objevení stojí hned; v klidu bliká jako systémový. Výběr rozsahu, vybraný uzel, mezerový kurzor, editor jen ke čtení a ztráta fokusu ho skryjí. Pohne se až ve snímku po vykreslení napsaného textu, takže klávesu nezdržuje. Omezené animace: jen přesun, bez blikání. Barva `--md-caret-color` (výchozí barva textu).

### Jazyk slovníku řídí formátování (Atlas 4.1)

- Slovník `MarkdownStrings` má slovo `locale` (výchozí `cs-CZ`; hostitel s anglickým slovníkem ho nastaví na `en-US`). Podle něj se řadí tabulka (`Intl.Collator`), píše velikost přílohy (`Intl.NumberFormat`) a formátuje datum v `MarkdownDocumentHistory` (prop `locale` ho dál přepíše). Čeština je výchozí, takže bez volání `setMarkdownStrings` se nic nemění.

### Tabulka s bloky v buňce zůstává tabulkou (Atlas 3.6)

- Tabulku, kterou GFM nenese (víc bloků v buňce, např. odrážky, spojené buňky, nestejné řádky), Markdown dřív zploštil na odstavce. Teď ji píše jako ```` ```a-table ```` (dialekt §4.12): řádek na položku, u buňky `header`, `colspan`, `rowspan`, `align` a její bloky jako Markdown v `content`. Čtení vrátí tutéž tabulku; neplatná data zůstanou blokem kódu a jsou chybou komponenty. Server (`MarkdownDialect.cs`) píše a čte totéž.

### Bez rozbalovacího bloku (Atlas 3.6)

- **Změna schématu (rich revize 7):** rozbalovací blok (`disclosure`, `disclosureTitle`) zmizel, sbaluje se každý nadpis. Pryč je i z nabídky `/`, „Změnit na…“, příkaz `insert-disclosure`, řetězce `disclosure*` a ikona. Uložený blok se při otevření (`legacyDocument.ts`) stane nadpisem s jeho názvem (prázdný: „Podrobnosti“) o úroveň pod nadpisem před ním (2–6, bez něj 3) a za ním jeho obsahem; vnořené také. Sbalitelný alert `> [!tip]- Název` se vloží jako nadpis nad informačním boxem. Klient revize 6 dostane `SCHEMA_MISMATCH`.

### Kotvy nadpisů, schéma 2 (Atlas 3.6)

- ID nadpisu smí obsahovat tečku (`portal.vpn`); nový atribut `aliases` drží dřívější ID nadpisu oddělená čárkou a `migrateHeadingAnchors(value, { copy: true })` ho vyprázdní. Dokument verze 1 je platný obsah verze 2; klient se schématem 1 dostane `SCHEMA_MISMATCH`.

### Editovatelnost je příznak (Atlas 3.6, kontrakt J1.2)

- `setEditable` (zámek stránky, Enter/Esc na kartě tabule) je jen změna vlastnosti pohledu: jedna aktualizace (`view.setProps`), ovládací prvky (úchyt bloku, bubliny) zůstávají připojené i bez práva zápisu a skryjí se samy, takže přepnutí neregistruje pluginy, nepřestaví pohledy pluginů ani nepřekreslí sdílený dokument z Yjs.
- `MarkdownModuleView.editable?(editable)`: blok s ním při přepnutí přepne své afordance na místě; bez něj se blok vykreslí znovu jako dosud. Grafy, diagramy a vzorce ho mají.
- Pohled jen ke čtení vzniká jen ke čtení: dříve se vykreslil upravitelný a pak přepnul `contenteditable` nad celým dokumentem (asi poloviční čas vytvoření pohledu jen ke čtení).

### Časová odrážka (Atlas 3.6)

- Nový typ seznamu `timedList`/`timedItem` (rich revize 5): odrážky, jejichž položky začínají datem a časem zápisu („3. 11. 2026 17:19 — “) jako textem, nad nímž je tlumený čip. Nová položka (Enter, `/cas`, `{ type: 'toggle-list', list: 'timed' }`) dostane razítko hned, prázdná prvním písmenem (číslicí ne); Backspace za čerstvým razítkem ho smaže. Markdown je běžný odrážkový seznam, markdown-safe ho čte jako odrážky; při otevření se odrážky začínající razítkem převedou (`legacyDocument.ts`). Revize 4 klient dostane `SCHEMA_MISMATCH`.
- Převod na místě z odrážek, číslovaného seznamu i checklistu a zpět (`toggle-list`, Ctrl+Shift+D, tlačítko mezi typy seznamů): razítko dostane každá položka seznamu i jen s kurzorem, položka začínající datem si ho nechá; jeden krok zpět.

### Spolupráce (Atlas 3.6)

- `createSignalRMarkdownAdapter({ reader: true })` (`useCollab({ reader })`): čtenář živé stránky je přítomný bez kurzoru, kurzory kolegů nekreslí a svůj nezveřejňuje. Hub u účastníka bez práva zápisu propustí v awareness jen identitu.
- Vzdálená změna nahradí v editoru jen změněný rozsah (patch `@tiptap/y-tiptap` v hostiteli), kurzory kolegů se překreslují nejvýš jednou za snímek a nemizí, když jejich pozice předběhne úpravu. Seznam spolupracovníků se publikuje jen při změně.
- Čerstvý join hydratuje bez zkoušky a back-sync. Roli snapshottera převezme první, kdo píše; vlastní kurzor se zveřejní až po první klávese nebo kliknutí.
- Hub ohlásí v joinu `projectionsDerived`, když projekce odvozuje sám; snapshotter pak posílá jen stav gzipem (`PushSnapshotGzip`). Spojení s `?seed=gzip` dostane seed gzipem (`seed`).
- Oprava: opravu tabulek po vzdálené změně hledá provider podle cesty v dokumentu, ne podle identity uzlu, a kontroluje všechny tabulky.

### Pravidla ovládání (Atlas 3.2)

- „Co s tím jde dělat“ (Ctrl+mezerník, Alt+Enter) na vybraném prvku nabídne jeho akce z lišty (Upravit, převody, smazání) a navíc Kopírovat a smazání jedním slovem: u čipu „Odebrat“ (kontakt, úkol, stránka zůstávají), u bloku „Smazat“ – u každého bloku, čipu i tabulky, bez vlastního kódu pluginu. Nové slovo slovníku `MarkdownStrings.delete` („Smazat“).
- `openMarkdownIntentionsMenu`: nabídka „Co s tím jde dělat“ i bez editoru pod kurzorem (u libovolného místa, s řádky od hostitele); tabule ji používá pro své prvky.
- „Co s tím jde dělat“ v editoru stránky otevře i pravé tlačítko, Ctrl+., Shift+F10 a klávesa nabídky jako na tabuli (Shift + pravé tlačítko nechá nabídku prohlížeče); název požadavku používá tutéž nabídku.
- Esc na vybraném prvku (po zavření jeho úpravy) zruší výběr a kurzor postaví hned za prvek – u bloků i čipů, i z ovládacích prvků komponenty a i v prohlížeči; pole uvnitř prvku si Esc vezme samo.
- Panel s „Použít“ (formulář kontaktu a modulů, data grafu) změny klikem mimo nebo odchodem fokusu použije; s neplatnou hodnotou zůstane otevřený s chybou u pole, klik se nikam nepropíše (tlačítka, nabídky ani tažení) a klávesnice zůstane u chyby (D3). Druhý klik na prvek, jeho „Upravit“ nebo čip kontaktu změněný formulář jednou použije a úpravu zavře. Totéž platí pro panely obrázku, nastavení hesla a skrytého obsahu; Ctrl+Enter je použije.
- `openMarkdownContextPopover`: nová volba `returnFocus`; formulář označený `data-md-apply` se při kliknutí mimo nebo odchodu fokusu odešle.
- Po dokončení úpravy zůstane prvek vybraný a klávesnice v editoru, nikdy na `<body>` – i po úpravě vzorce v textu a grafu a po zavření zdroje nebo dokumentace OpenAPI.
- Vybraná tabulka je jeden prvek se sdíleným obrysem výběru (ne výběr všech buněk), i z úchytu; Delete i nabídka akcí ji smažou celou a platí pro ni krok Esc.
- Heslo (karta i v textu) se upravuje na místě: klik, Enter nebo F2 otevře u odkazu jeho zobrazení, druhý klik ho zavře; „Vyměnit heslo“ je pojmenovaná akce v liště a na kartě, Enter už heslo nevymění. Heslo v textu je sdílený čip v textu (klíč a názvy polí, hodnoty pod mlhou, jediný prstenec výběru jako ostatní čipy); klepnutí na mlhu ji odkryje a nabídne kopírování i při úpravách.
- Skrytý obsah, informační box a rozbalovací blok se upravují na místě: klik na štítek či název, Enter, F2 nebo „Upravit“ v liště otevře jejich nastavení, typ nebo název na stejném místě a druhý stisk ho zavře. Šipka rozbalovacího bloku je samostatné tlačítko (i z klávesnice), které blok rozbalí a sbalí.
- Blok kódu: Enter nebo F2 na vybraném bloku otevře výběr jazyka.
- Obrázek: klik na obrázek nebo popisek, Enter nebo F2 otevře popisek a alternativní text přímo u obrázku; dvojklik obrázek zvětší, aniž by úprava napřed blikla.
- Úkol YouTrack a odkaz na stránku: klik, Enter nebo F2 otevře úpravu adresy u čipu, druhý klik ji zavře, Enter v poli adresu načte a použije; cíl otevře Ctrl+klik nebo „Otevřít“ v liště. Na sdílené stránce se úprava připraví hned, jak editor dovolí psát.
- Panel vzorce se zavře klikem mimo a text zůstane uložený.
- Ctrl+Z funguje i z tlačítka v liště komponenty a z otevřeného panelu.
- Lišty komponent mluví slovy pravidel: „Upravit“, „Převést na text“ u skrytého obsahu, „Odebrat heslo“, smazání „Smazat…“. Zdroj diagramu se zavírá „Hotovo“ a nápověda říká „Esc zavře, Ctrl+Z vrátí“. Dialog zdroje API má sdílený prstenec fokusu a české tlačítko „Vybrat soubor…“; dialogy „Upravit API“ a dokumentace API mají písmo editoru.
- Styl čipu v textu se načítá se styly editoru, ne z jádra; čip vypadá stejně v editoru, na tabulích i v galerii.
- Každá osoba ve větě (uživatel Atlasu, externí kontakt, nerozhodnutá) je tentýž čip; nerozhodnutá má zase čárkovaný rámeček a čipy si na stránce drží vlastní barvu. Zmínka kontaktu vypadá zase jako čip (bez rámečku, velká jako text), ne jako tlačítko, a iniciály nerozhodnutého kontaktu jsou čitelné.
- „Vložit úkol“ (YouTrack) a „Přidat z adresáře“ hledají za psaní, předvyberou první výsledek, Enter nebo Tab ho vezme a první Esc smaže hledání. Pole hledání má místo pro výsledky hned od otevření a řádky načítání se ukážou, až když odpověď trvá déle než 100 ms, takže panel nebliká „Načítání…“ ani neroste po skocích.
- Tab v seznamech v textu (@ lidé, /, emoji, odkazy na stránky) vloží zvýrazněný návrh stejně jako Enter a jako všechny ostatní výběry.
- Naučené zkratky z adresáře („MMu“) se v textu stránky, komentářů i tabulí jemně podtrhnou a klik, Ctrl+. nebo Ctrl+mezerník nabídne „Je to Marek Musil“; samy se na čip nikdy nepřevedou (zkratky ani iniciály). Třípísmenná zkratka platí jen tak, jak se naučila (ne „MMU“), a nikdy uvnitř e-mailu.
- Výběr osoby po „@MMu“, „Je to …“ (i u nerozhodnutého kontaktu) nebo v nabídce akcí se hlásí adresáři (`ContactDirectoryService.learn`), aby se mohl naučit napsanou zkratku; začátek jména („@Mar“) nenaučí nic.
- Seznam „@“ nechá nahoře lepší shodu; mezi stejně dobrými řadí podle kontextu bez AI: kdo už je na stránce, čí funkci nebo celé pole říkají slova před „@“ („PM @ja“), koho jste právě vybrali. Krátké slovo jako „na“ za roli nepočítá.
- Rozpoznávání jmen lidí v textu se načítá samo ve volné chvíli po prvním vykreslení editoru (nejpozději do sekundy); známí lidé se vyžádají hned se startem editoru, takže první jméno se pozná stejně rychle jako dřív a stránka se otevře rychleji. Při přestavbě editoru (změna režimu úprav, obnovené spojení) si ponechá známá jména, takže napsané jméno se změní na kontakt hned; nepovedené stažení se při další příležitosti zkusí znovu.
- Ctrl+. nad označeným jménem nabídne „Udělat kontakt“ hned, ještě než se rozpoznávání jmen načte. Seznam „Je to …?“ u nevyřešeného kontaktu bere šipky, Enter, Tab a Esc i tehdy; Enter už v té chvíli nerozdělí odstavec.
- Seznamy lidí („Je to …?“, nerozhodnutý čip, @) se otevřou hned a zpřesní se, až dorazí údaje o používání; nerozhodnutý čip, ke kterému nikoho neznáme, otevře výběr osoby.
- Na dotykových obrazovkách mají čipy, šipka rozbalovacího bloku, úchyty bloků, ovládání požadavku a odkazy karet tabule dotykovou plochu 44 px; úchyt bloku po klepnutí jde za kurzorem a nápovědy kláves jsou skryté. Úchyt bloku a jeho „+“ si klepnutí neberou, čip v textu nebere klepnutí určená řádkům nad a pod ním a klepnutí na konec údaje požadavku ho upraví místo odebrání.
- `MarkdownDocumentHistory` a `MarkdownSectionReview` jsou asynchronní komponenty (jako `MarkdownSourcePanel`): jejich kód se stáhne, až je hostitel vykreslí, a při nepovedeném stažení řeknou na místě, že se nepodařilo načíst (nové vykreslení stažení zopakuje). Úvodní JavaScript editoru je o 44 KiB menší (726 KiB proti limitu 763 KiB, gzip 231 KiB proti 239 KiB).
- Globální styl editoru už nenese druhou kopii stylů ovládacích prvků z nabídky kontroly sekcí.
- Kontraktní test psaní v dlouhém dokumentu (P17) měří úhoz se zapnutým rozpoznáváním jmen, každou odloženou úlohu po psaní (pod 50 ms) a projekci, kterou ukládání posílá na server, jako medián pěti měření (pod 100 ms); cíl krátké úlohy (50 ms) je pro projekci zapsaný jako známá mezera, dokud projekce nebude přírůstková. Kontraktní editor má zobrazení kódu jako Atlas.
- AI v nabídce „Co s tím jde dělat“ (Ctrl+mezerník) nabízí stejné záměry nad tím, na co ukazuje kurzor: výběr, vybraný prvek, nadpis i s celou sekcí, odstavec, seznam či tabulku a celou stránku. Když je sekce delší než limit 1 600 znaků, nabídne záměry AI nad samotným nadpisem; tabulka, informační box či rozbalovací blok vybraný úchytem nabízí Přepsat, Přeložit a Doplnit stejně jako s kurzorem uvnitř.
- Návrh změny od AI má tlačítka Použít / Zahodit; Zrušit zůstává jen u návrhu, který se ještě připravuje.
- Vysvětlit a Shrnout nad prvkem (vzorec, graf, diagram) pošlou jeho zdroj; tajné a skryté části se nikdy neposílají.

- **Contacts: names that are people.** `createContactsPlugin({ known, usage })`: `known({ signal })` lists the people whose full
  names the editor recognises (asked once per configuration). A typed known name becomes the `contactInline` chip at the space or
  punctuation after it (marked with `markMarkdownAutoConversion`); a name several people have becomes an unresolved contact
  (`MarkdownContact.unresolved: true`, Markdown `unresolved=true` / `unresolved: true`, dropped once `objectId` is set), which the
  render never links and which says its likely people („nejspíš … · nebo …“) ranked by the document's contacts, the words around
  the name against function/department/role (and their initials, „PM“), and `usage(objectId)` (`{ here, total }`); a click, Enter
  or the intentions rows pick one. Known names already written get a dotted underline (`md-contact-candidate`, debounced, at most
  200) that offers „Je to …“ on a click or Ctrl+.; the selection action `contacts.from-selection` („Udělat kontakt“, also Ctrl+.)
  opens `pick` with the selected name (`ContactPickRequest.editor` is new) and replaces it in one undo step. The contact plugin
  contributes intentions and registers `contactInline` as a chip (its name as text); the internal mention and the unresolved
  contact use the core chip in its mention look. An open suggestion list now takes Enter before a selected module's edit.
- **Revert contract of automatic chips:** `markMarkdownAutoConversion(tr, { from, to, text, revert? })` — `from`–`to` now covers
  the chip and the trigger character left after it, `text` is what was typed without the trigger. Backspace with the caret at `to`,
  only right after the conversion, acts as deleting the trigger: `text` comes back with the caret at its end (`revert` also undoes
  what the conversion wrote besides the text); typing the trigger again converts again. Emoji revert to `:name`, math to `$x$`.
  The one-separator grace of the previous contract is gone.
- **Inline chip primitive:** `createMarkdownChip(doc, options, element?)` → `{ element, update }` and `markdownChipSpec(options,
  attrs)` (renderHTML) build one chip markup (`md-chip md-chip--tag|mention`, slots `md-chip-media` / `md-chip-label` /
  `md-chip-detail` / `md-chip-mark`, `data-chip-state="unresolved|muted"`, selected = `ProseMirror-selectednode` or
  `data-chip-selected`) styled by `--md-chip-*` tokens (features/chips/chip.css); `markdownChipHint()` and the new `modules.chipHint`
  string name the chip keys in an editable chip's tooltip. The YouTrack issue (pixel-identical) and the page reference (now a tag
  chip, unavailable = muted) use it.
- **Intentions („Co s tím jde dělat“):** Ctrl+Space (also on macOS) or Alt+Enter opens one menu of what can be done with what is under
  the caret or selection, as the editor's suggestion list (first row preselected, arrows, Enter, Esc, typing filters). Plugins
  contribute rows with `MarkdownPluginInstance.intentions` (`MarkdownIntentionProvider`, sync or async), other code with
  `registerMarkdownIntentions(editor, provider)`; `openMarkdownIntentions(editor, { filter, aiFirst })` opens it. Core adds
  „Převést na text“ for a chip, pages a page link from selected words, youtrack an issue from a written key, emoji an emoji from
  `:name`. **Assist:** Alt+Enter is now this menu; the module contributes its block suggestions (first on Alt+Enter, last on
  Ctrl+Space) and the host's selection actions while `available`; the block's own list (reasons, Delete, „Nenabízet“) opens at its
  marker. Both keys are in `markdownKeyboardShortcuts`; new `MarkdownStrings` section `intentions`, keys `pages.linkSelection`,
  `youtrack.issueFromText`, `emoji.fromText`.
- **Chips back to text, one behaviour:** `registerMarkdownChipText(nodeName, node => text)` says what an inline chip reads as;
  `convertMarkdownChipToText(editor, pos)` turns it into that text (one undo step, no attributes left), Ctrl+Shift+Space does it
  on a selected chip or the one right before the caret (listed in `markdownKeyboardShortcuts`), and inline block modules and
  references get „Převést na text (Ctrl+Shift+Mezerník)“ in their toolbar. `markMarkdownAutoConversion(tr, { from, to, text })`
  marks an automatic conversion: it is its own undo step (Ctrl+Z undoes it, not the typing) and Backspace right after it (also
  after one separator) gives `text` back. Registered: `youtrackIssue` (its key), `pageReference` (its title), `emoji` (its
  character), `mathInline` (`$…$`); the `$x$ ` and `:name:` input rules mark their conversion. New `MarkdownStrings` key
  `modules.chipToTextLabel`.
- The `toc` block is edited in place: the title is a field (click or Tab+Enter; Enter/blur saves, Esc cancels, empty restores the default) and the heading range a chip with a small menu that applies at once. The edit form and its strings (`editTitle`, `nameLabel`, `fromLevelLabel`, `toLevelLabel`) are replaced by `titleLabel`, `rangeLabel` and `allHeadings`; node data and Markdown are unchanged.

### Breaking

- **Asistence (`./assist`): poznámka po „Upravit před použitím“ zmizela** i s `MarkdownAssistStrings.edited` a stylem
  `.md-assist-edited`; použití potvrzuje hostitel. Hostitel, který přepisoval `strings.edited`, ho musí odebrat.
- **Rich collaboration schema revision 4:** inline `code` combines with every other mark (`**`x`**`, `*`x`*`, `[`x`](url)`)
  and ranks last, so Markdown writes it innermost. A revision 3 client gets `SCHEMA_MISMATCH` (it would delete such a
  paragraph); hosts append a revision 4 entry like revision 3. `MarkdownDocumentSchema.version` is `1 | 2 | 3 | 4`. Mark
  paste rules skip code text themselves (they used to rely on the exclusion).
- **`searchEmoji` is async:** the emoji plugin draws from its own name table and loads the 600 KB upstream catalog only for
  the first search (picker or `:`), so a document no longer loads it.

### Added

- **Blocks are edited where they are shown:** in an editable editor a click on a block module's rendered content, or
  Enter/F2 while it is the selected node, runs its runtime's `edit` with the block element as `anchor` (a second click
  closes the popover). A click the runtime handles itself must call `preventDefault()`; links, buttons, fields and
  `[data-md-no-select]` never trigger it. `createMarkdownContextToolbar({ onActivate })` carries the action;
  editable blocks get the class `md-module-editable` (pointer and hover background on `--md-motion-fast`).
- **Math edited in place:** the math runtime's `edit` opens a live LaTeX field (under the block, in the anchored popover for the inline
  formula) instead of a Save form, keeps the last good render on a parse error, and the plugin adds the `$x$ ` and `$$` + Enter
  input rules (not in code or links, not for prices; Backspace gives the text back).
- **Chart edited in place:** the title edits where it is shown (Enter/blur saves, Esc cancels), the type is a chip on the chart,
  a click on the data table opens the data editor (first cell focused, Ctrl+Enter applies) and a linked chart's
  **Upravit data** moves the caret into its source table. Node data and Markdown are unchanged.
- **Secrets: swap and chips in place.** `createSecretsPlugin({ pickReplacement })` adds „Vyměnit heslo“ (card chip, toolbar,
  intentions menu, inline too; Enter on a selected reference opens its edit, not the swap): the host picker answers another secret id and only `secretId` changes, as one
  undo step, keeping the display settings. A card in edit mode shows its fields and automatic hiding as toggle chips; the
  popover keeps the rest. New `MarkdownStrings` keys `secrets.swapLabel`, `swapButtonText`, `cardEditLabel`, `autoHideChip`,
  `moreSettingsText`.
- **Compressed join snapshot:** the SignalR transport asks with `?snapshot=gzip`; a hub that answers
  `snapshotEncoding: 'gzip'` sends the join snapshot gzip-compressed and the provider inflates it before the usual validation.
- **Contact `objectId`:** a contact snapshot may carry the host directory object it came from (`objectId`, lower-case GUID; an invalid
  value is dropped). Markdown: `objectId=<guid>` in the `a-contact:?…` link, `objectId:` in the fence. Old documents are unchanged.
- **Slash arguments for blocks:** `/box tip`, `/graf koláč`, `/diagram sekvence`. A block module takes
  `parameter: { label, data(text), title(text) }`; `MarkdownActionParameter.command` may return nothing (no row) and takes
  an optional `title(text)`. `pickNamedChoice` (core) picks a choice by a typed prefix, ignoring diacritics. Block modules
  take a `description` for their slash row, and every built-in block now has one.
- **Keys:** Ctrl+Shift+0…6 set text and headings (a Czech keyboard never sends Ctrl+Alt+digit, which is its AltGr).
  The block keys move, duplicate and delete a selected component too. Escape on a selected component puts the caret after it.
  Escape, then Tab, leaves the editor from lists, tables and code blocks as well. A component panel keeps Tab inside.

### Fixed

- Atlas 3.6: the block grip's keyboard path (Alt+F10, then Tab) survives a redraw of its controls: after Space on a requirement's
  check redraws the heading, Tab, Shift+Tab and Esc go on along the path from the redrawn control. Esc used to select the whole
  heading instead of giving the caret back to its title, and the arrows after it could snap the caret back.
- Atlas 3.4: the content repair reads Atlas's `requirementSummary` block as the page overview `overview` with `kind: "requirements"`
  (id kept), in `engine/legacyDocument.ts` and Atlas.Markdown.Sync's `LegacyDocumentRepair` alike.
- Documents Atlas 2.x stored open instead of failing: a mark repeated on one text (a link in a link) keeps its innermost
  copy, and an empty list or task item, or one starting with another block, gets an empty leading paragraph
  (`engine/legacyDocument.ts`, on `normalize` and on the collaboration seed; Atlas.Markdown.Sync's `LegacyDocumentRepair`).
- The Monaco chunks wrote `{type:`module`}` into the worker options, which a host's Vite rejects (`vite build` failed).
- The detail workspace returns focus to the document when its opener is gone, and the selection toolbar stays over the
  editor instead of the page's sidebar.

- **Atlas.Markdown.Sync: `YjsDocumentMaterializer`** reconstructs in managed C# (no JavaScript or native code) the
  document a collaborative Y.Doc holds: `Materialize(state, updates)` returns exactly `JSON.stringify` of the editor's
  `getJSON()` for that Y.Doc, byte for byte, and throws `YjsMaterializationException` (a `ContentRejectionReason`) for
  anything the editor cannot show or never writes instead of returning part of it. A conformance corpus recorded from
  the editor (133 cases, 921 steps: the editor's fixtures, Unicode, marks, concurrent editors, GC, the Yjs boundary
  corpus and a 4 MiB document) keeps it equal to y-tiptap; a 4 MiB document takes 0.2–0.35 s, a 1 MiB one 20–35 ms
  ([document materialization](../../docs/markdown/document-materialization.md)).

## 0.18.0 — 2026-10-26

### Breaking changes

- **The `a-` dialect helpers moved behind `loadMarkdownDialect()`.** `@atlas/markdown/core` no longer exports
  `writeMarkdownDialectYaml`, `parseMarkdownDialectYaml`, `writeMarkdownDialectLink`, `parseMarkdownDialectLink`,
  `markdownDialectFence`, `markdownDialectLabel`, `reportMarkdownDialectWarning` and `isChartHeaderText`; they are
  members of the module that `await loadMarkdownDialect()` returns and `markdownDialect()` gives once it has loaded.
  A block module's `a-` component uses the new `dialect: { tag, lossy, write, read }` option instead of `markdown`
  and `parseHTML`, with its form registered by `registerMarkdownDialectForm`.
- **Before the dialect has loaded, an `a-` component cannot be read or written.** Editor controllers and source
  sessions start loading it; a parse that meets an `a-` fence or link, or a serialization of a contact or chart, in
  the short time before it resolves throws instead of keeping the component as code or text. Markdown without
  components is unaffected. Hosts that parse or serialize without an editor await `loadMarkdownDialect()` first.

### Changed

- **The Markdown dialect loads on demand.** The a-YAML reader and writer, the `a-` parser rules and the contact and
  chart forms (14.4 + 3.0 + 4.0 KiB raw) left the page's eager bundle: the rich-profile eager JavaScript drops from
  636.0/199.5 KiB to 618.0/193.7 KiB raw/gzip (614/192.1 KiB before the dialect). The lossiness checks stay synchronous
  and load nothing.

### Fixed

- **`MarkdownDetailWorkspace` fills the window.** An open detail (API reference, YAML, page source) reaches down to
  the bottom of the visible area below its header, bounded by a scrolling host pane when there is one, and follows
  window resizes. It no longer takes the document's height capped at 420–800 px (on phones 420 px–75dvh–640 px),
  which left a long reference ending above blank space.

## 0.17.0 — 2026-10-16

### Breaking changes

- **Markdown output: contacts and charts are written in the editor dialect.** The Markdown projection of a contact
  list is a ```` ```a-contact ```` fence (a-YAML sequence), an inline contact is `[Name](a-contact:?name=…&phone=…)`
  and a chart is a ```` ```a-chart ```` fence (header, `---`, GFM grid) instead of their readable text lines; a table a
  chart links to gets a `{#id}` paragraph after it. Such documents are no longer `markdownLossy`, and the page source
  view no longer protects contacts and unlinked charts. A chart whose link the dialect cannot carry (missing or
  non-GFM table, cells that are no longer one rectangle) keeps the old text and stays lossy.
- **The `a-` namespace is reserved.** A code block whose language starts with `a-` is written as
  ```` ```a-code <language> ```` (read back as that language). An `[…](a-<name>:…)` link that is not a known component
  now reads as its literal source text, and an unknown ```` ```a-<name> ```` fence stays a code block.

### Added

- **Markdown dialect for `a-contact` and `a-chart`, read on every Markdown path** (value load, paste, import, page
  source) where the schema has the node; validation is the plugins' own (`normalizeContacts`, `normalizeChart`). With
  a `source`, the chart takes its values from the linked table (identified by `{#id}`, with new cell ids). a-YAML
  typing is schema-directed: a plain value of a text field (contact fields, phones, e-mails, chart `title` and
  `source.table`) is read exactly as written (`+420777123456`, `2026`, `1.50`); `kind` and the row and column ranges
  keep strict typing.
- **`parseMarkdownDialect(editor, markdown, { strict })`** (core): strict mode returns every unknown or invalid `a-`
  component as an error with line, column and reason, and no document; lenient mode keeps them as code or text and
  returns warnings. A chart grid that differs from its linked table is a warning in both. Plugins get the helpers
  `writeMarkdownDialectYaml`/`parseMarkdownDialectYaml`, `writeMarkdownDialectLink`/`parseMarkdownDialectLink`,
  `markdownDialectFence` and `chartSourceRectangle`; a block module's `markdown(data, document)` now receives the
  document being written.

### Fixed

- **Collaboration: concurrent table rows and columns no longer multiply cells.** When one collaborator added a row while
  another added or deleted a column, every client repaired the merged table in an append to the remote change, which
  y-prosemirror never writes to Yjs; each client then wrote its own repair with its next edit, the repairs added the
  same cells several times, and each merge broke the table again until the server refused it (`table_too_large`) and
  the writers' sessions ended. Table repairs (prosemirror-tables' `fixTables`, the markdown-safe header and alignment
  normalization) now skip remote changes; the new `CollaborationTableRepair` (installed by `useCollab`) has exactly one
  collaborator, the author of the row the merge left short or long, repair the table as a local change of its own.
  The markdown-safe round-trip guard no longer refuses a remote change (it left the editor behind Yjs, and its next
  edit reverted the merge) and keeps accepting edits of a document a merge left lossy.
- Typing at the same time as other writers no longer loses characters. When a collaborator's change arrived before the
  server's acknowledgement of the writer's own change (sequence 24 before the ACK of 23, routine with concurrent
  writers), the V1 provider took the gap for a lost delivery and joined the document afresh; for that round trip the
  editor was readonly and dropped every key typed meanwhile (90–210 ms per join at an 80 ms round trip). The provider
  now holds updates received past a gap and applies them in sequence order once it closes; only a gap still open after
  3 s starts a fresh join.

## 0.16.0

The editor owns the rules of a valid document and `Atlas.Markdown.Sync` enforces them on every channel: stored rich
documents and every collaborative payload ([document validation](../../docs/markdown/document-validation.md)).

### Breaking changes

- **Atlas.Markdown.Sync: the collaboration content guard validates values and documents.** A host that registered
  `AddAtlasMarkdownContentGuard()` relied on an allowlist of names and attribute keys; the guard now also refuses
  every attribute value the editor would refuse (`ContentRejectionReason.InvalidAttributeValue`) and every update or
  snapshot whose resulting document the editor would not open (`InvalidDocument`: child types, marks, required
  attributes, plugin requirements of the bound schema, table bounds, depth). Clients see the usual
  `[INVALID_CONTENT]` and nothing is stored or broadcast. Documents that already hold such content can still be edited,
  but a change to an invalid element is refused until the element is valid, and a snapshot of an invalid document is
  refused. Secret ids that are not UUIDs are refused before an `IReservedNodeGuard` is asked. The opaque relay without
  the guard is unchanged. Collaborative content is not held to the rules that merging concurrent valid edits breaks,
  since refusing such an update would stop a user's session: marks that exclude each other, the order and count of
  children, table geometry, and a column width against the colspan
  ([merges of concurrent edits](../../docs/markdown/collaboration-content-guard.md#what-is-checked)).
- **Atlas.Markdown.Sync: `YjsContentPolicy.FromManifest` and `yjs-content-manifest.json` are gone.**
  `YjsContentPolicy.CreateDefault()` derives the policy from the editor's schema manifest; a host with other plugins
  uses `YjsContentPolicy.FromValidator(validator)`. The constructor with name lists remains as a bare allowlist.
- `ContentRejectionReason` gains `InvalidAttributeValue`, `InvalidDocument` and `TooManyItems` (appended; existing values keep their
  numbers).
- The `source.headingLevelUnsupported` string is removed: the page source no longer warns about `#####` and `######`.

### Added

- **`describeMarkdownSchema(plugins, options)`** (`@atlas/markdown/schema`, an entry of its own that editors never load): the document schema of an editor
  configuration as JSON, without a view: nodes and marks, attributes with defaults and value constraints, content
  expressions, groups, allowed and excluded marks, plugin ownership, plugin versions and the versions the editor
  migrates, the structured validators with JSON-schema-like descriptions.
- **`defineMarkdownAttributeValidator(constraint, validate)`**: an attribute declares its value rule (enum, integer or
  number range, text length/single-line/pattern, URL policy, named structured validator) next to its validator. Every
  built-in attribute declares one; Tiptap's own attributes are classified in the editor. `defineMarkdownBlockModule`
  takes `dataValidator`, the name of the module data validator a server implements.
- **`@atlas/markdown/schema.json`**: the manifest of every built-in plugin (`packages/markdown/schema`), also embedded
  in `Atlas.Markdown.Sync`.
- **Atlas.Markdown.Sync: `MarkdownDocumentValidator`** validates a `atlas-rich` envelope against a
  `MarkdownSchemaManifest` (`Builtin`, `WithPlugins(ids)`, or a host export with `Parse`) and returns the first violation
  with a JSON path, a stable code (`MarkdownDocumentViolationCodes`) and a message; bounded (64 node levels, JSON depth
  2 × 64 + 8, non-backtracking patterns, table bounds) and never throwing on invalid input. It agrees with the editor on
  a generated verdict corpus of 1,481 cases, apart from the deliberately stricter server rules (heading levels 1–6, JSON
  types of attributes the editor stores unvalidated, no `javascript:`/`vbscript:`/`data:` links) and the OpenAPI YAML
  parse it does not repeat.
- Heading levels 5 and 6. `#####` and `######` stay H5 and H6 on every path (value load, paste, `.md` drop, page source, typing `##### `) instead of becoming H4, and serialize back unchanged. The block-type menu and the slash menu offer Nadpis 5 and Nadpis 6 (`set-heading` accepts levels 1–6; new `slash.heading5Title`/`heading5Description`/`heading6Title`/`heading6Description` and `toolbar.heading5Label`/`heading6Label` strings, `Heading5Icon`/`Heading6Icon`). Editor, history and static renderer styles have their own H5/H6 sizes (grouped with H4); the global stylesheet's budget grows by the measured 0.5 KiB raw and about 40 bytes gzip.

### Fixed

- A paste the server rules refuse is refused by the editor's paste guard with the usual input error, instead of ending
  a collaborative session with `[INVALID_CONTENT]`: lists nested deeper than the 64 node levels, `<ol start>` beyond a
  safe integer, `<ol type>` over 100 characters, and a fence language over 200 characters or with control characters.
- Tab, list indent and wrap commands no longer nest content past the 64 node levels a server accepts; the command does
  nothing there. Remote collaborative changes are never filtered.
- Receiving a remote change no longer throws "Position N out of range" and ends the collaborative session when the
  caret sat beyond the end of the shortened document: @tiptap/y-tiptap 3.0.9 (the latest release) checks the restored
  selection against positions taken before the first Yjs transaction of a batch, also in later ones (Yjs' formatting
  cleanup, y-tiptap deleting a node it cannot build). `useCollab` adds `CollaborationRemoteSelection`, which keeps those
  positions in range; 8 of 100 concurrent fuzz sessions hit it before.
- A collaborative session the server ends with `[INVALID_CONTENT]` records a last recovery checkpoint (when a recovery
  branch is attached) holding every unsent edit, including valid ones batched with the refused update, and reports
  `collaborationStrings.contentRefused` instead of the raw server message.
- A stored H5 or H6 heading (already valid in the rich schema) rendered as `<h1>` in the editor and the static renderer, because only levels 1–4 were configured. It renders as `<h5>`/`<h6>` now and stays in the outline, table of contents and heading anchors at its level.
- The rich collaboration schema stays revision 3: the heading `level` attribute already held any value, and a revision 3 client keeps an H5/H6 node (it only drew it as H1), so no client deletes content it cannot represent.

### Changed

- Local untagged builds of `Atlas.Markdown.Sync` report at least 0.16 (`MinVerMinimumMajorMinor`); releases keep
  passing the npm version.

## 0.15.0

### Changed

- The block grip and its side "+" form one column in the editor's left gutter: the grip beside the block's first line, the "+" directly under it. The column fits the 32/40px gutter, the narrow document keeps a 32px gutter while editing, and the controls never reach past the editor's left edge, even when a host trims the padding. The grip precedes the "+" in focus order.
- The slash menu shows a small "Vložit" heading, each command's icon on a quiet tile and a bolder title (new optional `heading` of the shared suggestion list and its renderer, `slash.menuHeading` string). People, page and emoji suggestions keep their plain rows.
- The global stylesheet's raw budget grows by the measured 0.2 KiB of these two changes (gzip unchanged).

## 0.14.0

### Fixed

- Outline rail targets remain clickable across their full animated width, jump to the selected heading on the first click, and dismiss with Escape.
- Outline entries wrap long headings, retain the active heading colour, and keep the preview panel clear of the rail.
- Added regression coverage that reopening source editing preserves a dirty draft and its conflict state after the live document changes.

## 0.13.0

### Breaking changes

- Source sessions use the host-neutral md-protected fence language. The protected-block reason is now reserved-code, and the localized string key is protectedLanguageReserved. Source projections are session-local; stored document JSON is unchanged.

### Added

- Assist selection projections, asynchronous host proposals, live availability guards and informational inline results for host applications.
- Public block marker lookup and Markdown content parsing used by host writing actions.

### Fixed

- **Assist: an applied suggestion leaves its block's list with its alternatives.** A write that keeps the block's text
  (a link mark over the words, a card inserted after the block) left the block's entry, its marker and the applied
  row in place, so the marker offered what was just done. Now the applied suggestion and every suggestion whose range
  overlaps it leave the list; the block's other suggestions stay, and nothing is dismissed for the session.
## 0.11.0

### Added

- Unified inline reference providers through the additive MarkdownReferenceProvider contract. YouTrack and internal pages share URL paste conversion, an explicit metadata preview and confirmation, and Open, Copy, Edit address and Convert to link actions. Hosts supply URL recognition and authorized resolvers.
- Standalone plugin gallery with both real providers, local fixtures, slow/error states and readonly examples.

### Changed

- YouTrack search uses the shared anchored panel, fields, controls and motion tokens, with a standalone dialog fallback. Cancellation, keyboard focus and stale results share editor lifecycle rules.
- Reference controls and the YouTrack picker load on demand. URL paste remains immediate; cancelled or stale asynchronous work cannot change the document or reopen the UI. Existing document schemas and Markdown fallbacks are unchanged.

## 0.10.0

### Changed

- **Reduced motion no longer switches motion off.** The global kill switch (`styles/reduced-motion.css`, every
  transition and animation in `.md-editor` cut to 0.01 ms) is gone. Under `prefers-reduced-motion: reduce` the motion
  tokens take reduced values (at most 160 ms, `linear`, no travel, no pop scale, loops 0 s), so menus, drop guides,
  success states and file previews fade instead of jumping; inside `.md-editor` and `.md-outline-layout` transitions
  keep only opacity and colour, linear, so drag positions never show an in-between frame. Hosts that relied on
  `transition-duration`/`animation-name: none` under reduced motion now see short fades.
- **Motion defaults follow the owner's prototype.** `--md-motion-fast` is 120 ms (was 100 ms); the token rules sit on
  `:where(:root)` (zero specificity, was `:root`). Fallbacks such as `var(--md-motion-fast, 100ms)` are gone: the
  tokens are always defined by `style.css`. The drop guide, drag card, selection frame, inserter and block fades use
  `base`/`target` (selection frame 200 ms, was 240 ms), the success pop `--md-motion-pop`, the collaboration spinner
  `--md-motion-spin` 800 ms (was 1.5 s), the connection sweep `--md-motion-pulse` 1 s (was 1.4 s).
- **Every remaining motion literal became a token.** Menus, tables, pickers, the outline (anchored panel, wave,
  heading wash), page detail workspace, review modes, section review, charts and the concealed block settings read
  `--md-motion-*`; the afterglow of a jumped-to heading is `--md-motion-glow` (1.6 s, was 2.1 s) and the document
  margin and panel of the outline use `slow` (280 ms, was 360 ms). The page detail workspace, review modes and
  section review no longer animate only under `no-preference`: under reduced motion they fade (the last tab fades
  into the document, no ring) instead of snapping. The timers that mirror their CSS (detail close, outline highlight)
  derive from the tokens, so a host's slowdown applies. Heading-anchor copy feedback holds 1.6 s (was 1.8 s).
- **Invisible ink and secrets use the `--md-conceal-*` group** (see Added) instead of fifteen literal timings: border,
  curtain, mask roll and pending sign use `-roll` 2.8 s (were 2.2-3.2 s), the secret card unfolds in `-unfold` 900 ms
  (was 560-900 ms), actions, icons and request messages `-enter` 520 ms (were 360-680 ms). Under reduced motion the
  ink and vault entries fade in at most 160 ms, linear, instead of 200/520 ms `ease`.
- **`--md-assist-time-scale` multiplies only fixed literals.** Token durations of the assist module are used as the
  host resolved them; a host that slows motion down passes slowed tokens (otherwise durations were slowed twice).
  The `--md-assist-*` duration and curve tokens default to the core `--md-motion-*` tokens (`-menu` to
  `--md-motion-target`); their values are unchanged and the assist names still override.

### Fixed

- `CollaborationStatus` used the tokens `--dur-fast` and `--ease-out`, which exist nowhere; it follows the motion tokens.
- The `toc` plugin scrolled smoothly to a heading even under reduced motion; it follows `markdownScrollBehavior`.
- Blocks inserted by a file drop fade in under reduced motion (at most 160 ms) instead of appearing without any transition.

### Added

- **`--md-conceal-*` tokens** in `styles/motion.css` for the invisible ink and the secrets vault (README „Motion
  tokens“), and `markdownConcealMotion`, `markdownConcealDefaults`, `MARKDOWN_CONCEAL_EASE` and
  `MARKDOWN_CONCEAL_EASE_ROLL` in `/core` to read them in Web Animations.
- **Motion literal guard:** the architecture check fails on a literal duration or curve in any stylesheet outside
  `styles/motion.css` except a short, justified allowlist that may only shrink.
- **Motion tokens** in `styles/motion.css`, the one source of the editor's motion: `--md-motion-base` 200 ms, `-slow`
  280 ms, `-pop` 320 ms, `-glow` 1600 ms, `-loop` 1600 ms, `-spin` 800 ms, `-pulse` 1000 ms, `-ease-io`, `-rise` 4px,
  `-pop-from` .8, `-time-scale` 1 and the alias `--md-motion-surface` (= `-target`), next to `fast`, `menu`, `target`
  and `ease`. README „Motion tokens“ documents the host remap contract (map once to the host's tokens, which carry their
  own reduced path; loops 0 s).
- **`data-md-motion="reduced"`** on an ancestor forces the reduced path for a subtree (previews, a profile setting).
- **Shared motion helper in `/core`:** `markdownMotion`, `animateMarkdownMotion` (the prototype's `anim()`),
  `markdownScrollBehavior`, `isMarkdownReducedMotion`, `markdownMotionFinished`, `motionMilliseconds`,
  `markdownMotionDefaults`, `markdownMotionTokens`, `MARKDOWN_MOTION_EASE`, `MARKDOWN_MOTION_EASE_IN_OUT`,
  `REDUCED_MOTION_MAX_MS`. The assist module, block and table drag and file drop read their timing through it.
- `copyMarkdownOverlayTheme` copies every motion token, but only when the editor's value differs from the
  document's, so body-level overlays keep following reduced motion.

## 0.9.1

### Fixed

- Entering editing on a large collaborative document: the editor's menus and block handle register their
  ProseMirror plugins while the host wants editing, before the shared document is hydrated. Each late
  registration recreated the plugin views, and y-tiptap re-rendered the whole document from Yjs for every one
  (four extra full renders once the session became editable, and again on every reconnect).
- Table controls measure every table before writing any rail and look a table's position up only for a cell
  selection. They laid the document out once per table and walked the document for every rail chip on each
  selection change. A 1 MB page with tables became editable in 6.4 s instead of 12 s (profiled).

## 0.9.0

### Added

- **Slash search ignores diacritics and ranks.** `/zminka` finds "Zmínka". Results are ordered: title prefix, word
  start, keyword, substring; ties keep list order. An empty query is unchanged. Exports `foldSearchText`,
  `rankSuggestion` and `rankSuggestions` from `/core`.
- **`keywords` for block modules.** `defineMarkdownBlockModule({ keywords })` sets the slash and toolbar action's
  search terms. The built-in contacts, charts, diagrams, math, table of contents, attachments and OpenAPI modules and
  the YouTrack action carry Czech and English terms.
- **Slash parameter.** `MarkdownAction.parameter: { label, command(text) }` (and `SlashCommandItem.parameter`): after the
  action's label or a keyword and a space, `/kontakt Petr` offers "{label}: vyhledat „Petr“" and runs `command('Petr')`.
  Without a parameter a space closes the menu as before. The pages plugin (`/stránka vpn` starts `[[vpn`) and the
  YouTrack plugin (`/úkol 123`, new `YouTrackPickerContext.initialQuery`) use it.
- **`MarkdownPageReference.detail`.** An optional display-only label of a `[[` search result (a space name,
  "Koncept"), shown instead of the URL. `normalizePageReference` keeps a valid one; it is never written to the node.
- **Block keyboard hook.** `registerMarkdownBlockKeyboardHook(controller | editor, { id, controls(block) })` lets a
  plugin put its own controls into the block grip's Alt+F10 → Tab path, with Shift+Tab and Escape handled.
- **Optional assist module `@atlas/markdown/assist`** with its own stylesheet `@atlas/markdown/assist/style.css`.
  `createAssistPlugin({ sources })` offers block suggestions: one marker at the block (outside the document), the list
  on Alt+Enter through the shared suggestion list (Enter applies as one undo step, Tab and Escape never apply), a
  persistent warning marker and bar for risks such as a password in the text, and decoration-only effects.
  `openMarkdownProposalPanel` is an anchored before/after preview with Použít / Upravit před použitím / Zrušit. Motion
  follows the owner's prototype on `--md-assist-*` tokens, with a reduced-motion path. The core, `./vue`, the root
  entry and the plugins never import the module: without registration nothing changes, and the `richEager` and
  `globalCss` budgets are unchanged. No document schema change. See `docs/assist.md`.

## 0.8.0

### Added

- **Contact username.** `MarkdownContact.username` stores a single-line login or account name (at most 256
  characters). The contact form edits it, the card and the reader panel show it labelled, and a directory result
  that carries it refreshes it. The vCard export leaves it out: vCard 4.0 has no portable property for it.

## 0.7.3

### Fixed

- **Mouse wheel over a wide table.** A table wider than the document column scrolls sideways inside its own
  wrapper; the wrapper's `overscroll-behavior: contain` also swallowed the vertical wheel, so the page did not scroll
  while the pointer was over the table. Only the horizontal axis is contained now.
- **Table column chip delete target.** Hovering a column chip no longer shows the column inserter over the chip's
  delete target.
- **Attachment download in Firefox.** One click on an attachment's download link downloads it; the mouse down no
  longer moves the caret to the document start and scrolls away.
- **Node authority worker retirement (`Atlas.Markdown.Sync`).** A worker's admission slot reopens only after the
  process is really gone, and a worker exiting during its memory sample no longer throws.

## 0.7.2

### Fixed

- **Quote Markdown with a later paragraph that starts with a mark.** The 0.7.1 fix covered a callout's first
  paragraph only: any later paragraph of a quote or callout that started with bold, italic, strike or highlight still
  lost its empty `>` separator line and got corrupted delimiters (`> ****Bold**`). The paragraph serializer now writes
  the pending block prefix before a leading mark, for every block. The paragraph node is the editor's own
  (`MarkdownParagraph`, same schema, command and shortcut as Tiptap's); StarterKit's paragraph is disabled.

## 0.7.1

### Fixed

- **Callout Markdown with a leading mark.** A callout whose first paragraph starts with bold, italic, strike or
  highlight (such as an imported alert title) lost its `> ` prefix and got corrupted delimiters in the Markdown
  projection (`> [!WARNING]\n****Budget risk**`). It now writes `> [!WARNING]\n> **Budget risk**`, so page source,
  search text and exports read the callout back unchanged. The rich document was never affected.

## 0.7.0

### Breaking

- **Rich collaboration schema revision 3** (heading level 4 and the `caution` callout variant; see
  docs/collaboration-schema.md). Rich controllers declare `version: 3`; the TypeScript and .NET
  (`Atlas.Markdown.Sync`) normalizers accept revisions 1–3, and 2 and 3 do not join each other. **Hosts must accept
  revision 3 in their schema list and upgrade bound revision 2 documents like a plugin addition** (Atlas: append
  `new MarkdownDocumentSchema(3, "rich", plugins)` to `KnownHostSchemas`), and deploy the server with Sync 0.7.0
  together with the clients; otherwise existing collaborative documents answer `SCHEMA_MISMATCH`. Revision 2 clients
  can then no longer join, so they cannot delete caution callouts they do not know. The reference `FileDocumentStore`
  serves rich revision 2 documents as revision 3. `MarkdownDocumentSchema.version` is now `1 | 2 | 3`. The rebuilt
  `dist-authority` bundle declares revision 3 as well.
- New required `MarkdownStrings` keys: `secrets.requestHint`, `secrets.refused`, `secrets.requestFailed` (the visible
  access request), `slash.heading4Title`, `slash.heading4Description`, `toolbar.heading4Label`, `blocks.calloutCaution`
  and `input.calloutCaution`. Hosts that build a
  complete `MarkdownStrings` object must add them; overriding single keys through `setMarkdownStrings` is unaffected.
  The default `secrets.pending` text is now „Žádost odeslána · čeká na schválení“.
- **Markdown projections and the page source write the editor dialect** (below): a callout is now a GitHub alert
  (`> [!NOTE]` …) instead of a bold label and text, highlight is `==text==`, a diagram without caption a
  ```` ```mermaid ```` fence and math `$$ … $$` / `$…$`. Such documents no longer report `markdownLossy: true`. Hosts
  that store or publish `getProjection().markdown` see the new text.

### Changed

- **Requesting access to a secret reference is visible.** A reader without access sees „Hodnoty jsou skryté“ and a
  **Požádat o přístup** button with a lock on the ink at rest, on cards and inline references; the invisible 850 ms
  hold is gone for secrets (ordinary requestable text and images keep it). Tab reaches the button directly (the
  surface is no longer a tab stop) and Enter/Space sends it; the reason panel is unchanged. Sending, pending, not
  approved and a failed send replace the message in place with soft fades (also under reduced motion); an approved
  request still unlocks in place after `refresh`, now fading into its new state.
- **Heading 4.** `####` is H4 everywhere: schema levels 1–4 (`MAX_HEADING_LEVEL = 4`), slash item and block type
  menu „Nadpis 4“, Mod-Alt-4, the `set-heading` command, outline, table of contents, heading anchors, editor, reader
  and history styles. `#####` and `######` become H4 (the page source warns). Part of rich revision 3 (above).
- **Editor Markdown dialect.** GitHub alerts, `==highlight==`, ```` ```mermaid ```` and `$$`/```` ```math ````/`$`
  are parsed on every Markdown path (value load, `insertContent`, the page source and paste/drop) wherever the
  schema has the callout, highlight, diagram or math node, and written back the same way, so the page source applies
  them like paste and shows them again as Markdown instead of protected blocks. Alerts map NOTE→note, TIP→tip,
  IMPORTANT→important, WARNING→warning and CAUTION→caution, so all five round-trip unchanged; plain quotes stay
  quotes. `==` is ignored in code, link destinations and URLs. The markdown-safe profile is unchanged. Existing
  ```` ```mermaid ```` / ```` ```math ```` code blocks now appear as protected blocks in the page source.

- **Callouts.** A fifth variant `caution` („Pozor“, octagon icon, the theme's red) for `[!CAUTION]`, Obsidian
  `danger`/`error`/`bug`/`failure` and Wiki.js `is-danger` (these were `important`). „Důležité“ now has its own icon
  (a speech bubble with an exclamation mark) and a violet tone, so the two stay distinct. Read mode
  (`MarkdownRenderer`) shows the same header with icon and label as the editor; copied HTML is unchanged.
- **Diagrams** render at their natural size, never wider than the column, in read and edit mode (a small flow no
  longer stretches to the full width).

### Added

- Secrets: the object form of `SecretDiscovery` takes an optional `requestRefused` (the reader's last request was
  rejected, cancelled or expired). Without it the reference also shows „Žádost nebyla schválena“ when a request it
  showed as pending is found settled without access on `refresh`. `SecretsLabels` gains `requestHint`, `refused` and
  `requestFailed`.
- Concealment: `ConcealedContentOptions.requestRefused`, the labels `requestHint`, `refused` and `requestFailed`, and
  `data-concealed-request-state="idle|sending|pending|refused|failed"` on requestable secret surfaces.
- `defineMarkdownBlockModule` option `markdown?(data): string | undefined`: a module's lossless Markdown source; the
  block is Markdown-lossy only where it returns `undefined`. The icon registry gains `heading-4` and
  `message-square-warning`; `insert-callout` accepts `variant: 'caution'`.

## 0.6.1

### Changed

- **Smoother invisible ink.** The ink draws every frame for 10 s after an interaction and then eases to 30fps instead
  of dropping to about 8fps after 1.5 s; reduced motion twinkles at 30fps instead of about 11fps. Slower devices, such
  as older phones, keep the frame rate and draw fewer specks: the ink starts sparse and fades more specks in while its
  measured drawing time allows. Unattended ink fades out half of its specks and brings them back on the next
  interaction.
- **Invisible ink scales to whole pages and hundreds of surfaces.** A surface larger than the viewport, such as a
  concealed page, draws a sharp window around the viewport instead of one blurred canvas over the whole page. Pages with
  hundreds of concealed blocks or secrets keep canvas pixels only near the viewport, create the reveal mask only once
  something is uncovered, and animate the colour clouds only on touched surfaces; on a slow phone this doubles the
  glitter's frame rate and cuts canvas memory about tenfold.

## 0.6.0

### Breaking

- `MarkdownStrings.secrets` has new required keys for the request panel and the Markdown diagnostic
  (`requestReasonTitle`, `requestReasonLabel`, `requestReasonOptionalLabel`, `requestReasonPlaceholder`,
  `requestReasonSubmit`, `requestReasonRequiredError`, `requestReasonTooLongError`, `markdownInvalidReference`). Hosts
  that build a complete `MarkdownStrings` object must add them; overriding single keys through `setMarkdownStrings`
  is unaffected.

### Changed

- **Secret references now have a lossless Markdown form**, so the page source (Zdroj) keeps them: a block reference
  is `::secret{secretId=<uuid> …}` on its own line, an inline one `:secret{secretId=<uuid> …}` in the text. The
  directive carries only `secretId`, `field`, `fields`, `showCopy`, `showDetails` and `autoHideSeconds` (defaults
  left out), never a value. Markdown projections (`getProjection().markdown`, the Markdown source view) now contain
  these directives and report `markdownLossy: false` for references; plain text, HTML and clipboard output still
  carry nothing. Hosts that publish Markdown externally must filter the directives like the rich JSON. The rich
  schema stays v4; existing documents are unchanged.
- **Request access asks for a reason** in a small anchored panel (Zrušit / Odeslat, Escape or a click outside
  cancels, Ctrl+Enter sends) before anything is sent. `SecretsServices.requestAccess` receives
  `{ secretId, signal, reason }`; a host that ignores `reason` keeps working. `requestReason: 'off'` restores the
  direct request.

### Added

- Secrets: `createSecretsPlugin` returns a `SecretsPlugin` with `conceal(secretId?, { immediate? })` (cover released
  values again, the reference stays visible and interactive) and `refresh(secretId?)` (rerun discovery without a
  remount, e.g. after a 403 or an approved request; also brings back a revoked reference). Both return how many shown
  references matched.
- Secrets: `requestReason: 'optional' | 'required' | 'off'`, `SecretRequestContext`, and the directive helpers
  `serializeSecretDirective`, `parseSecretDirectiveAttributes` and `SECRET_DIRECTIVE`.
- Page source: a `:secret{…}` directive with a value, an unknown or repeated attribute or an invalid value is refused
  with a Czech diagnostic that never echoes the text. Pasted or dropped Markdown (another wiki, a `.md` file) never
  creates a reference, and HTML paste still cannot.
- Core: `openMarkdownReaderPopover`, the anchored reader panel that also works without an editable editor or an
  editor at all (contacts use it too), and `isMarkdownImportActive()` for plugin syntax that must not come from an
  import.
- Invisible ink: `confirmRequest` asks the host (for example for a reason) before `requestAccess`; a false answer
  cancels quietly.
- Icons: `shield`, `shield-alert`, `lock-open`, `network`, `globe`, `building-2`, `server`, `arrow-right-left`,
  `scroll-text`, `filter`, `circle-check`, `circle-x`, `activity`, `layout-dashboard`, `user-check`, `arrow-right`,
  `sparkles`, `calendar`, `plug` and `map-pin` (Lucide), for the Atlas settings.
- `Atlas.Markdown.Sync`: `ReservedNodeRequest.Payload` (`Update`, `Snapshot`, `Seed`) and
  `ReservedNodeChange.CurrentAttributes` / `CurrentSecretId` (the node's values as last admitted), so a reserved node
  guard can check the old and the new `secretId` of a rewrite. Additive: existing guards compile unchanged.

Eager JavaScript of a rich-profile host with every plugin: 577.4 → 591.0 KiB (179.8 → 183.8 KiB gzip), within the
608/190 KiB budget. The invisible ink still loads only with the first reference.

## 0.5.1

### Changed

- The invisible ink now loads on demand: with the first concealed block, or with a secret reference alongside its
  discovery. Pages without either no longer download it. The concealed block's settings panel loads for authors
  only, when an editable block's toolbar appears. Eager JavaScript of a rich-profile host with every plugin drops
  from 614.7 to 577.4 KiB (179.8 KiB gzip); no API or document change.
- Sealed concealed blocks show a placeholder ink for the moment until the surface is ready.

## 0.5.0

### Breaking

- `MarkdownStrings.concealed` has new required keys for the concealed block's settings panel and chip summary
  (`settings`, `settingsButtonText`, reveal-mode hints, the `reconceal*` and `complete*` choices, `customHint`,
  `settingsNote` and `summary*`). Hosts that build a complete `MarkdownStrings` object must add them; overriding
  single keys through `setMarkdownStrings` is unaffected.
- Concealed content plugin schema v2: blocks store `reconcealAfterMs` (default 1500, `null` never re-covers) and
  `completeAtPercent` (default 70, `null` off). v1 documents migrate unchanged and read the defaults. Hosts with a
  custom `YjsContentPolicy` must admit both attributes on `concealedContent`; the embedded manifest does.

### Added

- Concealed content blocks have an anchored **Zobrazení** settings panel (reveal by scratching or button, when to
  cover again, when to complete a scratched reveal) applied as one undo step, a chip summary and
  `setConcealedContentSettings`. The panel states that the block is visual protection, not a security feature.
- Invisible ink: `completeRevealAt` lets the rest of the mist part on its own, from the stroke, once that share of a
  scratched surface is open.
- Invisible ink: a passing pointer hands the specks momentum, so they glide on and settle elsewhere instead of
  springing back, and small calm currents carry neighbouring specks together. Surfaces released only by a button
  (including secrets) are stirred on hover without uncovering anything. With reduced motion specks keep twinkling
  in place.

### Fixed

- An uncovered inline capsule (a password in a sentence) no longer keeps a glitter rim over its value.

## 0.4.2

The package content of 0.4.0; only the release pipeline changed. 0.4.0 never reached the package registry (the
workflow could not authenticate), and 0.4.1 reached npm only. Use 0.4.2 for both `@atlas/markdown`
and `Atlas.Markdown.Sync`.

## 0.4.0

### Breaking

- `Atlas.Markdown.Sync` (behaviour change for hosts): collaboration traffic (updates, snapshots, awareness and
  their broadcasts) no longer re-authorizes peers; it uses each peer's last authorized access. A revocation takes
  effect at a join, at the periodic refresh (`MarkdownSyncOptions.AccessRefreshInterval`, 5 s by default) or when
  the host calls `DocumentAccessCoordinator.RefreshAsync(documentId)` / the new `RefreshAllAsync()`. Hosts must
  await one of them after committing an access change, or a revoked peer keeps receiving content until the next
  periodic refresh.
- `MarkdownStrings` has one required section per area besides the shared top-level words (core: `slash`,
  `blocks`, `images`, `drag`, `suggestions`, `table`, `toolbar`, `surface`, `outline`, `modules`, `input`,
  `source`, `collaboration`, `review`, `history`; plugins: `attachments`, `charts`, `concealed`,
  `contacts`, `diagrams`, `editorTools`, `emoji`, `headingAnchors`, `math`, `openapi`, `pages`, `secrets`,
  `toc`, `youtrack`). Hosts that build a complete `MarkdownStrings` object must add them; overriding
  single keys through `setMarkdownStrings` is unaffected.
- Secrets plugin: the default texts are Czech like the rest of the editor (card title, reveal/hide/copy,
  details, access-request states and field names). Hosts that pass `labels` / `fieldLabels` see no change.

### Added

- `MarkdownRenderer`: `variant="document"` renders with the editor's document layout (paper typography,
  table wrappers, `contenteditable="false"` content) and `outline` adds the editor's outline over the
  rendered headings. A reading view can use the static renderer instead of a readonly editor; measured
  5–13× faster to first content and about 40 % fewer DOM nodes.
- `@atlas/markdown/plugins/editor-tools`: `createStaticCodeHighlighting(options)`, the renderer's
  counterpart of `createCodeBlockHighlighting` (same languages, limits and `hljs-*` classes). Code blocks are
  highlighted as they approach the viewport.
- The user-visible strings of the editor and its first-party plugins are read at use time from
  `markdownStrings`, so `setMarkdownStrings` overrides them once for all editors. Sections merge key by
  key and keep their keys whether they are set before or after the section's code loads. Not yet covered:
  the texts of `MarkdownSourcePanel`, `MarkdownDraftConfirmation`'s defaults and `useMarkdownSourceDraft`
  notices.
- `@atlas/markdown/core`: `registerMarkdownStrings(section, defaults)` and the
  `MarkdownStringsOverrides` and `MarkdownStringsSection` types. Each section's defaults ship with the
  code that renders them, so a core-only page carries no plugin text.
- `@atlas/markdown/source`: `SourceEditorOptions.labels` (`SourceEditorLabels`, the same keys as
  `markdownStrings.source`) overrides the source editor's loading status and protected-block texts; the
  OpenAPI plugin passes `markdownStrings.source`.
- `MarkdownDetailWorkspace`: without a `label` prop the region name follows
  `markdownStrings.surface.detailAriaLabel`.
- `Atlas.Markdown.Sync`: public `RecentUpdateWindow` constants (the idempotency window of retained
  client updates).

### Fixed

- Collaboration (durable SignalR provider): the provider's own ACK advances the durable journal instead
  of triggering a rejoin; only a real sequence gap rejoins, and retries back off.
- `./source`: the Monaco protected-block guard compares block lines ignoring trailing blanks, like the
  headless session, so a damaged copy with trailing blanks can no longer slip past it.

## 0.3.2

### Fixed

- Collaboration: opening a page with diagram, math or OpenAPI blocks for editing no longer hangs in
  "reconnecting". The join seed was cloned into null-prototype objects that Yjs rejects as attribute
  values, so the seeding client failed silently and the relay timed out waiting for the seed.

## 0.3.1

### Added

- `Atlas.Markdown.Sync`: opt-in managed Yjs content guard (`AddAtlasMarkdownContentGuard`). The relay
  validates every update and snapshot against the editor's node/mark manifest before storing or broadcasting
  it and rejects violations with `[INVALID_CONTENT]`; hosts decide reserved (secret) nodes through
  `IReservedNodeGuard`. See `docs/collaboration-content-guard.md`.

### Changed

- Collaboration: a relay error `[INVALID_CONTENT]` (the opted-in server content guard refused an update or
  snapshot) is fatal for the SignalR provider. The session fails with the existing fatal-error handling
  instead of retrying the refused change forever. See `docs/collaboration-content-guard.md`.

## 0.3.0

### Breaking

- `MarkdownStrings` has four new required keys: `markdownImportFailed`, `pasteAsMarkdown`,
  `pasteAsMarkdownDescription` and `clipboardReadFailed`. Hosts that build a complete `MarkdownStrings`
  object (instead of spreading `markdownStrings` or overriding single keys) must add them.
- OpenAPI plugin: `OpenApiPluginOptions.openDetail` now receives a `MarkdownModuleNodeContext<OpenApiData>`
  instead of a `MarkdownModuleContext`. Its `signal` aborts only when the block is removed or replaced or
  the editor is destroyed (never on a readonly toggle); watch `onEditableChange` to end YAML editing and
  use `anchor` to position host UI (never render into it).
- Hosts with a Content Security Policy need `font-src data:` for the source editors: Monaco's stylesheet
  and codicon font are now injected lazily by the source chunk (a `data:` font URL) instead of being part
  of any global stylesheet. `style-src 'unsafe-inline'` is still required.

### Added

- New `@atlas/markdown/source` entry, a shared Monaco source editor surface that stays tiny when
  imported and loads Monaco, its CSS and workers only on mount or prefetch:
  - `mountSourceEditor(host, options)` (a static skeleton shows at once; Monaco replaces it),
    `prefetchSourceEditor(): boolean` (skipped under Save-Data) and the `SourceEditor`,
    `SourceEditorOptions`, `SourceLanguage`, `SourceDiagnostic`, `SourceProtectedBlock`,
    `SourceSetValueOptions`, `SourceEditorSetup`, `SourceEditorSetupEnvironment`, `SourceWorkerOptions`
    and `SourceDisposable` types. No public type mentions Monaco.
  - Markdown protected blocks (```` ```atlas <id> ```` fences) that can only be deleted or moved as a
    whole, with `onProtectedEditRejected`.
  - Async `createMarkdownSourceSession(options)`, a headless Markdown page-source session loaded on first
    use, with the `MarkdownSourceSession`, `MarkdownSourceSessionOptions`, `MarkdownSourceApplyResult`,
    `MarkdownSourceBlock`, `MarkdownSourceProtectedRange`, `MarkdownSourceProtectionReason` and
    `MarkdownSourceDiagnostic` (alias of `SourceDiagnostic`) types.
- `@atlas/markdown/vue`:
  - `MarkdownSourcePanel`, the page source ("Zdroj") view, as an async component: it costs the eager
    bundle nothing, and a failed download shows an in-place error that a re-render retries.
  - `MarkdownDraftConfirmation` (eager) for resolving a pending source draft.
  - `useMarkdownSourceDraft` with the `MarkdownSourceDraft`, `MarkdownSourceDraftOptions`,
    `MarkdownSourceDraftStatus`, `MarkdownSourceNotice`, `MarkdownSourceNoticeKind`,
    `MarkdownSourceSaveContext`, `MarkdownSourceSaveFailure` and `MarkdownSourceSaveResult` types; the
    draft logic loads on the first `open()`.
  - `MarkdownDetailWorkspace`: optional `label` prop (accessible region name, default
    "Detail komponenty"), a `tab-intent` event on pointer enter or focus of a tab so hosts can warm lazy
    views, and tabs styled with theme tokens only; the editor's theme tokens are copied onto the promoted
    workspace, so it follows the document theme (including Midnight).
  - `MarkdownEditor` props `onMarkdownImport`, `resolveWikiLink` and `resolveEmbed`.
- `@atlas/markdown/core`:
  - Controller options `onMarkdownImport`, `resolveWikiLink` and `resolveEmbed` with the
    `MarkdownImportHandler`, `MarkdownImportInfo`, `MarkdownImportDecision`, `MarkdownImportDegradedKind`,
    `MarkdownImportDegradedPart`, `MarkdownWikiLinkRequest`, `MarkdownWikiLinkResolution`,
    `MarkdownWikiLinkResolver`, `MarkdownEmbedResolution` and `MarkdownEmbedResolver` types.
  - `markdownImportNoticeEvent` and `MarkdownImportNoticeDetail`, reporting imported constructs kept
    as literal text.
  - The `paste-markdown` command (`{ type: 'paste-markdown', text }`) and a "Vložit jako Markdown" slash
    item that reads the clipboard.
  - `MarkdownModuleNodeContext` and `MarkdownModuleContext.nodeContext()`: a block node's context for host
    UI that outlives one render, with `anchor` and `onEditableChange`.
  - `MarkdownBlockModuleOptions.parseHTML`, extra pasted/imported DOM recognized as a module.
  - `MarkdownLossyOptions` (`ignoreIdentity`) as the optional second argument of a plugin's
    `markdownLossy(document, options)`.
- OpenAPI plugin: `prefetchOpenApiSourceEditor(): boolean` warms Monaco and the OpenAPI language setup
  (e.g. on hover or focus of a YAML tab). `mountOpenApiSourceEditor` now shows a static skeleton at once
  and shares the Monaco chunk with the Markdown page source.

### Changed

- One Markdown input boundary for paste, `.md` file drop and every string parsed by tiptap-markdown:
  - H4–H6 headings become H3 on every parse path (the schema has levels 1–3).
  - CommonMark soft line breaks become spaces on every parse path.
  - Pasted and dropped Markdown drops a leading YAML front matter block and reports it (and a leading
    H1) through `onMarkdownImport`; `$…$`/`$$…$$` TeX stays intact, and foreign-wiki syntax (GitHub,
    Obsidian, Wiki.js: wiki links, embeds, HTML, comments, footnotes, highlight, sub/superscript, TOC
    markers) is mapped or kept as literal text and reported as `degraded`.
- An image with the default presentation is no longer reported as lossy Markdown.
- Block images serialize as their own Markdown block instead of being glued to the following block.
- The source editors use a slim Monaco build; Ctrl+M (macOS: Ctrl+Shift+M) toggles whether Tab inserts
  indentation or moves focus.
- The package owns `globalThis.MonacoEnvironment` only while a source editor is mounted and chains any
  environment the host installed before it (`hostFirst` lets the host answer shared worker labels first).

### Fixed

- Duplicate focus ring declaration on the block inserter buttons (the shared focus ring applies).

## 0.2.0

New concealed and OpenAPI plugin entries, secrets schema v4 with multi-field inline references, plugin
selection actions and the shared invisible-ink animation loop. Scalar, Monaco and monaco-yaml are bundled
instead of consumer peers; the OpenAPI plugin needs the optional `yaml` peer.
