// Shared harness: a real @atlas/markdown controller (rich profile, every plugin) bound to a Y.Doc
// through the same Collaboration extension the SignalR adapter uses. Captures every Y update the
// editor emits, exactly as the provider would send it to MarkdownSyncHub.SendUpdate.
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import type { Editor } from '@tiptap/core'
const SRC = `${import.meta.dirname}/../../../../../src/Atlas.Markdown/src`
const core = await import(/* @vite-ignore */ `${SRC}/core/index.ts`)
const internal = await import(/* @vite-ignore */ `${SRC}/core/internal.ts`)
const P = {
  youtrack: await import(/* @vite-ignore */ `${SRC}/plugins/youtrack/index.ts`),
  contacts: await import(/* @vite-ignore */ `${SRC}/plugins/contacts/index.ts`),
  secrets: await import(/* @vite-ignore */ `${SRC}/plugins/secrets/index.ts`),
  concealed: await import(/* @vite-ignore */ `${SRC}/plugins/concealed/index.ts`),
  attachments: await import(/* @vite-ignore */ `${SRC}/plugins/attachments/index.ts`),
  charts: await import(/* @vite-ignore */ `${SRC}/plugins/charts/index.ts`),
  openapi: await import(/* @vite-ignore */ `${SRC}/plugins/openapi/index.ts`),
  diagrams: await import(/* @vite-ignore */ `${SRC}/plugins/diagrams/index.ts`),
  math: await import(/* @vite-ignore */ `${SRC}/plugins/math/index.ts`),
  toc: await import(/* @vite-ignore */ `${SRC}/plugins/toc/index.ts`),
  emoji: await import(/* @vite-ignore */ `${SRC}/plugins/emoji/index.ts`),
  pages: await import(/* @vite-ignore */ `${SRC}/plugins/pages/index.ts`),
  headingAnchors: await import(/* @vite-ignore */ `${SRC}/plugins/heading-anchors/index.ts`),
}

const secretsServices = {
  discover: async () => 'hidden', requestAccess: async () => {}, reveal: async () => { throw new Error('no') },
  copy: async () => {}, subscribeRevocation: () => () => {},
}

export function allPlugins(opts: { secrets?: boolean } = {}) {
  const list = [
    P.youtrack.createYouTrackPlugin({ client: { search: async () => [] } }),
    P.contacts.createContactsPlugin(), P.attachments.createAttachmentsPlugin(), P.charts.createChartsPlugin(),
    P.openapi.createOpenApiPlugin(), P.diagrams.createDiagramsPlugin(), P.math.createMathPlugin(), P.toc.createTocPlugin(),
    P.emoji.createEmojiPlugin(), P.pages.createPagesPlugin(), P.headingAnchors.createHeadingAnchorsPlugin(),
    P.concealed.createConcealedContentPlugin(),
  ]
  if (opts.secrets !== false) list.push(P.secrets.createSecretsPlugin({ services: secretsServices }))
  return list
}

export interface Session {
  doc: Y.Doc
  editor: Editor
  controller: any
  updates: Uint8Array[]
  /** Run fn and return the Y updates it produced (one per Y transaction). */
  capture(fn: (editor: Editor) => unknown): Uint8Array[]
  destroy(): void
}

export function openSession(opts: { secrets?: boolean; doc?: Y.Doc } = {}): Session {
  const doc = opts.doc ?? new Y.Doc()
  const updates: Uint8Array[] = []
  doc.on('update', (u: Uint8Array, origin: unknown) => { updates.push(u) })
  const adapter = internal.registerCollaborationAdapter((publish: any) => ({
    extensions: [Collaboration.configure({ document: doc, field: 'default' })],
    isRemoteTransaction: () => false,
    attachEditor: () => publish({ hydrated: true, writable: true }),
    detachEditor() {}, destroy() {},
  }))
  const controller = core.createMarkdownEditorController({ profile: 'rich', plugins: allPlugins(opts), source: { kind: 'collaborative', adapter } })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'spike' })
  const editor = internal.editorForController(controller) as Editor
  if (!editor) throw new Error('no editor')
  return {
    doc, editor, controller, updates,
    capture(fn) { const start = updates.length; try { fn(editor) } catch (error) { (globalThis as any).__captureErrors ??= []; (globalThis as any).__captureErrors.push(String(error)); } return updates.slice(start) },
    destroy() { controller.destroy(); host.remove(); doc.destroy() },
  }
}

export { Y }
