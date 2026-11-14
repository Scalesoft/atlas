// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Mark, type Editor, type JSONContent } from '@tiptap/core'
import {
  createMarkdownEditorController, markdownImportNoticeEvent, markdownInputErrorEvent,
  type LocalControllerOptions, type MarkdownEmbedResolver, type MarkdownImportHandler, type MarkdownImportNoticeDetail, type MarkdownInputErrorDetail,
  type MarkdownPlugin, type MarkdownProfile, type MarkdownRichValue, type MarkdownWikiLinkResolver,
} from '../core'
import { editorForController } from '../core/internal'
import { slashItems } from '../extensions/SlashCommands'
import { createDiagramsPlugin } from '../plugins/diagrams'
import { createMathPlugin } from '../plugins/math'
import { markdownStrings } from '../ui/strings'
import { degradedNoticeTitle } from '../ui/importNotice'
import { isCodeEditorCopy } from './markdownInput'

// Realistic pages exported from a GitHub wiki, an Obsidian vault and Wiki.js.
const pages = import.meta.glob<string>('./fixtures/foreign-wiki/*.md', { eager: true, query: '?raw', import: 'default' })
const page = (name: 'github-deployment-guide' | 'obsidian-meeting-notes' | 'wikijs-incident-policy') => pages[`./fixtures/foreign-wiki/${name}.md`]!

const cleanups: (() => void)[] = []
beforeEach(() => vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null }))
afterEach(() => {
  cleanups.splice(0).forEach(cleanup => cleanup())
  document.body.replaceChildren(); vi.unstubAllGlobals(); vi.restoreAllMocks()
})

interface MountOptions {
  plugins?: MarkdownPlugin[]
  onMarkdownImport?: MarkdownImportHandler
  resolveWikiLink?: MarkdownWikiLinkResolver
  resolveEmbed?: MarkdownEmbedResolver
}
function mount(profile: MarkdownProfile = 'rich', options: MountOptions = {}) {
  const value: string | MarkdownRichValue = profile === 'rich'
    ? { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } }
    : ''
  const controller = createMarkdownEditorController({ profile, source: { kind: 'local', value }, ...options } as LocalControllerOptions<MarkdownProfile>)
  cleanups.push(() => controller.destroy())
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Foreign wiki import' })
  const notices: MarkdownImportNoticeDetail[] = []
  host.addEventListener(markdownImportNoticeEvent, event => notices.push((event as CustomEvent<MarkdownImportNoticeDetail>).detail))
  return { controller, editor: editorForController(controller)!, host, notices }
}

function paste(editor: Editor, data: Record<string, string>): Event {
  const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => data[type] ?? '', types: Object.keys(data), files: [], items: [] } })
  editor.view.dom.dispatchEvent(event)
  return event
}
const pasteText = (editor: Editor, text: string) => paste(editor, { 'text/plain': text })

const blocks = (editor: Editor): JSONContent[] => (editor.getJSON().content ?? []).filter(node => node.type !== 'paragraph' || node.content)
function all(node: JSONContent, type: string): JSONContent[] {
  return [...(node.type === type ? [node] : []), ...(node.content ?? []).flatMap(child => all(child, type))]
}
const first = (node: JSONContent, type: string) => all(node, type)[0]
const marked = (editor: Editor, mark: string) => all(editor.getJSON(), 'text').filter(node => node.marks?.some(item => item.type === mark))
const plain = (node: JSONContent): string => node.text ?? (node.content ?? []).map(plain).join('')

function stubClipboard(readText: () => Promise<string>) {
  const navigator = document.defaultView!.navigator
  Object.defineProperty(navigator, 'clipboard', { value: { readText }, configurable: true })
  cleanups.push(() => { delete (navigator as { clipboard?: unknown }).clipboard })
}

// ---------------------------------------------------------------------------

