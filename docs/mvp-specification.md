# Atlas: specifikace použitelného MVP

Datum: 2026-09-06. Stav: výchozí návrh rozsahu pro implementaci. Potvrzené jsou .NET, Vue.js, NHibernate, OpenIddict, vlastní editor a důraz na Outline jako vizuální inspiraci. Model práv je přijatý v samostatném rozhodnutí; workflow a výkonnostní cíle zůstávají návrhem, nikoliv již ověřeným stavem.

## 1. Výsledek pro uživatele

Zaměstnanec se přihlásí, rychle najde správný postup, přečte jej v přehledném rozhraní a podle práv vytvoří či upraví stránku. Tým dokáže převést skutečný obsah z Wiki.js bez ztráty důležitých příloh, odkazů a přístupových hranic. Aplikace musí být užitečná i bez AI.

MVP má jeden interní workspace, prostory pro týmy nebo témata a stránky ve stromu. V uživatelském rozhraní používáme „Prostor“, „Stránka“, „Koncept“, „Publikovat“, „Oblíbené“ a „Koš“. Nezavádíme vedle prostorů ještě kolekce, knihy a složky se stejným významem.

## 2. Rozhraní inspirované Outline

### Aplikační rámec

- Levý panel přibližně 224–240 px: značka Atlas a účet, Domů, Hledat, Oblíbené, Moje koncepty; pod tím přepínač prostoru, Přehled prostoru a strom stránek jednoho prostoru (aktuálního, jinak naposledy použitého). Dole Koš a Nastavení podle práv.
- Aktivní stránka je jasně, ale nenápadně zvýrazněná. Rozbalení stromu se pamatuje pro uživatele. Po otevření výsledku hledání se zobrazí jeho místo ve stromu.
- Horní řádek: breadcrumbs, kopírování interního odkazu, hlavní akce podle režimu a nabídka dalších akcí. Sdílení neznamená veřejný odkaz.
- Dokument: velký název, stručné údaje o poslední publikované změně, čitelná šířka přibližně 720–840 px. Široké tabulky nesmějí rozbít celou navigaci.
- Výchozí obsah „Na této stránce“ je kompaktní a rozbalovací. U dlouhého článku lze na dostatečně široké obrazovce použít boční panel; jen jeden obsah současně, se zvýrazněním aktuálního nadpisu a odkazem na sekci.
- Na menší obrazovce se obsah a sidebar sbalí. Čtení nesmí vyžadovat současné zobrazení tří sloupců.
- Jemné oddělovače, neutrální povrchy a individuálně volený akcent shellu; stabilní barevné ikony prostorů. Stávající editor si zachová své styly a veřejné theme API. Žádný dashboard plný prázdných karet.

Rozměry jsou výchozí návrh k ověření s reálnými články. Do hlavního menu nepřidávat položky pro nehotové funkce.

### Přijatý vzhled a osobní preference

Rozhodnutí z 2026-09-06: aplikační rámec vychází z kompaktních návrhů Dock/Cove. Výchozí je **propojené rozložení Dock a modrý akcent**. Závazná vizuální reference se týká především levé navigace, členění ploch, barev a nenápadných aktivních stavů. Stávající editor a jeho ovládací prvky se podle mockupu nepřepisují; ani ilustrační tlačítko Upravit není nový vzor pro editorové komponenty.

V **Profil → Vzhled** si každý uživatel vybírá nezávisle:

| Preference | Hodnoty pro MVP | Výchozí stav |
| --- | --- | --- |
| Rozložení | Propojené (`connected`), Ostrovy (`islands`) | Propojené |
| Akcent | Modrá (`blue`), Zelená (`green`) | Modrá |
| Režim | Podle systému (`system`), Světlý (`light`), Tmavý (`dark`) | Podle systému; technický výchozí návrh |

Nejde o samostatná pevná témata „Dock“, „Cove“ nebo „Shore“. Obě rozložení podporují oba akcenty i oba výsledné režimy. Přepnutí akcentu nemění rozložení a přepnutí režimu nemění akcent. Volba uživatele nemění vzhled ostatních lidí, obsah stránky, práva ani nastavení prostoru. MVP má dvě ověřené akcentní palety, nikoliv libovolný color picker nebo editor témat.

Profil ukazuje dvě malé názorné volby rozložení, pojmenované barevné vzorky a tři pojmenované volby režimu. Výběr je patrný i bez rozlišování barev a funguje klávesnicí. Změny mají okamžitý lokální náhled a explicitní **Uložit / Zrušit**. Zrušení nebo opuštění formuláře vrátí poslední uložený vzhled; selhání ukládání zachová návrh a nabídne opakování, nesmí předstírat úspěch. Nastavení je dostupné z nabídky vlastního účtu také Readerovi, bez potřeby práv správce. Tlačítka tématu nad referenčními obrázky jsou ovládání návrhu; nepředepisují další trvalou produktovou lištu.

**Použitelnost má přednost před dekorací:**

