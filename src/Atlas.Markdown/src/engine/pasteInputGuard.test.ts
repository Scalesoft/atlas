// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CellSelection } from '@tiptap/pm/tables'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownProfile, type MarkdownRichValue } from '../core'
import { markdownInputErrorEvent, type MarkdownInputErrorDetail } from '../core/inputErrors'
import { editorForController } from '../core/internal'

const cleanups: (() => void)[] = []
// jsdom has no ClipboardEvent; retain the real PM clipboard parsing/dispatch path.
beforeEach(() => vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null }))
afterEach(() => { cleanups.splice(0).forEach(cleanup => cleanup()); document.body.replaceChildren(); vi.unstubAllGlobals() })

function table(rows: number, columns: number) {
  return '<table>' + Array.from({ length: rows }, (_, row) => '<tr>' + Array.from({ length: columns }, () =>
    row ? '<td>pasted</td>' : '<th>pasted</th>').join('') + '</tr>').join('') + '</table>'
}

function mount(profile: MarkdownProfile = 'rich') {
  const value: string | MarkdownRichValue = profile === 'rich'
    ? { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Original' }] }] } }
    : 'Original'
  const controller = createMarkdownEditorController({ profile, source: { kind: 'local', value } })
  cleanups.push(() => controller.destroy())
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Paste guard' })
  const errors: MarkdownInputErrorDetail[] = []
  host.addEventListener(markdownInputErrorEvent, event => errors.push((event as CustomEvent<MarkdownInputErrorDetail>).detail))
  const editor = editorForController(controller)!
  return { controller, editor, host, errors }
}

function lastCell(editor: ReturnType<typeof editorForController>) {
  let position = -1
  editor!.state.doc.descendants((node, pos) => { if (['tableCell', 'tableHeader'].includes(node.type.name)) position = pos })
  return position
}

function pasteText(editor: NonNullable<ReturnType<typeof editorForController>>, text: string) {
  // EditorView.pasteText deliberately requests plain text. Dispatch a clipboard
  // event to exercise the browser Markdown path instead.
  const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'clipboardData', { value: {
    getData: (type: string) => type === 'text/plain' ? text : '',
    types: ['text/plain'], files: [], items: [],
  } })
  editor.view.dom.dispatchEvent(event)
}

