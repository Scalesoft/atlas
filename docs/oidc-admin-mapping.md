# Správci wiki podle skupin v HUBu

V úvodním průvodci modulů i v Nastavení → Firemní účty otevřete svého poskytovatele OIDC. V části **Skupiny a role správců wiki** přidejte pravidlo:

- **Název claimu**: přesný název údaje, který HUB vrací (například `groups` nebo `roles`).
- **Hodnota skupiny nebo role**: přesná hodnota odpovídající skupině správců.
- Oprávnění je **Správce wiki**, tedy globální přístup. Stačí shoda jednoho pravidla; porovnání rozlišuje velikost písmen.

Názvy skupin nejsou pevně dané aplikací. HUB musí příslušný claim vydávat pro tohoto OIDC klienta; podle konfigurace HUBu je třeba doplnit scopes. Samotné přidání scope nezaručuje, že HUB claim vrací.

Uložte nastavení a restartujte server, jak požaduje formulář. Po novém načtení nastavení použijte **Ověřit mapping přihlášením**. Nová karta provede skutečné ověření u poskytovatele a oznámí shodu nebo neshodu. Nevytvoří ani nepropojí účet, nezmění práva a nevypisuje tokeny či seznam claimů. Zkouška musí skončit pod stejnou místní administrátorskou relací, která ji zahájila. Zkoušku můžete provést před dokončením průvodce, nebo nastavení ponechat na později. Po nasazení této funkce se pro zkoušku jednou znovu přihlaste, aby místní relace obsahovala svůj nový identifikátor.

Při běžném přihlášení pravidla platí pro nové i existující účty. Vytváření nových účtů nadále řídí samostatná volba **Povolit vytvoření místního účtu**. Shodný e-mail nepřipojí cizí identitu k existujícímu účtu.

Automatické oprávnění si pamatuje udělujícího poskytovatele. Při dalším úspěšném přihlášení přes něj se neodpovídající grant odebere. Účet má jeden aktuální automatický zdroj; odpovídající přihlášení přes jiného poskytovatele může tento zdroj nahradit. Jde o vyhodnocení při přihlášení, nikoliv o průběžnou synchronizaci HUBu. Změna oprávnění zneplatní staré relace. Ručně udělení správci, včetně existujících účtů před migrací, zůstávají zachováni.

Odebrání poskytovatele či změna jeho issueru neprovede zpětné hromadné odebrání už vydaných práv. Před takovou změnou zkontrolujte a případně odeberte jejich administrátorská práva v administraci uživatelů. Uchovejte místního správce pro obnovu přístupu.
