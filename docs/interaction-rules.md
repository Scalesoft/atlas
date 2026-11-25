# Pravidla ovládání Atlasu

Přehled „koncept → jediná komponenta“: [ux-concepts.md](ux-concepts.md).

Stav: odsouhlaseno vlastníkem 2026-10-30 (D1–D3 podle doporučení; P17 doplněno týž den na jeho požadavek; D4–D8
2026-10-31 podle doporučení). Platí pro editor stránky, tabule, celou aplikaci i AI. Každá nová
komponenta je musí dodržet; odchylka musí mít v galerii napsaný důvod. Soupis dnešního stavu, ze kterého pravidla vznikla,
je v části „Odchylky dnes“ na konci.

Tři zásady nad vším:

1. **Kde to vidíš, tam to upravíš.** Žádné hledání v lištách a nabídkách; lišta je jen další cesta myší.
2. **Stejný záměr = stejná klávesa, stejné slovo, stejný vzhled** ve stránce, na tabuli, v seznamu i v panelu.
3. **Nic se neztratí potichu.** Co je rozepsané, se uloží, nebo to člověk sám zahodil (Esc); každá změna jde vrátit.
4. **Dobře je snazší než špatně.** Provázání a struktura vznikají samy jako výchozí stav (P15); obejít je stojí úsilí,
   ne je udělat. Totéž platí pro vývoj: konzistentní komponenta vznikne ze sdílených dílů, nekonzistentní neprojde CI (P16).

## P1 Úprava začíná na prvku

- Klik na obsah prvku ve stránce začne jeho úpravu (pole na místě, štítek, panel u prvku). Druhý klik ji zavře.
- Enter nebo F2 na vybraném prvku dělá totéž co klik. Enter nikdy neotevírá něco jiného než úpravu (výměna hesla,
  otevření detailu a podobně jsou položky nabídky akcí, ne Enter).
- Na tabuli první klik prvek vybere (kvůli přesunu), úpravu začne Enter, F2 nebo dvojklik. *(Rozhodnutí D1: odsouhlaseno.)*
- Odkaz uvnitř prvku se klikem otevře (odkaz, e-mail, telefon, stažení); úpravu odkazu nabídne nabídka akcí.
- Tlačítko „Upravit“ v liště prvku zůstává jako cesta myší a vede na totéž místo jako klik. Stránka sama tlačítko nemá:
  kdo smí psát, píše klikem do textu, a kdo chce jen číst, zamkne ji zámkem (3.6).

## P2 Jedna nabídka akcí „Co s tím jde dělat“

- **Ctrl+mezerník** u kurzoru, na vybraném prvku nebo výběru. Stejnou nabídku otevře **Alt+Enter**, **Ctrl+.**,
  **Shift+F10**, klávesa nabídky a **pravé tlačítko** (Shift + pravé tlačítko nechá nabídku prohlížeče).
- Platí všude: text a prvky stránky, čipy, tabule a její položky, strom stránek, řádky seznamů (kontakty, koš, přístupy).
- Obsah podle toho, co je pod kurzorem: úpravy prvku, převody (na čip, na text, na odkaz), vložení, kopírovat odkaz,
  otevřít, smazat, a položky AI (P11). První řádek předvybraný, psaním se filtruje, Enter provede, Esc zavře.
- Pluginy a moduly do nabídky přidávají položky; AI je přidává samostatný modul asistenta, jen když umí odpovědět.

## P3 Esc

- Esc zavře **poslední otevřenou věc**: nabídku, pak pole, pak panel, pak úpravu prvku, pak výběr.
- **Rozepsaný koncept** (jednořádkové pole, formulář s „Použít“) Esc zahodí a vrátí hodnotu před úpravou.
- **Obsah, který se ukládá průběžně** (text poznámky, vzorec, zdroj diagramu), Esc jen zavře – je už uložený; vrátit ho jde
  Ctrl+Z. Nápověda pole to říká: „Esc zavře, Ctrl+Z vrátí“. *(Rozhodnutí D2: odsouhlaseno.)*
