# Záloha a obnova jedné instalace

Záloha musí spojovat databázi, soubory a odpovídající vydání aplikace. Export článků není provozní záloha. Následující postup předpokládá podporovaný jeden aktivní host; nový výstup publikace nikdy nepřepisuje běžící aplikaci.

Pro automatické ZIP archivy lze v nastavení záloh vybrat pojmenovaný profil úložiště a relativní složku. Profily jsou společné s [automatickým PDF exportem](pdf-export.md#automatický-export-do-úložiště). Migrace převede dosavadní cíl ZIP záloh do profilu se zachováním šifrovaných přihlašovacích údajů. Účel zálohování musí být v profilu povolený; změna nebo vypnutí profilu během běžící úlohy zabrání jejímu dokončení do změněného cíle.

## Co uchovat

- PostgreSQL databázi včetně historie, grantů, projekcí, účtů a potvrzeného stavu spolupráce.
- Skutečné nakonfigurované adresáře `DataProtection:KeysPath`, `Security:DataPath`, `Authentication:OpenIdConnect:CertificateDirectory`, `Attachments:DataPath`, `ContentImport:DataPath` a `Vaults:DataPath` (soubory KeePass umístění „Na serveru wiki“). Zachovat i používaný `ContentExport:DataPath`, pokud obsahuje rozpracované výstupy.
- Celý odpovídající publikační adresář včetně `wwwroot`, seznam SHA-256 souborů, verzi schématu a bezpečně uloženou provozní konfiguraci.

Databáze (a záloha instalace, která ji přenáší) obsahuje i hodnocení Mapy znalostí (`SkillRating`) s hodnotitelem u každého, také u soukromých a anonymních: kdo má zálohu, vidí, kdo koho jak hodnotil, což aplikace ostatním neukáže. Záloha obsahuje přihlašovací a obnovovací materiál. Přístup omezte stejně jako k databázi a klíčům serveru; zálohy, runtime konfigurace, cookie ani tokeny nepatří do Gitu či běžného testového reportu.

## Konzistentní záloha

1. Ověřit identitu skutečného hostu, databáze a absolutní cesty všech dat. Vytvořit nový adresář zálohy; starší zálohu nepřepisovat.
2. Zastavit tento aplikační host a ověřit, že do instalace nezapisuje další instance. Samotný transakční `pg_dump` nezajistí soulad databáze s právě přibývajícími soubory.
3. Pomocí klienta PostgreSQL 19 (verze serveru nebo novější) vytvořit vlastní archiv: `pg_dump --format=custom --no-owner --no-privileges --file=database.dump`. Cílovou databázi a přihlášení předat bezpečnou konfigurací PostgreSQL, například prostředím a chráněným password file; heslo nepatří do argumentů příkazu.
4. Zkopírovat uvedené datové adresáře a odpovídající aplikaci. Zkontrolovat návratové kódy a vytvořit manifest SHA-256. Teprve po úplné záloze znovu spustit původní host nebo pokračovat plánovaným upgradem.

## Ověření obnovy

1. Vytvořit jinou, novou prázdnou databázi a nové datové adresáře. Pokud databáze již existuje, postup ukončit; nepoužívat `--clean` proti neověřenému cíli.
2. Obnovit archiv přes `pg_restore --exit-on-error --no-owner --no-privileges --dbname=<nová-databáze> database.dump`. Aplikačnímu účtu přiřadit potřebná oprávnění podle cílového prostředí.
3. Obnovit soubory a klíče, porovnat jejich SHA-256 a před startem aplikace porovnat počty a kontrolní otisky řádků všech tabulek. Pro upgrade nejdříve ověřit obnovu odpovídajícím původním vydáním; poté testovat nové vydání na tomto izolovaném cíli.
4. Spustit obnovenou aplikaci na samostatném portu s novými absolutními datovými cestami. Ověřit existující přihlášení, rich obsah i Markdown, publikovanou verzi, skutečné bajty příloh a odmítnutí anonymního přístupu. Zachovat jen souhrn bez tajemství.
5. Před provozním přechodem ověřit stejné chování na aktuální firemní záloze a zdokumentovat návratový postup. Úspěch nad syntetickou testovací instalací tento krok nenahrazuje.

## Archiv wiki s verzemi, které dnešní editor neotevře

Obnova a přidání ZIP zálohy wiki ověří každou verzi každé stránky, historii včetně, stejnými pravidly jako každý jiný zápis (schéma připnutého editoru a limity dokumentu: 2 000 000 znaků JSON a 1 000 000 znaků Markdownu, který z něj napíše server). Kontrola archivu (`/import/preview`) a samotný import používají tatáž pravidla, takže co kontrola projde, import neodmítne až po vytvoření bezpečnostního archivu. Tvary, které Atlas 2.x ukládal a dnešní editor při otevření opravuje, import opraví stejně a kontrola je vypíše (`repaired`, `repairedDocuments`) se stránkou, verzí, cestou a tím, co vypadlo. Týká se to dvou případů:

- odkaz v odkazu: zůstane vnitřní odkaz, vnější se vypíše celý;
- položka seznamu bez úvodního odstavce: dostane prázdný odstavec.

Tučný nebo kurzivní kód je platný (revize schématu 4) a projde beze změny. Archiv s verzemi, které ani tak neprojdou, se odmítne celý (400 `invalid_documents`) se seznamem stránka, verze, název, cesta a důvod a nic se nezmění; převzít je „jak jsou“ nejde ani na výslovnou žádost. Cesty k obnově:

1. Opravit uvedené verze přímo v archivu: `pages/{id}/history/{verze}/content.json` upravit podle cesty a důvodu z kontroly, přepočítat jejich SHA-256 v `manifest.json` a archiv zkontrolovat znovu.
2. Nebo archiv obnovit verzí Atlasu, která ho vytvořila, a na aktuální verzi přejít běžnou aktualizací: uložená data zůstanou čitelná a pracovní či publikované verze, které dnešní editor neotevře, vypíše kontrola obsahu prostoru (`GET /api/spaces/{spaceId}/content-validation`) k opravě ve webu.

Proto u vlastních záloh uchovávat i odpovídající publikační adresář aplikace (viz [Co uchovat](#co-uchovat)).

Restartový test spolupráce a obnova databázové zálohy jsou dvě samostatné kontroly.
