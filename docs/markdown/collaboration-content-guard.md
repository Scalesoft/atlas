# Collaboration content guard

The relay (`Atlas.Markdown.Sync`) forwards opaque Yjs payloads by default. A host that must bound what
collaborators can put into a shared document opts in to the managed content guard. It then decodes every client
update, snapshot and initial seed in managed .NET code (no native Yjs/yrs) before the hub stores or broadcasts it.

```csharp
builder.Services.AddAtlasMarkdownSync(options => options.MaxDocumentBytes = 4L * 1024 * 1024);
builder.Services.AddAtlasMarkdownContentGuard();                     // the editor's embedded schema manifest
// or: AddAtlasMarkdownContentGuard(YjsContentPolicy.FromValidator(hostValidator))
builder.Services.AddSingleton<IReservedNodeGuard, MySecretNodeGuard>();   // optional; default rejects reserved nodes
```

Since 0.16.0 the guard enforces the editor's full document rules ([document validation](document-validation.md)):
nothing a client sends reaches storage or other peers unless the editor would open the document it produces.

The reference server (`src/Atlas.Markdown.Server`) opts in this way with a demo secret-node decision.

## What is checked

- **Well-formedness (lib0 v1).** Bounded varints (integers above `Number.MAX_SAFE_INTEGER` are refused, since
  JavaScript would read them differently), strict UTF-8 (lib0 uses a fatal `TextDecoder`), JSON that peers pass to
  `JSON.parse`, known content and type refs, Any nesting of at most 32, names of at most 256 bytes, no trailing
  bytes, and each client listed once. The size limit is `MaxDocumentBytes` for updates and snapshots alike.
- **Integration.** Every struct is resolved the way Yjs does (`Item.getMissing`). Anything Yjs would leave pending is
  refused: clock gaps and skip structs, unknown origins or parents, forward references within a client, reference
  cycles between clients, and delete ranges beyond the known state.
- **Content** (`YjsContentPolicy`). Only the allowed root fragment is used. Element names, attribute keys per node and
  mark names (including y-prosemirror's `name--hash` keys) must be in the schema. Content may only sit where
  y-prosemirror writes it: elements under the fragment or other elements, strings and marks inside XmlText,
  attribute values as Any on elements. Embeds, binaries, legacy JSON content, subdocuments and other shared types are
  refused. **Every** struct is checked, including content that the same payload deletes again. A replica observer
  never sees such content, but the relay would still forward it byte for byte.
- **Attribute values** (`InvalidAttributeValue`). Every attribute value a payload writes, also one it overwrites or
  deletes again, must be plain JSON (no binary, bigint, undefined inside a value or non-finite number) and pass the
  attribute's constraint from the manifest. An attribute set to undefined is absent, as y-prosemirror reads it. Rules
  that read a sibling attribute (`colwidth` reads `colspan`) check the value alone.
- **The document** (`InvalidDocument`). The guard keeps a replica of the admitted document (`YjsDocumentTree`: Yjs
  integration with YATA conflict resolution, splits and delete sets) and applies each payload to it with a journal.
  Every element the payload changes (attributes, children or the text of its XmlText runs), and the table around a
  changed row or cell, is validated with `MarkdownDocumentValidator` as y-prosemirror builds the ProseMirror node: its
  attributes (absent ones take their defaults, required ones must exist), its children's types against its content
  expression, each text run with the formats in force as marks (allowed by the element, mark attributes by their
  constraints), plugin ownership against the document's bound schema (all plugins of the manifest for a legacy document
  without one), table size bounds, and 64 node levels. Elements inside deleted content are invisible
  and not judged. A refused payload is rolled back exactly; an empty fragment is accepted (y-prosemirror fills it with
  an empty paragraph).
