# Průběžná záloha do Gitu

Po každé uložené změně obsahu zapíše Atlas commit do vzdáleného Git repozitáře. Autorem commitu je uživatel, který změnu provedl. Vzorem je Git storage z Wiki.js: jeden takový repozitář má za tři roky asi 1 250 commitů, na každou akci jeden. Funkce doplňuje [ZIP zálohy](backups.md), nenahrazuje je. Neslouží ani jako [provozní záloha databáze](backup-restore.md).

K čemu slouží:
- průběžná historie obsahu, ve které jsou vidět rozdíly mezi verzemi;
- obsah čitelný přímo v Git serveru (GitHub, GitLab, Gitea…), i když Atlas neběží;
- Markdown strom, který lze znovu naimportovat.

Rozhodnutí vlastníka (2026-09-29):
- Používá se git CLI s SSH deploy klíčem, ne knihovna LibGit2Sharp.
- Zálohuje se veškerý obsah.
- Cílem je nový, prázdný repozitář.
- Při prvním zapnutí se přehraje celá historie revizí.

## Nastavení

Správa wiki → Provoz → Zálohy, záložka Git (`#/settings/general/backups/git`). Karta Git repozitář nahoře na stránce ukazuje stav a spouští Synchronizovat teď; vedle ní jsou archivy ZIP. Funkci může nastavit jen správce wiki.

1. **Vygenerovat klíč.** Server vytvoří pár klíčů Ed25519 (`ssh-keygen`).
   - Soukromý klíč se ukládá chráněný přes ASP.NET Data Protection a nikdy neopouští server.
   - Veřejný klíč se zobrazí ke zkopírování.
2. V Git serveru otevřít nastavení repozitáře → Deploy klíče. Vložit veřejný klíč a **povolit zápis**. Deploy klíč platí jen pro jeden repozitář, botí účet proto není potřeba.
3. Vyplnit adresu repozitáře a větev (výchozí `main`).
   - Podporované tvary adresy: `ssh://git@server:port/cesta.git`, `git@server:cesta.git` a `file:///cesta` (pro místní repozitář a testy).
   - HTTPS, jiné protokoly a `ext::` se odmítnou.
4. **Otestovat přístup.** Test spustí `git ls-remote` a `git push --dry-run` do dočasné větve. Ověří tím čtení i zápis, ale v repozitáři nic nezmění.
   - Při prvním spojení se uloží klíč SSH serveru (trust on first use) a v nastavení se zobrazí jeho otisk `SHA256:…`.
   - Pokud se klíč serveru později změní, synchronizace skončí chybou.
   - Změna adresy na jiný server uložený klíč serveru zapomene.
5. Zapnout a uložit. První synchronizace začne hned.

Atlas zapisuje jen do repozitáře, který sám založil. Pozná ho podle souboru `.atlas/repository.json` s identifikátorem této instalace. Do neprázdného cizího repozitáře (například záloha z Wiki.js) nezapíše nic a ohlásí chybu.

## Obsah repozitáře

```
README.md                            popis formátu, vygenerovaný
.atlas/repository.json               značka instalace
.atlas/pages/<UUID>.json             nativní obsah editoru (bezztrátový) a identita stránky
.atlas/modules/<modul>/<Tabulka>/…   celá instalace bezztrátově, řádek po řádku (formát 7)
.atlas/blobs/<sha256>                přílohy a nahrané obrázky prostorů všech revizí, každý obsah jednou
<prostor>/<stránka>.md               Markdown s hlavičkou
<prostor>/<stránka>/<podstránka>.md  podstránky ve složce se stejným názvem
<prostor>/<stránka>.assets/<soubor>  přílohy a obrázky aktuální verze stránky
```

- **Cesty** vznikají z názvů prostorů a stránek:
  - Používají se malá písmena bez diakritiky a pomlčky, například `nasazeni-v-nemocnici.md`.
  - Když se dva sourozenci jmenují stejně, další z nich dostane krátký kód z UUID. Stávající soubor si cestu ponechá.
  - Díky malým písmenům se repozitář dá rozbalit i na Windows. U Wiki.js to nešlo, protože soubory `Docs/Docker.html` a `Docs/docker.html` se liší jen velikostí písmen.
