# Trvalá identita nadpisů

Plugin `heading-anchors` používá upstream `@tiptap/extension-unique-id` pouze pro rich nadpisy a ukládá atribut `headingId`. Schéma pluginu má verzi 1. Identita nadpisu není identita wiki stránky ani review sekce.

Pravidla identity před implementací:

| Operace | Pravidlo |
| --- | --- |
| Přejmenování, změna úrovně, přesun | Zachovat ID. |
| Rozdělení uprostřed | Levá část zachová ID, pravá dostane nové. |
| Rozdělení na začátku | Původní nadpis zachová ID; běžný `splitBlock` před něj vloží odstavec bez ID. |
| Spojení | Výsledný nadpis zachová ID prvního nadpisu. |
| Kopie/paste | Nové identity pro zkopírované nadpisy. Přesun v témže editoru je zachová. |
| Obnova revize stejného dokumentu | Zachovat ID obsažená v obnovované revizi. |
| Kopie celého dokumentu | Hostitel výslovně regeneruje všechna ID migračním helperem. |
| Starý dokument | Čistá idempotentní migrace doplní chybějící/neplatná ID a opraví opakování; první platný výskyt zůstává. |
| Readonly/statický render | Nikdy negenerovat ani zapisovat ID dokumentu. |
| Vzdálená transakce | Nikdy ji neopravovat další klientskou migrací ani přegenerováním ID. |

Pro spolupráci provede hostitel migraci jednoho autoritativního dokumentu před publikováním jeho nové verze a zapnutím klientských úprav. Helper se nesmí současně spustit na každém klientovi po synchronizaci. `collaborationReady` je výslovný hostitelský gate: dokud nepotvrdí dokončenou migraci a synchronizaci, plugin ani při lokálních úpravách nepřidává ID. Tento callback sám neprovádí migraci, synchronizaci ani volbu vedoucího klienta.

DOM `id` obsahuje prefix konkrétního pohledu a není součástí uloženého rich JSON. Odkaz pro sdílení používá `#heading=<headingId>` a bázovou URL hostitele. Hostitel může při otevření odkazu použít `navigateToHeading`; stejná URL tedy nevybírá náhodně jeden z několika dokumentových pohledů.

Markdown zachová text a úroveň nadpisu, ale ztratí `headingId`; projekce je proto označena jako ztrátová. Starší klient bez pluginu odmítne rich dokument s tímto schématem. Automatická migrace sdílených Yjs dat na všech klientech není součástí pluginu.
