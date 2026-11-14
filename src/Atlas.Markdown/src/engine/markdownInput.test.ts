// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownImportHandler, type MarkdownPlugin, type MarkdownProfile, type MarkdownRichValue } from '../core'
import { editorForController } from '../core/internal'
import { createMathPlugin } from '../plugins/math'
import { markdownInputErrorEvent, type MarkdownInputErrorDetail } from '../core'
import { MAX_MARKDOWN_TEXT_LENGTH } from '../core/documentConstraints'
import { markdownStrings } from '../ui/strings'
import { isPasteMarkdownShortcut, pasteMarkdownShortcutLabel, splitMarkdownFrontMatter } from './markdownInput'

const cleanups: (() => void)[] = []
// jsdom has no ClipboardEvent; retain the real PM clipboard parsing/dispatch path.
beforeEach(() => vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null }))
afterEach(() => { cleanups.splice(0).forEach(cleanup => cleanup()); document.body.replaceChildren(); vi.unstubAllGlobals(); vi.restoreAllMocks() })

function mount(profile: MarkdownProfile = 'rich', options: { plugins?: MarkdownPlugin[]; onMarkdownImport?: MarkdownImportHandler } = {}) {
  const value: string | MarkdownRichValue = profile === 'rich'
    ? { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } }
    : ''
  const controller = createMarkdownEditorController({ profile, source: { kind: 'local', value }, ...options })
  cleanups.push(() => controller.destroy())
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Markdown import' })
  return { controller, editor: editorForController(controller)!, host }
}

function pasteText(editor: NonNullable<ReturnType<typeof editorForController>>, text: string) {
  const event = new ClipboardEvent('paste', { bubbles: true, cancelable: true })
  Object.defineProperty(event, 'clipboardData', { value: {
    getData: (type: string) => type === 'text/plain' ? text : '',
    types: ['text/plain'], files: [], items: [],
  } })
  editor.view.dom.dispatchEvent(event)
}

const blocks = (editor: NonNullable<ReturnType<typeof editorForController>>): JSONContent[] => editor.getJSON().content ?? []
const types = (nodes: readonly JSONContent[]): string[] => nodes.map(node => node.type!)
function hasNode(node: JSONContent, type: string): boolean { return node.type === type || !!node.content?.some(child => hasNode(child, type)) }

const obsidianNote = `---
title: Meeting Notes
aliases: [Weekly sync, "Sync: backend"]
tags:
  - meetings
  - team/backend
created: 2026-09-01
cssclasses:
  wide: true
---

# Meeting Notes

Linked to [[Project Alpha]] and wrapped
across two source lines.`

const wikiJsPage = `---
title: Incident Policy
description: How we handle incidents
published: true
date: 2026-08-01T10:00:00.000Z
tags: ops, incidents
editor: markdown
---

# Incident Policy
Severity levels are described below.`

describe('Markdown front matter', () => {
  it('reads flat Obsidian and Wiki.js front matter and returns the body without it', () => {
    const obsidian = splitMarkdownFrontMatter(obsidianNote)
    expect(obsidian.title).toBe('Meeting Notes')
    expect(obsidian.frontMatter).toEqual({
      title: 'Meeting Notes', aliases: ['Weekly sync', 'Sync: backend'], tags: ['meetings', 'team/backend'], created: '2026-09-01',
    })
    expect(obsidian.body.trimStart().startsWith('# Meeting Notes')).toBe(true)
    const wikiJs = splitMarkdownFrontMatter(`\uFEFF${wikiJsPage}`)
    expect(wikiJs.frontMatter).toMatchObject({ title: 'Incident Policy', published: true, tags: 'ops, incidents', date: '2026-08-01T10:00:00.000Z' })
    expect(wikiJs.body).not.toContain('editor: markdown')
  })

  it('keeps a leading thematic break, setext heading or unterminated block as Markdown', () => {
    for (const text of ['---\nJust a paragraph\n---\n\nText', '---\n\nText below a rule', '---\ntitle: Draft\n\nNo closing fence', '---\n---\n']) {
      expect(splitMarkdownFrontMatter(text)).toEqual({ body: text })
    }
  })

  it('keeps a numeric or boolean-looking title as text and reads values after long blank runs', () => {
    const { title, frontMatter } = splitMarkdownFrontMatter(`---\ntitle: 2024\nversion: 1.10\nsummary: ok${' '.repeat(50_000)}\n---\nBody`)
    expect(title).toBe('2024')
    expect(frontMatter).toEqual({ title: '2024', version: 1.1, summary: 'ok' })
    expect(splitMarkdownFrontMatter(['---', 'title: true # draft', '---', ''].join('\n')).title).toBe('true')
  })

  it('never lets front matter keys reach object prototypes and ignores block scalars safely', () => {
    const { frontMatter, title } = splitMarkdownFrontMatter('---\n__proto__: polluted\nconstructor: x\ntitle: "Quoted \\"title\\""\nsummary: >\n  Folded\n  text\n---\nBody')
    expect(title).toBe('Quoted "title"')
    expect(frontMatter).toEqual({ title: 'Quoted "title"', summary: 'Folded text' })
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
  })
})

