import * as Y from 'yjs'
import { prosemirrorToYDoc, updateYFragment } from '@tiptap/y-tiptap'
import type { Node } from '@tiptap/pm/model'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin } from '../core/plugins/types'
import type { MarkdownDocumentSchema } from '../core/types'
import { RICH_FORMAT } from '../core/types'
import { MarkdownDocumentCodec } from '../engine/documentCodec'
import { createTiptapEditor } from '../engine/editorFactory'
import { destroyEditor } from '../engine/editorLifecycle'
import { confirmDocumentSchema, normalizeDocumentSchema } from '../collab/documentSchema'
import { builtinSchemaRevision } from '../core/documentConstraints'
import { decodeRecoveryDocument } from '../collab/recovery/decodeDocument'
import { u8ToB64 } from '../collab/provider/protocol'
import { repairLegacyDocument } from '../engine/legacyDocument'
import { createDocument, type JSONContent } from '@tiptap/core'
import { EditorState } from '@tiptap/pm/state'

/**
 * The JavaScript reference of the server's managed authority (3.6: dev-time, for the parity fixtures of tests/Atlas.Yjs.Tests), never in
 * the client bundle. Requests parse and write Markdown synchronously:
 * the host awaits `loadMarkdownDialect()` (`./core`) once, after importing its plugins, so their forms load too. No top-level await
 * here: in an entry of the library build it reshapes the shared chunks (tests/package/bundle-budget.test.mjs).
 */
export interface AuthorityRequest {
  schema: MarkdownDocumentSchema
  fragment: string
  snapshot?: string
  updates?: readonly string[]
  seedMarkdown?: string
  /** The stored rich document (its `doc` node): the live document seeded exactly as the browser seeds it (SignalRYjsProvider). */
  seedRich?: unknown
}

function binary(value: string): Uint8Array {
  if (typeof value !== 'string' || value.length > 24 * 1024 * 1024
    || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) {
    throw new TypeError('Invalid or oversized authority binary.')
  }
  return Uint8Array.from(atob(value), character => character.charCodeAt(0))
}

/** A server-side write against the live document: the whole target document (rich JSON), diffed per block into one Yjs update. */
export interface LiveWriteRequest extends AuthorityRequest {
  target: unknown
}

type Write = (doc: Y.Doc, codec: MarkdownDocumentCodec<'rich'>, plugins: MarkdownDocumentSchema['plugins']) => { update: Uint8Array, target: Node }

/** The host's trusted plugin factories are the allowlist; requests cannot load code. Plugins are the host's, not imported here: the
 * server entry then shares no plugin code with the browser entries (tests/package/bundle-budget.test.mjs). */
export function processAuthorityRequest(request: AuthorityRequest, hostFactories: Record<string, () => MarkdownPlugin>) {
  return reduce(request, hostFactories)
}

/**
 * The update that turns the live document (snapshot + updates) into `target`, as a collaborator would type it: y-tiptap's
 * updateYFragment keeps every unchanged block and diffs changed text, so concurrent edits of other blocks merge without loss.
 */
export function processLiveWriteRequest(request: LiveWriteRequest, hostFactories: Record<string, () => MarkdownPlugin>) {
  return reduce(request, hostFactories, (doc, codec, plugins) => {
    const before = Y.encodeStateVector(doc)
    const parsed = codec.parseRichDocument({ format: RICH_FORMAT, version: 1, document: request.target, plugins })
    // Plugins parse structured attributes into null-prototype objects (contacts' data), which Yjs refuses to store: plain JSON again.
    const target = parsed.type.schema.nodeFromJSON(JSON.parse(JSON.stringify(parsed.toJSON())))
    updateYFragment(doc, doc.getXmlFragment(request.fragment), target, { mapping: new Map(), isOMark: new Map() })
    return { update: Y.encodeStateAsUpdate(doc, before), target }
  })
}

