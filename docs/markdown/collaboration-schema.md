# Collaboration schema compatibility

Controller sessions declare `controller.schema`: the built-in content schema revision, content profile, and each content plugin's stable ID and schema version. UI-only changes do not change this identity. Plugin order is irrelevant; IDs are sorted before comparison. Built-in revisions, profiles and plugin versions must match exactly. There are no automatic migrations or schema changes during a live session.

## Built-in schema revisions

| Client content schema | Declared identity |
| --- | --- |
| Original rich editor | `version: 1, profile: 'rich'` |
| Rich editor with image presentation attributes, callouts and disclosures | `version: 2, profile: 'rich'` |
| Rich editor with heading level 4 and the `caution` callout variant (0.7.0) | `version: 3, profile: 'rich'` |
| Rich editor whose inline code combines with the other marks (Atlas 3) | `version: 4, profile: 'rich'` |
| Rich editor with the timed list `timedList`/`timedItem` (Atlas 3.6) | `version: 5, profile: 'rich'` |
| Rich editor whose disclosure title is the node `disclosureTitle` (Atlas 3.6) | `version: 6, profile: 'rich'` |
| Rich editor without the disclosure block (Atlas 3.6, every heading folds) | `version: 7, profile: 'rich'` |
| Markdown-safe editor (unchanged content schema) | `version: 1, profile: 'markdown-safe'` |

Current rich controllers always declare revision 7, including an empty document or
a document which has not used the new components yet. Otherwise an old client
could join first and subsequently discard new nodes or attributes. Optional
modules continue to declare their own identities in `plugins`.

### Raising a content plugin's version

A plugin that adds nodes, attributes or values raises its `schemaVersion` exactly like a built-in revision, for the same reason:
a client with the old version would delete them from the shared document. When every old document is valid content of the new
version (Atlas 3.4: `atlas-requirements` 2 adds the `overview` node and the requirement's `stateLabel` and `system`; Atlas 3.6:
`atlas-requirements` 3 adds the requirement heading's attributes and the paragraph's `dependencies`, `heading-anchors` 2 the dot in
heading ids and `aliases`), this needs no
data migration:

- the plugin declares an identity migration `{ from: 1, to: 2 }`, so the editor opens stored values declaring version 1 and writes 2
  on the next save; the exported manifest lists the old version under `migratesFrom`, so the server keeps accepting them;
- the host appends an entry with the new version to `KnownHostSchemas`; the next open moves a document bound to the previous entry to
  a new generation (or reseeds it from the saved revision), and an old tab gets `SCHEMA_MISMATCH` and reloads;
- an old client refuses a stored value declaring the newer version (`data is newer than the installed schema`) instead of editing it.

### Rich revision 7

Revision 7 removes the disclosure block (`disclosure`, `disclosureTitle`): every heading folds, so the block was a second way to do
the same. A revision 6 client could still insert one, so it gets `SCHEMA_MISMATCH`. Stored content converts by the content repair
(legacyDocument.ts, LegacyDocumentRepair rule `disclosure_heading`): a disclosure is a heading carrying its title (the title node of
revision 6, the `title` attribute before, „Podrobnosti“ when empty), one level below the heading before it (2–6, 3 with none), then its
body, in place, nested ones too; its body takes that level and the next block the one before again, so consecutive disclosures stay
siblings. The job `pages.requirements-overview` rewrites working and published revisions (migration 2026110995), a live state bound to
revision 6 whose changes are saved is seeded again from the repaired revision; history, boards, notes and drafts are read through the
repair. A revision 7 editor cannot hold a disclosure: a live state with one and unsaved edits must be saved before the upgrade. Atlas appends a revision 7 entry to `KnownHostSchemas`.

### Rich revision 6

Revision 6 makes the disclosure's title document content, written in place as Notion's toggle line:
`disclosure` holds `disclosureTitle? block+`, `disclosureTitle` holds `inline*` (marks, links, chips, collaborative merge). The
`title` attribute stays in the schema (nullable, never written) and the title is optional for the schema only, so a stored or live
document of revision 5 stays valid: y-tiptap would otherwise delete such a block from the shared document. On open (legacyDocument.ts,
LegacyDocumentRepair rule `disclosure_title`; for a live document the elected snapshotter) the attribute becomes the title node; the job
`pages.requirements-overview` rewrites the working and published revisions (migration 2026110960; since revision 7 the job it queues makes a disclosure a heading), history is read through the repair.
A revision 5 client would delete the title nodes, so it gets `SCHEMA_MISMATCH`. The Markdown is unchanged: the title's inline Markdown
as a paragraph, then the body (the block stays lossy). Atlas appends a revision 6 entry to `KnownHostSchemas`.

### Rich revision 5