describe('native paste input boundaries', () => {
  it('imports the supported Markdown subset into a local rich document through the public projection', () => {
    const markdown = '# Import\n\n- One\n- Two\n\n| A | B |\n| --- | --- |\n| 1 | 2 |\n\n[Link](/wiki/page)\n\n![Image](https://example.test/image.png)\n\n```js\nconst x = 1\n```'
    const importer = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: markdown } })
    cleanups.push(() => importer.destroy())
    importer.attachView(document.createElement('div'), { ariaLabel: 'Markdown import' })
    const projection = importer.getProjection()
    const { controller, host } = mount('rich')
    controller.setValue(projection.rich)
    expect(host.querySelector('h1')?.textContent).toBe('Import')
    expect(host.querySelectorAll('li')).toHaveLength(2)
    expect(host.querySelectorAll('td')).toHaveLength(2)
    expect(host.querySelector('a')?.getAttribute('href')).toBe('/wiki/page')
    expect(host.querySelector('img')?.getAttribute('src')).toBe('https://example.test/image.png')
    expect(host.querySelector('pre')?.textContent).toBe('const x = 1')
    expect(controller.getProjection().markdown).toBe(projection.markdown)
    importer.setValue(controller.getProjection().markdown)
    expect(importer.getProjection().rich.document).toEqual(projection.rich.document)
  })

  it.each(['rich', 'markdown-safe'] as const)('parses Markdown text paste through the guarded native path in %s', profile => {
    const { controller, editor, host, errors } = mount(profile)
    editor.commands.selectAll()
    pasteText(editor, '# Nadpis\n\n- První\n- Druhá\n\n[Odkaz](/wiki/page)\n\n![Obrázek](https://example.test/image.png)\n\n```js\nconst x = 1\n```')
    expect(host.querySelector('h1')?.textContent).toBe('Nadpis')
    expect(host.querySelectorAll('li')).toHaveLength(2)
    expect(host.querySelector('a')?.getAttribute('href')).toBe('/wiki/page')
    expect(host.querySelector('img')?.getAttribute('src')).toBe('https://example.test/image.png')
    expect(host.querySelector('pre')?.textContent).toContain('const x = 1')
    expect(errors).toHaveLength(0)
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getProjection().text).toBe('Original')
  })

  it('keeps code block paste literal and rejects oversized Markdown tables atomically', () => {
    const { controller, editor, errors } = mount()
    controller.dispatch({ type: 'toggle-code-block' })
    editor.commands.setTextSelection({ from: 1, to: editor.state.doc.firstChild!.nodeSize - 1 })
    pasteText(editor, '# Literal\n- still code')
    expect(editor.state.doc.firstChild?.type.name).toBe('codeBlock')
    expect(editor.state.doc.textContent).toBe('# Literal\n- still code')
    controller.dispatch({ type: 'undo' }); controller.dispatch({ type: 'undo' })
    const before = controller.getValue()
    const row = '| ' + Array(30).fill('cell').join(' | ') + ' |'
    const divider = '| ' + Array(30).fill('---').join(' | ') + ' |'
    pasteText(editor, [row, divider, ...Array(33).fill(row)].join('\n'))
    expect(errors).toHaveLength(1)
    expect(controller.getValue()).toEqual(before)
    expect(controller.state.lifecycle).toBe('ready')
  })

  // A server (Atlas.Markdown.Sync) refuses these documents; in collaboration that would stop the session.
  it('refuses a paste that would break a server rule of the document', () => {
    const { controller, editor, errors } = mount()
    const nested = (levels: number): string => levels ? `<ul><li><p>level</p>${nested(levels - 1)}</li></ul>` : ''
    const before = controller.getValue()
    editor.commands.setTextSelection(1)
    for (const html of [nested(31), '<ol start="99999999999999999999"><li><p>x</p></li></ol>', `<ol type="${'a'.repeat(101)}"><li><p>x</p></li></ol>`]) {
      editor.view.pasteHTML(html)
    }
    pasteText(editor, '```' + 'a'.repeat(201) + '\ncode\n```')
    expect(errors).toHaveLength(4)
    expect(controller.getValue()).toEqual(before)
    // Within the bounds: 30 nested lists (their innermost text is level 63 of 64), the largest safe start, a 200-character language.
    editor.view.pasteHTML(nested(30))
    editor.view.pasteHTML(`<ol start="${Number.MAX_SAFE_INTEGER}"><li><p>x</p></li></ol>`)
    pasteText(editor, '```' + 'a'.repeat(200) + '\ncode\n```')
    expect(errors).toHaveLength(4)
  })

  it('does not indent a list past the server depth by keyboard', () => {
    const { editor } = mount()
    // 30 nested lists whose innermost holds two items: their text is level 63 of 64.
    const item = (text: string, ...more: Record<string, unknown>[]) => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }, ...more] })
    let list: Record<string, unknown> = { type: 'bulletList', content: [item('a'), item('b')] }
    for (let level = 29; level >= 1; level--) list = { type: 'bulletList', content: [item(`l${level}`, list), item(`s${level}`)] }
    editor.commands.setContent({ type: 'doc', content: [list] })
    const textAt = (text: string) => { let at = -1; editor.state.doc.descendants((node, pos) => { if (node.text === text) at = pos }); return at }
    const before = editor.getJSON()
    expect(textAt('b')).toBeGreaterThan(0)
    editor.chain().setTextSelection(textAt('b')).sinkListItem('listItem').run()
    expect(editor.getJSON()).toEqual(before)
    // Wrapping in a quote: 60 nested quotes put the text at level 63; one more wrap reaches 64, the next would pass it.
    let quote: Record<string, unknown> = { type: 'paragraph', content: [{ type: 'text', text: 'q' }] }
    for (let level = 0; level < 60; level++) quote = { type: 'blockquote', content: [quote] }
    editor.commands.setContent({ type: 'doc', content: [quote] })
    const deepest = () => { let level = 0; editor.state.doc.descendants((node, pos) => { if (!node.childCount) level = Math.max(level, editor.state.doc.resolve(pos).depth + 2) }); return level }
    expect(deepest()).toBe(63)
    expect(editor.chain().setTextSelection(textAt('q')).wrapIn('blockquote').run()).toBe(true)
    editor.chain().setTextSelection(textAt('q')).wrapIn('blockquote').run()
    expect(deepest()).toBe(64)
    editor.commands.setContent(before)
    // Higher up there is room: s29 goes under l29, its text at level 63.
    editor.chain().setTextSelection(textAt('s29')).sinkListItem('listItem').run()
    expect(editor.getJSON()).not.toEqual(before)
  })

  it('keeps two rapid paste actions and adjacent typing as separate undo steps', () => {
    const { controller, editor } = mount()
    editor.commands.setTextSelection(9)
    editor.commands.insertContent(' typed')
    const typed = controller.getProjection().text
    pasteText(editor, ' **first**')
    const first = controller.getProjection().markdown
    pasteText(editor, ' **second**')
    editor.commands.insertContent(' after')
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().text).not.toContain(' after')
    expect(controller.getProjection().text).toContain('second')
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().markdown).toBe(first)
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().text).toBe(typed)
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().text).toBe('Original')
  })

  it.each(['rich', 'markdown-safe'] as const)('rejects 1,020 cells atomically in %s without poisoning value or undo', profile => {
    const { controller, editor, errors } = mount(profile)
    controller.dispatch({ type: 'set-heading', level: 2 })
    const before = controller.getValue()
    const selection = editor.state.selection.toJSON()
    const revision = controller.state.contentRevision
    const changes = vi.fn()
    const stop = controller.subscribe(state => { if (state.contentRevision !== revision) changes(controller.getValue()) })
    editor.view.pasteHTML(table(34, 30))
    expect(controller.getValue()).toEqual(before)
    expect(editor.state.selection.toJSON()).toEqual(selection)
    expect(controller.state.contentRevision).toBe(revision)
    expect(changes).not.toHaveBeenCalled()
    expect(errors).toHaveLength(1)
    expect(errors[0]).toEqual({ source: 'paste', message: expect.stringContaining('Obsah nelze vložit') })
    expect(controller.state.lifecycle).toBe('ready')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getProjection().text).toBe('Original')
    editor.commands.insertContent('next')
    expect(controller.getProjection().text).toContain('next')
    expect(changes).toHaveBeenCalled()
    stop()
  })

  it.each(['rich', 'markdown-safe'] as const)('accepts 1,000 pasted cells and native undo in %s', profile => {
    const { controller, editor, host, errors } = mount(profile)
    editor.view.pasteHTML(table(40, 25))
    expect(host.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(() => controller.getValue()).not.toThrow()
    expect(errors).toHaveLength(0)
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(host.querySelector('table')).toBeNull()
    expect(controller.getProjection().text).toBe('Original')
  })

  it('rejects oversized source and small source that would expand an existing table past 1,000 cells', () => {
    const { controller, editor, host, errors } = mount()
    editor.view.pasteHTML(table(20, 50))
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, lastCell(editor) + 2)))
    const before = controller.getValue()
    const selection = editor.state.selection.toJSON()
    const revision = controller.state.contentRevision
    editor.view.pasteHTML(table(34, 30))
    editor.view.pasteHTML(table(2, 1))
    expect(errors).toHaveLength(2)
    expect(controller.getValue()).toEqual(before)
    expect(editor.state.selection.toJSON()).toEqual(selection)
    expect(controller.state.contentRevision).toBe(revision)
    expect(host.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(controller.state.lifecycle).toBe('ready')
  })

  it('allows native text-selection table growth to exactly 1,000 cells', () => {
    const { controller, editor, host, errors } = mount()
    editor.view.pasteHTML(table(2, 2))
    let first = -1
    editor.state.doc.descendants((node, pos) => { if (first < 0 && node.type.name === 'tableHeader') first = pos })
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, first + 2)))
    editor.view.pasteHTML(table(40, 25))
    expect(errors).toHaveLength(0)
    expect(host.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(() => controller.getProjection()).not.toThrow()
  })

  it('preserves native CellSelection clipping instead of incorrectly treating it as table growth', () => {
    const { controller, editor, host, errors } = mount()
    editor.view.pasteHTML(table(20, 50))
    const position = lastCell(editor)
    editor.view.dispatch(editor.state.tr.setSelection(CellSelection.create(editor.state.doc, position)))
    editor.view.pasteHTML(table(2, 2))
    expect(errors).toHaveLength(0)
    expect(host.querySelectorAll('td,th')).toHaveLength(1_000)
    expect(() => controller.getValue()).not.toThrow()
  })

  it('bounds spans and rectangularization before native tables allocate missing cells', () => {
    const { controller, editor, errors } = mount()
    const before = controller.getValue()
    editor.view.pasteHTML('<table><tr><td colspan="1000000">bad</td></tr></table>')
    editor.view.pasteHTML('<table><tr><td colspan="500">wide</td></tr><tr><td rowspan="500">tall</td></tr></table>')
    expect(errors).toHaveLength(2)
    expect(controller.getValue()).toEqual(before)
  })

  it('keeps ordinary inline paste marks and source text handling on the native path', () => {
    const { controller, editor, errors } = mount()
    editor.view.pasteHTML('<p><strong>Bold</strong> text</p>')
    expect(controller.getProjection().markdown).toContain('**Bold** text')
    expect(errors).toHaveLength(0)
  })

  it.each([
    '<table><tr><th colspan="2">Title</th></tr><tr><td>A</td><td>B</td></tr></table>',
    '<table><tr><td rowspan="2">A</td><td>B</td></tr><tr><td>C</td></tr></table>',
    '<table><tr><td>A</td><td>B</td></tr><tr><td>C</td></tr></table>',
  ])('allows valid spans and bounded geometry repairs through the original paste handler', html => {
    const { controller, editor, host, errors } = mount()
    editor.view.pasteHTML(html)
    expect(host.querySelector('table')).not.toBeNull()
    expect(host.textContent).toContain('A')
    expect(errors).toHaveLength(0)
    expect(() => controller.getValue()).not.toThrow()
  })
})
