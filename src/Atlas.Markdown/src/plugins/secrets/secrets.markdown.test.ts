// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { JSONContent } from '@tiptap/core'
import { DOMParser as ProseMirrorDOMParser } from '@tiptap/pm/model'
import { createMarkdownEditorController, type MarkdownRichValue } from '../../core'
import { editorForController } from '../../core/internal'
import { importMarkdownFile } from '../../engine/markdownInput'
import { createMarkdownSourceSession } from '../../source/session'
import { createSecretsPlugin, parseSecretDirectiveAttributes, serializeSecretDirective, type SecretsServices } from './index'
// The surface the plugin imports lazily, loaded up front: a test that ended before that import settled failed the run (CI 142).
import '../../concealment'
import { secretsStrings } from './strings'

const id = 'd847b3af-4a87-4e97-aab8-a74a4cf48c89'
const otherId = 'ccff88d6-437d-4665-83a8-4c179ad8a523'
const services = (): SecretsServices => ({
  discover: vi.fn(async () => 'hidden' as const), requestAccess: vi.fn(async () => {}),
  reveal: vi.fn(async () => { throw new Error('never released in these tests') }), copy: vi.fn(async () => {}),
  subscribeRevocation: vi.fn(() => () => {}),
})
const plugins = () => [createSecretsPlugin({ services: services() })]
const envelope = (content: JSONContent[]): MarkdownRichValue => ({
  format: 'atlas-rich', version: 1, plugins: [{ id: 'secrets', version: 4 }], document: { type: 'doc', content },
} as MarkdownRichValue)
const block = (attrs: Record<string, unknown>): JSONContent => ({ type: 'secretReference', attrs })
const inline = (attrs: Record<string, unknown>): JSONContent => ({ type: 'secretInlineReference', attrs })
const paragraph = (...content: JSONContent[]): JSONContent => ({ type: 'paragraph', content })
const text = (value: string): JSONContent => ({ type: 'text', text: value })
const all = (attrs: Record<string, unknown>) => ({ field: 'password', fields: null, showCopy: true, showDetails: false, autoHideSeconds: 60, ...attrs })

/** Every presentation variant, including a persisted non-canonical field order, in both layouts. */
const page = () => envelope([
  { type: 'heading', attrs: { level: 2 }, content: [text('VPN')] },
  block(all({ secretId: id, fields: ['notes', 'path', 'username', 'password'], showDetails: true })),
  paragraph(text('Login '), inline(all({ secretId: id, fields: ['username', 'password'], showCopy: false, autoHideSeconds: null })), text(' then connect.')),
  block(all({ secretId: otherId, field: 'url', autoHideSeconds: 300 })),
  { type: 'bulletList', content: [{ type: 'listItem', content: [paragraph(text('Nested')), block(all({ secretId: otherId }))] }] },
])

const disposers: (() => void)[] = []
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null) })
afterEach(() => { disposers.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); vi.restoreAllMocks() })

function controllerFor(value: MarkdownRichValue) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: plugins(), source: { kind: 'local', value } })
  disposers.push(() => controller.destroy())
  const element = document.createElement('div'); document.body.append(element)
  controller.attachView(element, { ariaLabel: 'Edit' })
  return { controller, editor: editorForController(controller)! }
}
function sessionFor(value: MarkdownRichValue) {
  const session = createMarkdownSourceSession({ value, plugins: plugins() })
  disposers.push(() => session.destroy())
  return session
}
const references = (node: JSONContent | undefined): JSONContent[] => !node ? [] : [
  ...(node.type === 'secretReference' || node.type === 'secretInlineReference' ? [node] : []), ...(node.content ?? []).flatMap(references),
]

