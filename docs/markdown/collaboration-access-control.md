# Oprávnění otevřených kolaboračních relací

Platí pro implementaci `DocumentAccessCoordinator` přidanou ve druhé MIT etapě 5. 9. 2026. Server zůstává vlastníkem oprávnění; lokální readonly stav klienta není bezpečnostní kontrola.

## Registrace a hostitelský kontrakt

`AddAtlasMarkdownSync()` registruje koordinátor i periodickou obnovu oprávnění. Dosavadní `ICollabAuthenticator` ani `IDocumentStore` nezískávají nový povinný člen. Host stále implementuje autentizaci při handshake a `AuthorizeAsync(CollabUser, documentId, CancellationToken)`.

**`AuthorizeAsync` nyní vrací aktuální oprávnění i během živé relace.** Volá se při joinu, před zápisem/seedem, před publikováním obsahu nebo awareness a při periodické kontrole neaktivních relací. Implementace musí respektovat dokončené změny ACL. Případná hostitelská cache musí mít vlastní správnou invalidaci; server nedokáže poznat, že authenticator vrací zastaralý výsledek. Identitu předává zachycený `CollabUser`; nespoléhejte na ambientní HTTP request uvnitř background kontroly.

Každý lookup má vlastní DI scope, takže authenticator smí používat scoped databázové služby. Musí vracet asynchronní úlohu a podporovat cancellation. Neomezeně blokující synchronní hostitelský kód nelze zastavit timeoutem nad Task.

Pro přesnou hranici revokace host po trvalém zápisu nové ACL **awaituje**:

```csharp
// access je DocumentAccessCoordinator z DI.
// Volat až po úspěšném commitu hostitelské změny oprávnění.
await access.RefreshAsync(documentId, cancellationToken);
```

Metoda obnoví všechny živé relace daného dokumentu, včetně více spojení stejného uživatele. Úspěšné dokončení znamená aplikovaná nová oprávnění a zahájené oznámení klientům/úklid skupin; neznamená potvrzení doručení klientského oznámení. Pokud volání skončí cancellation nebo chybou, host nesmí prezentovat dokončenou revokační bariéru. Při neexistujícím dokumentu metoda nevytváří registry položku; budoucí join znovu autorizuje aktuální ACL.

Současný relay má registry v jednom procesu. `RefreshAsync` není distribuovaná bariéra mezi více serverovými instancemi; takové nasazení vyžaduje zvláštní koordinaci.

V dodané aplikaci je pouze `DevelopmentAuthenticator`, který ve vývojovém režimu povoluje zápis. Produkční ACL úložiště a endpoint pro jeho změnu v tomto repozitáři nejsou. Funkční integraci změny ACL ověřují testovací hostitelé se skutečnými WebSockety; produkční host musí napojit vlastní ACL službu.

## Účinnost změny a souběhy

Autorizace, změna peeru a přijetí publikace jsou koordinovány pod dokumentovým zámkem. Kontroly mimo zámek slouží jen jako preflight před vytvořením dokumentu; neopravňují pozdější zápis nebo hydrataci. Po pomalém loadu/bindu a po čekání na seed se oprávnění kontroluje znovu.

- **Write → Read:** spojení dál smí přijímat obsah a awareness; nesmí posílat obsahové aktualizace ani snapshoty. Role snapshottera přejde na jiného zapisovatele.
- **Read/Write → Denied:** peer se odstraní z oprávněných příjemců. Zápis, snapshot, hydratace i další publikace obsahu jsou odmítnuty. Další dokument téhož spojení má vlastní oprávnění.
- **Read → Write:** klient vyžádá nový autoritativní join a povolí psaní až podle jeho výsledku. Po úplném odebrání přístupu či fatal chybě je potřeba nová relace/provider.

Operace přijatá před účinností revokace se může dokončit. Zejména již zahájený durable append/seed nelze zpětně prohlásit za neuložený; seed zůstává vlastněn do vyřešení trvalého výsledku a nesmí jej předběhnout nový seeder. Další zápis už projde novou kontrolou.