function reduce(request: AuthorityRequest, hostFactories: Record<string, () => MarkdownPlugin>, write?: Write) {
  const schema = normalizeDocumentSchema(request.schema)
  if (typeof request.fragment !== 'string' || !request.fragment.trim() || request.fragment.length > 256) throw new TypeError('Invalid authority fragment.')
  if ([request.snapshot, request.seedMarkdown, request.seedRich].filter(value => value !== undefined).length > 1) {
    throw new TypeError('Snapshot and seed are mutually exclusive.')
  }
  if (!Array.isArray(request.updates ?? []) || (request.updates?.length ?? 0) > 4096) throw new RangeError('Too many authority updates.')
  const plugins = schema.plugins.map(({ id }) => {
    if (!Object.hasOwn(hostFactories, id)) throw new TypeError(`Authority plugin is not installed: ${id}`)
    return hostFactories[id]()
  })
  const registry = new MarkdownPluginRegistry(plugins, schema.profile)
  let editor: ReturnType<typeof createTiptapEditor> | null = null
  let doc = new Y.Doc({ gc: false })
  try {
    const codec = new MarkdownDocumentCodec(schema.profile, registry, { profile: schema.profile })
    confirmDocumentSchema(schema, { version: builtinSchemaRevision(schema.profile), profile: schema.profile, plugins: registry.schemaPlugins })
    editor = createTiptapEditor({ element: null, editable: false, extensions: codec.extensions,
      content: { type: 'doc', content: [{ type: 'paragraph' }] } })
    if (request.snapshot !== undefined) Y.applyUpdate(doc, binary(request.snapshot))
    else if (request.seedRich !== undefined) {
      // The browser's seed (SignalRYjsProvider.seedInitialContent): setContent of the repaired stored document over the empty page, its
      // plugins' appended transactions included (the trailing paragraph…), then ySync's updateYFragment into the empty fragment, which is
      // what prosemirrorToYDoc does. A headless editor has no plugins (Tiptap adds them with the view), so they run on a state of their own.
      // Invalid content refuses, never seeds an empty page.
      if (!request.seedRich || typeof request.seedRich !== 'object' || JSON.stringify(request.seedRich).length > 5 * 1024 * 1024) {
        throw new RangeError('Authority seed exceeds its budget.')
      }
      const empty = EditorState.create({ schema: editor.schema, doc: editor.state.doc, plugins: editor.extensionManager.plugins })
      const content = createDocument(repairLegacyDocument(request.seedRich as JSONContent), editor.schema, {}, { errorOnInvalidContent: true })
      const seeded = prosemirrorToYDoc(empty.apply(empty.tr.replaceWith(0, empty.doc.content.size, content).setMeta('preventAutolink', true)).doc, request.fragment)
      doc.destroy()
      doc = seeded
    } else {
      const seed = request.seedMarkdown ?? ''
      if (typeof seed !== 'string' || new TextEncoder().encode(seed).byteLength > 5 * 1024 * 1024) throw new RangeError('Authority seed exceeds its budget.')
      const parsed = seed ? codec.parseMarkdown(editor, seed) : codec.schema.topNodeType.createAndFill()!
      const seeded = prosemirrorToYDoc(parsed, request.fragment)
      doc.destroy()
      doc = seeded
    }
    for (const update of request.updates ?? []) Y.applyUpdate(doc, binary(update))
    if (doc.store.pendingStructs || doc.store.pendingDs) throw new TypeError('Authority update has missing dependencies.')
    const written = write?.(doc, codec as MarkdownDocumentCodec<'rich'>, schema.plugins)
    const state = Y.encodeStateAsUpdate(doc)
    if (state.byteLength > 16 * 1024 * 1024) throw new RangeError('Authority state exceeds its budget.')
    const document = decodeRecoveryDocument({ yjsState: state, fragment: request.fragment, pendingUpdates: [] }, { maxNodes: 100_000, maxDepth: 100 })
    const parsed = codec.parseRichDocument({ format: RICH_FORMAT, version: 1, document, plugins: schema.plugins })
    // Project the validated node directly; never install input through setContent or repair Yjs.
    const projection = codec.project(editor, parsed)
    codec.getValue(editor, parsed) // Enforce the markdown-safe round-trip contract too.
    // The written document must read back as exactly the target, or the update would store something else than was asked.
    if (written && JSON.stringify(projection.rich) !== JSON.stringify(codec.project(editor, written.target).rich)) {
      throw new TypeError('Live write does not reproduce its target.')
    }
    return { snapshot: u8ToB64(state), markdown: projection.markdown, editorJson: JSON.stringify(projection.rich),
      plainText: projection.text, ...(written && { update: u8ToB64(written.update) }) }
  } finally {
    doc.destroy()
    const errors = destroyEditor(editor)
    try { registry.destroy() } catch (error) { errors.push(error) }
    if (errors.length) throw new AggregateError(errors, 'Authority cleanup failed.')
  }
}
