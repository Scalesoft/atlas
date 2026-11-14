# Časová platnost oprávnění

Přiřazení role uživateli nebo skupině v prostoru má dva nezávislé časové intervaly:

| Sloupce | Význam |
| --- | --- |
| `StartAt`, `ExpiresAt` | Kdy oprávnění dovoluje přístup. Prázdný začátek je bez omezení počátku; prázdný konec je bez expirace. |
| `ActiveFrom`, `ActiveTo` | Historie změn přiřazení. `ActiveTo = null` označuje aktuální verzi záznamu. |

Přístup platí jen pro aktuální záznam a v intervalu `StartAt <= nyní < ExpiresAt`; prázdná hranice se neomezuje. Uživatel musí zároveň splnit ostatní podmínky přístupu. Role se kombinují až po vyřazení budoucích a expirovaných přiřazení. Expirované oprávnění správce například nezvýší současně platnou roli čtenáře.

V administraci jsou pole **Platí od** a **Platí do** v místním časovém pásmu prohlížeče. API používá `startAt` a `expiresAt` jako datum s časovým posunem a ukládá UTC s přesností PostgreSQL. Obě hranice jsou nepovinné; `null` nebo vynechání v požadavku znamená neomezenou hranici. Klienti, kteří mění jen roli existujícího omezeného přiřazení, musí poslat i jeho zachované hranice. Převrácené, prázdné nebo nekonečné intervaly se odmítají.

Změna role nebo platnosti uzavře dosavadní historický záznam a vytvoří nový. Platí kontrola verze proti souběžné úpravě. Historické údaje ani audit se nepřepisují. Při změně posledního trvalého přiřazení správce musí v prostoru zůstat jiné trvalé, již účinné přiřazení správce.

Dočasný správce nesmí udělit přístup ani vytvořit veřejný odkaz na dobu delší, než je jeho aktuální oprávnění správce. Více současně platných oprávnění správce se posoudí společně; budoucí přiřazení limit nezvyšuje. Globální správce a správce s trvalým oprávněním tento časový limit nemají. Pro veřejné odkazy dál platí stávající nejvyšší doba 30 dnů.

Odkaz se trvale zneplatní, pokud při odebrání oprávnění nebo členství jeho doba platnosti přesahuje zbývající oprávnění autora odkazu. Pozdější obnovení členství nebo role starý odkaz neoživí.

Změna platnosti správce uzavírá původní přiřazení. Pokud autora odkazu v tomto kroku nepokrývá jiné dostatečné oprávnění správce, zruší se i jeho stávající odkazy — včetně změny, která platnost prodlužuje. Jde o konzervativní zachování pravidla, že ukončené přiřazení a následné nové přiřazení neobnovuje staré odkazy. V takovém případě vytvořte nové odkazy; formulář na tento dopad upozorňuje.

Migrace `2026092502`, `2026092503` a `2026092504` postupně doplňují hranice a databázové kontroly, filtrování čtecích cest a zneplatňování odkazů. Původní přiřazení mají nové hranice prázdné a zachovávají dosavadní přístup. Změna neimplementuje trezory, MFA ani vydávání hesel; to jsou následující části verze 1.1.