- **Merges of concurrent edits.** Yjs can merge two edits that were each valid into a document the editor would not
  write itself; y-prosemirror shows it (it builds nodes without checking their content) and the editor repairs it.
  Refusing the update that arrives second would stop its author's session, so the guard does not hold collaborative
  content to the rules a merge breaks (found by `YjsGuardFuzzTests` in concurrent real-editor sessions; stored
  documents keep them all):
  - marks that exclude each other (code with bold or a link before rich revision 4; a mark repeated): one peer formats text another peer formatted, or types
    where a format is in force that its own document had cleaned up (Yjs deletes redundant formats on receipt, and the
    result depends on the history);
  - the order and count of children (two peers delete the two paragraphs of a quote or the two items of a list), and
    table geometry including empty tables (one peer adds a row while another adds a column, or both delete rows);
  - rules reading a sibling attribute (one peer merges cells while another resizes the column).

  Moving such an element or restoring it by undo creates it anew with the same content, so new elements are judged
  the same way, and so are snapshots and seeds. Names, attribute values, the child types an element allows, marks
  allowed by the element, plugin ownership, depth and table size bounds hold everywhere: an element's type, parent and
  depth never change in Yjs, so no merge breaks them.
- **Reserved nodes** (secret references). A new reserved node, or an attribute write on an existing one, is handed to
  the host's `IReservedNodeGuard` with the document id, author, current peers and the written attributes as JSON.
  The guard is asynchronous and gets one request per payload. Content inside a reserved node is always refused.
  Without a host guard every reserved node change is refused.
  - `ReservedNodeRequest.Payload` says what the changes come from: `Update` (a client update), `Snapshot` (a full
    state for a document that already has admitted content) or `Seed` (the first state of an unseeded document).
    A snapshot or seed carries the whole document, so every reserved node in it is reported with `IsNewNode`.
  - `ReservedNodeChange.CurrentAttributes` holds the node's attribute values as the server last admitted them, and
    `CurrentSecretId` its current `secretId`: for an attribute rewrite of an existing node, and for a node a snapshot
    sends again. It is empty for a node the payload creates and for a seed. A host checks the old and the new id
    (`CurrentSecretId` against `Attributes["secretId"]`), for example that the author may manage the old reference
    and every peer may discover the new one. Under concurrent writes of one key, Yjs may settle on another admitted
    value than the last one the server saw.
  - Both are additive: a guard written against the earlier request compiles and behaves as before.

```csharp
public sealed class SecretNodeGuard(ISecretAccess access) : IReservedNodeGuard
{
    public async ValueTask<bool> AdmitAsync(ReservedNodeRequest request, CancellationToken ct)
    {
        foreach (var change in request.Changes)
        {
            var next = change.Attributes.TryGetValue("secretId", out var id) ? id.GetString() : change.CurrentSecretId;
            // A snapshot re-sends admitted nodes: only an unknown node or a changed id needs a new decision.
            if (change.CurrentSecretId is { } current && current == next) continue;
            if (change.CurrentSecretId is { } previous && !await access.MayManageAsync(request.AuthorUserId, previous, ct)) return false;
            if (next is null || !await access.MayDiscoverAllAsync(request.PeerUserIds, next, ct)) return false;
        }
        return true;
    }
}
```
- **Snapshots.** A snapshot must be self-contained and pass the policy on its own, because joining peers load it as
  is, and its whole document is validated. For a seeded document it may not contain clocks that were never admitted.
  The first snapshot of an unseeded document (the seed) becomes the document's index.

Admission is ordered: structural scan and value checks, apply to the replica and validate the changed elements, collect
reserved changes, await the host decision, and only then record the payload in the per-document index and keep it in
the replica. A rejected payload changes nothing. The sender receives
`[INVALID_CONTENT] … (<ContentRejectionReason>)`, which the web provider treats as fatal: resending the same change
could never succeed, so the session fails instead of retrying forever. The editor stays readable with the unsent edits,
reports `collaborationStrings.contentRefused` (the change was refused, the document no longer saves, copy the unsent
edits), and with a [recovery branch](collaboration-recovery-checkpoints.md) records a last checkpoint holding them; the
durable (V2) provider keeps the refused batch in its journal for `MarkdownRecoveryPanel`.

## Index and lifetime

