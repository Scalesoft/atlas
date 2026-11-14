# Úložiště návrhů dokumentu

Pravidla schvalování nejsou součástí Git provideru. Host implementuje veřejný `IProposalDocumentStore` ze jmenného prostoru `Atlas.Markdown.Server.Proposals`:

```csharp
public interface IProposalDocumentStore
{
    Task<ProposalStoreSnapshot?> LoadAsync(string documentId, CancellationToken ct);
    Task<bool> TryCommitAsync(string documentId, string? expectedVersion,
        ProposalStoreCommit commit, CancellationToken ct);
    Task<IReadOnlyList<DocumentProposal>> ReadHistoryAsync(string documentId,
        Guid proposalId, int limit, CancellationToken ct);
}
```

- `ProposalStoreSnapshot`: konzistentní pohled na schválenou verzi a všechny proposaly, plus neprůhledný token `Version` pro optimistickou konkurenci. Token není revize obsahu.
- `ProposalStoreCommit`: volitelně nový schválený dokument, volitelně jeden změněný proposal a popis zápisu. `null` znamená ponechat beze změny. Přijetí nese obě změny v jednom commitu.
- `ApprovedDocument` a `DocumentProposal`: celé rich JSON hodnoty, revize obsahu, autor a čas; návrh navíc nese základ, stav a posouzenou revizi. Stavy první verze jsou `draft`, `submitted`, `accepted`, `rejected`.

## Povinnosti implementace

`TryCommitAsync` musí v jedné trvalé transakci zkontrolovat očekávaný token, zapsat všechny části změny, připojit historii a vytvořit nový token. `expectedVersion = null` očekává neexistující dokument. `false` znamená pouze konflikt verze; chyby disku nebo databáze vyhazují výjimku. Souběžně může pro tentýž token uspět právě jeden zápis, i při více serverových procesech. Pád nesmí odhalit změněný schválený dokument bez příslušného rozhodnutí.

Čtení vrací konzistentní, oddělené hodnoty; volající nesmí dostat měnitelné interní objekty store ani `JsonElement` navázaný na již uvolněný `JsonDocument`. Uchovává se celý rich envelope včetně pluginových dat. Převod přes Markdown je nepřípustný. Historie je neměnná, řazená od nejnovějšího záznamu a zůstává dostupná i po odmítnutí. Limit API je zatím 50 posledních záznamů; Git uchovává i starší.

SQL provider může v transakci podmíněně změnit řádek dokumentu podle `rowversion`/vlastního tokenu a ve stejné transakci zapsat proposal a historické řádky. Souborový provider potřebuje zámek fungující mezi procesy, trvalý zápis a atomické zveřejnění celého stavu; obyčejné přepsání dvou JSON souborů tento kontrakt nesplňuje.

## Registrace

```csharp
// Před AddDocumentProposals; MyDatabaseProposalStore je implementace hosta.
builder.Services.AddSingleton<IProposalDocumentStore, MyDatabaseProposalStore>();
builder.Services.AddSingleton<IProposalReviewPolicy, MyReviewPolicy>();
builder.Services.AddDocumentProposals();
```

Registrace používá `TryAdd`, takže provider hosta nepřepíše. Pokud host nic nedodá, referenční server používá `GitProposalDocumentStore` nad `ATLAS_MARKDOWN_DATA_ROOT/proposals`. Pouze tento provider vyžaduje spustitelný `git` v PATH. Databázový ani souborový provider nejsou závislé na Gitu. SQL provider se singleton registrací musí vytvářet vlastní kontext/transakci pro operaci, například přes `IDbContextFactory`; nesdílí jeden `DbContext` mezi požadavky.

`IProposalReviewPolicy` je oddělený od persistence i od `ICollabAuthenticator`. Čtení kontroluje právo číst dokument, editace právo zapisovat, rozhodnutí navíc právo schvalovat. V produkci výchozí policy schvalování nepovolí. Query parametr `user` je pouze vývojová autentizace reference serveru, nikoli produkční přihlášení.

## Rozdělení odpovědností

`DocumentProposalService` kontroluje stav, přesnou posuzovanou revizi a základ vůči aktuální schválené verzi. Po neúspěšném CAS znovu načte stav a znovu ověří všechna pravidla. Uzavřený návrh nepřepisuje. Opakované rozhodnutí nad stejnou revizí vrací stejné rozhodnutí bez nového zápisu do historie.

`GitProposalDocumentStore` provádí pouze persistence operace. Jeden strom obsahuje `approved.json` a `proposal-<id>.json`; reference historie se mění jedním Git CAS. Čtenář nejprve zachytí commit a z něj čte všechny položky. Neúspěšný CAS může zanechat nedosažitelné objekty, ale nezveřejní částečný stav. Git volání mají timeout, nepoužívají shell ani společnou pracovní kopii a zapínají fsync. Fyzické selhání disku/napájení nebylo simulováno.

Provozní hranice MVP: 512 KiB na rich hodnotu, 600 KiB na HTTP tělo, 200 návrhů na dokument a 50 posledních záznamů v historii API. Snapshot rozhraní zatím načítá všechny proposaly dokumentu; pro rozsáhlé dokumenty/provozy je potřeba měření a případně rozšíření čtecí části o stránkování. Bez automatického slučování nebo retenčního mazání.

## Ověření a aktuální rozsah

Stejná sada workflow testů běží nad Git providerem i nezávislým paměťovým testovacím providerem. Ověřuje konkurenční schválení, zápis během rozhodnutí, zachování odmítnutých návrhů a jejich historie, opakované rozhodnutí, uzavření návrhu, zachování pluginového JSON a zastaralé revize. API testy ověřují výměnu provideru, práva a odmítnutí nepodporovaného částečného režimu. Browser testy používají skutečné HTTP API a Git provider.

Galerie `?story=proposals` odpovídá backendu: jen přijetí/odmítnutí celku, checkpointy a obnova místních úprav při konfliktu. Nezahrnuje živé Yjs psaní uvnitř návrhu. Existující `IDocumentStore` pro live transport zůstává samostatný; žádné automatické přeznačení nebo migrace jeho dokumentů neprobíhá. Bezpečné živé přesuny bloků zůstávají samostatnou nedokončenou částí.