describe('Markdown text paste', () => {
  it.each(['rich', 'markdown-safe'] as const)('turns CommonMark soft wraps into spaces but keeps hard breaks in %s', profile => {
    const { editor } = mount(profile)
    // GitHub wiki source hard-wrapped at 80 columns, with one explicit backslash break.
    pasteText(editor, 'Deployments are frozen on Fridays. Ask the\non-call engineer before merging\ninto `main`.\n\nFirst line\\\nsecond line')
    const [first, second] = blocks(editor)
    expect(first!.content).toEqual([
      { type: 'text', text: 'Deployments are frozen on Fridays. Ask the on-call engineer before merging into ' },
      { type: 'text', text: 'main', marks: [{ type: 'code' }] },
      { type: 'text', text: '.' },
    ])
    expect(types(second!.content!)).toEqual(['text', 'hardBreak', 'text'])
  })

  it('keeps the line breaks of plain text (no Markdown in it) as hard breaks', () => {
    const { editor } = mount()
    pasteText(editor, 'Děkuji.\n\nS pozdravem\nPetr Novák')
    const [first, second] = blocks(editor)
    expect(first!.content).toEqual([{ type: 'text', text: 'Děkuji.' }])
    expect(types(second!.content!)).toEqual(['text', 'hardBreak', 'text'])
  })

  it('does not insert hard breaks at tight list and task item edges', () => {
    const { editor } = mount()
    pasteText(editor, '- [ ] VPN configured\n  - [ ] nested task\n1. Second\n   1. Nested ordered')
    expect(JSON.stringify(editor.getJSON())).not.toContain('hardBreak')
    expect(editor.getText()).toContain('VPN configured')
    expect(editor.getText()).not.toContain(' VPN')
  })

  it('keeps a dated history line one list item of text, not nested ordered lists, outside fenced code', () => {
    const { editor } = mount()
    pasteText(editor, '- 30. 10. 2026 23:40 — **Urgováno**: IT KBN\n- 1. 11. 2026 — telefonát\n\n```\n1. 2. 2026 log\n```')
    expect(hasNode(editor.getJSON(), 'orderedList')).toBe(false)
    const [list, code] = blocks(editor)
    expect(list!.content!.map(item => item.content![0]!.content![0]!.text)).toEqual(['30. 10. 2026 23:40 — ', '1. 11. 2026 — telefonát'])
    expect(code!.content![0]!.text).toBe('1. 2. 2026 log')
  })

  it.each(['rich', 'markdown-safe'] as const)('keeps H3-H6 at their level in %s', profile => {
    const { editor, controller } = mount(profile)
    pasteText(editor, '### Level three\n\n#### Level four\n\n##### Level five\n\n###### Level six')
    expect(blocks(editor).filter(node => node.content).map(node => [node.type, node.attrs?.level, node.content?.[0]?.text])).toEqual([
      ['heading', 3, 'Level three'], ['heading', 4, 'Level four'], ['heading', 5, 'Level five'], ['heading', 6, 'Level six'],
    ])
    expect(controller.getProjection().markdown).toBe('### Level three\n\n#### Level four\n\n##### Level five\n\n###### Level six')
    expect(controller.dispatch({ type: 'set-heading', level: 5 }) && controller.dispatch({ type: 'set-heading', level: 6 })).toBe(true)
    expect(controller.query({ type: 'set-heading', level: 6 }).active).toBe(true)
  })

  it('turns typed ##### and ###### into H5 and H6', () => {
    const { editor } = mount()
    const type = (text: string) => {
      const { from, to } = editor.state.selection
      if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, text, () => editor.state.tr.insertText(text, from, to))))
        editor.view.dispatch(editor.state.tr.insertText(text, from, to))
    }
    for (const character of '##### Five') type(character)
    expect(blocks(editor)[0]).toMatchObject({ type: 'heading', attrs: { level: 5 }, content: [{ text: 'Five' }] })
    editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph' }] })
    for (const character of '###### Six') type(character)
    expect(blocks(editor)[0]).toMatchObject({ type: 'heading', attrs: { level: 6 }, content: [{ text: 'Six' }] })
  })

  it('strips Obsidian front matter instead of inserting a rule and YAML text, and reports it to the host', () => {
    const onMarkdownImport = vi.fn<MarkdownImportHandler>()
    const { editor, controller } = mount('rich', { onMarkdownImport })
    pasteText(editor, obsidianNote)
    expect(types(blocks(editor))).toEqual(['heading', 'paragraph'])
    expect(controller.getProjection().text).not.toContain('aliases')
    expect(blocks(editor)[1]!.content![0]!.text).toBe('Linked to [[Project Alpha]] and wrapped across two source lines.')
    expect(onMarkdownImport).toHaveBeenCalledExactlyOnceWith({
      source: 'paste', title: 'Meeting Notes', leadingHeading: 'Meeting Notes',
      frontMatter: expect.objectContaining({ title: 'Meeting Notes', tags: ['meetings', 'team/backend'] }),
      // No resolveWikiLink hook: the link stays literal and is reported.
      degraded: [{ kind: 'wiki-link', count: 1, excerpt: '[[Project Alpha]]' }],
    })
    expect(Object.isFrozen(onMarkdownImport.mock.calls[0]![0])).toBe(true)
  })

  it('drops a duplicated leading H1 in the same undo step when the host asks for it', () => {
    const { editor, controller } = mount('markdown-safe', {
      onMarkdownImport: ({ title, leadingHeading }) => ({ omitLeadingHeading: !!title && title === leadingHeading }),
    })
    pasteText(editor, wikiJsPage)
    expect(controller.getProjection().markdown).toBe('Severity levels are described below.')
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().markdown).toBe('')
    pasteText(editor, '---\ntitle: Other page\n---\n# Incident Policy\n\nBody')
    expect(controller.getProjection().markdown).toBe('# Incident Policy\n\nBody')
  })

  it('does not call the host without front matter or leading H1 and isolates host failures', () => {
    const error = vi.spyOn(console, 'error').mockImplementation(() => {})
    const onMarkdownImport = vi.fn<MarkdownImportHandler>(() => { throw new Error('host failure') })
    const { editor, controller } = mount('rich', { onMarkdownImport })
    pasteText(editor, 'Plain **text**')
    expect(onMarkdownImport).not.toHaveBeenCalled()
    pasteText(editor, '\n\n# Heading\n\nBody')
    expect(onMarkdownImport).toHaveBeenCalledOnce()
    expect(error).toHaveBeenCalledOnce()
    expect(controller.getProjection().text).toContain('Heading')
  })

  it('preserves TeX source literally in markdown-safe instead of processing Markdown escapes or emphasis', () => {
    const { editor, controller } = mount('markdown-safe')
    pasteText(editor, 'Energy $E = mc^2$ with $a_1 + b_1$ costs $5 and $10.\n\n$$\n\\int_0^1 x^2 \\, dx\n\\\\ \\{a\\}\n$$')
    const [inline, block] = blocks(editor)
    expect(inline!.content).toEqual([{ type: 'text', text: 'Energy $E = mc^2$ with $a_1 + b_1$ costs $5 and $10.' }])
    expect(block!.content).toEqual([
      { type: 'text', text: '$$' }, { type: 'hardBreak' }, { type: 'text', text: '\\int_0^1 x^2 \\, dx' }, { type: 'hardBreak' },
      { type: 'text', text: '\\\\ \\{a\\}' }, { type: 'hardBreak' }, { type: 'text', text: '$$' },
    ])
    // The Markdown value escapes the literal backslashes and reloads to the same text.
    const markdown = controller.getProjection().markdown
    controller.setValue(markdown)
    expect(controller.getProjection().markdown).toBe(markdown)
    expect(controller.getProjection().text).toContain('\\int_0^1 x^2 \\, dx')
  })

  it('maps $…$ and $$…$$ to math nodes when the math plugin is registered', () => {
    const { editor } = mount('rich', { plugins: [createMathPlugin()] })
    pasteText(editor, 'Inline $\\frac{a}{b}$ math.\n\n$$\n\\sum_{i=1}^n i = \\frac{n(n+1)}{2}\n$$\n\n> $$ x \\, y $$')
    const [paragraph, block, quote] = blocks(editor)
    expect(paragraph!.content![1]).toEqual({ type: 'mathInline', attrs: { data: { version: 1, expression: '\\frac{a}{b}' } } })
    expect(block).toEqual({ type: 'mathBlock', attrs: { data: { version: 1, expression: '\\sum_{i=1}^n i = \\frac{n(n+1)}{2}' } } })
    expect(quote!.content![0]).toEqual({ type: 'mathBlock', attrs: { data: { version: 1, expression: 'x \\, y' } } })
  })

  it('keeps escaped dollars and code spans literal and leaves an unclosed $$ block as text', () => {
    const { editor } = mount('rich', { plugins: [createMathPlugin()] })
    pasteText(editor, 'Price \\$5 and `$x$` code.\n\n$$\nnot closed')
    expect(hasNode(editor.getJSON(), 'mathInline') || hasNode(editor.getJSON(), 'mathBlock')).toBe(false)
    expect(editor.getText()).toContain('Price $5 and $x$ code.')
    expect(editor.getText()).toContain('$$ not closed')
  })

  it('keeps shell variables as text and ends an unclosed $$ at the first blank line (Pandoc)', () => {
    const { editor } = mount('rich', { plugins: [createMathPlugin()] })
    pasteText(editor, 'Paths $HOME/$USER and $PATH stay text.\n\n$x$ is math.\n\n$$\na + b\n\nLater $$ y $$ text')
    const [shell, math, unclosed, later] = blocks(editor)
    expect(shell!.content).toEqual([{ type: 'text', text: 'Paths $HOME/$USER and $PATH stay text.' }])
    expect(math!.content![0]).toEqual({ type: 'mathInline', attrs: { data: { version: 1, expression: 'x' } } })
    // The unclosed `$$` does not swallow the next paragraph; that one holds its own math.
    expect(unclosed!.type).toBe('paragraph')
    expect(hasNode(unclosed!, 'mathBlock') || hasNode(unclosed!, 'mathInline')).toBe(false)
    expect(later!.type).toBe('paragraph')
    expect(hasNode(later!, 'mathInline')).toBe(true)
  })

  it('scans many unmatched dollars in linear time', () => {
    const { editor } = mount('rich', { plugins: [createMathPlugin()] })
    const started = performance.now()
    pasteText(editor, '$a '.repeat(20_000))
    expect(performance.now() - started).toBeLessThan(5_000)
    expect(hasNode(editor.getJSON(), 'mathInline')).toBe(false)
  })

  it('pastes text above the input limit literally instead of parsing it as Markdown', () => {
    const { editor } = mount('rich')
    const large = `# Not a heading\n\n${'x'.repeat(MAX_MARKDOWN_TEXT_LENGTH)}`
    pasteText(editor, large)
    expect(hasNode(editor.getJSON(), 'heading')).toBe(false)
    expect(editor.getText()).toContain('# Not a heading')
  })

  it('reports a Markdown paste the document cannot hold instead of throwing', () => {
    const { editor, host } = mount('rich')
    const errors: MarkdownInputErrorDetail[] = []
    host.addEventListener(markdownInputErrorEvent, event => errors.push((event as CustomEvent<MarkdownInputErrorDetail>).detail))
    const before = editor.getJSON()
    const parser = (editor.storage as unknown as { markdown: { parser: { parse(text: string): string } } }).markdown.parser
    vi.spyOn(parser, 'parse').mockImplementation(() => { throw new Error('unsupported structure') })
    expect(editor.commands.pasteMarkdown('**bold**')).toBe(false)
    expect(errors).toEqual([{ source: 'paste', message: markdownStrings.markdownImportFailed }])
    expect(editor.getJSON()).toEqual(before)
  })

  it('recognises Ctrl+Shift+Alt+V but not AltGr+Shift+V, and labels the shortcut per platform', () => {
    const key = (init: KeyboardEventInit, altGraph = false) => Object.assign(new KeyboardEvent('keydown', { key: 'V', code: 'KeyV', shiftKey: true, altKey: true, ...init }),
      { getModifierState: (name: string) => name === 'AltGraph' && altGraph })
    expect(isPasteMarkdownShortcut(key({ ctrlKey: true }))).toBe(true)
    expect(isPasteMarkdownShortcut(key({ ctrlKey: true }, true))).toBe(false)
    expect(isPasteMarkdownShortcut(key({ metaKey: true }))).toBe(true)
    const view = (userAgent: string) => ({ navigator: { userAgent } }) as unknown as Window
    expect(pasteMarkdownShortcutLabel(view('Mozilla/5.0 (Macintosh; Intel Mac OS X 14_5)'))).toBe('⌥⇧⌘V')
    expect(pasteMarkdownShortcutLabel(view('Mozilla/5.0 (Windows NT 10.0; Win64; x64)'))).toBe('Ctrl+Shift+Alt+V')
  })
})