describe('secret references in Markdown', () => {
  it('writes a directive with only the reference attributes, leaving defaults out', () => {
    expect(serializeSecretDirective('block', { secretId: id })).toBe(`::secret{secretId=${id}}`)
    expect(serializeSecretDirective('inline', all({ secretId: id, fields: ['password', 'username'], showCopy: false, showDetails: true, autoHideSeconds: null })))
      .toBe(`:secret{secretId=${id} fields="password username" showCopy=false showDetails=true autoHideSeconds=null}`)
    expect(serializeSecretDirective('block', all({ secretId: id, field: 'url', fields: ['url'], autoHideSeconds: 5 })))
      .toBe(`::secret{secretId=${id} field=url fields=url autoHideSeconds=5}`)
  })

  it('round-trips block and inline references, as document → Markdown → document', () => {
    const { controller } = controllerFor(page())
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(false)
    expect(projection.markdown).toContain(`::secret{secretId=${id} fields="notes path username password" showDetails=true}`)
    expect(projection.markdown).toContain(`Login :secret{secretId=${id} fields="username password" showCopy=false autoHideSeconds=null} then connect.`)
    expect(projection.markdown).toContain(`::secret{secretId=${otherId} field=url autoHideSeconds=300}`)
    // The plain-text export still carries nothing of a reference.
    expect(projection.text).not.toContain(id); expect(projection.text).not.toContain('secret')

    const session = sessionFor(page())
    expect(session.protectedBlocks).toEqual([])
    expect(session.text.trim()).toBe(projection.markdown.trim())
    const unchanged = session.apply(session.text)
    expect(unchanged).toMatchObject({ ok: true, changed: false })
    // Parsing the Markdown again yields the same nodes: the page source is lossless for references.
    const reparsed = controllerFor(page()).editor
    reparsed.commands.setContent(session.text)
    expect(references(reparsed.getJSON())).toEqual(references(controller.getProjection().rich.document as JSONContent))
  })

  it('keeps references identical when other text of the page is edited in the source', () => {
    const session = sessionFor(page())
    const result = session.apply(session.text.replace('then connect.', 'then connect to the VPN.').replace('## VPN', '## Remote access'))
    expect(result.ok).toBe(true)
    if (!result.ok) return
    expect(result.changed).toBe(true)
    expect(references(result.value.document as JSONContent)).toEqual(references(page().document as JSONContent))
    expect(JSON.stringify(result.value)).toContain('then connect to the VPN.')
  })

  it('lets the source move, change and add references, and never protects them', () => {
    const session = sessionFor(page())
    const edited = session.text
      .replace(`::secret{secretId=${otherId} field=url autoHideSeconds=300}`, `::secret{secretId=${otherId} field=url showDetails=true}`)
      .concat(`\nSee also :secret{secretId=${otherId}}.\n`)
    const result = session.apply(edited)
    expect(result).toMatchObject({ ok: true, changed: true })
    if (!result.ok) return
    const found = references(result.value.document as JSONContent)
    expect(found).toHaveLength(5)
    expect(found[2]).toEqual(block(all({ secretId: otherId, field: 'url', showDetails: true })))
    expect(found[4]).toEqual(inline(all({ secretId: otherId })))
  })

  it('rejects values, unknown attributes and malformed directives with a diagnostic that never echoes them', () => {
    const session = sessionFor(page())
    for (const directive of [
      `::secret{secretId=${id} value=hunter2}`, `::secret{secretId=${id} password="hunter2"}`, `::secret{secretId=${id} label=Hunter2}`,
      `::secret{secretId=${id} secretId=${otherId}}`, `::secret{secretId=${id.toUpperCase()}}`, `::secret{secretId=${id} fields="password password"}`,
      `::secret{secretId=${id} showCopy=yes}`, `::secret{secretId=${id} autoHideSeconds=1}`, `::secret{secretId=${id} fields=ciphertext}`,
      `::secret{}`, `::secret{secretId=${id} hunter2}`, `Inline :secret{secretId=${id} value="hunter2"} text`,
    ]) {
      const result = session.apply(`${session.text}\n${directive}\n`)
      expect(result.ok, directive).toBe(false)
      const messages = result.diagnostics.map(item => item.message).join('\n')
      expect(messages).toContain(secretsStrings.markdownInvalidReference)
      expect(messages).not.toMatch(/hunter2/i)
    }
    expect(() => parseSecretDirectiveAttributes('secretId=x')).toThrow(secretsStrings.markdownInvalidReference)
    expect(parseSecretDirectiveAttributes(`secretId=${id}  fields="url  title"`)).toEqual(all({ secretId: id, fields: ['url', 'title'] }))
  })

  it('treats text that only looks like a directive as lossy, so the page source protects it', () => {
    const literal = envelope([
      paragraph({ type: 'text', text: `Write :secret{secretId=${id}} to reference a password.` }),
      paragraph(text('Code '), { type: 'text', text: `:secret{secretId=${id}}`, marks: [{ type: 'code' }] }),
      paragraph(text('Password:'), inline(all({ secretId: id }))),
    ])
    const { controller } = controllerFor(literal)
    expect(controller.getProjection().markdownLossy).toBe(true)
    const session = sessionFor(literal)
    expect(session.protectedBlocks.map(range => range.label)).toHaveLength(2)
    expect(session.text).toContain(`\`:secret{secretId=${id}}\``)
    const result = session.apply(session.text)
    expect(result).toMatchObject({ ok: true, changed: false })
  })

  it('never creates a reference from pasted or dropped Markdown of another wiki', () => {
    const { editor } = controllerFor(envelope([{ type: 'paragraph' }]))
    const directive = `::secret{secretId=${id}}\n\nText :secret{secretId=${id}} here.`
    expect(editor.commands.pasteMarkdown(directive)).toBe(true)
    expect(references(editor.getJSON())).toEqual([])
    expect(JSON.stringify(editor.getJSON())).toContain(':secret{secretId=')
    // An invalid directive is literal text during an import too: no diagnostic, no value.
    expect(editor.commands.pasteMarkdown(`:secret{value=hunter2}`)).toBe(true)
    expect(references(importMarkdownFile(editor, directive, 'page.md').reduce<JSONContent>((doc, node) => ({ ...doc, content: [...doc.content!, node] }), { type: 'doc', content: [] })))
      .toEqual([])
  })

  it('never creates a reference from pasted HTML, even HTML shaped like the parser output', () => {
    const { editor } = controllerFor(envelope([{ type: 'paragraph' }]))
    const attrs = JSON.stringify(all({ secretId: id })).replace(/"/g, '&quot;')
    const clipboard = document.createElement('div')
    clipboard.innerHTML = (`<p>a<span data-md-secret-source="0123456789abcdef0123456789abcdef" data-md-secret-attrs="${attrs}"></span>b</p>`
      + `<div data-md-secret-source="" data-md-secret-attrs="${attrs}"></div><span data-md-secret-inline="" hidden></span><span data-md-secret="" hidden></span>`)
    // What a paste does with clipboard HTML: the schema's parse rules, nothing else.
    editor.view.dispatch(editor.state.tr.replaceSelection(ProseMirrorDOMParser.fromSchema(editor.schema).parseSlice(clipboard)))
    expect(references(editor.getJSON())).toEqual([])
    expect(JSON.stringify(editor.getJSON())).not.toContain(id)
  })
})
