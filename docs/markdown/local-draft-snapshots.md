# Optional local draft snapshots

`@atlas/markdown/draft` provides periodic local snapshots for a noncollaborative controller. It is an optional, separate entry point with no Yjs dependency. The existing `v-model`, host save timing and controller `documentSave` state remain unchanged.

```ts
import {
  createIndexedDbMarkdownDraftStore,
  createMarkdownDraft,
} from '@atlas/markdown/draft'

const store = createIndexedDbMarkdownDraftStore()
const scope = {
  serverId: verifiedInstallationId,
  tenantId: verifiedTenantId,
  accountId: verifiedAccountId,
  documentId: currentDocumentId,
}
const draft = createMarkdownDraft({ controller, store, scope })
const unsubscribe = draft.subscribe(state => {
  // pending | persisted | failed | closed
  renderLocalDraftStatus(state)
})

// For a deliberate navigation/save action, capture now and await this exact commit.
const recorded = await draft.checkpoint()
// recorded.snapshot = { sessionId, revision, value }
```

Use stable identities supplied by the host's authenticated document context. Presence names and access tokens are not account identities. The scope contains all four required fields and is captured when the session is created. Changing account or document requires destroying this session and creating another with the new verified scope. A scope separates storage records; it is not a browser security boundary against scripts already running in the same origin.

The adapter requires a local controller with the optional content snapshot capability. It rejects collaboration controllers and external structural controllers without that capability. Every adapter creates a distinct UUID branch. Creating it does not load or overwrite editor content.

## Capture and status

The adapter observes lightweight content stamps. Full values are requested after one second without a new content event, or after five seconds of continuous events. This also schedules an initial snapshot. `delayMs` and `maxWaitMs` can explicitly change the intervals; `maxWaitMs` must be at least `delayMs`, positive and at most 60 seconds.

This is a **periodic snapshot draft**, not an every-edit journal. Changes since the latest successful capture may be absent after a tab or process ends. The V2 collaborative journal is a separate API. This adapter does not install an unload handler or claim that a timer survives browser termination.

`checkpoint()` synchronously captures and detaches the current value, then queues its write. The returned promise resolves after the store reports its atomic transaction complete. The record contains the document schema, exact scope and branch, a storage revision, timestamp and the controller snapshot's session/revision identity. Storage revisions and controller revisions are separate counters.

`state.persisted` identifies only the snapshot that completed. If the editor changed while that write was pending, status remains `pending`; the older result cannot label the newer revision as persisted. A content failure remains `failed` even when an older valid write subsequently completes. None of these states confirms a host/backend save or server receipt.

A failed capture or commit reports `failed` and stops automatic retry. The previous successfully committed record remains available. After resolving the storage problem, explicitly call `checkpoint()` to retry the latest value. Subscriber exceptions are isolated.

## Explicit inspection, restore and discard

```ts
const records = await store.list(scope) // Explicit read; includes full owned values.
const selected = records.find(record => record.branchId === selectedBranchId)
if (selected) {
  // Show the value and schema to the user; choose a compatible local controller.
  // The host decides whether and when to replace the current document.
  controller.setValue(selected.snapshot.value)

  // Discard only after the user has approved this exact selected revision.
  await store.discard(scope, selected.branchId, selected.revision)
}
```

`store.read(scope, branchId)` selects a single record. Returned records are detached and deeply frozen. Store validation checks plain metadata and the value envelope; it does not replace the editor's schema validation. The normal explicit `setValue` boundary validates the value before installing it. No migration or recovery apply runs automatically.

`write(record, expectedRevision)` atomically compares the previous storage revision and requires `record.revision === expectedRevision + 1`. Zero creates a new branch. A stale write cannot overwrite a newer draft. `discard` compares the selected revision and leaves a tombstone, so a delayed writer cannot recreate that branch. There is no automatic eviction or tombstone purge. Independent tabs should keep their independently generated branches.

The native store defaults to `atlas-markdown-drafts` and a 64 MiB total byte budget. Both are configurable through `databaseName` and `maxTotalBytes`. It updates the selected record and usage accounting in one transaction without scanning other full draft values. Writes request IndexedDB `durability: 'strict'` and resolve on `complete`, not request success. Aborted transactions and quota errors preserve the previous record. This is not a claim of tested physical power-loss durability.

## Lifetime

```ts
unsubscribe()
draft.destroy()
// Once outstanding work is intentionally finished or no longer needed:
store.close()
```

Destroying the adapter cancels timers and subscriptions. Already captured, detached writes may still finish; no later completion changes its closed state or notifies subscribers. Destroying the controller also closes its adapter. The host owns the store lifetime. Closing the native store aborts its active transactions and rejects future operations; a database version change closes the connection so another tab can upgrade it.

Capture/export can be expensive for large documents. Revision mode avoids full exports per edit, while this opt-in adapter still performs a full snapshot at each chosen checkpoint. The revision-mode typing benchmark does not measure an enabled draft checkpoint or an IndexedDB commit.
