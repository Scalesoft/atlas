# Materializing a Yjs document in C#

`YjsDocumentMaterializer` (`Atlas.Markdown.Sync`) reconstructs the document a collaborative Y.Doc holds, without a
browser, JavaScript or native code: the stored full state and the updates admitted after it go in, and out comes the
ProseMirror JSON the editor shows for that Y.Doc, exactly `JSON.stringify(editor.getJSON())`, byte for byte. A server can
so read the confirmed document itself (AI, export, Git backup, restore from the log) and check a client's projection
against it instead of trusting it.

```csharp
var materializer = YjsDocumentMaterializer.Builtin;   // every built-in plugin, the "default" fragment, 4 MiB payloads
// or: new YjsDocumentMaterializer(MarkdownSchemaManifest.Builtin.WithPlugins(ids), root: "default", maxPayloadBytes: options.MaxDocumentBytes)
string json = materializer.Materialize(baseline, log.Select(record => (ReadOnlyMemory<byte>)record.Update));
// {"type":"doc","content":[...]}: wrap it in the atlas-rich envelope and validate it with MarkdownDocumentValidator
```

It reuses the content guard's managed replica (`YjsDocumentTree`, Yjs 13.6 integration with YATA, splits, the delete
set and GC), which for this keeps the characters too, and writes the editor's fragment as y-tiptap converts it:

- an element is a node of the same name, with the schema's attributes in declaration order (ProseMirror's
  `computeAttrs`): an unset, deleted or `undefined` attribute takes its default, an attribute the schema does not
  declare is dropped;
