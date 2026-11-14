# Zálohy obsahu wiki

Správa wiki → Provoz → Zálohy jsou dostupné správci wiki. Nastavuje interval (5 minut až rok), obsah zálohy, cílové umístění a počet uchovávaných archivů.

Obsah zálohy:

- **Celá instalace** (`complete`, doporučená a výchozí v nové instalaci): formát 7 ([specifikace](backup-format-v7.md)) se vším obsahem včetně tabulí, kontaktů, komentářů a poznámek, s účty, oprávněními a nastavením v části šifrované klíčem zálohy. Zapnutí režimu klíč vytvoří a Atlas ho ukáže jednou; uložte ho mimo Atlas. Obnovuje se v Zálohy → Obnovení ze zálohy → „Obnovit celou instalaci ze ZIP“ do prázdné instalace; bez klíče se obnoví obsah bez účtů, oprávnění a tajemství. Stažení takového archivu potvrzuje nové přihlášení.
- **Stránky se všemi verzemi** (FULL) a **Aktuální a publikované verze** (LATEST): přenosný archiv stránek a příloh popsaný níže. Tabule, kontakty, komentáře ani poznámky neobsahuje.

Instalace, která už ZIP zálohy nastavila, si svůj režim ponechá. Změny platí bez restartu. Zapnutí nebo uložení plánu vyvolá kontrolu během následující minuty; další kontroly se řídí intervalem. Při výpadku aplikace se po startu provede jedna aktuální kontrola.

## Umístění a test přístupu

