# Local collaboration recovery checkpoints

This opt-in SDK feature records a manual recovery copy of the current collaboration session in browser IndexedDB. It is disabled unless the host supplies a `recovery` handle. The existing network protocol and server save status keep their current meaning.

It records periodic full checkpoints, not a durable journal of every edit. Reloading does not restore, merge or transmit a saved branch. The SDK can prepare an isolated manual preview/export; no recovery picker, preview, comparison or restore UI component is included. See the stage 5 implementation record, stage 6 preview record and implementation plan for verification status and the larger recovery design.

## Enable a branch

The public entry point is `@atlas/markdown/collaboration/signalr`:

```ts
import {
  createIndexedDbRecoveryStore,
  createMarkdownRecovery,
  createSignalRMarkdownAdapter,
  type RecoveryScope,
} from '@atlas/markdown/collaboration/signalr'

// These stable IDs come from the host's verified authenticated session.
const scope: RecoveryScope = {
  serverId: 'production-editor-service',
  tenantId: 'verified-tenant-id',
  accountId: 'verified-account-id',
  documentId: 'wiki:team-handbook',
}

const store = createIndexedDbRecoveryStore({
  databaseName: 'my-app-editor-recovery',
  maxCheckpointBytes: 16 * 1024 * 1024,
  maxTotalBytes: 64 * 1024 * 1024,
})
const recovery = createMarkdownRecovery({
  store,
  scope,
  checkpointDelayMs: 1000,
  maxWaitMs: 5000,
})
const collaboration = createSignalRMarkdownAdapter({
  hubUrl: '/hubs/tiptap',
  documentId: scope.documentId,
  presence: { id: 'caret-id', name: 'Display name' },
  recovery,
})
// Pass collaboration to MarkdownEditor or the controller as usual.
```

`serverId`, `tenantId`, `accountId` and `documentId` are all required, exact host identities. Do not derive them from display names, presence IDs, access tokens or editable browser input. Tokens remain in the existing `accessToken` factory; they do not belong in recovery metadata. Scope partitioning prevents accidental cross-account lookups through this API; it is not authentication, encryption or protection against other code running in the same origin. The host must verify the active account before listing or exposing local copies.

The document ID must match the provider's document ID. One recovery handle attaches to one provider only, including after that provider is destroyed. Create a fresh handle for another tab, document or session; it receives a fresh UUID branch. Several handles may share one host-owned store.

The same optional `recovery` handle is accepted by `new SignalRYjsProvider({ ...options, recovery })` and `useCollab({ ...options, recovery })`. `useCollab` requires `hubUrl` when recovery is enabled; its local-only mode does not capture checkpoints. The adapter passes its handle to the networked session. With low-level APIs, set `field` to the actual Yjs fragment; the default is `default`. Recovery does not enable offline editing: the adapter's existing `allowOfflineEditing` option remains a separate host decision.

## Recording and status

```ts
const unsubscribe = recovery.subscribe(state => {
  // Show this separately from the controller's server save status.
  renderRecoveryStatus(state.status, state.dirty, state.lastRecorded, state.error)
})

// Use at an explicit host checkpoint action while the provider is still alive.
const recorded = await recovery.checkpoint()
console.log(recorded.branchId, recorded.revision, recorded.pendingCount)
```

`subscribe` immediately receives the current immutable state and returns an unsubscribe function. Subscriber failures do not interrupt recording. `getState()` returns the same state shape:

| Field | Meaning |
| --- | --- |
| `status` | `idle`, `recording`, `recorded`, `degraded` or `closed`. |
| `branchId` | This session's independently stored UUID branch. |
| `dirty` | A newer capture is needed; inspect this even when the last completed operation was recorded. |
| `lastRecorded` | The last successful local summary, or `null`; retained after a later storage failure. |
| `error` | The latest recording error, or `null`. |