describe('Markdown copied from a code editor', () => {
  const markdown = '## Rollback\n\n- [ ] VPN configured\n- **Bold** item'
  // VS Code's clipboard HTML: coloured, monospace, white-space: pre rendering of the plain text.
  const vscodeHtml = '<meta charset="utf-8"><div style="color: #d4d4d4;background-color: #1e1e1e;font-family: Consolas, \'Courier New\', monospace;font-size: 14px;white-space: pre;">'
    + '<div><span style="color: #569cd6;font-weight: bold;">## Rollback</span></div><br><div><span style="color: #6796e6;">-</span><span> [ ] VPN configured</span></div>'
    + '<div><span style="color: #6796e6;">-</span><span> </span><span style="color: #569cd6;font-weight: bold;">**Bold**</span><span> item</span></div></div>'
  const jetbrainsHtml = '<html><head><meta http-equiv="content-type" content="text/html; charset=UTF-8"><style type="text/css">.s0 { color: #0033b3;}</style></head>'
    + `<body><pre style="background-color:#ffffff;color:#080808;font-family:'JetBrains Mono',monospace;font-size:9,8pt;"><span class="s0">## Rollback</span>\n\n- [ ] VPN configured\n- **Bold** item</pre></body></html>`

  it('treats a VS Code Markdown copy as Markdown although the clipboard also has HTML', () => {
    const { editor } = mount()
    const event = paste(editor, { 'text/plain': markdown, 'text/html': vscodeHtml, 'vscode-editor-data': '{"version":1,"isFromEmptySelection":false,"mode":"markdown"}' })
    expect(event.defaultPrevented).toBe(true)
    expect(blocks(editor).map(node => [node.type, node.attrs?.level])).toEqual([['heading', 2], ['taskList', undefined], ['bulletList', undefined]])
    expect(marked(editor, 'bold').map(node => node.text)).toEqual(['Bold'])
  })

  it('treats a JetBrains copy (monospace HTML of the same text) as Markdown', () => {
    const { editor } = mount('markdown-safe')
    paste(editor, { 'text/plain': markdown, 'text/html': jetbrainsHtml })
    expect(blocks(editor).map(node => node.type)).toEqual(['heading', 'taskList', 'bulletList'])
  })

  it('keeps the HTML paste for rendered pages, other VS Code languages and plain prose', () => {
    expect(isCodeEditorCopy(document, { types: ['text/plain', 'text/html'], getData: () => '' }, 'Rollback\nPlain bold', '<h2>Rollback</h2><p>Plain <strong>bold</strong></p>')).toBe(false)
    expect(isCodeEditorCopy(document, { types: ['text/plain', 'text/html', 'vscode-editor-data'], getData: () => '{"mode":"typescript"}' }, '# not a heading', vscodeHtml)).toBe(false)
    expect(isCodeEditorCopy(document, { types: ['text/plain', 'text/html'], getData: () => '' }, 'Just prose.', '<pre style="font-family: monospace">Just prose.</pre>')).toBe(false)
    // Monospace HTML whose text is not the clipboard text is a different document.
    expect(isCodeEditorCopy(document, { types: ['text/plain', 'text/html'], getData: () => '' }, markdown, '<pre style="font-family: monospace"># Other</pre>')).toBe(false)
    const { editor } = mount()
    paste(editor, { 'text/plain': 'Rollback\nPlain bold', 'text/html': '<h2>Rollback</h2><p>Plain <strong>bold</strong></p>' })
    expect(blocks(editor).map(node => [node.type, node.attrs?.level])).toEqual([['heading', 2], ['paragraph', undefined]])
  })

  it('keeps Shift+paste literal', () => {
    const { editor } = mount()
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Shift', shiftKey: true, bubbles: true }))
    paste(editor, { 'text/plain': markdown, 'text/html': vscodeHtml, 'vscode-editor-data': '{"mode":"markdown"}' })
    expect(all(editor.getJSON(), 'heading')).toEqual([])
    expect(editor.getText()).toContain('## Rollback')
  })
})

