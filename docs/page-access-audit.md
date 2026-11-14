# Přístup ke stránce a historie otevření

Správce stránky otevře **Kdo má přístup**. Přehled odlišuje současné oprávnění, anonymní sdílené odkazy a historii. Poslední otevření vychází pouze z událostí načtení obsahu, nikoli z vytvoření odkazu nebo z prohlížení tohoto přehledu.

V editoru prostoru se příjemce vybírá jako uživatel nebo skupina. Anonymní odkazy mají samostatnou sekci a zůstávají omezené na konkrétní publikovanou revizi stránky. Vytvoření odkazu a jeho expirace se nastavují u stránky; změna oprávnění správce může jeho odkazy trvale zrušit.

## Co se zaznamenává

Migrace `2026092505_page_access_audit.sql` zavádí neměnnou tabulku `PageAccessEvent`. Události jsou:

- `page.opened`: autorizované načtení stránky přes `GetPage`, včetně přesné vrácené revize a uživatele.
- `share.opened`: autorizované načtení stránky přes anonymní odkaz, včetně revize a ID odkazu. Nemá identitu návštěvníka.
- `share.created`: vytvoření odkazu a jeho autor.
- `share.revoked`: explicitní i automatické zrušení odkazu. Pokud transakce nemá přihlášeného aktéra, zobrazuje se systém.

Zápis probíhá v transakci před vrácením výsledku handleru. Při selhání auditu se obsah nevrací; vytvoření nebo zrušení odkazu se vrátí zpět s transakcí. Záznam neprokazuje přečtení člověkem ani úspěšné doručení přes síť. Opakovaná serverová načtení se neslučují.

Historie začíná nasazením migrace, starší údaje se nevymýšlejí. Tabulka neobsahuje text, hesla, tokeny, hashe tokenů, IP ani user-agent. Jména v přehledu se připojují z aktuálního adresáře, identifikátory událostí zůstávají stabilní. Aktualizace a mazání událostí blokuje databázový trigger; databázový superuživatel není touto aplikační ochranou omezen.

## Přístup a rozsah

`GET /api/pages/{pageId}/access-history` vyžaduje současný přístup ke stránce a roli správce jejího prostoru. Historie zůstává i po odebrání přístupu bývalému čtenáři. S přesunem stránky přechází její historie do nového prostoru. Stránkování používá sestupné ID, `beforeId` a limit 1–100; JSON ID i pokračovací kurzor jsou řetězce, aby JavaScript neztrácel přesnost.

Stávající audit účtů, skupin, přiřazení rolí a jejich platnosti zůstává v `AccessAuditEvent`; historie změn obsahu v revizích. Přehled čtenosti `PageView` je nadále analytika hlášená klientem se slučováním 60 sekund a není zdrojem této bezpečnostní evidence.

Tato etapa pokrývá načtení stránky uvedenými dvěma cestami a životní cyklus odkazů. Exporty, jednotlivé soubory, přenosy spolupráce a budoucí vydání/kopírování hesel vyžadují vlastní auditní události v místě vydání dat; nelze je považovat za pokryté záznamem `page.opened`.
