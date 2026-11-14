# Yjs boundary corpus

Captured on 2026-09-26 by the Yjs boundary spike (Atlas WP3, R0) from the real editor: a rich controller with
every built-in plugin, bound to a `Y.Doc` through the same Collaboration extension the SignalR adapter uses. Every
update is exactly what the provider sends to `MarkdownSyncHub.SendUpdate`.

- `node-*/`, `editing/`, `all-nodes/`: baseline state, updates and final state per scenario. `manifest.json` lists
  each file with the verdict of the JavaScript observer oracle (`generator/facts.ts`). Final states also contain a few
  edits that were not captured as update files, so they are checked standalone.
- `hostile/`: payloads crafted with raw Yjs, each an independent diff against `000-baseline.state.bin`. The oracle
  accepts 012 and 013 (inserted and deleted in one update) because an observer never sees them; the managed guard
  deliberately refuses them.
- `perf/{50k,256k,1m}/`: `state.bin` plus 200 random edits (`generator/perf.gen.ts`). `perf/4m` and
  `roundtrip/4m-*` were dropped for size (the 4 MiB state is also slightly above the default `MaxDocumentBytes`).
- `roundtrip/`: the perf states re-encoded by yrs 0.19, with their state vectors.
- `yrs-crashes/`: a sample of fuzz inputs that crashed native yrs in process; the managed guard must only return verdicts.
- `schema.json`: the editor schema the corpus was captured with (the source of the former hand-captured relay manifest).
- `documents.json`: a hash of the document real Yjs holds after every step (elements, attributes, text run lengths and
  formats), recorded by `packages/markdown/src/collab/yjsCorpusDocuments.test.ts`; `YjsDocumentTreeTests` requires the
  guard's replica to hash the same.

The spike's generator changed attribute values to ones the editor never writes ("<value> edited", a `#ffeeaa` cell
colour). The oracle only knew names, so it accepted them; since the guard validates values and documents (0.16.0) they
are refused, listed in `YjsCorpus.RefusedValues`.

`generator/` holds the capture scripts (vitest `*.gen.ts` files importing `src/Atlas.Markdown/src`). They are kept as
notes; regenerating also needs the spike's vitest config and aliases.
