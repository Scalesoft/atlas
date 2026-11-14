// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import type { Editor, JSONContent } from '@tiptap/core'
import * as authority from './authority'
import { builtinAuthorityPlugins } from './testPlugins'
import { b64ToU8 } from '../collab/provider/protocol'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import { MarkdownDocumentCodec } from '../engine/documentCodec'
import { createTiptapEditor } from '../engine/editorFactory'
import { repairLegacyDocument } from '../engine/legacyDocument'
import { builtinSchemaRevision } from '../core/documentConstraints'

// 3.6: the server seeds the live document from the stored rich JSON (`seedRich`), so the page editor never seeds it. It must be the state
// the browser's seed builds (SignalRYjsProvider.seedInitialContent): an editor that opens it sees the same document, writes nothing on
// attach, and the same edits give the same document.
const fragment = 'default'
const pluginIds = ['heading-anchors', 'contacts', 'math', 'charts', 'pages']
const cleanup: Array<() => void> = []
afterEach(() => { while (cleanup.length) cleanup.pop()!() })

function codec() {
  const registry = new MarkdownPluginRegistry(pluginIds.map(id => builtinAuthorityPlugins[id]()), 'rich')
  cleanup.push(() => registry.destroy())
  return { registry, codec: new MarkdownDocumentCodec('rich', registry, { profile: 'rich' }) }
}

const { registry } = codec()
const schema = { version: builtinSchemaRevision('rich'), profile: 'rich' as const, plugins: registry.schemaPlugins.map(({ id, version }) => ({ id, version })) }

/** A browser's editor bound to `ydoc` as the page editor binds it (useCollab: Collaboration on the fragment `default`), once ySync has rendered. */
async function bound(ydoc: Y.Doc): Promise<Editor> {
  const element = document.createElement('div')
  document.body.append(element)
  const editor = createTiptapEditor({ element, extensions: [...codec().codec.extensions, Collaboration.configure({ document: ydoc, field: fragment })] })
  cleanup.push(() => { editor.destroy(); element.remove() })
  // ySync renders the shared fragment in a later task; the provider waits for it before it seeds (isYSyncViewMounted).
  await new Promise(resolve => setTimeout(resolve, 20))
  return editor
}

/** The browser's seed of today: setContent of the repaired stored document inside one transaction of the shared document. */
async function clientSeed(stored: JSONContent) {
  const ydoc = new Y.Doc()
  const editor = await bound(ydoc)
  ydoc.transact(() => { editor.commands.setContent(repairLegacyDocument(stored), { emitUpdate: false }) })
  return { ydoc, editor }
}

async function serverSeed(stored: JSONContent) {
  const result = authority.processAuthorityRequest({ schema, fragment, seedRich: stored }, builtinAuthorityPlugins)
  const ydoc = new Y.Doc()
  Y.applyUpdate(ydoc, b64ToU8(result.snapshot))
  const written: Uint8Array[] = []
  ydoc.on('update', (update: Uint8Array) => written.push(update))
  const editor = await bound(ydoc)
  return { ydoc, editor, written, result }
}

/** A stored page of the kinds Atlas keeps: Markdown-born blocks, plugin nodes, a 2.x link in a link and a disclosure title attribute. */
function storedPage(): JSONContent {
  const markdown = [
    '# Nasazení', '', 'Úvod s **tučným**, *kurzívou*, `kódem` a [odkazem](https://example.com).', '',
    '## Postup', '', '1. první', '2. druhý', '   - vnořený', '', '- [ ] úkol', '- [x] hotovo', '',
    '| A | B |', '| - | - |', '| 1 | 2 |', '', '```ts', 'const a = 1', '```', '', '> citace', '', '$$', 'E = mc^2', '$$', '',
  ].join('\n')
  const base = JSON.parse(authority.processAuthorityRequest({ schema, fragment, seedMarkdown: markdown }, builtinAuthorityPlugins).editorJson).document
  // A saved page holds its tables' chart references; one without them gets random ones from the charts plugin on every seed, either side.
  let chart = 0
  JSON.stringify(base, (key, value) => {
    if (value && typeof value === 'object' && !Array.isArray(value) && 'chartRef' in value && value.chartRef === null) value.chartRef = `chart-${++chart}`
    return value
  })
  const link = { type: 'link', attrs: { href: 'https://example.com/a' } }
  return {
    ...base,
    content: [
      ...base.content,
      { type: 'paragraph', content: [{ type: 'text', text: 'Dvojí odkaz', marks: [link, { ...link, attrs: { href: 'https://example.com/b' } }] }] },
      { type: 'disclosure', attrs: { title: 'Podrobnosti' }, content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Skryté' }] }] },
    ],
  }
}

describe('server seed (seedRich) parity with the browser seed', () => {
  it('builds the state the browser builds: the same document and fragment, nothing written on attach, the same result of the same edits', async () => {
    const stored = storedPage()
    const client = await clientSeed(stored)
    const server = await serverSeed(stored)

    expect(server.editor.getJSON()).toEqual(client.editor.getJSON())
    expect(server.ydoc.getXmlFragment(fragment).toJSON()).toEqual(client.ydoc.getXmlFragment(fragment).toJSON())
    expect(server.written).toHaveLength(0)
    // The legacy repairs ran (the disclosure is a heading and its body since revision 7, one link mark) and the projection is the stored page.
    expect(JSON.stringify(server.editor.getJSON())).not.toContain('"disclosure')
    expect(JSON.parse(server.result.editorJson).document).toEqual(client.editor.getJSON())

    for (const { editor } of [client, server]) {
      editor.chain().setTextSelection(3).insertContent('X').run()
      editor.commands.insertContentAt(editor.state.doc.content.size, { type: 'paragraph', content: [{ type: 'text', text: 'Nový odstavec' }] })
      editor.chain().setTextSelection({ from: 5, to: 9 }).deleteSelection().run()
      editor.chain().selectAll().toggleBold().run()
    }
    expect(server.editor.getJSON()).toEqual(client.editor.getJSON())
    expect(server.ydoc.getXmlFragment(fragment).toJSON()).toEqual(client.ydoc.getXmlFragment(fragment).toJSON())
  })

  it('refuses content the schema does not open instead of seeding an empty page', () => {
    expect(() => authority.processAuthorityRequest({ schema, fragment, seedRich: { type: 'doc', content: [{ type: 'nope' }] } }, builtinAuthorityPlugins))
      .toThrow()
    expect(() => authority.processAuthorityRequest({ schema, fragment, seedRich: { type: 'doc' }, seedMarkdown: 'x' }, builtinAuthorityPlugins))
      .toThrow(/mutually exclusive/)
  })
})