describe('Paste as Markdown command', () => {
  it('reads the clipboard on Ctrl+Shift+Alt+V and inserts it as one undo step', async () => {
    const readText = vi.fn(async () => '> [!TIP]\n> Use **Markdown**.')
    stubClipboard(readText)
    const { editor, controller } = mount()
    const key = new KeyboardEvent('keydown', { key: 'V', code: 'KeyV', ctrlKey: true, altKey: true, shiftKey: true, bubbles: true, cancelable: true })
    editor.view.dom.dispatchEvent(key)
    expect(key.defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(first(editor.getJSON(), 'callout')?.attrs?.variant).toBe('tip'))
    expect(readText).toHaveBeenCalledOnce()
    expect(marked(editor, 'bold').map(node => node.text)).toEqual(['Markdown'])
    controller.dispatch({ type: 'undo' })
    expect(first(editor.getJSON(), 'callout')).toBeUndefined()
  })

  it('does nothing when readonly and reports a clipboard the browser refuses', async () => {
    const readText = vi.fn(async () => { throw new DOMException('Denied', 'NotAllowedError') })
    stubClipboard(readText)
    const { editor, controller, host } = mount()
    const errors: MarkdownInputErrorDetail[] = []
    host.addEventListener(markdownInputErrorEvent, event => errors.push((event as CustomEvent<MarkdownInputErrorDetail>).detail))
    const shortcut = () => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'V', code: 'KeyV', ctrlKey: true, altKey: true, shiftKey: true, bubbles: true, cancelable: true }))
    controller.setEditable(false)
    shortcut()
    expect(readText).not.toHaveBeenCalled()
    controller.setEditable(true)
    const before = editor.getJSON()
    shortcut()
    await vi.waitFor(() => expect(errors).toEqual([{ source: 'paste', message: markdownStrings.clipboardReadFailed }]))
    expect(editor.getJSON()).toEqual(before)
  })

  it('is a controller command hosts can dispatch with their own text', () => {
    const { editor, controller } = mount('markdown-safe')
    expect(controller.query({ type: 'paste-markdown', text: '' }).enabled).toBe(false)
    expect(controller.dispatch({ type: 'paste-markdown', text: '# Title\n\n##### Deep\n\nBody' })).toBe(true)
    expect(controller.getProjection().markdown).toBe('# Title\n\n##### Deep\n\nBody')
    controller.setEditable(false)
    expect(controller.query({ type: 'paste-markdown', text: 'More' }).enabled).toBe(false)
    expect(editor.getText()).not.toContain('More')
  })

  it('is offered in the slash menu, replaces the typed query and needs clipboard text support', async () => {
    const item = slashItems.find(entry => entry.id === 'paste-markdown')!
    expect([item.title, item.icon]).toEqual([markdownStrings.pasteAsMarkdown, 'svg:clipboard-paste'])
    expect(item.available!()).toBe(false)
    stubClipboard(async () => 'Text with ==mark==')
    expect(item.available!()).toBe(true)
    const { editor } = mount()
    editor.commands.insertContent('/md')
    item.command({ editor, range: { from: 1, to: 4 } })
    await vi.waitFor(() => expect(marked(editor, 'highlight').map(node => node.text)).toEqual(['mark']))
    expect(editor.getText()).toBe('Text with mark')
  })
})

