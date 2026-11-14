# Revision events and explicit value snapshots

The existing `v-model` API still exports values synchronously and observes deep mutable host changes. For large rich documents, opt in to lightweight content notifications and request full values only when needed.

```ts
import { getMarkdownContentCapability } from '@atlas/markdown/core'

const content = getMarkdownContentCapability(controller)
if (content) {
  const stop = content.subscribe(change => {
    // { sessionId, revision, origin }; no full document is exported here.
    markHostDocumentDirty(change)
  })
  const snapshot = content.getSnapshot()
  // { sessionId, revision, value }; value is detached and deeply frozen.
  await saveHostDocument(snapshot)
  stop()
}
```

Each controller has its own opaque `sessionId`; revisions belong to that session only. A content event identifies one accepted final transaction batch, including appended plugin transactions. Selection and connection changes do not produce synthetic content events. Reentrant edits generate separate ordered events. Subscription does not emit an initial event; `getStamp()` returns the current lightweight identity.

`getSnapshot()` is an explicit synchronous export. It returns the identity of the value captured by that call, not a historical value associated with an older notification. Snapshots are detached from live attributes and deeply frozen. They remain stable after later edits. This API does not add persistence, acknowledgements or a journal. Existing `getValue()` remains a compatible detached mutable value read.

`getMarkdownContentCapability()` returns `null` for external structural controllers. It does not synthesize atomic snapshot guarantees from their ordinary `getValue()` and `state` methods. Existing controller implementations require no new mandatory methods.

## Vue revision mode

```vue
<MarkdownEditor
  mode="revision"
  profile="rich"
  :initial-value="initialRichValue"
  aria-label="Team document"
  @created="controller = $event"
  @content-change="markHostDocumentDirty"
/>
```

This mode uses `initialValue` once and emits `contentChange` metadata. It does not emit `update:modelValue` or `change(value)`. Read an explicit snapshot through the capability, or replace local content explicitly with the local controller's `setValue(value)`. Changing the `initialValue` prop later does not replace the document. Do not combine revision mode with `modelValue`/`v-model`.

For collaboration, pass the normal `collaboration` adapter with `mode="revision"` and omit both local value props. The controller remains the authority for collaboration hydration and save status. Detached collaborative views cannot export a current snapshot while the underlying session continues receiving changes; reattach before reading.

Choose event mode when mounting. Remount with a new Vue key to change it; a remount creates a new session identity. `useMarkdownEditor()` additionally returns `content`, the same capability as the core helper.

## Validation and failure state

Ordinary transaction JSON safety checks walk live node/mark data without allocating a full JSON document. Only recursively frozen plain attribute values are cached. Mutable nested plugin attributes are rechecked, and live attributes are never frozen by this optimization. Final accepted transaction batches also undergo schema and built-in document checks, including final table geometry. This is not a claim that all mutable PM subtrees can safely be skipped.

A content failure changes controller state to `failed`, disables further controller edits and reports degraded save state. A projection failure reports `projectionSave: 'degraded'` and a separate projection error; it does not invalidate a valid rich value. Explicitly retrying a successful projection clears that local projection error. The standard editor surface shows a persistent accessible error for both local and collaborative documents.

Revision mode removes automatic full exports from normal content notifications. Explicit snapshots, visible source projections and compatibility lifecycle captures at detach/destroy can still serialize content. Plugins requesting whole-document projections retain their own cost. Performance results must distinguish revision mode from ordinary `v-model`; neither mode provides a durable server receipt by itself.

An optional [local draft adapter](local-draft-snapshots.md) consumes this capability for periodic, explicitly scoped IndexedDB snapshots. It does not change ordinary content event timing.

## Production benchmark, 2026-09-05

The production fixture used Chromium with 4× CPU throttling, simple paragraph documents and three alternating runs per mode. Each run measured 30 text insertions after five warm-up insertions. The 1,000-paragraph revision mode met the 50 ms p95 synchronous update target in all three runs:

| 1,000 paragraphs, CPU 4× | p50 per run (ms) | p95 per run (ms) |
| --- | --- | --- |
| Compatibility `v-model` | 160.1 / 128.9 / 126.0 | 218.7 / 173.7 / 172.1 |
| Revision mode | 25.4 / 23.0 / 19.6 | 30.2 / 29.7 / 22.8 |

The additional 5,000-paragraph revision stress run measured p95 54.2 / 47.1 / 57.8 ms. Revision samples called no `Node.toJSON`; compatibility samples called it 4,002 times per insertion. Raw results are in `tests/audit/performance-revision-results.json` and `performance-revision-stress-results.json`.

Reproduce with the production build from `tests/audit/performance-reassessment.config.mjs`, serve its output, and run `tests/audit/performance-reassessment.mjs` with `REASSESSMENT_BASE_URL` pointing to that server, `REASSESSMENT_MODES=vue,vue-revision`, `REASSESSMENT_SIZES=1000`, `REASSESSMENT_RATES=4`, and `REASSESSMENT_RUNS=3` under Node 24. This measures synchronous editor updates in the fixture, not browser INP, a physical mobile device, module-heavy content, collaboration, enabled draft persistence, or disk latency.