The default debounce is 1 second and the maximum scheduled wait is 5 seconds from the first dirty notification. Both are configurable integer milliseconds with `1 <= checkpointDelayMs <= maxWaitMs <= 60000`. These are scheduling bounds, not a durability deadline: suspended tabs, a busy event loop and pending storage transactions can delay completion. Work since the last completed checkpoint can be lost if the window terminates. A storage failure sets `degraded`; retries happen after a later edit or explicit `checkpoint()`, without a continuous retry loop.

`checkpoint()` is asynchronous even when called inside a synchronous editor or Yjs callback. Capture happens after the current synchronous edit and its provider enqueue/ACK bookkeeping, using the actual resulting Y.Doc and pending queue. The returned summary covers at least the state requested by that call; changes arriving during a write may require another checkpoint. A clean call can return the existing successful summary. This is not the canonical durable-before-send edit collector proposed for a future full journal.

With the supplied IndexedDB store, success is acknowledged only by the native transaction `complete` event. Read/write transactions request `durability: 'strict'`; failure to create or commit such a transaction is reported, without silently choosing relaxed durability. This does not establish a tested guarantee against power loss or browser-process crashes. Custom stores must resolve `write` only after their own commit boundary. See the documented [transaction durability options](https://developer.mozilla.org/en-US/docs/Web/API/IDBDatabase/transaction) and [transaction completion event](https://developer.mozilla.org/en-US/docs/Web/API/IDBTransaction/complete_event).

## What is stored

Each branch has one current `RecoveryCheckpoint`, replaced atomically by a newer revision. The checkpoint includes:

| Data | Interpretation |
| --- | --- |
| `formatVersion: 1`, `scope`, `branchId`, positive `revision`, `capturedAt` | Local format, identity, revision and capture timestamp. |
| `schema`, `fragment` | Normalized document schema or `null` for legacy, and the actual editor fragment. |
| `observedSequence`, `documentRevision`, `connectionGeneration` | Captured provider bookkeeping; these are not server authorization. |
| `yjsState` | Full encoded local Yjs state, including local work visible at capture. |
| `pendingUpdates` | Captured queue entries with their `id`, owned `update` bytes, `updateCount` and `attempted` flag. |
| `epoch: null`, `replayPolicy: 'manual-only'` | No negotiated server lineage or automatic replay permission. |

A captured pending entry is not a durable server receipt. `attempted` does not prove acceptance, and inclusion of an update in the local full state does not acknowledge it. A successful recovery write does not change the controller's document-save or projection-save status. The store validates metadata and byte ownership; it does not prove semantic validity, authority or replay safety of the Yjs content.

The native store copies and validates input before its first asynchronous wait. Reads return detached checkpoint data. `list(scope)` returns summaries only: scope, branch, revision, capture time, pending count and byte length. `pendingCount` sums the captured entries' `updateCount`; `byteLength` charges binary content plus UTF-8 metadata, rather than attempting to predict exact browser disk usage.

## Inspect or discard a manual copy

```ts
const candidates = await store.list(scope)
const selected = candidates[0]
if (selected) {
  const candidate = await store.read(scope, selected.branchId)
  // Host review operates on an isolated copy with compatible schema/plugins.
  // It must not automatically apply candidate to the live provider.
  showManualRecoveryCandidate(candidate)

  // Only after the host's explicit discard action for this reviewed revision:
  await store.discard(scope, selected.branchId, selected.revision)
}
```

`showManualRecoveryCandidate` is a host UI integration point, not a supplied component. The SDK preview function below prepares validated data for such a UI. On reload, create the normal collaboration session independently; inspect previous branches only through an explicit host recovery flow. Actual restoration requires a separately designed, permission-checked reconciliation or export workflow.

`write` rejects a revision less than or equal to the stored revision. `discard` is a compare-and-swap operation: it rejects if the reviewed revision is missing or has changed. Successful discard replaces the branch with a tombstone; all later writes to that branch are rejected, including higher revisions from another tab. Repeating discard for the same tombstone revision succeeds. Reads return `null` for a tombstone and lists omit it. There is no purge or tombstone reset API; continued recording requires a new branch.

The default limits are 16 MiB per checkpoint and 64 MiB across the database, including tombstone accounting. Quota failure preserves the last good checkpoint and other branches; the store never evicts them. An atomic accounting record and branch lookup avoid scanning every checkpoint during writes. Listing explicitly reads the selected scope's branches. Native IndexedDB can also reject a write because of its own storage limit.

`RecoveryStoreError.code` distinguishes `invalid`, `unavailable`, `closed`, `blocked`, `conflict`, `discarded`, `quota`, `corrupt` and `storage`. A blocked open is rejected, with any late-opened connection closed. A version change closes the store. Missing or malformed accounting fails as corruption rather than silently resetting the budget. The host should surface these failures alongside the retained `lastRecorded` summary.

## Prepare a selected manual preview and export

`prepareMarkdownRecovery` reads exactly the selected branch and revision. Call it in a browser DOM runtime after the host has verified the active account and supplied its expected schema, plugins and fragment. It does not write, discard or close the supplied store, merge pending updates, create a collaboration provider or apply content to a live document.

```ts
import {
  prepareMarkdownRecovery,
  RecoveryPreviewError,
} from '@atlas/markdown/collaboration/signalr'
import type { MarkdownDocumentSchema } from '@atlas/markdown/core'

// The host's expected content configuration, not inferred from an old copy.
const expectedSchema: MarkdownDocumentSchema = {
  version: 2, profile: 'rich', plugins: [],
}
const abort = new AbortController()
const candidates = await store.list(scope)
const selected = candidates[0] // Replace with the host's explicit selection.
if (selected) {
  try {
    const preview = await prepareMarkdownRecovery({
      store, scope, branchId: selected.branchId, revision: selected.revision,
      schema: expectedSchema, fragment: 'default', plugins: [],
      signal: abort.signal,
      maxCheckpointBytes: 16 * 1024 * 1024,
      maxNodes: 100_000,
      maxDepth: 100,
    })
    // Data for host-controlled export actions; nothing is downloaded or sent.
    const richJson = JSON.stringify(preview.projection.rich, null, 2)
    const markdown = preview.projection.markdown
    const plainText = preview.projection.text
    const markdownLossy = preview.projection.markdownLossy
    // Present these values and the fidelity flag in the host's UI.
    // preview.value can also initialize a separate local readonly controller,
    // using preview.schema.profile and the same trusted plugin configuration.
  } catch (error) {
    if (!(error instanceof RecoveryPreviewError)) throw error
    // Surface error.code; the raw checkpoint remains available in the store.
    console.error(error.code, error.message)
  }
}
```

The returned `MarkdownRecoveryPreview` contains deeply immutable JSON: `source: RecoverySummary`, `schema`, `fragment`, `value` and `projection`. `value` is a Markdown string for `markdown-safe`, or a `MarkdownRichValue` envelope for `rich`. `projection` uses the existing `MarkdownProjection` shape: `rich`, `markdown`, `text` and `markdownLossy`. A lossy Markdown export does not replace the rich value. No HTML export method or new UI component is added by this API.

`source` identifies a local recovery copy. Its time, revision, pending count and size are not authoritative server history, proof of acceptance or permission to replay. Scope comparisons do not authenticate the account; the host must verify that identity before exposing local content.

The checkpoint's scope, branch, revision, schema and fragment must match the explicit selection. Legacy `schema: null` is rejected. Current built-in schema revisions are rich 2 and markdown-safe 1; content plugin versions must exactly match the installed host configuration. No migration is run to make an older checkpoint fit. Unresolved Yjs dependencies, a missing document fragment or an additional nonempty root are rejected rather than silently omitted. Pending entries are checked for clock/deletion inclusion in the full state; they are not merged into it, and inclusion does not establish payload authority.

The decoder uses a raw JSON conversion helper currently marked deprecated upstream, with regression coverage, because the newer schema-based converter can repair or delete unsupported nodes. The resulting JSON passes strict editor validation before initialization. Unknown nodes, marks and attributes must not silently disappear. The checkpoint is copied in the first store-read fulfillment callback before neighboring host promise continuations can mutate their returned buffers.

`plugin.create`, extension hooks and serializers are trusted host code; isolation from a live document is not a sandbox. Tie resources allocated during plugin creation or early extension hooks to the registry's `AbortSignal`. Tiptap can throw in an extension's `onBeforeCreate` before its constructor returns an editor instance. Preview still cleans up the registry and aborts its signal, but cannot guarantee `onDestroy` for every partially constructed third-party extension or undo arbitrary constructor side effects.

| Preview option | Default | Maximum |
| --- | --- | --- |
| `maxCheckpointBytes` | 16 MiB | 64 MiB |
| `maxNodes` | 100,000 | 1,000,000 |
| `maxDepth` | 100 | 256 |

All limits must be positive safe integers. Byte accounting includes the checkpoint's binary payloads and metadata. Traversal limits apply to metadata and document content, including XML nodes, text runs and JSON attribute values; they are not simply visible paragraph counts. The document root is depth zero. These bounds are not a CPU timeout or complete adversarial Yjs validation.

`RecoveryPreviewError.code` is `missing`, `stale`, `scope`, `schema`, `content`, `limit`, `aborted` or `storage`. A newer stored revision requires a new selection; it is never substituted silently. Cancellation rejects promptly while waiting for a store read and suppresses its late result. It does not cancel the underlying store I/O, whose interface has no signal, or interrupt synchronous decoding/engine work already running. The browser engine is loaded lazily and destroyed after projection; this API does not promise a pure Node/no-DOM export path.

## Lifetime

Factory creation is synchronous and does not open IndexedDB. The recovery handle starts scheduling after provider attachment. Destroying the provider stops future captures and closes its recovery session; it does not close the shared store. A checkpoint whose data has already been captured and passed to the store may finish after provider destruction. A capture that has not started cannot read a destroyed provider.

When the session ends with a fatal error (the server refused a change with `[INVALID_CONTENT]`, a read-only or
access change, a protocol error), the provider records a last checkpoint before it stops, so the edits the server
never acknowledged stay in the branch: the refused update and every valid edit batched with it or queued behind it. The
checkpoint is captured synchronously from the live outbox and written after any write in flight; the state then reads
`closed` with that `lastRecorded`. The refused update is never resent.

If a host action requires a final local copy, await `recovery.checkpoint()` before destroying the provider. Browser unload is not a reliable place to await that work. Call `unsubscribe()` when the host observer is no longer needed. Call `store.close()` only when all sessions using it have finished: close aborts active transactions and rejects pending opening work. Neither destruction nor close deletes stored branches.

## Optional .NET store capability

`Atlas.Markdown.Sync.IDocumentRecoveryStore` is an optional capability separate from `IDocumentStore`. Existing store implementations do not gain new mandatory members:

```csharp
if (documentStore is IDocumentRecoveryStore recoveryStore)
{
    // Host authentication and document authorization must already have succeeded.
    DocumentRecoveryCheckpoint checkpoint =
        await recoveryStore.LoadRecoveryCheckpointAsync(documentId, cancellationToken);
    // Epoch, SnapshotSequence, Document and SchemaBound belong to one store read boundary.
}
```

`FileDocumentStore` implements this capability. The first explicit capability call can establish a durable lineage UUID in a separate identity sidecar without binding a document schema. The epoch remains stable across appends, snapshot compaction and restart; snapshot baseline, durable tail, retry information and schema state are captured under the document gate, with detached mutable buffers. Invalid identity metadata fails rather than being regenerated silently.

This capability is not called automatically by the current hub/provider protocol. Browser checkpoints still contain `epoch: null`. It introduces neither epoch fencing of writes nor a lineage-rotation API, automatic replay, authorization or multi-process store coordination. A matching epoch alone would not prove that an old pending update is safe to submit. Host code must treat this as a foundation for a later explicit recovery protocol.