Cíl lze vybrat z pojmenovaných **Profilů úložišť** ve Správě wiki → Provoz a doplnit relativní složku pro zálohy. Stejný profil slouží také pro [automatické PDF stránek](pdf-export.md#automatický-export-do-úložiště). Profil musí povolovat použití pro ZIP zálohy. Dosavadní nastavení cíle se při migraci převede do profilu „Zálohy wiki“ bez ztráty přihlašovacích údajů; přímé zadání zůstává kompatibilní.

- Lokální absolutní složka serveru, například `C:\WikiBackups` na Windows nebo `/var/lib/atlas/backups` na Linuxu.
- `smb://server/sdileni/slozka` s odděleným uživatelským jménem, doménou a heslem. SMB zajišťuje přímo .NET knihovna SMBLibrary, i na Linuxu. Není potřeba mount, NFS ani mapovaný disk. SMB sdílení a kořen profilu musí existovat; relativní podsložky profilu se vytvoří při použití; používá se TCP 445.
- „Otestovat přístup“ vytvoří dočasný soubor, zapíše a přečte jeho obsah, ověří přejmenování a odstranění. Výsledek popíše případné selhání.
- SMB heslo je chráněné ASP.NET Data Protection. Prázdné heslo při editaci zachovává uložené; odpověď API heslo neobsahuje. Uchovejte persistentní klíče Data Protection mezi restarty.
- Zálohy se nesmí ukládat pod veřejný wwwroot. Symbolické odkazy a junction se posuzují podle toho, kam skutečně vedou: složka pod junction (například `D:\Data`) projde, odkaz do wwwroot nebo podsložka profilu, která odkazem vede mimo kořen úložiště, ne. UNC cesta se zadává jako smb://, nikoliv jako mapovaný disk.

## Kdy vznikne ZIP

Kontrola porovná historii uložených revizí článků, příznaky šablon a úvodní obrázky prostorů s poslední úspěšnou zálohou stejného režimu a umístění. Samotný přesun článku novou zálohu nevyvolává. Neuložené změny otevřeného editoru nejsou zálohovány.

Beze změn se zaznamená: „Od poslední úspěšné zálohy se obsah nezměnil. Poslední záloha zůstala zachována.“ Archiv se znovu nevytváří ani nestahuje; ověří se jeho existence. Chybějící archiv se vytvoří znovu. Retence probíhá až po úspěšném vytvoření nového ZIPu a maže pouze archivy evidované touto funkcí v daném cíli. Skipped/failed běhy nemažou předchozí zálohy.

Historie zobrazuje čas, výsledek, zprávu, cíl, režim, velikost a stažení dostupného ZIPu. Poslední dobrý archiv zůstává dostupný i po více než 100 kontrolách beze změn. Výstraha správci označuje nenastavené/vypnuté zálohy, chybu, problém retence nebo opožděnou kontrolu.

## Přenosný formát

Každý ZIP je samostatný; nevyžaduje předchozí archiv ani databázový server k přečtení.

- README.md popisuje formát.
- manifest.json obsahuje verzi formátu, režim, čas, otisk historie a SHA-256 jednotlivých souborů.
- wiki.json obsahuje název a seznamy obsahu, včetně popisných jmen autorů.
- spaces/<UUID>/space.json obsahuje metadata prostoru, spaces/<UUID>/cover.json obrázek prostoru, pokud ho někdo nastavil: nahraný obrázek (se souborem cover.png/.jpg/.webp vedle), vybranou kresbu z vestavěného katalogu, nebo volbu „bez obrázku“ (obojí bez souboru).
- pages/<UUID>/page.json a content.md/content.json obsahují metadata a aktuální obsah.
- Historické verze jsou v adresáři history u článku.
- attachments/<UUID>/attachment.json a soubor s čitelným názvem obsahují přílohu.

Markdown používá relativní odkazy uvnitř archivu a je určený také pro převod do jiné wiki. Nativní JSON zachovává bohatý obsah editoru. FULL obsahuje historii a koš; LATEST aktuální a případně odlišné publikované revize bez koše. Přílohy zahrnují soubory potřebné vybranými revizemi.

Šablony jsou běžné stránky a zálohují se stejným způsobem včetně příznaku šablony. Archiv s tímto příznakem používá verzi formátu 3; obnova nadále přijímá starší verze. Obnova starého archivu nezruší příznak u existující šablony.

Nahraný obrázek prostoru (i volba sladění do barev prostoru) se zálohuje od verze formátu 5, kterou archiv dostane jen tehdy, má-li některý prostor nahraný obrázek. Kresba vybraná z vestavěného katalogu a volba „bez obrázku“ se zálohují od verze formátu 6 jako zdroj a klíč kresby, bez souboru; verzi 6 archiv dostane jen tehdy, má-li některý prostor takovou volbu. Automaticky vybíraný obrázek se nezálohuje: určuje se při každém čtení z názvu, popisu a ikony prostoru. Archiv bez vybrané kresby a bez volby „bez obrázku“ tak zůstává ve verzi 5 nebo nižší a přečte ho i Atlas 2.5. Změna obrázku nebo volby vyvolá novou zálohu. Obnova přijímá verze 1–6 a nastaví archivovaný obrázek nebo volbu jako aktuální; kresba, kterou katalog běžící verze nezná, se obnoví jako automatický výběr. Prostor, pro který archiv obrázek nemá, si ponechá svůj. Git záloha obrázky nenese, stejně jako ostatní metadata prostorů.

Prostor v koši (Správa wiki → Koš prostorů) je jen v úplné záloze FULL: vedle space.json leží spaces/<UUID>/trash.json s časem přesunu a dávkou, se kterou šly do koše jeho stránky. Archiv s ním dostane verzi formátu 7. Záloha LATEST prostor v koši vynechá stejně jako stránky v koši. Obnova, která prostor vytváří, ho vrátí do koše i s jeho stránkami; existující živý prostor zůstane živý, protože může mít stránky, které archiv nezná. Obnova přijímá verze 1–7.

Archiv FULL a LATEST je záloha uložených stránek, nikoli databázový dump: neobsahuje tabule, kontakty, komentáře ani poznámky a neobnovuje účty, hesla, přístupové granty, konfiguraci integrací, provozní logy ani fronty. Současný formát nezahrnuje samostatné tabulky štítků/dekorací a historii organizačních přesunů.

## Obnova a přidání vedle

Import je dostupný v nastavení a v úvodním průvodci po vytvoření/přihlášení správce. Nejprve se nahraje ZIP a zobrazí náhled. Validace ověřuje strukturu, kontrolní součty a vazby ještě před změnou obsahu.

- Přidat jako kopie: nové identifikátory stránek/příloh, přípona -restore-… v názvech a přesměrování interních odkazů. UI před potvrzením ukazuje skutečnou příponu. Adresy Atlas používají UUID, nikoli textové slugy.
- Obnovit podle ID: existující články dostanou zálohovaný obsah, chybějící se doplní. Obsah navíc v cíli se nemaže. Na existujících stránkách se obnovené revize připojí k historii, aby neporušily existující historické odkazy; v prázdném cíli se původní revize a jejich časové údaje zachovají. Import nevytváří uživatelské účty ani nepřiděluje oprávnění.
- Před přepisem se musí podařit bezpečnostní FULL archiv. Použije nastavený cíl, nebo lokální .data/backup-safety při úvodní obnově bez konfigurace.

Limity formátu v1: ZIP 1 GiB, rozbalený obsah 4 GiB, textová metadata 256 MiB, 100 000 souborů, 10 000 článků. Překročení skončí chybou; poslední dobrá záloha zůstává.

## Vývojové ověření

Backend: dotnet test s ATLAS_TEST_CONNECTION na izolovanou PostgreSQL.
Živé SMB testy používají ATLAS_TEST_SMB_DESTINATION, ATLAS_TEST_SMB_USERNAME, ATLAS_TEST_SMB_DOMAIN (volitelné) a ATLAS_TEST_SMB_PASSWORD; testovací sdílení musí být vyhrazené. SMB adapter byl ověřen z Windows i Linux .NET proti Linux Samba bez mountování.