Obsahové broadcasty čekají na předchozí `BroadcastTail` mimo dokumentový zámek. Poté se znovu autorizují příjemci a explicitní seznam connection IDs se předá SignalR pod zámkem; na dokončení síťového přenosu se čeká mimo něj. Totéž platí pro awareness a dokumentové flush notifikace. Odstranění ze SignalR skupiny je pouze úklid: jeho selhání nesmí obnovit přístup.

**Bajty předané transportu před revokací nelze odvolat.** Mohou dorazit později. Garance se vztahuje na nové přijetí zápisu a nové publikace po bariéře, nikoli na vymazání obsahu, který už uživatel obdržel.

## Timeouty, dostupnost a náklady

| Volba `MarkdownSyncOptions` | Výchozí hodnota | Význam |
| --- | --- | --- |
| `AccessRefreshInterval` | 5 sekund | Kadence obnovy idle relací; není pevnou SLA. Kontroly zápisu a publikace na tento timer nečekají. |
| `AuthorizationTimeout` | 5 sekund | Maximální čekání na asynchronní lookup, včetně čekání na volný slot. |
| `MaxConcurrentAuthorizations` | 32 | Procesový limit aktivních lookupů a jejich cleanupu. Zahrnuje úlohy ignorující cancellation. |

Výjimka, timeout, null nebo neznámá access úroveň neudělují přístup. U již připojeného peeru neúspěšný lookup odebere oprávnění; dostupnost se nesmí zaměnit za povolení. Po obnově hostitelské autorizace se takový peer musí znovu připojit.

Pozdní odpověď timeoutovaného lookupu nikdy sama neobnoví oprávnění. Jeho DI scope zůstane živý až do skutečného dokončení, cleanup běží mimo dokumentový zámek. Nekončící lookup drží jeden omezený slot; další požadavky po naplnění limitu timeoutují bez zakládání dalších scoped služeb. Peer lookupy v jedné dávce mohou běžet paralelně pod tímto společným limitem.

Kontroly na každé publikaci mají cenu závislou na počtu příjemců a latenci hostitelské ACL služby. Tato etapa nevydává neověřenou cache za aktuální oprávnění. Pro větší instalaci je potřeba změřit ACL backend a případně navrhnout verzované lease s prokazatelnou invalidací. Automatická obnova prochází dokumenty; interval není zárukou dokončení všech kontrol v pěti sekundách.

## Klient a zachování dat

Nová volitelná serverová událost má tvar `DocumentAccessChanged(documentId, canRead, canWrite)`. Nezměnily se návratové typy dosavadních hub metod ani uložené dokumenty.

Odmítnutá hub operace používá již podporované kódy `[FORBIDDEN]` nebo `[READ_ONLY]`. Klient tak skončí bezpečnou chybou i tehdy, když mu asynchronní oznámení změny práv nedorazilo.

Nový provider ihned odebere možnost zápisu. Čistý reader zůstane připojený. Pokud má při odebrání zápisu nepotvrzené změny nebo nedokončený seed, přejde do viditelné chyby a zachová Y.Doc/outbox v aktuální relaci. Denied zastaví transport a další dokumentové události. Generace spojení zneplatní pozdní join, ACK, výpočet hashe a snapshot z doby před revokací.

Zachování změn je zatím **pouze v paměti**; není to obnova po reloadu/pádu ani možnost po revokaci dál odesílat obsah. Starší klient může ignorovat novou událost a jeho UI nemusí ihned změnit readonly stav; aktuální server jeho zápis a příjem kontroluje nezávisle. Aktuální klient se starším serverem nadále používá oprávnění z joinu a nedostává nové serverové garance.

Tato změna neřeší sémantickou důvěryhodnost klientských Yjs snapshotů, autoritativní reducer, serverové epochy ani trvalou offline frontu. Ty zůstávají dalšími etapami celkového plánu.
