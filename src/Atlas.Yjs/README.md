# Atlas.Yjs

A faithful managed port of the parts of **yjs 13.6.33** (with **lib0 0.2.117**) and of the y-prosemirror binding as
**@tiptap/y-tiptap 3.0.9** ships it that the Atlas server runs, so the server never starts `node` for them. No ASP.NET or
Atlas dependency; `Atlas.Markdown.Sync` builds on it and passes it the schema it needs (`PmSchema`).

| File | Ports |
| --- | --- |
| `Lib0Reader.cs`, `Lib0AnyJson.cs`, `JsValue.cs` (`Lib0Writer`, `Js`) | lib0 decoding/encoding: varuint, varint, varstring, readAny/writeAny; JavaScript values and `===` |
| `YjsUpdateDecoder.cs`, `YjsUpdate.cs` | readClientsStructRefs and readDeleteSet of an update v1 |
| `YjsUpdateMerger.cs` | Y.mergeUpdates (utils/updates.js) |
| `YjsDocumentTree.cs` | the document replica: Item.getMissing, Item.integrate (YATA), splitItem, the delete set, Item.mergeWith, tryToMergeWithLefts |
| `YDocument.cs` | one local transaction: typeListInsertGenerics(After), typeListDelete, typeMapSet/Delete/GetAll, typeListToArray, Item.delete with ContentType.delete, the prelim types' _integrate; YText insertText, formatText, deleteText, cleanupFormattingGap, insertAttributes, insertNegatedAttributes, minimizeAttributeChanges, findPosition/findNextPosition, ItemTextListPosition.forward, applyDelta, toDelta; Y.encodeStateAsUpdate (writeClientsStructs, writeStructs, Item.write and the contents' write, writeDeleteSet of createDeleteSetFromStructStore) |
| `ProseMirror/PmModel.cs` | prosemirror-model 1.25.11 Node.fromJSON (computeAttrs, Mark.setFrom, Fragment.fromArray, compareDeep) |
| `ProseMirror/YProseMirror.cs` | y-tiptap prosemirrorToYDoc, updateYFragment, normalizePNodeContent, createTypeFromElementNode/TextNodes, equalAttrs, equalYTextPText, equalYTypePNode, computeChildEqualityFactor, ytextTrans, updateYText, yattr2markname, marksToAttributes, hashOfJSON; lib0 simpleDiffString |

Every ported function names its source function and version in its comment. Upstream changes are applied function by function:
diff the upstream function between the two versions and apply the same change to its port.

Left out, because Atlas's calls never reach it: search markers (they only speed up walks; a text with formats has none), the
binding's mapping (Atlas calls updateYFragment with a fresh `new Map()`, so every mapping lookup misses), snapshots, embeds,
subdocuments, garbage collection of a local transaction (the live-write document is a `Y.Doc({ gc: false })`).

Parity is proved byte for byte against the JavaScript implementation by `tests/Atlas.Yjs.Tests` on the fixtures
`src/Atlas.Web/src/server/yjsWriterFixtures.test.ts` records (the merger's by `YjsUpdateMergerTests`, within the divergences
below). That file's default web run fails when the installed versions differ from those the fixtures were recorded with, or
when the editor now seeds or writes a sample of them differently: regenerate them then (the command is in its header) and port
the change.

## Divergences from Yjs

Where the port is deliberately not Yjs, so an upgrade knows where to look:

- **Deleting a type does not mark its descendants deleted** (`YjsDocumentTree`, remarks). Yjs's ContentType.delete deletes every
  child; the replica only marks the type's own item and reads visibility from the ancestors instead, which yields the same
  document. Two places compensate and must follow any change of it: `YDocument.WriteDeleteSet` counts an item under a deleted type
  as deleted (as createDeleteSetFromStructStore sees it in Yjs), and `YjsTreeValidation.Placement` (Atlas.Markdown.Sync) treats
  content inside a deleted type as invisible.
- **`YjsUpdateMerger` is byte-identical to Y.mergeUpdates only when no clock is in two payloads.** Where payloads overlap with
  different structs at one clock (a GC in one, an item in another), Yjs's sort comparator answers -1 both ways (utils/updates.js,
  the `lazyStructDecoders.sort` of mergeUpdatesV2) and the JavaScript engine's sort decides; the port keeps the payload order. Both
  results delete the same clocks and apply as the same document (`YjsUpdateMergerTests`).
- **No garbage collection.** The server merges and writes with Y.mergeUpdates / `Y.Doc({ gc: false })` semantics, so deleted
  content stays in the server's states until a browser (a `gc: true` document) pushes a snapshot; how that stays bounded is in
  `DocumentCompactor`'s comment (Atlas.Markdown.Sync).