- Pole uvnitř panelu si svůj Esc vezme samo (zruší pole); teprve další Esc zavře panel.
- Ve vyhledávání: první Esc smaže napsaný text, druhý vyhledávání zavře.

## P4 Odchod z pole a klik mimo

- Tab nebo klik jinam **uloží** platnou hodnotu (stejně jako Enter). Neplatná hodnota pole nechá otevřené s chybou pod ním.
- Klik mimo panel s „Použít“ změny **použije**, jsou-li platné; jinak panel zůstane otevřený s chybou. *(Rozhodnutí D3: odsouhlaseno.)*
- Klávesnice po uložení zůstane tam, kam člověk šel (klik do textu, Tab na další pole), ne zpátky na poli.

## P5 Ukládání

- **Průběžně, bez tlačítka**: pole, štítky, čipy, text, nastavení zobrazení prvku.
- **„Použít“** jen u velkých nebo strukturálních změn, které je dobré dopsat celé: zdroj API (YAML), tabulka dat grafu,
  ořez a efekty obrázku, práva a přístupy. Jedno slovo „Použít“, Ctrl+Enter = Použít.
- **Nastavení** (stránky nastavení) ukládá lišta „Uložit“ s hlídáním neuložených změn.
- Jinde žádné „Uložit“. Stránka se ukládá sama; Ctrl+S ve stránce uloží pojmenovanou verzi („Uložit verzi“), nic jiného ho nepřebírá.

## P6 Zpět

- **Ctrl+Z / Ctrl+Y** vrací poslední změnu odkudkoli: z pole, z čipu, ze štítku nebo tlačítka komponenty, na tabuli, v panelu.
- Jedna akce = jeden krok zpět (převod na čip, výběr rozsahu, výměna hesla, přejmenování).
- Každé smazání objektu nebo položky, přesun do koše, přejmenování, přesun a převod ukáže oznámení s **„Zpět“**. Jeden systém oznámení. Smazání bloku ve stránce oznámení nemá (je to text, Zpět je u kurzoru).

## P7 Fokus

- Po dokončení úpravy je klávesnice na upraveném prvku (vybraném) – i když se prvek překreslil.
- Po zavření panelu nebo dialogu se klávesnice vrátí tam, odkud byl otevřen.
- Nikdy nezůstane na `<body>`. Žádné zpožděné „honění“ fokusu časovači.

## P8 Výběry a vyhledávání

- První výsledek je předvybraný; Enter ho vezme (i když se výsledky ještě načítají, vezme ten správný); Tab vezme návrh.
- Šipky se pohybují, Esc podle P3.
- Stejná jména se vždy rozliší (firma, e-mail, cesta, prostor).
- U lidí fungují iniciály („MM“, „MMu“) a naučené zkratky; řazení podle kontextu (tato stránka, prostor, role, četnost).

## P9 Čip v textu

- Jedna komponenta a jeden vzhled pro kontakt, nerozhodnutý kontakt, úkol YouTrack, odkaz na stránku, čipy požadavku
  a odkaz na heslo. Vzhled vychází ze zmínky kontaktu a čipu YouTrack.
- Klik nebo Enter otevře detail či výběr; Ctrl+mezerník nabídku akcí; **Ctrl+Shift+mezerník** převede čip na text.
- Jednoznačný text (známé celé jméno, klíč úkolu, odkaz na stránku) se při psaní sám převede na čip. Backspace hned po
  převodu vrátí text bez spouštěcího znaku; nové napsání znaku nebo nabídka akcí udělá čip znovu. Text vrácený záměrně se
  sám znovu nepřevede.
- Naučené zkratky, přezdívky a iniciály („MMu“, „MM“) se jen nabídnou (podtržení; Enter nebo Ctrl+mezerník je převede),
  nikdy se samy nepřevedou na čip. Sám se převádí jen celý jednoznačný text. *(D4)*
- Víc možností → nerozhodnutý čip „nejspíš X · nebo Y“, nikdy tichý výběr.
- Označený čip + Backspace ho smaže (jako slovo).
- V polích samotných entit (jméno kontaktu, vyhledávací pole) se nic nepřevádí a nic se nehledá.

