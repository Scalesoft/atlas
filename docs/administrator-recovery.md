# Obnova přístupu správce ze serveru

Obnova funguje i při výpadku HUBu, vypnutém místním přihlášení, deaktivovaném účtu nebo ztracené roli posledního správce. Není potřeba upravovat databázi.

1. Na přihlašovací stránce zvolte **Obnovit přístup správce**.
2. Na serveru otevřete soubor uvedený ve formuláři (`administrator-recovery.json`; formulář je anonymní, a proto adresář serveru neukazuje). Výchozí cesta je `.data/security/administrator-recovery.json` relativně k content root aplikace; umístění lze změnit serverovou konfigurací `Security:DataPath` (`Security__DataPath` v prostředí).
3. Zkopírujte hodnotu `Token`, zadejte nové heslo a potvrďte obnovu. Pole `Username` v souboru označuje účet, který obnovujete.

Obnova účet aktivuje, nastaví nové místní heslo, přidělí mu ručně spravovanou roli WikiAdmin a rovnou vás přihlásí. Předchozí přihlášení a nevyužité aktivační/resetovací kódy tohoto účtu zneplatní. Ruční role není při příštím přihlášení odebrána mapováním skupin z HUBu. Běžné místní přihlášení zůstává v původním nastavení; pokud je vypnuté, nové heslo samo o sobě běžné přihlášení nepovolí. V obnovené administraci můžete opravit konfiguraci HUBu.

Token je jednorázový a platí sedm dní. Server ho po použití nebo expiraci automaticky nahradí, takže vždy použijte aktuální soubor. Serverový log při vydání nového tokenu uvede účet, platnost a cestu k souboru, hodnotu tokenu ne: kdo čte logy, nesmí se tím stát správcem. Soubor představuje přístup správce a patří pouze provozovateli serveru. Soubor má omezená oprávnění a nesmí být ve veřejném webovém adresáři.

## Pokud chybí účet pro obnovu

Server standardně použije existujícího správce a zapamatuje si vazbu v tokenovém souboru. Tato vazba zůstává použitelná i po odebrání jeho role. Pokud soubor chybí a již není žádný správce, nebo původní účet byl odstraněn, nastavte na serveru `Recovery:Username` (proměnná prostředí `Recovery__Username`) na uživatelské jméno existujícího účtu a restartujte aplikaci. Server pro něj vytvoří nový token; administrátorem se účet stane až po jeho uplatnění. Změna cílového účtu zneplatní předchozí token.

Obnova nevytváří nové účty a před dokončením první instalace je nedostupná. Pro první instalaci slouží samostatný instalační token. Datový adresář se soubory tokenů musí být pro proces serveru zapisovatelný a při nasazení v kontejneru uložený na trvalém svazku.