- **Hlavička** stránky obsahuje `title`, `id`, `version`, `published` (publikovaná verze, pokud existuje), `updated` a `author`.
- **Odkazy** na stránky a přílohy jsou relativní cesty uvnitř repozitáře, takže je webové rozhraní Git serveru zobrazí včetně obrázků.
- **Nativní JSON** zachovává rich obsah, který Markdown nepřenese.

Od Atlasu 3 nese repozitář vedle čitelného stromu i celou instalaci ve [formátu 7](backup-format-v7.md): prostory a jejich koš, stránky se všemi revizemi a publikacemi, štítky, dekorace, přílohy, **tabule** s položkami, skupinami, konektory a nastavením, **objekty** (kontakty, osobní knihovna) a jejich historii, **komentáře**, **odkazy ve stromu**, katalog Projectoru a potvrzený neuložený stav spolupráce. Repozitář z 2.x pokračuje beze změny historie, první synchronizace do něj přidá `.atlas/modules` a `.atlas/blobs`.

## Kdy vznikne commit

Synchronizace je úloha na pozadí „Záloha do Gitu“ (`backups.git`, Správa wiki → Provoz → Úlohy na pozadí). Spustí ji publikace, přesunutí do koše nebo obnovení stránky, a to hned po uložení; žádosti během běhu se sloučí do jednoho dalšího běhu. Každý automatický běh ale začne, až nikdo minutu needitoval, nejpozději 30 minut po žádosti (rozhodnutí vlastníka 8. 11. 2026, [úlohy v klidu](background-jobs.md#definice)); git přitom běží se sníženou prioritou procesoru (nice 10, na Windows BelowNormal) a na Linuxu s nečinnou třídou disku (`ionice -c3`). Po 10 minutách bez běhu proběhne sama, nejpozději tedy za 10 minut se zapíše i to, co publikace neohlásí: uložená verze bez publikace, přesun, přejmenování prostoru, ručně upravený řádek v databázi, obnovená databáze nebo ztracený stav zálohy. Mimo úlohu běží ruční „Synchronizovat teď“; zámek běhu je od sebe drží.

Běh porovná aktuální stav databáze s tím, co už do repozitáře zapsal. Nespoléhá na jednotlivé události, proto se po výpadku nebo chybě nic neztratí a vše se samo dorovná. Když od posledního úspěšného běhu není co zapsat ani nedozrál snímek, skončí běh dřív, než sáhne na lokální kopii a síť. První běh po startu aplikace a ruční „Synchronizovat teď“ porovnají a znovu vykreslí vše.

| Změna v Atlasu | Commit | Autor |
| --- | --- | --- |
| Nová stránka | `docs: create <cesta>` | autor revize |
| Uložená verze (nejnovější revize verze) | `docs: update <cesta>` | autor revize, datum revize |
| Přejmenování (nová revize s jiným názvem) | `docs: rename <stará> to <nová>` | autor revize |
| Přesun ve stromu nebo mezi prostory, přejmenování prostoru | `docs: move <stará> to <nová>` | kdo přesunul nebo přejmenoval |
| Přesun do koše | `docs: delete <cesta>` | kdo stránku smazal |
| Obnova z koše | `docs: restore <cesta>` | kdo stránku obnovil |
| Publikace | `docs: publish <cesta>` | kdo publikoval |
| Změněné řádky instalace (tabule, objekty, komentáře, odkazy ve stromu, prostory, účty…) | `chore: snapshot installation` | Atlas |

Podrobnosti:
- Commit má každá verze stránky, ne každé automatické uložení (rozhodnutí vlastníka 8. 11. 2026). Verze jsou tytéž jako v historii stránky (`RevisionVersions`: pauza 30 minut, publikace, přejmenování, pojmenovaná verze…). Běh zapíše za každou stránku nejnovější revizi každé verze, kterou repozitář ještě nemá; otevřenou verzi zapíše jako její dosud poslední revizi a další běh ji zapíše znovu, až v ní přibudou revize. Revize uložené před 3.6 nemají verzi a mají commit každá. Mezikroky automatického ukládání v čitelném stromu nejsou, všechny revize ale zůstávají v `.atlas` a obnova je vrátí všechny.
- Chybějící verze se při dorovnání zapíší postupně jedna po druhé.
- Přílohy jsou součástí commitu revize, která je poprvé použije. Přesun stránky přesune i složku jejích podstránek, a to ve stejném commitu.
- Zpráva commitu končí řádky `Atlas-Page: <UUID>` a `Atlas-Version: <n>`.
- Commity vytváří Atlas (`Atlas <atlas@atlas.invalid>`). Autor má zobrazované jméno a e-mail účtu. Účet bez e-mailu dostane `<uživatel>@atlas.invalid`.
- První synchronizace přehraje všechny verze od nejstarší s původními autory a daty. Cesty přitom odpovídají dnešnímu stromu stránek.
- Snímek instalace následuje po každém běhu, který zapsal stránky. Bez změny stránek, například když se upravuje jen tabule nebo komentář, vznikne snímek nejpozději za 15 minut. Obsahuje jen změněné řádky. Zda se něco změnilo, pozná běh nejdřív z počtů zápisů do zálohovaných tabulek ve statistikách PostgreSQL (bez čtení tabulek); jen když se pohnuly, porovná otisky všech řádků. Statistiky zápis ohlásí do několika sekund po commitu, takže změna z posledních sekund před kontrolou počká na další.
- Neuložené úpravy otevřeného editoru jdou do snímku, jakmile je server potvrdí (stav spolupráce). Čitelný Markdown strom nese jen uložené revize.

Po každém běhu následuje `git push`. Atlas nikdy nepoužívá force push.
- Když push selže, commity zůstanou v lokální kopii. Úloha to zkusí znovu s rostoucím odstupem (5 pokusů od 30 s), pak při příští publikaci nebo po 10 minutách. Správci se mezitím zobrazí výstraha.
- Když do větve zapsal někdo jiný, Atlas udělá `fetch` a `rebase`. Pokud rebase skončí konfliktem, synchronizace se zastaví a ohlásí chybu.
- Lokální kopie (`GitBackup:DataPath`, v kontejneru `/data/git-backup`) slouží jen jako mezipaměť. Když chybí nebo je poškozená, Atlas repozitář znovu naklonuje.

## Bezpečnost

- **Kdo má přístup k repozitáři, vidí veškerý obsah wiki**, včetně konceptů, soukromých poznámek Mého prostoru, stránek s omezeným přístupem a historie smazaných stránek. Trvale smazaná položka zůstává v historii repozitáře. Stejné je to u ZIP záloh FULL. Přístup k repozitáři proto musí mít jen správci.
- To je záměr: pravidlo je úplná obnova bez klíče, takže obsah včetně soukromých položek se nešifruje (šifrování by obnovu bez klíče znemožnilo). Chrání se proto cíl zálohy. Nová adresa repozitáře, nový deploy klíč, nová složka ZIP záloh, nový kořen, server nebo účet profilu úložiště, který ZIP zálohy používají, i přepnutí ZIP záloh do režimu COMPLETE jsou bezpečnostní změna: vyžadují nové přihlášení správce a zapíšou se do auditu přístupů (`backup.destination_changed`) i s novým cílem, v transakci uložení. Stažení archivu COMPLETE chce nové přihlášení také a zapíše se jako `backup.downloaded`. Uložení stejného cíle znovu nic nevyžaduje.
- Obsah (stránky, tabule, objekty, komentáře) je v repozitáři čitelně a bez šifrování. Účty, hesla, přístupové granty a tajná nastavení se do repozitáře dostanou jen zašifrované klíčem zálohy v `.atlas/secure`. Obsah stránek nese jen odkazy na tajemství z trezorů, jejich hodnoty v něm nejsou. Se zapnutým klíčem zálohy ale `.atlas/secure` (stejně jako archiv ZIP COMPLETE) nese i trezory: soubory `.kdbx` spravovaných umístění, hlavní hesla trezorů a hesla umístění na SMB. **Repozitář spolu s klíčem zálohy proto otevře všechna hesla ve všech trezorech**, bez grantů a bez záznamu o vydání hesla. Vydání nového klíče zálohy (tlačítkem i uložením, které zapne Git nebo režim COMPLETE a chybějící klíč přitom vytvoří) je proto bezpečnostní změna jako změna cíle: vyžaduje nové přihlášení správce a zapíše se do auditu přístupů (`backup.destination_changed`, druh `backup.key`, s ID klíče, nikdy se samotným klíčem).
- Soukromý klíč existuje na disku jen během běhu gitu: v datové složce, s právy 0600, a po běhu se smaže.
- Git a SSH běží bez systémové a uživatelské konfigurace, bez agenta a v neinteraktivním režimu. Povolené jsou jen protokoly `ssh` a `file`.

## Obnova

- **Celá wiki** se obnoví z repozitáře bezztrátově (jen správce wiki, do prázdné instalace po průvodci). Klíč zálohy k obnově obsahu není potřeba.
  - V UI: Nastavení → Zálohy → Obnovení ze zálohy → Obnovit celou instalaci z Gitu. Postup je: volitelně vložit klíč zálohy, Zkontrolovat repozitář (náhled ukáže rozsah, počet řádků, varování a důvody, proč obnova nejde), Obnovit instalaci a potvrzení opsáním slova OBNOVIT. Server před obnovou vytvoří bezpečnostní archiv.
  - Bez UI: `POST /api/admin/backups/git/restore/preview` a `/git/restore` s `{ "key": null, "acknowledge": true }`.
  - Bez klíče se obnoví veškerý obsah s historií: prostory, stránky, revize, přílohy, tabule, objekty, komentáře a odkazy ve stromu.
  - Autoři se obnoví jako neaktivní účty `restored-…` bez přihlášení, granty a trezory chybí. Revize uložená přes API nebo MCP si ponechá obsah, ale ztratí odkaz na token, a její kanál se proto změní na `legacy`.
  - Podrobnosti popisuje [formát 7](backup-format-v7.md#obnova).
- Stačí také ZIP stažený z Git serveru nebo `git clone`. Markdown strom s hlavičkami načte stávající import z Wiki.js (Nastavení → Moduly → Import z Wiki.js), ztrátově a s novými identifikátory.
- Konkrétní verzi lze obnovit tak, že se v gitu otevře příslušný commit.

## Ověření

- Serverové testy (`tests/Atlas.Server.Tests/Backups/GitBackup*`) používají skutečný git CLI proti místnímu bare repozitáři přes `file://`. Pokrývají autory, přejmenování, přesuny, koš, přílohy, idempotenci po pádu, cizí repozitář a přehrání historie.
- `InstallationBackupGitTests` vyplní každý sloupec každé obsahové tabulky (hlídá to `InstallationBackupTests.The_round_trip_data_fills_every_column_of_every_content_table`). Pak synchronizují, upraví jen tabuli, naklonují repozitář a obnoví ho bez klíče do prázdné instalace. Výsledek porovnají řádek po řádku včetně bajtů příloh a obrázku prostoru.
- Obraz: Dockerfile má stupeň `runtime` s `git` a `openssh-client`. Z něj vzniká produkční obraz i serverový kontejner Podman e2e.
- `tests/browser/e2e-podman.ps1` spouští v podu SSH git server. `git-backup.spec.ts` projde nastavení v UI, deploy klíč, test přístupu a uložení stránky s obrázkem. Skript pak přímo v bare repozitáři ověří commit, autora, Markdown i obrázek.