- an XmlText is the text nodes of its delta (`toDelta`): one per run between visible formats, its marks the formats in
  force (y-prosemirror's `name--hash` keys name the mark) with their attributes completed by the defaults, sorted by
  mark rank; adjacent text nodes with equal marks join (`Fragment.fromArray`), also across XmlTexts;
- keys in `toJSON()` order (`type`, `attrs`, `content`; `type`, `marks`, `text` for text), strings escaped and numbers
  written as `JSON.stringify` writes them, object keys in JavaScript's order (array indexes first);
- an empty fragment is the editor's empty document (`{"type":"doc","content":[{"type":"paragraph","attrs":{"textAlign":null}}]}`);
- other roots, attributes on texts and the root's map are not shown and not read.

It does not validate values: validate the result with `MarkdownDocumentValidator`, as for any stored document.

## Refusals

Whatever the editor cannot show, or shows only because y-tiptap passes a structure the editor never writes, throws
`YjsMaterializationException`; no partial document is returned. `Reason` names it as the content guard does:

| Reason | When |
| --- | --- |
| `Malformed`, `LimitExceeded`, `ClockGap`, `UnknownReference`, `DeleteOutOfRange` | a payload is no Yjs v1 update, exceeds a bound (also nesting beyond 64 nodes), or misses an earlier one (Yjs would keep it pending) |
| `UnknownNode`, `UnknownMark` | an element or format the schema does not know (y-tiptap deletes it) |
| `InvalidDocument` | content or a mark the schema does not accept at that place, a missing required attribute (y-tiptap deletes the element; ProseMirror would rewrap top-level nodes) |
| `InvalidStructure` | a text directly in the fragment, an element named `text` |
| `UnsupportedContent` | embeds and shared types inside a text (ProseMirror builds a text node of an object and drops its paragraph's content), hooks, maps or plain values among an element's children, an attribute value that is not a lib0 value |
| `InvalidAttributeValue` | `undefined`, bigints, binary data or non-finite numbers inside a value, a `__proto__` key, a format value that is no object for a mark with attributes |

## Conformance

`packages/markdown/src/collab/yjsMaterializerCorpus.test.ts` records the corpus in
`tests/Atlas.Markdown.Sync.Tests/Fixtures/yjs-materializer` with the repository's `yjs`, `@tiptap/y-tiptap` and
`prosemirror-model` (Node is used for this test tooling only): for every step of every case (a full state, then updates)
the expected JSON, computed with y-tiptap's own conversion as `_forceRerender` runs it, or why the editor cannot show it.
Cases written by the editor are also opened in a real collaborative editor, whose `getJSON()` must equal the expected
document and which must write nothing back. Without `ATLAS_UPDATE_SCHEMA=1` the test recomputes every expectation
from the committed payloads, so a dependency upgrade that changes the conversion fails it.

The 133 cases (921 steps: 293 exact, 5 compared by SHA-256, 26 refused, 597 only applied in long logs):

- the editor's fixtures written by a collaborative editor: the web document with every node, mark and component
  (contacts, secrets, charts, diagrams, math, tables, task lists, …), the twelve imported foreign-wiki pages (GitHub,
  Obsidian, Wiki.js; nine are the pages of Atlas's `tests/fixtures/wiki-import`, all but `Editor-Constructs.md`), the
  four secrets fixtures migrated to the current version, the two y-tiptap crash states;
- Czech, astral characters (emoji, ZWJ sequences, flags), combining marks, other scripts, invisible and control
  characters; overlapping, adjacent and nested marks and links with attributes; empty blocks and the empty document;
- an editor session sending one update per command (typing, marks, a link, split, delete, align, hard break, table,
  task list, undo), two editors editing the same sentence at once and the updates they answer, content collected by
  GC and an update referencing it, every block deleted, an insert between the halves of a surrogate pair (U+FFFD);
- raw Yjs: attributes out of schema order, unknown, `undefined`, null or removed; formats out of rank order, hashed
  and partial mark attributes; adjacent texts that join; numbers and object keys as JavaScript writes them; the
  structures above that must be refused;
- every step of the Yjs boundary corpus (`Fixtures/yjs-corpus`: real-editor sessions per node type, editing, hostile
  payloads, the 50 KiB–1 MiB documents with 200 random edits) and a 4,173,340-byte document (just under the default
  4 MiB `MaxDocumentBytes`) of 9,931 varied blocks and 19,862 small edits of four other clients.

`YjsMaterializerCorpusTests` requires all of them; `YjsDocumentMaterializerTests` adds the refusal reasons, object keys of
another encoder, number layout, another fragment name, a host schema without a plugin, text through merges, splits and a
rolled-back payload, and the nesting bound.

## Cost

Release build, AMD Ryzen Threadripper 9970X, median of 11 after a warm-up (server GC / workstation GC):

| Document | Items | `Materialize` | of which writing the JSON |
| --- | --- | --- | --- |
| 4,471 B (web document) | 200 | 0.1 ms | 0.0 ms |
| 57 KiB, + 200 updates | 1,346 | 0.7 / 0.6 ms, with the log 1.4 / 1.0 ms | 0.2 ms |
| 264 KiB, + 200 updates | 6,271 | 3.7 / 3.6 ms, with the log 4.2 / 7.3 ms | 0.7 ms |
| 1 MiB, + 200 updates | 24,719 | 20 / 35 ms, with the log 20 / 41 ms | 3–4 ms |
| 4.0 MiB (near the limit) | 185,166 | 213 / 343 ms (slowest of 11: 440–470 ms) | 36–48 ms |

Integrating the state costs what the guard's replica costs (keeping the characters adds nothing measurable); writing
the 6.7 MB of JSON is the rest. The 4 MiB document is item-dense (22 bytes per item; editor states hold 40 and more), so a
real document of that size takes less. `Materializing_a_document_near_the_4_MiB_limit_fits_under_a_document_lock`
reports the time and bounds it loosely. A host materializes under its per-document lock or on a copy of the stored
payloads, which needs no lock.

## Limits

- shortcut: an empty node is filled only for content of one repeated set (`block+`, the top node's), with the first
  type in schema order that needs no attribute, as ProseMirror's `createAndFill` does there; another required content
  is refused. Upgrade if a schema's top node gets another expression.
- Format keys with line terminators are refused (y-prosemirror's regular expression would read a mark name out of them).
- Only `XmlElement` and `XmlText` children are read; a plain `Y.Text` child, which y-tiptap would render like an
  XmlText, is refused.
- The editor's own normalizations on load (plugins' appended transactions) are not run: the materializer shows the
  Y.Doc as it is. For every editor-written case the corpus checks that the editor writes nothing on load.