## P10 Čtení

- Co nemění obsah, funguje i při čtení: otevřít, kopírovat odkaz (i na nadpis), stáhnout, detail kontaktu, AI „Zeptat se /
  Vysvětlit / Shrnout“.
- Úpravy při čtení nejsou vidět vůbec (žádné tužky, kurzor textu ani najetí jako u úprav).

## P11 AI

- Stejné záměry – **Zeptat se, Vysvětlit, Shrnout, Přepsat, Přeložit, Doplnit, Navrhnout odkazy a kontakty** – nad čímkoli,
  na co jde ukázat: výběr, **nadpis a jeho sekce**, odstavec, seznam, tabulka, každý prvek (graf, vzorec, diagram, kontakt,
  požadavek…), celá stránka, tabule a její položky, kontakt v adresáři.
- Ve čtení i při úpravách (při čtení jen záměry, které nemění obsah). Cesty: nabídka akcí (P2), výběrová lišta, panel asistenta.
- Výsledek je vždy návrh k potvrzení (náhled, Použít / Zahodit), jeden krok zpět.
- Jeden limit velikosti a jedno pravidlo dostupnosti; když AI odpovědět neumí, položky nejsou vidět (žádná hláška).
- Tajné a skryté části se nikdy neposílají; ostatní akce (kopírovat odkaz) zůstávají i tam.

## P12 Vzhled a slova

- Jedny tokeny pro najetí, vybráno, upravuje se, fokus, chyba, nerozhodnuto; jedna délka a křivka animací (motion tokeny).
- Slova: **Upravit**, **Použít**, **Zrušit**, **Převést na text**, **Odebrat** (ze stránky/tabule, objekt zůstává),
  **Smazat** (do koše), **Zpět**. Nápověda zkratek v popisku prvku.
- Fokus vypadá všude stejně: kroužek z tokenu `--state-focus-ring`, i u textových polí (žádný zvláštní 2px obrys). *(D6)*

## P13 Načítání

- Hned zobrazit, co je; místo pro načítané vyhrazené kostrou ve správném rozměru. Žádné „Načítání…“ bez rozměru, žádný skok.
- Místo je vyhrazené od začátku; kostra se ukáže, až načítání trvá déle než 100 ms. Co přijde dřív, se ukáže rovnou,
  bez probliknutí kostry. *(D7)*

## P14 Stránka a tabule

- Stejný prvek (text, blok, kontakt, čip) se na tabuli chová stejně jako ve stránce: stejné Esc, Enter, ukládání, Zpět, AI,
  nabídka akcí. Tabule přidává jen výběr a přesun (P1).
- Soubory a obrázky na tabuli zatím nejsou: tabule nemá úložiště souborů (D12). Zkopírovaný blok nebo karta kontaktu z tabule
  se do stránky vloží jako tatáž komponenta (Markdown v textu schránky); ze stránky na tabuli přejde text.
- Diskuse k jedné vybrané položce tabule je řádek „Diskuse“ v nabídce akcí, stejně jako u objektu: karta stránky nebo objektu
  otevře diskusi svého objektu, ostatní položky diskusi tabule ukotvenou u položky (D13).
- Ve stránce je každý kontakt samostatný blok (mezi kontakty jdou psát nadpisy). Na tabuli smí jedna karta nést víc
  kontaktů: je to jeden objekt se seznamem lidí. *(D8)*

## P15 Provázání samo od sebe

Cíl: podklad, který člověk napíše normálně, je provázaný bez další práce; nepropojený text je výjimka, kterou je vidět.

- **Entity se poznají při psaní** a stanou se čipem, když je to jednoznačné (P9): lidé (jména, iniciály, zkratky),
  úkoly YouTrack, požadavky, stránky (název existující stránky se nabídne jako odkaz stejným podtržením jako známé jméno).
- **Nejednoznačné se nevybírá potichu**, zůstane nerozhodnuté a viditelně čeká na výběr (nerozhodnutý čip) – nikdy se
  nezahodí ani nespojí s nesprávnou osobou.
