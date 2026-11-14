# Formát zálohy 7 – úplná záloha instalace

Atlas 3 zálohuje celou instalaci se všemi moduly distribuce, i vypnutými. Jeden formát má dva transporty:

- **Git** ([záloha do Gitu](git-backup.md)): strom níže v repozitáři vedle čitelného Markdownu. Historie je v commitech.
- **ZIP** (režim `complete` [ZIP záloh](backups.md)): tentýž strom `.atlas` v jednom archivu, bez Markdownu.

Obnova čte oba.

**Zálohuje se obsah, ne provoz** (rozhodnutí vlastníka 27. 10. 2026). Obsah jsou prostory, stránky s revizemi, publikacemi a organizační historií, štítky, dekorace, přílohy, tabule s položkami a nastavením, objekty (`ContentResource`, jejich historie `ObjectChange`), potvrzený neuložený stav spolupráce (snímek a ještě nezahrnuté aktualizace) a vše, bez čeho obnovená instalace nefunguje: účty, skupiny, granty, trezory a konfigurace. Logy, fronty, relace, jednorázové tokeny, mezipaměti, indexy, počítadla a stav doručování se nezálohují. **Auditní logy** (přístupy, uvolnění tajemství, API, změny účtů a konfigurace) zůstávají provozní: obnovená instalace začíná s prázdným auditem, ten původní zůstává v provozní záloze databáze ([záloha a obnova](backup-restore.md)). Starší formáty se obnovují dál svou cestou: ZIP 1–6 importem archivu, Markdown starého Git repozitáře importem z Wiki.js. Repozitář 2.x pokračuje ve 3 bez přepisu historie: první synchronizace do něj přidá `.atlas/modules`.

## Strom

```text
.atlas/manifest.json                         formát, verze modulů, schéma, id klíče, SHA-256 každého souboru
.atlas/modules/<modul>/<Tabulka>/<klíč>.json jeden řádek databáze, bezztrátově
.atlas/modules/identity/names/<id>.json      veřejné jméno účtu (jako autoři v archivech 2.x a v commitech)
.atlas/blobs/<sha256>                         přílohy a nahrané obrázky prostorů, každý obsah jednou
.atlas/secure/<modul>/<Tabulka>/<klíč>.json.enc  řádek zabezpečené tabulky, šifrovaně
.atlas/secure/vaults/files/<trezor>.kdbx.enc  soubor trezoru ve spravovaném umístění
.atlas/.gitattributes                         jen Git: zakáže převod konců řádků
```

- **Řádek** je objekt JSON se sloupci tabulky, jak je PostgreSQL uloží (`to_jsonb`; čas v UTC, `bytea` jako `\x…`). Klíče jsou seřazené, každý na svém řádku, konce řádků `\n`. Stejná data dají stejné bajty na každém hostu, změněný sloupec změní jeden řádek souboru.
- **Název souboru** je primární klíč: hodnoty spojené `_`, pokud jsou malými písmeny a číslicemi, jinak prvních 32 znaků SHA-256.
- **Manifest** neobsahuje čas běhu. Nezměněná instalace dá stejný manifest i stejný ZIP (položky mají pevný čas).
- **Moduly** a jejich tabulky určuje jediný seznam `InstallationModules`. Tabulka bez modulu musí být v `BackupCoverage` jako odvozená, provozní nebo hostitelská; test pokrytí to hlídá.

| Modul | Obsah | Zabezpečený |
|---|---|---|
| identity | účty, hesla, externí identity, skupiny a členství, vypnutá oznámení, osobní API tokeny (pověření účtu jako heslo), OIDC, Hub, MCP, bezpečnostní nastavení, pravidlo úklidu koše wiki | ano (jména účtů veřejně) |
| platform | wiki, prostory a jejich historie, obrázky prostorů, připnutí, sdílení navenek, pravidla úklidu koše prostorů, obálka obsahu `ContentResource` (i kontakty a poznámky) | ne |
| pages | stránky, revize, publikace, organizační historie, režim přístupu, štítky, dekorace, oblíbené, potvrzený stav spolupráce, mapování importu z Wiki.js (staré cesty dál fungují) | ne |
| attachments | přílohy a jejich vazby na revize; bajty v `.atlas/blobs` | ne |
| vaults | umístění, trezory, tajemství (jen metadata), soubory spravovaných trezorů | ano |
| access | granty prostorů a položek, sdílené odkazy | ano |
| settings | profily úložišť, YouTrack, export PDF | profily a YouTrack ano |
| ai | nastavení, profily a politiky AI | klíče profilů ano |
| boards | tabule s nastavením a umístěním ve stromu, položky včetně skupin a vlastností | ne |
| objects | změny objektů (`ObjectChange`) | ne |
| comments | komentáře k objektům a zmínky účtů v nich (`Comment`, `CommentMention`) | ne |
| projector | stránky označené jako katalog požadavků (bloky požadavků jsou obsah stránek) | ne |