The index keeps, for every clock, the parent a struct integrated under (consecutive runs share one segment), plus
kind and node name of every live shared type, and the last admitted attribute values of reserved nodes (the
source of `CurrentAttributes`), at roughly 0.3–1× the state size. With document validation it also holds the replica:
every item with its position, deletion, attribute value or format, but no text, about 4–5× the state size (4.4 MiB for
the 1 MiB perf document of 25,000 items), about 300 bytes per item with its index. Adjacent items of one client merge
once a payload is kept, as Yjs merges them, so typing extends a run instead of adding an item per keystroke: a 2-hour
real-editor typing session (36,000 keystrokes at 5 a second, 28,992 updates) leaves 9,674 items, as many as a rebuild
from its final state, where the replica without merging held 31,076 (update p95 0.20 → 0.06 ms). An update or snapshot that would
leave more than `YjsLimits.ItemBudget` items (65,536 plus one per 16 integrated bytes) is refused with `TooManyItems`:
a hostile 4 MiB update of 400,000 one-character items kept 119 MiB before and is now refused in 1 s, leaving 0.3 MiB
behind. A stored state is never refused for it. YATA conflict resolution passes at most
`YjsLimits.MaxConflictScan` items per payload (inserts of thousands of clients at one position are quadratic). Both are built on first use from the hub's in-memory baseline, i.e. the snapshot and log loaded from the
host's `IDocumentStore`. It lives exactly as long as that baseline: the hub renews its content session on reload,
reseed, or when an admitted update or seed fails to persist. All calls for one document run under the hub's
per-document gate, the same critical section as the durable append.

Measured on the Yjs boundary corpus (perf documents with 200 random real edits, Release build):

| Document state | Update p50 / p95 | Snapshot / seed check, one document (p50 / p95) | Four documents at once (p50 / p95) | Allowlist only (before 0.16) |
| --- | --- | --- | --- | --- |
| 55 KiB | 0.03 / 0.10 ms | 1 / 2–5 ms | 5–8 / 10–24 ms | seed 3 ms |
| 264 KiB | 0.03 / 0.17 ms | 5–10 / 6–27 ms | 30–48 / 43–78 ms | seed 3 ms |
| 1 MiB | 0.03 / 0.35 ms | 22–48 / 43–58 ms | 74–173 / 121–221 ms | update p95 0.014 ms, seed 12–14 ms |

Snapshot and seed checks were re-measured on 2026-10-12 (64-core workstation, 12 rounds per document after 2 warm-up
rounds, a fresh guard per document): the lower values with server GC (`DOTNET_gcServer=1`, as ASP.NET Core runs), the
higher with workstation GC. Four documents validating at once share memory bandwidth and the collector, so each takes 3–4×
longer; one document's checks never wait for another's (each runs under its own gate).

An update costs the size of the elements it changes: typing re-checks one paragraph; adding or removing a top-level
block re-checks the document's content expression over its blocks (about 2,000 in the 1 MiB document), which is the
p95. A snapshot or seed validates the whole document once.

## Manifest

The default policy (`YjsContentPolicy.CreateDefault()`) is derived from the editor's schema manifest embedded in the
package (`MarkdownSchemaManifest.Builtin`, generated by `describeMarkdownSchema` from every built-in plugin) with the
secret references as reserved nodes. A host with another plugin set passes
`YjsContentPolicy.FromValidator(new MarkdownDocumentValidator(manifest))`, where the manifest is the built-in one
narrowed with `WithPlugins(ids)` or its own `describeMarkdownSchema` export. The constructor with name lists remains
as a bare allowlist without value or document validation. The former `yjs-content-manifest.json` and
`YjsContentPolicy.FromManifest` are gone.

## Scope

The guard covers the V1 relay (`SendUpdate`, `PushSnapshot`, seed). The V2 authority path validates through its own
reducer. `SnapshotValidation` still checks only the snapshot envelope (its projections are the snapshotter's claim;
the Yjs state is what the guard validated). Tests: `tests/Atlas.Markdown.Sync.Tests` (`YjsCorpusTests`,
`YjsContentGuardTests`, `YjsDocumentValidationTests`, `YjsDocumentTreeTests`, `YjsRobustnessTests`,
`ContentGuardHubTests`) with the corpus in `Fixtures/yjs-corpus`. `YjsDocumentTreeTests` requires the replica to hold
the document real Yjs holds after every corpus step (`documents.json`, recorded by
`packages/markdown/src/collab/yjsCorpusDocuments.test.ts`) and a rolled-back payload to restore it exactly.
`YjsGuardFuzzTests` replays sessions of three concurrent real editors (`yjsGuardFuzz.test.ts`, regression traces in
`Fixtures/yjs-fuzz`): every update must be admitted and the replica must hold what real Yjs holds after each one.
The same replica, keeping the characters as well, reconstructs the editor's document in C# ([document materialization](document-materialization.md)).