- Propojené: sidebar navazuje přímo na obsah, pouze jemný oddělovač. Ostrovy: stejné komponenty a pořadí, malé mezery mezi navigací a obsahovou plochou, umírněné zaoblení. Žádné další karty kolem běžných odstavců.
- Sidebar vychází z Dock, přibližně 224–240 px na běžném desktopu; názvy se přizpůsobí dostupnému prostoru. Menší okraje a mezery nesmějí vést ke zmenšování textu nebo klikacích cílů. Dlouhé názvy a několik úrovní stromu jsou součástí přejímky.
- Aktivní stránka má jemný barevný podklad a zřetelný text; bez výrazných levých proužků a bez řetězení dekorativních zvýraznění. `aria-current` označuje aktuální stránku, rozbalovací ovladač oznamuje svůj stav.
- Barva a symbol prostoru tvoří stabilní společnou identitu prostoru; osobní akcent je nepřebarvuje. Pro MVP připravit konzistentní kurátorovanou sadu ikon prostorů a akcí (např. vývoj, produkty, onboarding, tým; otisk pro identitu). Nezadávat vlastní SVG od uživatele. Ilustrační ikony na PNG nejsou hotová vlastní ikonová knihovna.
- Významové barvy chyb, varování, úspěchu, YouTrack stavů a autorem zvoleného obsahu se nemění podle akcentu. Odkazy a vybraný stav musí být srozumitelné i bez barvy.
- Obsah krátké stránky nemá trvale zabírat pravý sloupec. Výchozí je kompaktní rozbalovací „Na této stránce“; u dlouhého dokumentu lze při dostatku místa využít boční navigaci. Nezobrazovat oba obsahy současně. Na úzké obrazovce musí zůstat funkční tlačítko Menu, hledání a hlavní akce.
- Přepnutí vzhledu zachová otevřenou stránku, rozbalení stromu, rozepsaný dokument, kurzor, undo i spolupráci. Režim ostrovů nesmí ořezat editorové toolbary, popovery nebo fullscreen omnisearch.

**Hranice editoru:** respektovat [existující UI/UX kontrakt](markdown/decision-editor-component-ui.md), jeho typografii, tlačítka, toolbar, popovery, readonly a lazy loading. Shell může předat podporovaný světlý/tmavý režim a veřejné theme tokeny; vzhled editoru se plošnými CSS selektory nepřestylovává. Akcent se do editoru promítá pouze tam, kde to jeho existující veřejné API podporuje. Bez této podpory zůstane editorový akcent stávající. Textová specifikace a skutečné komponenty v galerii jsou zdrojem pro implementaci.

### Reference pro implementaci

Aktuální komponenty, rozložení a jejich světlé i tmavé stavy ukazuje vývojová galerie na `/?gallery=1`.
Její příklady používají skutečné komponenty a místní demonstrační data; zdrojem je
[`ComponentGallery.vue`](../src/Atlas.Web/src/components/dev/ComponentGallery.vue).

### Zakládání prostorů přímo v sidebaru

Přepínač prostoru v sidebaru má na konci nabídky položku „Nový prostor“ (bez prostorů je v sidebaru přímo tlačítko). Vytvoření prostoru není schované pouze v Nastavení. Tlačítko vidí uživatel s explicitním globálním oprávněním zakládat prostory; role Správce existujícího prostoru toto oprávnění automaticky neuděluje. V MVP má toto právo WikiAdmin; samostatné oprávnění CreateCollection je možné pozdější rozšíření.

Kliknutí otevře kompaktní formulář ukotvený u spouštěče: povinný název, volitelná ikona/barva a přístup skupin s rolemi. Popis je nepovinný. Bez vybraných skupin vzniká prostor dostupný zakladateli s přímým grantem Admin a všem WikiAdmin; formulář tuto skutečnost uvádí. Výchozí nastavení nesmí automaticky zpřístupnit prostor celé organizaci.

Vytvořit založí prostor a členství atomicky. Během požadavku zabrání opakovanému odeslání, chyba ponechá formulář vyplněný. Zrušit, Escape nebo kliknutí mimo rozpracovaný formulář nic nevytvoří. Formulář má přístupný název a popisky, sdílený styl panelů a explicitní akce Vytvořit/Zrušit.

Po úspěchu se prostor objeví v sidebaru, vybere se a otevře prázdný přehled s hlavní akcí „Vytvořit první stránku“. Další správa je v nabídce `…` prostoru → Nastavení prostoru: název, ikona, popis a členství. Nastavení tedy slouží pro následnou správu; založení je dostupné přímo tam, kde uživatel s prostory pracuje.

### Přihlášení a účty

MVP podporuje dva při startu registrované moduly: builtin jméno/heslo a obecný OpenID Connect. Hlavní firemní přihlášení vede na JetBrains Hub, lokální cesta je dostupná při zapnutém builtin modulu. WikiAdmin zakládá builtin účty; veřejná registrace je vypnutá. Nový federovaný účet nemá automaticky přístup k obsahu. Autorství a práva patří stabilnímu User.Id a přežijí řízený přechod na jiný IdP. Podrobný kontrakt je v kapitole 8 [architektury](architecture.md).

### Obrazovky MVP

