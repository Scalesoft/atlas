# Autoritativní spolupráce V2 a místní journal

V2 je explicitně zapínaná cesta. Vlastní editor zůstává MIT, nepoužívá Tiptap Cloud, Compare ani beta kodek. Existující V1 klient a hostitelská rozhraní zůstávají dostupná; V2 se při chybě potichu nevrací k V1. Serverovou instalaci, cutover a provozní rozpočty popisuje serverový průvodce.

## Klient

```ts
import {
  createIndexedDbDurableJournal,
  createSignalRMarkdownAdapter,
} from '@atlas/markdown/collaboration/signalr'

const store = createIndexedDbDurableJournal()
const scope = {
  serverId: 'my-service',
  tenantId: authenticatedTenant.id,
  accountId: authenticatedAccount.id,
  documentId,
}
const collaboration = createSignalRMarkdownAdapter({
  hubUrl: '/hubs/markdown', documentId,
  accessToken: () => authentication.currentAccessToken(),
  presence: { id: authenticatedAccount.id, name: authenticatedAccount.name },
  allowOfflineEditing: true,
  durable: {
    store, scope,
    onJournal: status => { localRecoveryStatus.value = status },
  },
})
```

Předejte adapter komponentě `MarkdownEditor` s `profile="rich"`. Pro efektivní odběr obsahu použijte `mode="revision"`; [revizní API](editor-revision-snapshots.md) vysvětluje explicitní snapshoty a kompatibilitu `v-model`. Scope musí dodat autentizovaný host. Názvy z presence ani uživatelem upravitelná URL nejsou důkazem identity účtu. Do journalu se neukládají přístupové tokeny.

Vynechání `branchId` vytvoří samostatnou větev pro každé připojení. Automatické hledání a sloučení cizího tabu se neprovádí. Pro uživatelem vybranou existující větev vytvořte nový adapter s `durable: { store, scope, branchId }` a odpojte předchozí adapter. Server musí nejprve potvrdit schéma, epochu, fragment a aktuální přístup. Potom se větev převezme pomocí CAS. Zápis starého vlastníka skončí konfliktem; již odeslané stejné ID stále podléhá serverové deduplikaci.

`allowOfflineEditing` dovoluje pokračovat po výpadku již načtené a autorizované relace. Nová relace se bez počátečního ověření serverem neotevře k editaci. Odebrání přístupu nebo změna epochy automatický replay zastaví. Změny zůstávají dostupné pro samostatný místní náhled/export.

## Co potvrzuje jednotlivý stav

- Dokončená PM editace zahrnuje její append kroky. V2 collector ji uzavře před externími transaction subscribery; jejich případná reentrantní editace tvoří další dávku. Samostatná Yjs transakce používá hranici `afterAllTransactions`.
- Dávka má neměnné update ID, bajty a SHA-256. Její stav dokumentu a položka fronty se zapíší atomicky v jedné strict IndexedDB transakci. Síťové odeslání čeká na `transaction.complete`.
- `onJournal({ state: 'durable' })` potvrzuje místní kopii, nikoli uložení na serveru. `pending` říká, kolik změn server dosud nepotvrdil. Při storage chybě se stav změní na `failed`; obsah se neoznačí za uložený.
- Položka zmizí z místní fronty až po ověřeném V2 receipt se stejnou epochou, ID a hashem a dostatečnou persisted sekvencí. Ztracený ACK vede k opakování původního ID. Starý ACK po reconnectu neovládá novou relaci.
- Remote změna zapisuje místní obnovovací stav bez nového odchozího echa. Server odvozuje snapshot i Markdown/JSON/text projekce sám. `projectionSave: current` ve V2 odpovídá serverově uložené projekci.

Strict IDB completion je dostupná prohlížečová hranice durability. Neznamená ochranu proti uživatelskému vymazání dat webu, privátnímu režimu, ztrátě zařízení nebo hardwaru, který nedodrží zápis. Nová editace před dokončením jejího zápisu je stále místně nepotvrzená. UI tento interval nesmí označit jako uložený.

## Náhled, export a úklid

Samostatný lazy vstup nevtahuje Yjs do běžné Vue komponenty:

```ts
import { MarkdownRecoveryPanel } from '@atlas/markdown/collaboration/recovery-panel'
```

Panel přijímá `store`, přesný ověřený `scope`, `schema`, případně `fragment`, `plugins`, `resourcePolicy` a `activeBranchId`. Zobrazuje místní větve, izolovaný readonly náhled a stažení úplného JSON nebo Markdownu. Událost `resume(branchId)` předává hostiteli uživatelem vybranou větev k otevření v novém adapteru. Aktivní větev nelze z panelu smazat ani otevřít znovu; odstranění jiné větve vyžaduje druhé výslovné kliknutí a přesný revision/owner CAS.

Bez Vue lze použít `prepareDurableRecovery({ store, scope, branchId, revision, epoch, schema, fragment, plugins, signal })`. Náhled ověří hash pending položek, surový Yjs stav a podporované schéma. Nemění vlastnictví, frontu ani živý editor. Neplatný nebo zastaralý výběr skončí chybou. Markdown export může být ztrátový; úplný JSON je dostupný odděleně.

`store.discard(scope, branchId, revision, owner)` zanechá tombstone, takže pozdní starý tab větev nevzkřísí. Místní úložiště má výchozí agregovaný limit 64 MiB; nemaže tiše cizí větve. `store.close()` ruší rozpracované transakce. Host proto nesmí při běžném zničení editoru zavřít sdílený store před dokončením už zachycených zápisů. Samotný provider store nevlastní a nezavírá jej.

Navigace může před odpojením komponenty použít `getMarkdownJournalCapability(collaboration)?.flushLocal()`. Capability je dostupná po připojení V2 adapteru ke controlleru; při zániku bindingu se z registru odstraní. `flushLocal()` čeká na zápisy zachycené v okamžiku zavolání, ne na serverové ACK ani na budoucí editace. Při chybě journalu odmítne promise. Stejná metoda je volitelně na V2 `useCollab` handle a přímo na `DurableSignalRYjsProvider`. Při plánované navigaci je správné nejprve zastavit nové editace a počkat na tuto hranici; násilné ukončení procesu nemůže asynchronní guard garantovat.

Pro samostatný editor bez spolupráce slouží odlišný [periodický local draft adapter](local-draft-snapshots.md). Jeho časované checkpointy se nevydávají za V2 journal každé editace.