describe('Markdown value input', () => {
  it('loads H5 in a markdown-safe value at its level', () => {
    const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: '##### Runbook\n\nSoft\nwrap' } })
    cleanups.push(() => controller.destroy())
    controller.attachView(document.createElement('div'), { ariaLabel: 'Value' })
    expect(controller.state.lifecycle).toBe('ready')
    expect(controller.getProjection().markdown).toBe('##### Runbook\n\nSoft wrap')
  })

  it('keeps plain CommonMark escapes for stored values, so serialized backslashes round-trip', () => {
    const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: 'Cost $a\\\\,b$' } })
    cleanups.push(() => controller.destroy())
    controller.attachView(document.createElement('div'), { ariaLabel: 'Value' })
    expect(controller.getProjection().text).toBe('Cost $a\\,b$')
    expect(controller.getProjection().markdown).toBe('Cost $a\\\\,b$')
  })
})

describe('Markdown projection of block images', () => {
  it.each(['rich', 'markdown-safe'] as const)('separates a block image from the following paragraph in %s', profile => {
    const { editor, controller } = mount(profile)
    // Obsidian: an image line directly followed by text on the next line.
    pasteText(editor, 'Standard image:\n![Whiteboard](images/whiteboard.png)\nTransclusion: ![[Project Alpha#Summary]]')
    expect(types(blocks(editor))).toEqual(['paragraph', 'image', 'paragraph'])
    const markdown = controller.getProjection().markdown
    expect(markdown).toBe('Standard image:\n\n![Whiteboard](images/whiteboard.png)\n\nTransclusion: !\\[\\[Project Alpha#Summary\\]\\]')
    const reloaded = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: markdown } })
    cleanups.push(() => reloaded.destroy())
    reloaded.attachView(document.createElement('div'), { ariaLabel: 'Reloaded' })
    expect(reloaded.getProjection().markdown).toBe(markdown)
  })

  it('keeps image titles and escapes parentheses in the source URL', () => {
    const { controller } = mount('markdown-safe')
    controller.setValue('![Timeline](/assets/ops/time(line).png "Incident timeline")\n\nAfter')
    expect(controller.getProjection().markdown).toBe('![Timeline](/assets/ops/time\\(line\\).png "Incident timeline")\n\nAfter')
  })
})