**Odvozené** (po obnově se přestaví): vyhledávací dokumenty stránek, tabulí a komentářů, odkazy mezi stránkami, zmínky kontaktů v publikovaných i pracovních revizích (`PageObjectMention`), katalog entit (`EntityOccurrence`, přes outbox) a zmínky, o kterých publikace už řekly (`PageMentionNotice`, obnova nikomu nic neoznámí znovu), index požadavků Projectoru, příspěvky autorů ke stránkám, AI index (generaci vytvoří správce znovu). **Provozní** (po obnově prázdné): auditní logy a historie změn účtů a konfigurace, přihlášení, relace a jednorázové tokeny, výsledky kontrol poskytovatelů identit, počítadla použití a naposledy navštívené stránky, log hledání a jeho vyřešené mezery, konverzace s asistentem AI, zamítnuté návrhy AI indexu, žádosti o přístup k trezorům, náhledy importu, log operací tabulí (klient po obnově dostane `resync_required` a tabuli načte znovu), nedoručené události outboxu (`OutboxMessage`; odvozená data se přestaví, nepřehrávají), schránka oznámení (`Notification`), průběh migrací kontaktů (`ContactLinkRun`) a stav Git zálohy. Důvod u každé tabulky uvádí `BackupCoverage`. **Hostitelské** (obnova je nechá): žurnál migrací, nastavení a historie záloh, klíč zálohy.

## Citlivá data

- Šifra AES-256-GCM. Klíč zálohy (32 B, base64url, 43 znaků) vznikne při zapnutí Git zálohy nebo režimu `complete`, případně `POST /api/admin/backups/key`. Atlas ho ukáže jednou; správce ho uloží mimo Atlas. Host ho drží chráněný svým Data Protection, ale šifrování na Data Protection nezávisí.
- Šifrování je deterministické (nonce = HMAC cesty a obsahu), takže nezměněný řádek se v Gitu nemění. Cesta souboru je ověřovaná, soubory nejde zaměnit.
- Sloupce chráněné Data Protection (hesla SMB, tokeny integrací, OIDC tajemství, AI klíče, hesla trezorů) jdou uvnitř šifrovaného souboru dešifrované a obnovující host je zašifruje svým klíčem.
- Rotace klíče: další export přešifruje zabezpečenou část. Starší commity a archivy dál potřebují starý klíč.

## Obnova

`POST /api/admin/backups/restore/preview` a `/restore` (ZIP, multipart `file`, `key`, `acknowledge`) nebo `/git/restore/preview` a `/git/restore` (JSON `{ key, acknowledge }`, mělký klon nastaveného repozitáře) – jen správce wiki.

1. Kontrola: SHA-256 všech souborů, žádný soubor navíc, klíč patří k záloze, verze formátu modulů, schéma ne novější než instalace, žádný sloupec, který běžící verze nezná. Náhled ukáže režim, počty řádků, problémy a varování. Záloha staršího schématu projde: sloupec, který v ní chybí, dostane výchozí hodnotu, a co datové migrace od té doby doplnily nebo opravily, doplní krok 3 (`InstallationModules.DataMigrations`). Nová migrace, která plní nebo opravuje zálohované řádky z jiných řádků nebo spouští práci nad nimi, se do toho seznamu přidá ve stejné změně.
2. Instalace musí být prázdná (bez stránek, tabulí a objektů, jako po průvodci). Před obnovou vznikne bezpečnostní ZIP `complete`.
3. Jedna transakce: vypnuté triggery, vyprázdnění tabulek, vložení řádků modulů v pořadí závislostí, zapnuté triggery, datové migrace novější než schéma zálohy, přestavba odvozených dat. Soubory příloh a trezorů se při neúspěchu smažou.
4. **S klíčem** se obnoví vše a účty instalace nahradí účty ze zálohy. **Bez klíče** se obnoví obsah a nastavení bez tajemství: autoři jsou neaktivní účty `restored-…` bez přihlášení, granty a trezory chybí, účet obnovujícího správce zůstane. Odkazy na neobnovené tabulky se vynulují, řádek, který bez nich existovat nemůže, se vynechá a nahlásí.
5. Po obnově restartovat server (načte OIDC a mezipaměti).

## Git

- Každá uložená revize stránky je dál vlastní commit autora. Po každém běhu následuje commit `chore: snapshot installation` (Atlas) se změněnými řádky všech modulů. Bez změn stránek se ostatní moduly (účty, nastavení, tabule, živý stav spolupráce) zapíší nejvýš jednou za 15 minut.
- Export je přírůstkový: řádky starší než horizont posledního snímku (`xmin`) se nečtou celé; po restartu procesu se jednou porovná vše.
- Žádný force push; cizí změny ve větvi se rebasují jako dosud.