Revision 5 adds the timed list („Časová odrážka“): `timedList` (content `timedItem+`, a `tight`
attribute as a bullet list's) and `timedItem` (`paragraph block*`). An item's stamp
„3. 11. 2026 17:19 — “ is its own text, so the Markdown is a bullet list's and the markdown-safe
profile reads it as one. A revision 4 client would delete the nodes from the shared document, so it
gets `SCHEMA_MISMATCH`. Every revision 4 document is valid revision 5 content; on open
(legacyDocument.ts, LegacyDocumentRepair rule `timed_list`) a bullet list whose first item starts
with a stamp becomes a timed list, its Markdown unchanged. Atlas appends a revision 5 entry to
`KnownHostSchemas`.

### Rich revision 4

Revision 4 lets the inline `code` mark share a text node with every other mark
(`**`x`**`, `*`x`*`, `[`x`](url)`, as Markdown writes them); before, code excluded
them all. Code now ranks last, so the Markdown serializer writes it innermost. Atlas 2.x
stored such text (it never checked marks), and the import keeps it instead of dropping
a mark. A revision 3 client cannot build such a text node (ProseMirror's check fails and
y-tiptap deletes the paragraph from the shared document), so the revision changes and
such a client gets `SCHEMA_MISMATCH`. Every revision 3 document is valid revision 4
content; Atlas appends a revision 4 entry to `KnownHostSchemas` like revision 3 below.

### Rich revision 3

Revision 3 adds values to existing attributes only: heading `level` 4 and callout
`variant` `caution`. There is no new node, attribute or mark, so the relay's allowlist of
names was unchanged (since 0.16.0 the content guard also checks values, from the editor's schema manifest). It still needs its
own revision: a revision 2 client validates the callout variant, cannot build a
caution callout and y-tiptap then deletes the element from the shared document. With
a distinct identity such a client gets `SCHEMA_MISMATCH` instead of joining.

Every revision 2 document is valid revision 3 content, so moving a document forward
keeps its Yjs state (unlike revision 1 → 2 below). Hosts append a revision 3 entry
to their schema list and upgrade bound documents like a plugin addition: Atlas adds
`new MarkdownDocumentSchema(3, "rich", plugins)` to `KnownHostSchemas`, whose
generation move rebinds revision 2 documents. The reference `FileDocumentStore`
serves a document bound to rich revision 2 as revision 3 with the same plugins when
it loads it (persisted with the next metadata write); a deploy restarts the server,
so no revision 2 session outlives the upgrade. Never move a document back to 2.
Local recovery journals keep the revision they were written with and are only
previewed by a matching runtime.

Heading levels 5 and 6 (added after 0.15.0) do not change the revision: the
heading `level` attribute has always stored any value, and a revision 3 client
keeps such a node in the shared document (it only renders it as H1 until it is
updated). Nothing is deleted, so there is no `SCHEMA_MISMATCH` to protect against.

The editor's Markdown dialect (GitHub alerts, `==highlight==`, Mermaid and math
fences) changes Markdown input and projections only, not the collaborative schema.

The TypeScript and .NET normalizers accept revisions 1–3 and preserve the
declared value; recognizing an older revision does **not** make it compatible
with a newer one (2 and 3 do not join each other either). A revision 1 rich client cannot join a revision 2 document, and
a revision 2 rich client cannot join an existing revision 1 document. Both
directions fail before hydration or group membership. A server that only knows
revision 1 rejects the new rich client; clients never retry with a lower revision.

The persisted rich JSON envelope remains `{ format: 'atlas-rich', version: 1,
... }`. Its `version` describes the envelope format, independently of the live
collaboration identity. Loading compatible old rich JSON locally does not grant
permission to reuse or relabel an old collaborative Yjs document.

### Deploying rich revision 2

Deploy the server that recognizes both revisions before deploying new clients.
Existing revision 1 documents remain revision 1; old clients may continue to use
them with their matching schema. Back up their schema metadata, snapshots and
update logs together. For a document to move to revision 2, coordinate a write
freeze and preserve pending/offline edits, export and validate its content using
the original schema, then create a **new** revision 2 document and fresh Yjs state
from the verified content. Verify content and module data, then switch clients
to the new document ID. Keep the original state for rollback. Do not change only
`metadata.json` or merge old Yjs updates into the new revision. This release does
not provide an automatic migration command or convert existing collaborative
documents in place.

The SignalR adapter passes the identity to `JoinDocumentWithSchema`. The server authorizes the user, durably binds or compares the identity, and only then adds the connection to the document group and returns its snapshot. The client also verifies the returned identity before applying the snapshot or buffered updates. A mismatch or unsupported negotiation is fatal for that session; there is no fallback to a weaker schema and local edits are retained in the client's document.

The join response also carries the effective per-document write permission. The controller combines it with the host's `readonly` preference, so a reader hydrates normally but never becomes editable or eligible to publish a snapshot. Stable `[READ_ONLY]` errors are terminal instead of transport-retried. If permission is revoked while a client has offline changes, reconnect fails closed and leaves those changes in the in-memory document rather than claiming they were saved.

`FileDocumentStore` writes the identity into `metadata.json` before the first snapshot exists and preserves it when appending updates or flushing snapshots. An abandoned initial seed or a server restart therefore cannot admit a differently configured client. Schema metadata is part of the document's identity and must be backed up together with its snapshot and update log.

## Legacy documents

The original one-argument `JoinDocument` method remains available for unversioned legacy sessions. They have no inferred `rich` or `markdown-safe` profile. Metadata without a schema, and existing opaque snapshots or updates without metadata, are treated as legacy. New legacy documents are also bound to legacy mode before joining.

Legacy clients cannot enter versioned documents, and versioned clients cannot silently adopt legacy documents. Existing hosts using `useCollab` without `schema` continue on the legacy protocol. A host explicitly supplying `schema` must use a matching editor configuration. Controller adapters supply their controller's schema automatically.

Moving an existing legacy document to the versioned API requires an explicit, coordinated migration: preserve its original state, identify and validate the actual content schema, create a matching versioned document, and switch clients after verification. No migration command or automatic conversion is included in this change.

## Custom document stores

`IDocumentStore.BindSchemaAsync` is a default interface method, so existing store implementations continue to compile. The default supports legacy sessions only and rejects versioned sessions. It never claims to persist a schema that the store cannot preserve.

To support versioned sessions, implement atomic and durable binding: accept the requested identity only for a new empty document; compare all later requests; preserve the binding across flush, eviction, external reseed, and restart; and return it from `LoadAsync` through `StoredDocument.Schema`. A null identity explicitly means legacy mode. The binding must finish before returning success, including when the initial document snapshot has not yet arrived.