- **Kontext řadí návrhy**: tato stránka a prostor, projekt a jeho požadavky, role ve stránkách („PM projektu“), slova kolem
  kurzoru, četnost. Bez AI funguje celé; s AI embeddingy navíc zpřesní řazení a navrhnou souvislosti.
- **Struktura z šablon**: stránka projektu se zakládá ze šablony s místy pro PM, „čekáme na“, termíny a požadavky – vyplnění
  míst rovnou vytváří čipy a vazby, ne volný text.
- **Při zakládání stránky** se nabídne podobná existující stránka („Podobná stránka už existuje: …“) dřív, než vznikne duplicita.
- **Viditelné mezery**: nepropojená známá jména a názvy stránek jsou jemně podtržené; prostor má přehled „Nepropojené
  zmínky“ s jedním potvrzením na položku. Zda je něco propojené, říká jen stránka: propojené je čip, odmítnuté zůstane
  textem a nabídne se znovu; mimo stránku se nic nepamatuje. Zpětné odkazy a „Kde se používá“ jsou u každé entity.
- **Tabule ukazují vazby samy**: „Lidé projektu“, „Na co čekáme“, „Kde se používá“ se skládají z existujících vazeb, ne ručně.
- Nic se nezapíše bez potvrzení člověka tam, kde by šlo o odhad (nerozhodnuté, návrhy AI); jednoznačné převody jdou
  vrátit jedním Backspace nebo Ctrl+Z.

## P16 Konzistence vynucená

- Koncepty a jejich komponenty: [ux-concepts.md](ux-concepts.md).
- Komponenty se skládají jen ze sdílených dílů: čip v textu, pole na místě, panel u prvku, nabídka akcí, výběr se
  seznamem návrhů, oznámení se Zpět, kostra načítání. Vlastní varianta potřebuje důvod v galerii.
- **Kontraktní test v CI** projde každý blok, čip, položku tabule a pole a ověří P1–P7 (klik/Enter/Esc/odchod/Ctrl+Z/fokus,
  stejné třídy stavů). Nová komponenta, která pravidla poruší, neprojde.
- Galerie má ke každé komponentě ukázku; změněná ukázka dostane řádek „Ovládání“ podle těchto pravidel (zatím ho nemají všechny, hlídá se jen při změně).
- Kde: `src/Atlas.Markdown/src/contracts/interaction.contract.test.ts` (editor), `src/Atlas.Web/src/contracts/*.contract.test.ts`
  (tabule, aplikace, bloky Atlasu). Jedna tabulka popisů prvků na plochu; přijaté odchylky v `interaction-deviations.json` a
  `src/Atlas.Web/src/contracts/deviations/*.json` jsou úkoly oblastí sjednocení; galerie „Pravidla ovládání“ je ukazuje živě.

## P17 Svižnost a responzivita

Cíl: první odezva je skoro ihned. *(Požadavek vlastníka 2026-10-30.)*

- **První viditelná odezva na každý klik a klávesu do 100 ms**: změna stavu, otevřené pole, nabídka nebo panel, optimistická
  hodnota. Nic nečeká na síť, než ukáže ovládání: data se načtou do kostry ve správném rozměru (P13), ukládání je optimistické
  a při odmítnutí se vrátí se zprávou (oznámení, P6).
- **Psaní nikdy nezaostává.** Práce na každý úhoz (rozpoznání jmen, čipy, návrhy, průběžné ukládání) je malá, odložená nebo
  naplánovaná na volnou chvíli; žádné opakované měření rozložení. Dlouhý dokument (např. 2 000 bloků) drží zpoždění psaní v rozpočtu.
- **Nabídky, výběry a seznamy návrhů** se otevřou hned s prvními řádky z mezipaměti; výsledky ze sítě je jen zpřesní (žádné
  prázdné bliknutí, žádný skok – místo je vyhrazené).