describe('Foreign-wiki syntax on existing nodes', () => {
  const alerts = '> [!NOTE]\n> Frozen on Fridays.\n\n> [!warning] Budget risk\n> Over by **10 %**.\n\n> [!tip]- Collapsed tip\n> Hidden until expanded.\n\n'
    + '> Severity 1 pages on-call.\n{.is-warning}\n\n> Wiki.js danger box\n\n{.is-danger}\n\n> [!CAUTION]\n> Irreversible.'

  it('maps GitHub/Obsidian alerts and Wiki.js classes to callouts, foldable ones to a heading over the callout', () => {
    const { editor } = mount()
    pasteText(editor, alerts)
    const content = blocks(editor)
    expect(content.map(node => node.type)).toEqual(['callout', 'callout', 'heading', 'callout', 'callout', 'callout', 'callout'])
    expect(content.filter(node => node.type === 'callout').map(node => node.attrs!.variant)).toEqual(['note', 'warning', 'tip', 'warning', 'caution', 'caution'])
    expect(content[1]!.content![0]).toMatchObject({ type: 'paragraph', content: [{ type: 'text', text: 'Budget risk', marks: [{ type: 'bold' }] }] })
    expect(content[2]).toMatchObject({ type: 'heading', attrs: { level: 3 }, content: [{ type: 'text', text: 'Collapsed tip' }] })
    expect(content[3]!.content![0]).toMatchObject({ type: 'paragraph', content: [{ type: 'text', text: 'Hidden until expanded.' }] })
    expect(editor.getText()).not.toMatch(/\[!|\.is-/)
  })
  it('puts a foldable alert one level below the heading before it, siblings at one level', () => {
    const { editor } = mount()
    pasteText(editor, '#### Sekce\n\n> [!tip]- První\n> a\n\n> [!note]- Druhý\n> b')
    expect(blocks(editor).filter(node => node.type === 'heading').map(node => [node.attrs!.level, node.content![0]!.text])).toEqual([[4, 'Sekce'], [5, 'První'], [5, 'Druhý']])
  })

  it('keeps an alert as a labelled quote in markdown-safe and leaves stored values literal', () => {
    const { controller, editor } = mount('markdown-safe')
    pasteText(editor, '> [!NOTE]\n> Frozen on Fridays.\n\n> [!warning] Budget risk\n> Over.')
    expect(controller.getProjection().markdown).toBe('> **Poznámka**\n>\n> Frozen on Fridays.\n\n> **Budget risk**\n>\n> Over.')
    // Without callout, highlight and plugin nodes the editor dialect stays plain CommonMark.
    const stored = '> \\[!NOTE\\] Stored text.\n\nText ==x== and $y$.\n\n```mermaid\nA --> B\n```'
    controller.setValue('> [!NOTE]\n> Stored text.\n\nText ==x== and $y$.\n\n```mermaid\nA --> B\n```')
    expect(controller.getProjection().markdown).toBe(stored)
  })

  it('maps ```math and ```mermaid fences to plugin nodes, respecting validation, else to code blocks', () => {
    const fences = '```math\n\\frac{a}{b}\n```\n\n```mermaid\nflowchart LR\n  A --> B\n```\n\n```mermaid\n%%{init: {"theme": "dark"}}%%\nflowchart LR\n```'
    const withPlugins = mount('rich', { plugins: [createMathPlugin(), createDiagramsPlugin()] })
    pasteText(withPlugins.editor, fences)
    expect(blocks(withPlugins.editor)).toEqual([
      { type: 'mathBlock', attrs: { data: { version: 1, expression: '\\frac{a}{b}' } } },
      { type: 'diagram', attrs: { data: { version: 1, source: 'flowchart LR\n  A --> B', caption: '' } } },
      { type: 'codeBlock', attrs: { language: 'mermaid' }, content: [{ type: 'text', text: '%%{init: {"theme": "dark"}}%%\nflowchart LR' }] },
    ])
    const without = mount()
    pasteText(without.editor, fences)
    expect(blocks(without.editor).map(node => [node.type, node.attrs?.language])).toEqual([['codeBlock', 'math'], ['codeBlock', 'mermaid'], ['codeBlock', 'mermaid']])
    expect(without.notices).toEqual([])
  })

  it('maps ==highlight== to the highlight mark, and keeps it literal where the mark does not exist', () => {
    const rich = mount()
    pasteText(rich.editor, 'This is ==highlighted *text*== and ==not closed.')
    expect(marked(rich.editor, 'highlight').map(node => node.text)).toEqual(['highlighted ', 'text'])
    expect(rich.editor.getText()).toBe('This is highlighted text and ==not closed.')
    const safe = mount('markdown-safe')
    pasteText(safe.editor, 'This is ==highlighted== text.')
    expect(safe.editor.getText()).toBe('This is ==highlighted== text.')
    expect(safe.notices[0]!.degraded).toEqual([{ kind: 'highlight', count: 1, excerpt: '==highlighted==' }])
  })

  it('turns footnotes into superscript references and an endnote list in reference order', () => {
    const { editor, notices } = mount('markdown-safe')
    pasteText(editor, 'Second[^b] and first[^a], missing[^x].\n\n[^a]: Note A with **bold**.\n[^b]: Note B\n    continued.')
    const content = blocks(editor)
    expect(content.map(node => node.type)).toEqual(['paragraph', 'horizontalRule', 'orderedList'])
    expect(plain(content[0]!)).toBe('Second¹ and first², missing[^x].')
    expect(all(content[2]!, 'listItem').map(plain)).toEqual(['Note B continued.', 'Note A with bold.'])
    expect(marked(editor, 'bold').map(node => node.text)).toEqual(['bold'])
    expect(notices[0]!.degraded).toEqual([{ kind: 'footnote', count: 1, excerpt: '[^x]' }])
  })

  it('maps ~sub~ and ^sup^ only when the marks exist', () => {
    const literal = mount()
    pasteText(literal.editor, 'H~2~O and E=mc^2^, ~~struck~~ and x^2 alone.')
    expect(literal.editor.getText()).toBe('H~2~O and E=mc^2^, struck and x^2 alone.')
    expect(literal.notices[0]!.degraded).toEqual([
      { kind: 'subscript', count: 1, excerpt: '~2~' }, { kind: 'superscript', count: 1, excerpt: '^2^' },
    ])
    const scripts: MarkdownPlugin = { id: 'test.scripts', apiVersion: 1, schemaVersion: 1, create: () => ({ extensions: [
      Mark.create({ name: 'subscript', parseHTML: () => [{ tag: 'sub' }], renderHTML: () => ['sub', 0] }),
      Mark.create({ name: 'superscript', parseHTML: () => [{ tag: 'sup' }], renderHTML: () => ['sup', 0] }),
    ] }) }
    const withMarks = mount('rich', { plugins: [scripts] })
    pasteText(withMarks.editor, 'H~2~O and E=mc^2^ and note[^1].\n\n[^1]: Text.')
    expect(marked(withMarks.editor, 'subscript').map(node => node.text)).toEqual(['2'])
    expect(marked(withMarks.editor, 'superscript').map(node => node.text)).toEqual(['2', '1'])
    expect(withMarks.notices).toEqual([])
  })
})

describe('Editor Markdown dialect', () => {
  const example = [
    '#### Nadpis 4', '',
    '> [!NOTE]', '> Poznámka', '',
    '> [!TIP]', '> Tip', '',
    '> [!IMPORTANT]', '> Důležité', '',
    '> [!WARNING]', '> Varování', '',
    '> [!CAUTION]', '> Pozor', '',
    'Text se ==zvýrazněním== a `==kód==` a https://example.com/?a==b==c', '',
    '```mermaid', 'flowchart LR', '  A --> B', '```', '',
    '$$', 'E = mc^2', '$$', '',
    'Odkaz www.example.com/?x==y==z zůstává odkazem.',
  ].join('\n')
  const plugins = () => [createMathPlugin(), createDiagramsPlugin()]

  it('keeps the quote prefix before a first paragraph that starts with a mark', () => {
    const source = ['> [!WARNING]', '> **Budget risk** over by 10 %.', '', '> [!NOTE]', '> ==Pozor== na *pátek* a ~~sobotu~~.'].join('\n')
    const loaded = mount('rich', { plugins: plugins() })
    loaded.editor.commands.setContent(source)
    expect(blocks(loaded.editor).map(node => node.attrs?.variant)).toEqual(['warning', 'note'])
    expect(loaded.controller.getProjection().markdown).toBe(source)
  })

  // Any block whose text starts with a mark writes back unchanged, wherever it is nested.
  it.each([
    '> Quote first paragraph.\n>\n> **Bold** starts the second paragraph.',
    '> **Bold** starts the only paragraph.',
    '- **Bold** item\n- *Italic* item',
    '1. **Bold** first\n2. ~~Strike~~ second',
    '- [ ] **Bold** task',
    '- Item\n\n  **Bold** second paragraph',
    '## **Bold** heading',
    '| **Bold** | *it* |\n| --- | --- |\n| ==mark== | ~~s~~ |\n\nText after the table.',
    '> [!NOTE]\n> Text.\n>\n> **Bold** second paragraph.',
    '**Bold** plain paragraph',
  ])('writes a leading mark back unchanged: %s', (source) => {
    const loaded = mount('rich', { plugins: plugins() })
    loaded.editor.commands.setContent(source)
    expect(loaded.controller.getProjection().markdown).toBe(source)
  })

  it('parses the dialect outside imports as paste does and writes the same Markdown back', () => {
    const loaded = mount('rich', { plugins: plugins() })
    loaded.editor.commands.setContent(example)
    const content = blocks(loaded.editor)
    expect(content.map(node => [node.type, node.attrs?.level ?? node.attrs?.variant])).toEqual([
      ['heading', 4], ['callout', 'note'], ['callout', 'tip'], ['callout', 'important'], ['callout', 'warning'], ['callout', 'caution'],
      ['paragraph', undefined], ['diagram', undefined], ['mathBlock', undefined], ['paragraph', undefined],
    ])
    expect(marked(loaded.editor, 'highlight').map(node => node.text)).toEqual(['zvýrazněním'])
    expect(marked(loaded.editor, 'code').map(node => node.text)).toEqual(['==kód=='])
    expect(marked(loaded.editor, 'link').map(node => node.marks![0]!.attrs!.href)).toEqual(['https://example.com/?a==b==c', 'http://www.example.com/?x==y==z'])
    expect(content[7]!.attrs!.data).toEqual({ version: 1, source: 'flowchart LR\n  A --> B', caption: '' })
    expect(content[8]!.attrs!.data).toEqual({ version: 1, expression: 'E = mc^2' })
    // All five alerts come back unchanged; only the usual link forms differ.
    const projection = loaded.controller.getProjection()
    expect(projection.markdown).toBe(example.replace('https://example.com/?a==b==c', '<https://example.com/?a==b==c>')
      .replace('www.example.com/?x==y==z', '[www.example.com/?x==y==z](http://www.example.com/?x==y==z)'))
    expect(projection.markdownLossy).toBe(false)

    const pasted = mount('rich', { plugins: plugins() })
    pasteText(pasted.editor, example)
    expect(blocks(pasted.editor)).toEqual(content)
    expect(pasted.notices).toEqual([])
  })

  it('writes a callout whose content starts with another block after an empty quote line, also inside a list', () => {
    const { controller, editor } = mount('rich')
    const p = (text: string): JSONContent => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const doc: JSONContent = { type: 'doc', content: [
      { type: 'callout', attrs: { variant: 'tip' }, content: [{ type: 'orderedList', attrs: { start: 2 }, content: [{ type: 'listItem', content: [p('Druhý krok')] }] }] },
      { type: 'bulletList', attrs: { tight: false }, content: [{ type: 'listItem', content: [p('Položka'),
        { type: 'callout', attrs: { variant: 'warning' }, content: [{ type: 'codeBlock', attrs: { language: null }, content: [{ type: 'text', text: 'x = 1' }] }] }] }] },
    ] }
    controller.setValue({ format: 'atlas-rich', version: 1, document: doc as MarkdownRichValue['document'] })
    const before = editor.getJSON()
    const { markdown, markdownLossy } = controller.getProjection()
    expect(markdown).toBe('> [!TIP]\n>\n> 2. Druhý krok\n\n- Položka\n\n  > [!WARNING]\n  >\n  > ```\n  > x = 1\n  > ```')
    expect(markdownLossy).toBe(false)
    editor.commands.setContent(markdown)
    expect(editor.getJSON()).toEqual(before)
  })

  it('scans many unmatched == in linear time', () => {
    const { editor } = mount('rich')
    const started = performance.now()
    editor.commands.setContent('==a '.repeat(20_000))
    expect(performance.now() - started).toBeLessThan(5_000)
    expect(marked(editor, 'highlight')).toEqual([])
  })
})

describe('Wiki links and embeds', () => {
  let resolveWikiLink: ReturnType<typeof vi.fn<MarkdownWikiLinkResolver>>
  beforeEach(() => {
    resolveWikiLink = vi.fn<MarkdownWikiLinkResolver>(({ target, heading }) => target === 'Project Alpha'
      ? { href: `/wiki/project-alpha${heading ? `#${heading.toLowerCase()}` : ''}` } : null)
  })

  it('asks the host for each [[link]], inserts resolved links and keeps unresolved ones literal', () => {
    const onMarkdownImport = vi.fn<MarkdownImportHandler>()
    const { editor, notices } = mount('rich', { resolveWikiLink, onMarkdownImport, plugins: [createMathPlugin()] })
    pasteText(editor, page('obsidian-meeting-notes'))
    expect(resolveWikiLink.mock.calls.map(([request]) => request)).toEqual([
      { target: 'Project Alpha', raw: 'Project Alpha', source: 'paste' },
      { target: 'Project Alpha', heading: 'Risks', raw: 'Project Alpha#Risks', source: 'paste' },
      { target: 'Project Alpha', alias: 'the project', raw: 'Project Alpha|the project', source: 'paste' },
      { target: 'Nonexistent Page', raw: 'Nonexistent Page', source: 'paste' },
    ])
    expect(Object.isFrozen(resolveWikiLink.mock.calls[0]![0])).toBe(true)
    expect(marked(editor, 'link').map(node => [node.text, node.marks!.find(mark => mark.type === 'link')!.attrs!.href])).toEqual([
      ['Project Alpha', '/wiki/project-alpha'], ['Project Alpha › Risks', '/wiki/project-alpha#risks'], ['the project', '/wiki/project-alpha'],
    ])
    expect(editor.getText()).toContain('a missing [[Nonexistent Page]].')
    // The rest of the vault page maps onto callout, heading (a foldable callout's title), highlight and math nodes.
    expect(blocks(editor).map(node => node.type)).toEqual(['heading', 'paragraph', 'paragraph', 'image', 'paragraph', 'callout', 'heading', 'callout', 'paragraph', 'taskList', 'mathBlock', 'codeBlock', 'heading'])
    expect(marked(editor, 'highlight').map(node => node.text)).toEqual(['highlighted'])
    const degraded = [
      { kind: 'wiki-link', count: 1, excerpt: '[[Nonexistent Page]]' },
      { kind: 'embed', count: 2, excerpt: '![[whiteboard.png]]' },
      { kind: 'comment', count: 1, excerpt: '%%hidden comment%%' },
    ]
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ source: 'paste', title: 'Meeting Notes', degraded }))
    expect(notices).toEqual([{ source: 'paste', degraded }])
  })

  it('uses the host label, rejects unsafe URLs and isolates a failing host', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const { editor } = mount('rich', { resolveWikiLink: ({ target }) => {
      if (target === 'Boom') throw new Error('host failure')
      return target === 'Home' ? { href: 'https://wiki.example.com/home', label: 'Start page' } : { href: 'javascript:alert(1)' }
    } })
    pasteText(editor, 'See [[Home]], [[Evil]] and [[Boom]].')
    expect(marked(editor, 'link').map(node => [node.text, node.marks![0]!.attrs!.href])).toEqual([['Start page', 'https://wiki.example.com/home']])
    expect(editor.getText()).toBe('See Start page, [[Evil]] and [[Boom]].')
    expect(error).toHaveBeenCalledOnce()
  })

  it('keeps [[_TOC_]] literal without asking the host, and never resolves links in stored values', () => {
    const { editor, notices, controller } = mount('markdown-safe', { resolveWikiLink })
    pasteText(editor, '[[_TOC_]]')
    expect(resolveWikiLink).not.toHaveBeenCalled()
    expect(notices[0]!.degraded).toEqual([{ kind: 'toc', count: 1, excerpt: '[[_TOC_]]' }])
    controller.setValue('Stored [[Project Alpha]]')
    expect(resolveWikiLink).not.toHaveBeenCalled()
    expect(controller.getProjection().markdown).toBe('Stored \\[\\[Project Alpha\\]\\]')
  })

  it('maps Obsidian ![[embeds]] through resolveEmbed to images or links', () => {
    const resolveEmbed = vi.fn<MarkdownEmbedResolver>(({ target, heading }) => target === 'whiteboard.png'
      ? { src: '/files/whiteboard.png' }
      : target === 'Project Alpha' ? { href: `/wiki/project-alpha#${heading?.toLowerCase()}`, label: 'Project Alpha – Summary' } : null)
    const { editor, notices } = mount('rich', { resolveEmbed, resolveWikiLink })
    pasteText(editor, 'Embedded image: ![[whiteboard.png]]\n\nTransclusion: ![[Project Alpha#Summary]] and ![[missing.pdf]]')
    expect(all(editor.getJSON(), 'image').map(node => [node.attrs!.src, node.attrs!.alt])).toEqual([['/files/whiteboard.png', 'whiteboard.png']])
    expect(marked(editor, 'link').map(node => [node.text, node.marks![0]!.attrs!.href])).toEqual([['Project Alpha – Summary', '/wiki/project-alpha#summary']])
    expect(resolveWikiLink).not.toHaveBeenCalled()
    expect(notices[0]!.degraded).toEqual([{ kind: 'embed', count: 1, excerpt: '![[missing.pdf]]' }])
  })
})