| Obrazovka | Základní obsah a akce |
| --- | --- |
| Domů | Nedávno navštívené, oblíbené a poslední publikované změny z dostupných prostorů; srozumitelný začátek pro nového uživatele |
| Prostor | Popis a uspořádané stránky, vytvoření stránky, správa prostoru dle role |
| Čtení stránky | Obsah, nadpisová navigace, breadcrumbs, oblíbené, odkaz, Upravit podle práv |
| Editace | Existující editor, název, ukládání pracovního stavu, Publikovat a přechod na publikovanou verzi |
| Omnisearch | Hledat / Ctrl/Cmd+K plynule rozvine hledání přes celou obrazovku aplikace; jedno pole pro stránky, sekce a prostory, filtry, klávesnice a návrat do článku; [podrobný kontrakt](mvp-specification.md#vyhledavani) |
| Profil → Vzhled | Osobní rozložení Propojené/Ostrovy, akcent Modrá/Zelená a režim Podle systému/Světlý/Tmavý; náhled, Uložit/Zrušit |
| Moje koncepty | Nové nepublikované stránky a rozpracované stránky, na kterých uživatel pracoval |
| Historie | Autor, čas, revize, náhled a obnovení staršího obsahu jako pracovní verze |
| Koš | Smazané stránky dostupných prostorů, obnova dle práv |
| Nastavení prostoru | Název, popis, ikona, skupiny a role; žádný vlastní identity management, pokud jej poskytuje interní systém |

### Společné interakce

Klávesnice musí zvládnout navigaci, hledání i menu. Fokus je viditelný. Nabídky akcí a jednoduchých voleb nemají viditelný nadpis ani křížek; mají přístupný název a zavření klávesnicí i kliknutím mimo. Vlastnosti editorových komponent používají sdílené ukotvené panely, ne nové dialogy. Zachovat readonly, zrušení návrhu, samostatné undo kroky a lazy loading. Závaznou referencí editoru je [UI/UX rozhodnutí](markdown/decision-editor-component-ui.md).

Každá obrazovka musí mít stav načítání, prázdna, chyby a obnovení pokusu. Chyba ukládání nesmí vypadat jako úspěch. Navigace prohlížečem zpět zachová dotaz, filtry a přiměřeně i pozici výsledků.

## 3. Funkční rozsah P0

P0 znamená nutné před nahrazením současné Wiki.js. Neznamená vše v první implementační iteraci.

| ID | Funkce | Akceptace |
| --- | --- | --- |
| MVP-01 | Builtin a obecné OIDC přihlášení | Lokální jméno/heslo a federace přes JetBrains Hub používají společný účet a session; funguje odhlášení, expirace, reset/deaktivace a odebrání přístupu; žádné párování podle e-mailu |
| MVP-02 | Prostory a členství | WikiAdmin vytvoří prostor, Admin prostoru přiřadí skupiny či jednotlivce s rolí; nepovolený uživatel nevidí ani jeho název |
| MVP-03 | Strom stránek | Vytvoření, přejmenování, změna pořadí a přesun bez cyklů; drag má alternativu v nabídce pro klávesnici |
| MVP-04 | Trvalé odkazy | Přejmenování a přesun nerozbijí odkaz podle ID; staré migrované cesty mají mapování; název není identita |
| MVP-05 | Čtení a orientace | Typografie, breadcrumbs, obsah nadpisů, kopírování odkazu a fungující běžné odkazy i v readonly |
| MVP-06 | Editace a přílohy | Host integruje existující editor, upload a autorizované stahování; po znovuotevření zachová rich obsah |
| MVP-07 | Ukládání a publikování | Uložený koncept se nezamění za publikaci; selhání je viditelné; publikování vytvoří konkrétní neměnnou revizi |
| MVP-08 | Současná editace | Připojit současné Yjs/SignalR, ukázat ostatní editory; ověřit reconnect, publikování a odvolání práv při otevřené relaci |
| MVP-09 | Historie a obnova | Obnova vytvoří pracovní verzi a následně novou publikovanou revizi; nepřepíše historické záznamy |
| MVP-10 | Koš | Smazání odebere stránku z běžného hledání; obnova řeší rodiče i kolizi názvu; trvalé mazání není běžná akce editora |
| MVP-11 | Velmi dobré vyhledávání | Splněná relevance, odezva, české dotazy a autorizace podle search specifikace |
| MVP-12 | Osobní orientace | Oblíbené, nedávné stránky a koncepty fungují jen nad aktuálně dostupnými daty |
| MVP-13 | Migrace Wiki.js | Dry-run, report chyb, mapování odkazů a práv; nepodporovaný obsah se tiše nezahodí |
| MVP-14 | Export a provoz | Export rich dat + Markdown a příloh; ověřená záloha/obnova; audit změn práv, publikací, smazání a obnovy |
| MVP-15 | YouTrack integrace | Povinný serverový modul a editorové napojení: najít issue, vložit odkaz, publikovat a znovu otevřít článek s jeho zobrazením; oprávnění a chybové stavy ověřené na firemní instanci |
| MVP-16 | Osobní vzhled | Nezávislé rozložení a akcent v profilu, světlý/tmavý/systémový režim; persistence po přihlášení, oddělení uživatelů a zachování stávajícího editoru |

### YouTrack je součást prvního vydání

Zadavatel potvrdil YouTrack jako must-have/P0. Předchozí návrh produktového review odložit jej mezi nice-to-have se neuplatňuje. Modul ověří serverovou modularitu na skutečné integraci; samotný mock nebo obecný testovací modul není finální přejímka.

MVP průchod: WikiAdmin nakonfiguruje připojení a povolený rozsah, Writer v editoru najde issue podle ID nebo textu, vloží jej, publikuje článek a oprávněný Reader po znovuotevření vidí odkaz a povolené základní údaje. Dosavadní návrh minimálního obsahu je ID, název a stav issue s odkazem do YouTracku. Zapisování do YouTracku, synchronizace komentářů a celého projektu zůstávají mimo MVP.

Přístup k údajům issue se nesmí odvozovat pouze z toho, že service účet je umí přečíst. Před pilotem se ověří sdílený povolený rozsah projektů pro daný prostor, nebo se implementuje per-user přístup podle architektury. Zamítnutý dotaz neprozradí název ani další metadata. Nelze nahradit chybějící autorizaci otevřením celého projektu.

Akceptace zahrnuje neznámé a nepovolené issue, vypršelý credential, timeout/výpadek, opětovné připojení, restart a technické vypnutí/zapnutí modulu bez poškození uloženého rich obsahu. Při výpadku zůstávají již uložené povolené údaje/odkaz čitelné s označením neaktuálnosti. Běžná wiki funguje dál, ale k přijetí MVP je nutné předvést funkční napojení, nikoliv pouze tento fallback. Autentizace přes Hub sama neposkytuje YouTrack API oprávnění.

## 4. Role, skupiny a přístup prostorů

Přijatý model a jeho akceptaci definuje [rozhodnutí o oprávněních](architecture.md#opravneni). Prostor a kolekce označují stejnou entitu; v UI zůstává Prostor.

Uživatel nebo skupina dostane v konkrétním prostoru pevnou roli `Reader`, `Writer` nebo `Admin`. Writer může editovat, publikovat a pracovat s historií; Admin navíc spravuje prostor, přístupy a obnovu z koše. Při souběhu přímých a skupinových přiřazení vyhrává nejvyšší role. Bez přiřazení není přístup. Žádná pravidla Deny, vnořené skupiny ani ACL jednotlivých stránek v MVP.

Globální `WikiAdmin` spravuje celou wiki, lokální skupiny a má přístup ke všem prostorům i obsahu včetně soukromých konceptů. V MVP také zakládá prostory. Prostorový Admin tyto globální pravomoci nemá. UI výslovně uvádí globální administrátorský přístup.

Nový koncept vidí autor s aktuálním právem Writer a WikiAdmin. Po první publikaci je pracovní verze dostupná Writer/Admin prostoru; Reader vidí pouze publikovaný obsah. Přílohy respektují stejnou viditelnost. Historie je pro Writer/Admin, obnova z koše pro Admin. Žádné veřejné odkazy ani hosté v MVP.

Dva moduly Auth.Builtin a Auth.OpenIdConnect přihlašují ke stejnému lokálnímu User.Id. První externí provider je JetBrains Hub; validované `(iss, sub)` se páruje přes ExternalIdentity. Lokální skupiny, členství a role ukládá NHibernate v databázi Atlas; groups/roles claims nejsou nutné. Externí skupiny lze napojit později přes stabilní ID a explicitní synchronizaci. Práva kolekcí neukládáme do tokenu.

Přesun mezi prostory vyžaduje Writer nebo vyšší roli na obou stranách a zobrazení změny přístupu. Nevyřešená aktivní editace, koncepty nebo vazby příloh přesun blokují. Jednotná serverová autorizace pokrývá API, SignalR, historii, hledání, našeptávání, seznamy a exporty; změna přístupů invaliduje cache a přehodnotí otevřené relace.

Migrační výjimky Wiki.js se vyřeší rozdělením do prostorů nebo explicitní změnou rozsahu před přechodem. Nepodporované omezení se nikdy nepřeloží na širší přístup.

## 5. Jednoduché publikování

Nová stránka začíná jako koncept. Autosave uchovává pracovní stav; tlačítko Publikovat zveřejní konkrétní revizi. Další změny publikované stránky se autosavují do pracovní verze a zveřejní až další publikací. V MVP se neschvaluje po sekcích ani více lidmi. Existující schopnosti editoru zůstanou využitelné pro pozdější workflow.

V realtime editaci publikace musí pracovat s konkrétním potvrzeným stavem: uživatel vidí, zda se publikuje i společná práce. Novější editace po zachycení revize zůstávají pracovní změnou, nesmějí se ztratit. CRDT stav, rich snapshot a publikovaná projekce musí mít jednoznačnou vazbu. To je integrační práce, nikoliv již hotová vlastnost wiki.

MVP neslibuje editaci offline. Při výpadku ukazuje neodeslané změny a riziko zavření karty, dokud není implementováno trvalé lokální úložiště. Znovupřipojení nesmí potvrdit neuloženou změnu jako uloženou.

## 6. Co přijde později

Aktualizace roadmapy 2026-09-25: po přejímce tohoto MVP následuje verze 1.1 s rozšiřitelnými právy prostoru podle typu položky, KeePass trezory, vlastním 2FA, auditem vydání a externím sdílením s expirací pro anonymní čtenáře i ověřené hosty. Verze 2.0 navazuje na převzatou 1.1 a zahrnuje RAG/chat s citacemi, její aktuální autorizací a samostatnou konfigurací chatu a embeddingů. Celý dřívější plán 2.0 včetně tabulí, nového obsahového/synchronizačního jádra, Flutteru a Projectoru je nyní verze 3.0. Níže uvedené P1/P2 jsou historické priority backlogu, nikoli čísla vydání; ostatní položky se tím automaticky nestávají součástí nové 2.0 a již dodané funkce se neodkládají.

P1: štítky v UI a filtrech (migrované štítky lze zachovat už teď), zpětné odkazy, jednoduché šablony, komentáře a zmínky, sledování změn, vlastník obsahu a připomínky revizí, archiv, tmavý motiv a kvalitní tisk. Pokud inventura Wiki.js odhalí některou z těchto funkcí jako nezbytnou, upraví se rozsah před přechodem.

P2: RAG s citacemi, sémantické hledání podle výsledků měření, schvalovací workflow, pravidla jednotlivých stránek, OCR a fulltext příloh, veřejné publikování, hosté, více workspace, další integrace mimo povinný YouTrack a skutečný offline režim. Žádná mikroservisní platforma nebo obecný plugin marketplace pro wiki v MVP.

## 7. Migrace je součást použitelnosti

Zjistit verzi Wiki.js, jazyky, editory/formáty, počty stránek, objem souborů, cesty, kotvy, skupiny a pravidla. Na reprezentativním vzorku ověřit tabulky, kód, diagramy, HTML, obrázky, přílohy a interní odkazy. Značky Wiki.js, které náš editor neumí, musí dostat explicitní převod nebo report a zachování originálu.

Import je opakovatelný podle zdrojového ID/cesty a hashe; vytváří mapu stará URL → nové ID. Oprávnění nemají bezpečný implicitní překlad, nejprve vznikne mapování skupin na prostory. Nespárovaný obsah zůstává v nepřístupném importním prostoru. Přílohy se inventarizují samostatně, nelze předpokládat úplnost Git exportu.

Přechod: záloha zdroje, dry-run, kontrola reportu, krátké uzavření editace zdroje, finální import a přepnutí odkazů. Návrat vyžaduje jasné zacházení s novými zápisy v Atlas; nesmí vzniknout dvě souběžně editované autority. Původní historii uchovat alespoň v dostupném zabezpečeném archivu, pokud se nepřevádí do nové historie.

## 8. Podmínka vydání

Pilotní tým zvládne scénář přihlásit → najít → přečíst → upravit → publikovat → dohledat → obnovit bez podpory vývojáře. P0 scénáře jsou ověřené na reálném migračním vzorku; hledání splňuje samostatnou měřitelnou akceptaci. Nezůstávají nevysvětlené ztráty obsahu ani rozšíření přístupů. Záloha byla skutečně obnovena do čistého prostředí. Povinný YouTrack průchod MVP-15 je ověřen na firemní instanci včetně oprávnění; bez něj nelze první vydání přijmout.


<a id="vyhledavani"></a>

## 9. Omnisearch a kvalita vyhledávání


Datum: 2026-09-06. Stav: navržená akceptace, nikoliv naměřené výsledky. Hledání je hlavní funkce Atlas. RAG neslouží jako náhrada kvalitních výsledků.

### 9.1 Omnisearch: jednotné hledání přes celou obrazovku

Potvrzený požadavek zadavatele: hledání bude omnisearch, který se hezkou plynulou animací zvětší přes celou obrazovku aplikace. Nahrazuje původní návrh malého rychlého hledání s přechodem na samostatnou stránku výsledků. Konkrétní časy a rozměry níže jsou výchozí designové hodnoty k ověření.

#### Otevření a animace

- Položka Hledat v levém panelu a Ctrl/Cmd+K otevřou tentýž omnisearch. Zkratka funguje i z editoru, aniž by měnila obsah nebo historii dokumentu; nesmí přerušit skládání textu pomocí IME.
- Po kliknutí se povrch hledání vizuálně rozvine z místa spouštěče do celého viewportu aplikace. Při klávesovém otevření použije stejný počátek, pokud je viditelný; při sbaleném sidebaru jemně expanduje ze středu horní části obrazovky.
- Orientačně 240–300 ms pro otevření, 180–220 ms pro zavření, klidná ease-out křivka bez pružení a přestřelení. Zaoblený počáteční povrch se změní na souvislou plochu; obsah výsledků jemně přejde do viditelnosti. Neanimovat natahování písmen a neprovádět každý frame nákladný přepočet celého layoutu.
- Vyhledávací pole přijme fokus a psaní ihned při otevření. Animace nečeká na síť a neblokuje dotaz. Rychlé otevření/zavření/přerušení nesmí vytvářet více overlayů, ztrácet napsaný text nebo ponechat uzamčený scroll.
- Finální povrch pokrývá celou aplikaci, včetně sidebaru. Jde o aplikační fullscreen overlay, nikoli browser Fullscreen API. Článek zůstává zachovaný pod ním se svým scrollem a pracovním stavem.
- `prefers-reduced-motion` vypne zvětšování a prostorový pohyb; použije okamžité zobrazení nebo krátký opacity přechod. Na mobilu se respektuje virtuální klávesnice, dynamická výška viewportu a safe-area.

#### Vzhled a obsah

Horní část obsahuje výrazné hledací pole s ikonou, placeholderem „Hledat ve Atlas…“ a nenápadným tlačítkem Zavřít s nápovědou Esc. Pod polem jsou kompaktní filtry. Klidné pozadí, typografie, barvy a řádkové výsledky navazují na Outline a společné theme tokeny editoru. Samotný obsah hledání je uprostřed v přiměřené šířce přibližně 960–1200 px; fullscreen neznamená řádky textu přes celý široký monitor.

Ve výchozím prázdném stavu se zobrazí nedávno otevřené stránky a oblíbené, vždy po aktuální kontrole přístupu. Nový uživatel dostane stručnou nápovědu, žádné fiktivní výsledky. Po zadání dotazu je hlavní plocha věnovaná výsledkům; pole a filtry zůstávají dostupné při scrollování. Na mobilu jeden sloupec, bez povinného bočního náhledu. Náhled článku v druhém panelu není podmínkou MVP.

Omnisearch v MVP hledá napříč dostupnými prostory: stránky, shody v jejich sekcích a samotné prostory. Stránky tvoří hlavní relevance seznam, odpovídající prostory mohou být malá oddělená skupina. Shody v sekcích a názvech příloh odkazují na hostitelskou stránku, nevytvářejí duplicitní výsledky. Jedno pole neznamená AI chat ani obecnou paletu příkazů; hledání osob, externích systémů a provádění akcí je mimo MVP.

Filtry: Prostor a Poslední změna, výchozí řazení podle relevance a alternativně podle poslední publikace. Rozsah obsahu lze zúžit na publikované stránky nebo na dostupné koncepty; výchozí je vše, tedy publikované stránky i koncepty, ke kterým má uživatel přístup, v jednom seřazeném seznamu a s koncepty označenými štítkem. Publikační stav je filtr, do kterého uživatel vstupuje sám, ne výchozí omezení: čerstvě naimportovaná wiki nemá žádnou publikovanou revizi, takže publikovaný výchozí rozsah odpovídal na každý dotaz „nic jsme nenašli“. Každý rozsah si drží vlastní práva — čtení u publikovaných, zápis a pravidlo vlastníka u konceptů — takže sjednocení nerozšiřuje přístup. Archiv, koš a cizí soukromé koncepty se nezobrazují. Štítky se přidají až s jejich produktovou podporou. Vybraný prostor je viditelný filtr, ne skryté omezení podle právě otevřeného článku.

Výsledek obsahuje název, ikonu typu, prostor/cestu, úryvek se zvýrazněním shody a čas publikované změny. Otevření shody v sekci přejde na odpovídající kotvu. Uživatel nemusí přecházet z rychlého dialogu na další stránku: další výsledky se načítají ve stejném omnisearchi dostupným tlačítkem „Načíst další“ nebo rovnocenným přístupným postupem.

#### Klávesnice, fokus a zavírání

- Šipky nahoru/dolů v poli vybírají výsledek, Enter jej otevře. Tab vede přes pole, filtry, výsledky a zavření; fokus se nedostane do zakrytého článku. Skutečné odkazy zachovají otevření v nové kartě.
- Escape nejprve zavře případnou nabídku filtru a teprve další Escape celý omnisearch. Zavření bez výběru obnoví původní článek, scroll a fokus, včetně pozice v editoru. Pokud původní prvek již neexistuje, fokus přejde na Hledat nebo hlavní obsah.
- Fullscreen nemá smysluplnou oblast „kliknutí mimo“, která by ho zavírala; slouží Escape, viditelné Zavřít a navigace Zpět. Volné místo mezi výsledky hledání nezavře.
- Omnisearch je pojmenovaný modal dialog s vhodnou sémantikou vyhledávacího pole a výsledků. Podklad je inertní, oznámení načítání a počtu výsledků přístupné a přiměřeně omezené. Zápis dotazu, otevření a zavření nemění undo editoru.
- Pravidlo menu bez viditelného titulku a křížku platí pro nabídky filtrů/akcí. Omnisearch je samostatná celostránková vyhledávací úloha, nikoli kontextové menu ani formulář vlastností editorové komponenty; viditelné Zavřít je zde záměrné.

#### Návrat, URL a asynchronní stavy

Otevření vytvoří jeden navigační stav hledání. Dotaz, filtry a řazení se promítají do URL přes replace, nikoli novým záznamem za každý znak. Při zavření se obnoví původní adresa. Otevření výsledku zachová v historii stav hledání, aby Zpět vrátilo dotaz, filtry, výběr a scroll výsledků. Přímá adresa hledání otevře stejný fullscreen povrch; bez předchozího článku vede Zavřít na Domů, nikoli mimo aplikaci. Nové otevření přes Hledat začíná prázdným dotazem; obnovení přes historii prohlížeče zachová předchozí hledání.

Rozpracovaný článek se kvůli hledání nesmí zničit ani znovu načíst. Odchod na výsledek respektuje stejnou ochranu neodeslaných změn jako ostatní navigace. Stav při zavření animace a skutečný routovaný obsah musí být synchronizované i při rychlém použití Zpět/Vpřed.

Krátký debounce přibližně 150 ms, rušení starého dotazu a ochrana před opožděnou odpovědí. Výsledky předchozího dotazu se při novém hledání nesmějí tvářit jako aktuální ani být omylem aktivované Enterem. Načítání zachová stabilní rozložení, chyba nabídne opakování bez ztráty dotazu, nulový výsledek vysvětlí filtry. Odezva zůstává v limitu níže a nezahrnuje čekání na dokončení animace jako umělou bariéru.

#### Akceptace omnisearche

- Otevření z viditelného sidebaru, klávesnicí a na mobilu plynule skončí celoplošným povrchem; psaní funguje od začátku.
- Změna dotazu, otevření výsledku, zavření a Zpět/Vpřed zachovají správný stav bez ztráty pracovního obsahu.
- Escape v nabídce filtru nezavře rovnou celou úlohu; další Escape ano. Fokus je zachycen a po zavření vrácen.
- Rychlé opakované spuštění, pomalá síť, chyba, nulové výsledky a opožděná odpověď nevytvoří duplicitní overlay nebo nesprávně otevřený výsledek.
- Reduced motion, zoom 200 %, úzký viewport, virtuální klávesnice a základní čtečka obrazovky mají použitelný průchod.
- Stránky, prostory, úryvky, počty i osobní doporučení respektují aktuální oprávnění; zobrazení overlaye neobchází autorizaci.

### 9.2 Co musí hledání umět

- Názvy, nadpisy, tělo stránky, staré názvy/aliasy a importované cesty. Názvy souborů mohou pomoci nalézt hostitelskou stránku; obsah PDF/DOCX je mimo první vydání.
- Čeština s diakritikou i bez ní, nezávislost na velikosti písmen, přiměřená tolerance překlepů a českých tvarů slov. Normalizace diakritiky sama neřeší skloňování.
- Smíšené české a anglické dokumenty, technické identifikátory, zkratky a čísla chyb. Symboly se nesmějí bez rozmyslu zahodit u dotazů jako C#, .NET, HTTP 401 a názvů konfigurací.
- Přesná shoda názvu má obvykle přednost před volnou shodou v dlouhém těle. Nadpis má větší váhu než běžný odstavec. Přesné identifikátory se nesmějí utopit pod fuzzy výsledky.
- Fráze v uvozovkách, více slov, prefix pro rychlé dohledání názvu. Pokročilý query jazyk není podmínkou MVP.
- Nulový výsledek vysvětlí aktivní filtry a nabídne jejich zrušení; nevymýšlí obsah ani tvrzení o existenci skryté stránky.

Čerstvost je vedlejší signál, nemá vytlačit relevantní stabilní postup. Osobní historie nesmí být nutnou podmínkou dobrého výsledku pro nového zaměstnance.

### 9.3 Bezpečnost a aktuálnost

Stejná služba oprávnění omezuje všechny výsledky a stavy omnisearche. Nepovolený obsah nesmí uniknout v názvu, úryvku, počtu, filtru, návrhu dotazu ani cache. Kontrola proběhne před sestavením odpovědi, ne až po kliknutí.

Index nese ID stránky, ID publikované revize a případně sekce. Publikace vyvolá opakovatelnou indexační úlohu. Smazání nebo odebrání přístupu musí přestat vracet obsah i při opožděném indexu; kontrola aktuální databázové viditelnosti má přednost. Starší job nepřepíše novější revizi. Index lze kompletně obnovit bez ztráty zdrojových dat.

Cache musí být oddělená podle oprávnění a invalidovaná při jejich změně. Logy dotazů mohou obsahovat interní informace: pro měření preferovat anonymizované agregace, přístup a retenci surových dotazů stanovit před sběrem.

### 9.4 Měřitelná přejímka

Před implementací rankingu sestavit alespoň 60 reálných dotazů s očekávanými relevantními stránkami. Zahrnout běžné úkoly, přesné názvy, zkratky, české tvary, překlepy, diakritiku, nulové výsledky a skrytý obsah. Rozdělit na ladicí a oddělenou přejímkovou sadu, neoptimalizovat jen na známé příklady.

| Metrika | Navržený cíl |
| --- | --- |
| Přesný existující název nebo jednoznačný identifikátor | Správný výsledek na 1. místě alespoň v 95 % těchto dotazů |
| Běžný úkol s dostupným správným článkem | Alespoň jeden hodnocený relevantní článek v top 5 u nejméně 90 % dotazů |
| Kvalita celé první stránky | Ruční posouzení top 10 a sledování nDCG@10 pro srovnání variant; cílový práh určit po označení dat |
| Omnisearch v prohlížeči | p95 do 700 ms od posledního stisku po použitelné výsledky, včetně debounce a sítě |
| Backend hledání | p95 do 300 ms při předem dohodnuté běžné zátěži |
| Čerstvost | Nová publikace dohledatelná do 10 s za běžného provozu |
| Izolace | Nula úniků v celé sadě oprávnění včetně změny členství, cache a smazání |

Výchozí syntetický benchmark: 10 000 stránek a 20 souběžně hledajících uživatelů. Není to odhad vaší současné wiki ani garantovaná kapacita. Po inventuře jej nahradit skutečným korpusem a očekávanou zátěží; zaznamenat velikost textů, hardware, souběžné indexování a studenou/teplou cache. Pokud je skutečný rozsah vyšší, přejímka musí použít vyšší hodnotu.

Ukázkové dotazy k nahrazení reálnými: „obnova zalohy“ → Obnova databázové zálohy; „pripojeni vpn“ → Připojení k VPN; „NHibernate“ → interní persistence postup; „HTTP 401“ → správný autentizační troubleshooting. Nejde o tvrzení, že tyto stránky již existují.

### 9.5 Výběr technologie

Databáze ještě není potvrzená. Pokud bude PostgreSQL, začít krátkým prototypem fulltext + pg_trgm nad skutečným obsahem. Doménová data zůstávají v NHibernate; specializovaný parametrizovaný vyhledávací dotaz může být za `ISearchService`.

PostgreSQL podporuje [trigramové porovnání](https://www.postgresql.org/docs/17/pgtrgm.html); samo však nedokládá splnění české relevance. Pokud prototyp nesplní přejímku, porovnat samostatný Elasticsearch, který má [český analyzátor](https://www.elastic.co/docs/reference/text-analysis/analysis-lang-analyzer). Rozhodnutí zaznamenat s výsledky a provozními náklady, ne podle oblíbenosti frameworku.

Azure AI Search zůstává alternativa při rozhodnutí pro Azure; [hybridní hledání](https://learn.microsoft.com/en-us/azure/search/hybrid-search-overview) není samo důvod zavádět cloudovou službu do MVP. Vektory, embeddingy a Microsoft.Extensions.AI přidat až tehdy, když řeší doložené typy dotazů nebo pozdější RAG. První vydání nemusí mít žádnou AI závislost.


<a id="inspirace"></a>

## 10. Inspirace Outline a Wiki.js


Datum rešerše: 2026-09-06. Podklady: veřejná prezentace a dokumentace Outline, oficiální dokumentace Wiki.js. Neproběhl přístup do přihlášeného Outline ani do provozované Wiki.js. Tato rešerše není interaktivní test produktu ani benchmark vyhledávání.

### Co je na dodaném screenshotu dobré

Screenshot zachycuje světlý postranní panel s globální navigací, kolekcemi a vnořenými stránkami. Aktivní stránka má klidné zvýraznění. Horní řádek ukazuje cestu, spolupracovníky a několik akcí. Vedle článku je úzký obsah podle nadpisů. Článek má výrazný název, nenápadná metadata a čitelnou šířku. Barevné kurzory dávají přehled o souběžné editaci.

Pro Atlas přebíráme princip: orientace v levém panelu, dokument jako hlavní obsah, minimum trvale viditelných ovládacích prvků. Screenshot je vizuální reference, nikoliv doklad aktuálního chování všech funkcí Outline. Rozměry a přesné rozložení pro naši implementaci jsou návrhy v MVP specifikaci.

### Ověřené vlastnosti a jejich využití

| Zdroj | Zjištění | Důsledek pro Atlas |
| --- | --- | --- |
| [Outline: produkt](https://www.getoutline.com/) | Nabízí realtime spolupráci, hledání, AI odpovědi a další týmové funkce. | Převzít plynulost práce a důraz na obsah. AI a další integrace jsou odložené; YouTrack je explicitní P0 výjimka pro první vydání. |
| [Outline: Collections](https://docs.getoutline.com/s/guide/doc/collections-l9o3LD22sV) | Kolekce organizují témata a jsou hlavním místem přidělování práv skupinám a uživatelům. Podporují manuální či abecední řazení. | Použít jeden pojem „Prostor“, uvnitř strom stránek. MVP má práva prostoru, bez výjimek jednotlivých stránek. |
| [Outline: import](https://docs.getoutline.com/s/guide/doc/import-D2ZvLqz411) | Import podporuje více formátů; dokumentace upozorňuje na možné ztráty věrnosti a na oddělení obsahu od konfigurace přístupů. | Migraci obsahu, příloh a oprávnění kontrolovat odděleně. Nepředpokládat bezchybný převod Markdownu. |
| [Outline: export](https://docs.getoutline.com/s/guide/doc/export-Da6C7HqL8M) | Export nabízí Markdown, HTML a JSON na úrovni kolekcí. | Umožnit přenosný export obsahu i uchování plného rich formátu. |
| [Wiki.js: skupiny a práva](https://docs.requarks.io/groups) | Práva využívají skupiny a pravidla stránek, včetně pravidel podle cesty. | Při migraci vytvořit explicitní mapu přístupů. Pouhé přenesení stromu nezachová bezpečnostní hranice. |
| [Wiki.js: PostgreSQL search](https://github.com/requarks/wiki-docs/blob/master/search/postgres.md) | Dokumentace označuje databázový engine jako základní kompromis pro vyhledávání. | Kvalitu Atlas měřit na našich dotazech; přítomnost fulltextového indexu není akceptace. |
| [Wiki.js: Git storage](https://raw.githubusercontent.com/requarks/wiki-docs/master/storage/git.md) | Obsah lze synchronizovat s Gitem. Dokumentace popisuje i situace, kdy starší stránky nejsou v repozitáři a vyžadují dosynchronizování. | Git export může být vstup migrace, ale jeho úplnost se ověřuje proti zdrojové wiki. Nelze jej automaticky považovat za úplnou zálohu. |

### Co převzít, upravit a odložit

| Oblast | Převzít | Upravit pro Atlas | Odložit |
| --- | --- | --- | --- |
| Navigace | Stálý sidebar, strom, breadcrumbs | České názvosloví, jedna organizace | Přepínač více organizací |
| Článek | Typografie, obsah nadpisů, kompaktní akce | Náš editor a jeho společné ovládací prvky | Nový editor nebo alternativní editační režimy |
| Hledání | Dostupné odkudkoliv, rychlá práce klávesnicí | Česká diakritika, zkratky, přesné názvy a měření relevance | AI chat jako hlavní vstup |
| Organizace | Prostory, vnořené stránky, oblíbené | Jasné rozlišení konceptů a publikované verze | Univerzální databáze, kanban a kalendáře |
| Práva | Skupiny přiřazené prostoru | Jednoduchý model bez pravidel podle URL | Veřejné sdílení, hosté, libovolné ACL stromu |
| Migrace | Přenosný obsah a přílohy | Jednorázový import Wiki.js s reportem | Obousměrná synchronizace dvou wiki |

Z Outline nepřebíráme branding ani automaticky jeho implementaci. Inspirace slouží pro vlastní Vue aplikaci. Marketingová tvrzení o rychlosti nejsou naměřené cíle Atlas.