- **Pohyb jen tokeny**, krátký (fast/base), omezený pohyb respektovaný; animace nikdy nezdrží odezvu.
- **Responzivní**: každá plocha funguje na šířce 375 px dotykem (žádné akce jen při najetí, žádné vodorovné posouvání),
  klávesnicí i čtečkou obrazovky. Na dotykovém zařízení má každý cíl plochu aspoň 44 × 44 px: neviditelné rozšíření
  přes jeden token, vzhled se nemění a s myší zůstává vše beze změny. *(D5)*
- **Líné načítání zůstává**: těžké moduly (Monaco, Scalar, KaTeX, Mermaid, asistent) se načtou při záměru nebo ve volné chvíli
  a nikdy neblokují první vykreslení; hlídání velikosti balíčku zůstává zelené.
- Kontraktní test (P16) měří, co jde měřit bez prohlížeče: úprava se ukáže ve stejné úloze jako gesto, úhoz v dlouhém dokumentu
  se vejde do rozpočtu, výběr ukáže známé řádky hned, načítání má kostru. Šířku 375 px a dotyk ověřují kola revizí v prohlížeči
  podle popisu „dotyk“ u každého prvku v kontraktním testu.

## Rozhodnutí vlastníka (2026-10-30: vše podle doporučení)

- **D1** Tabule: první klik vybere, úprava Enter/F2/dvojklik (doporučeno), nebo klik rovnou upravuje jako ve stránce?
- **D2** Esc u průběžně ukládaného textu (poznámka, vzorec, diagram): zavřít a nechat (doporučeno, vrací Ctrl+Z), nebo
  vrátit stav před začátkem úpravy?
- **D3** Klik mimo panel s „Použít“: použít (doporučeno), nebo zahodit?

## Rozhodnutí vlastníka (2026-10-31: vše podle doporučení)

Otázky ze souběhu se soupisem a auditem 2026-10-30.

- **D4** Naučená zkratka nebo iniciály jako automatický čip? Ne: jen se nabídnou, převede je Enter nebo Ctrl+mezerník (P9).
- **D5** Plocha cíle pro dotyk: 32 px, nebo 44 px? 44 × 44 px na dotykovém zařízení, neviditelně a bez změny vzhledu (P17).
- **D6** Fokus u polí: vlastní 2px obrys, nebo společný kroužek? Společný kroužek `--state-focus-ring` všude (P12).
- **D7** Kdy ukázat kostru: po 100, nebo 150 ms? Po 100 ms, místo vyhrazené hned (P13).
- **D8** Víc kontaktů v jednom bloku: ve stránce ne (každý kontakt je blok), na tabuli ano (jedna karta se seznamem lidí) (P14).

## Odchylky dnes (stav po 2. kole kontroly návrhu, 2026-10-31)

Kontraktní testy (P16) hlásí devět přijatých odchylek; všechno ostatní ze soupisu 2026-10-30, co testy pokrývají, už pravidla
dodržuje. Další body níže testy nepokrývají nebo jsou úkoly navazujících balíčků.

Přijaté odchylky v kontraktních testech
- Požadavek: Enter na vybraném požadavku vstoupí do názvu, F2 nic neudělá; pole otevře jen klik na údaj nebo tužka (P1,
  `deviations/editorHost.json`).
- Tabule, karta stránky: dvojklik, Enter i F2 otevřou úpravu stránky v okně nad tabulí, v kartě samotné se nic neupravuje (P1,
  `deviations/board.json`).
- Strom stránek: řádek je odkaz, první klik stránku otevře; přejmenování začíná F2, dvojklikem nebo „Přejmenovat“ v nabídce (P1,
  `deviations/app.json`).
- Při čtení nemá prvek (vzorec, graf, diagram) Zeptat se, Vysvětlit a Shrnout: „Co s tím jde dělat“ se při čtení neotevře
  (P10, P11, `deviations/ai.json`).
- Sekce či stránka delší než limit 1 600 znaků nabídne AI jen nad samotným nadpisem; chybí odkaz, který si asistent přečte sám
  (P11, `deviations/ai.json`).