describe('Import report', () => {
  it('reports what a GitHub wiki page kept as text to the host and as a notice', () => {
    const onMarkdownImport = vi.fn<MarkdownImportHandler>()
    const { editor, notices } = mount('rich', { onMarkdownImport, plugins: [createMathPlugin(), createDiagramsPlugin()] })
    pasteText(editor, page('github-deployment-guide'))
    const degraded = [{ kind: 'wiki-link', count: 2, excerpt: '[[Home]]' }, { kind: 'html', count: 2, excerpt: '<details>' }]
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith({ source: 'paste', leadingHeading: 'Deployment Guide', degraded })
    expect(Object.isFrozen(onMarkdownImport.mock.calls[0]![0].degraded)).toBe(true)
    expect(notices).toEqual([{ source: 'paste', degraded }])
    const content = blocks(editor)
    expect(content.map(node => node.type)).toEqual([
      'heading', 'paragraph', 'callout', 'callout', 'heading', 'taskList', 'bulletList', 'orderedList', 'table', 'heading', 'diagram', 'paragraph',
      'mathBlock', 'heading', 'paragraph', 'image', 'paragraph', 'paragraph', 'paragraph', 'paragraph', 'codeBlock', 'horizontalRule', 'orderedList',
    ])
    expect(plain(content[14]!)).toContain('Footnote reference¹.')
    expect(plain(content.at(-1)!)).toBe('The footnote text.')
    // `<br>` is a line break, not literal HTML.
    expect(content[19]!.content!.map(node => node.type)).toEqual(['text', 'hardBreak', 'text', 'hardBreak', 'text'])
  })

  it('reports Wiki.js constructs in markdown-safe and calls the host for degraded-only imports', () => {
    const { editor, notices } = mount('markdown-safe')
    pasteText(editor, page('wikijs-incident-policy'))
    expect(notices[0]!.degraded.map(part => part.kind)).toEqual(['toc', 'subscript', 'superscript', 'html', 'comment'])
    expect(notices[0]!.degraded.find(part => part.kind === 'html')).toEqual({ kind: 'html', count: 11, excerpt: '<kbd>' })
    expect(blocks(editor)[2]!.type).toBe('blockquote')
    const onMarkdownImport = vi.fn<MarkdownImportHandler>()
    const second = mount('rich', { onMarkdownImport })
    pasteText(second.editor, 'Plain **text**')
    expect(onMarkdownImport).not.toHaveBeenCalled()
    expect(second.notices).toEqual([])
    pasteText(second.editor, 'See [[Home]] and <kbd>Ctrl</kbd>.')
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith({ source: 'paste', degraded: [
      { kind: 'wiki-link', count: 1, excerpt: '[[Home]]' }, { kind: 'html', count: 1, excerpt: '<kbd>' },
    ] })
  })

  it('reports literal TeX when the math plugin is not registered', () => {
    const { editor, notices } = mount('markdown-safe')
    pasteText(editor, 'Energy $E = mc^2$.\n\n$$\nx^2\n$$')
    expect(notices[0]!.degraded).toEqual([{ kind: 'math', count: 2, excerpt: '$E = mc^2$' }])
  })

  it('titles the notice with Czech plural agreement', () => {
    expect([1, 2, 4, 5, 11].map(degradedNoticeTitle)).toEqual([
      '1 část byla vložena jako text', '2 části byly vloženy jako text', '4 části byly vloženy jako text',
      '5 částí bylo vloženo jako text', '11 částí bylo vloženo jako text',
    ])
  })
})