- Celá tabule nebo velký rám delší než limit 1 600 znaků nemá bez výběru řádky AI; chybí odkaz na tabuli, který si asistent
  přečte sám (P11, `deviations/ai.json`).
- „Navrhnout odkazy a kontakty“ není záměr na vyžádání, jen návrhy modulu asistence u bloku (P11, `deviations/ai.json`).
- Text na tabuli má jen Zeptat se, Vysvětlit a Shrnout; Přepsat, Přeložit a Doplnit čekají na modul asistence v textovém editoru
  tabule (P11, P14, `deviations/ai.json`).
- Upravovaný text tabule má v nabídce akcí jen Zeptat se, Vysvětlit a Shrnout a jeho bublina nad výběrem nemá „AI“; čeká na
  týž modul asistence (P11, P14, `deviations/ai.json`).

Editor stránky
- Popisky lišty bloků modulů jsou „Upravit: X“ a „Smazat: X“ (s názvem prvku) místo „Upravit“ a „Smazat“; tabule z popisku čte předponu (P12).
- Panel zdroje diagramu se klikem mimo nezavře a panely vzorce a diagramu nejsou ve sdíleném pravidle jedné otevřené nabídky
  (P4; core neexportuje registerMarkdownPopover).
- Projekce dokumentu pro uložení není přírůstková: v 2 000 blocích trvá déle než rozpočet krátké úlohy 50 ms (P17, známá mezera
  kontraktního testu).

Tabule
- Diskuse tabule je na telefonu v doku, diskuse stránky pod obsahem (P14, přijatá odchylka).
- Karta kontaktu přejde do úprav už fokusem a má vlastní obsluhu Esc (ContactCard, P1, P3).
- Výběr kontaktů na tabuli se nenaučí napsanou zkratku jako ostatní výběry osob (P8).
- Komponenty a karty kontaktu a stránky nemají pro AI čitelný text, takže AI nad nimi nic nenabízí (P11).

Aplikace
- Komentář se upravuje tužkou (upravuje se zpráva, ne obsah; Notionův model, P1, přijatá odchylka).
- Oznámení o přesunu stránky nebo tabule do koše a o přesunu do jiného prostoru nemají „Zpět“ (P6; obnovu z koše smí jen správce).
- Výběr strany požadavku (PartyInput) má ještě vlastní obsluhu Esc, Enter a Tab místo sdíleného useSearchKeys (P16).
- Smazání komentáře se potvrzuje dialogem a nemá „Zpět“; chybí obnova komentáře na serveru (P6).

Provázání (P15, z větší části nepostavené)
- Názvy stránek se při psaní nerozpoznávají a klíče YouTrack se jen nabízejí přes pravidla asistence, samy se na čip nepřevedou.
- Přehled „Nepropojené zmínky“ (záložka prostoru) je hotový ve webu proti smlouvě `GET /api/spaces/{id}/unlinked` a
  `POST /api/pages/{id}/references`; „Zpět“ v oznámení čeká na cestu serveru, která potvrzení vrátí. Šablona Projekt má místa pro PM a „čekáme na“;
  termíny a požadavky jsou místem jen s modulem Projector (prázdný požadavek), jinak prosté nadpisy.
- Embeddingy návrhy lidí nevylepšují; řazení podle souvislostí funguje jen bez AI.

AI
- Komentáře nemají hostitele nabídky „Co s tím jde dělat“; řádky stromu stránek, kontaktů a Mého prostoru ji mají, ale bez akcí AI
  (P2, P11).
- Shrnutí stránky jen u publikované stránky při čtení, ne u konceptu (P11).
- Použitý návrh asistenta s nástroji (ikony a tagy, přejmenování, přesuny, publikace) nemá „Zpět“: karta drží jen popis změny
  (u přesunu cestu, ne rodiče a pořadí), takže vrátit ho musí server jedním vrácením celého návrhu (P6, P11).
- Dvě místa „zeptat se wiki“ s různými úlohami serveru (hledání `search.answer`, panel asistenta `chat.answer`); jedno místo čeká
  na rozhodnutí vlastníka (P11).

