// @vitest-environment jsdom
import type { Editor } from '@tiptap/core'
import { describe, expect, it } from 'vitest'
import { createMarkdownEditorController, type MarkdownRichValue } from '../../core'
import { createMathBlockNode, createMathInlineNode, createMathPlugin, normalizeMath } from './index'
import { mathBlockRuntime, mathInlineRuntime } from './runtime'

describe('math module', () => {
  it('validates size and version while preserving LaTeX source', () => {
    expect(normalizeMath({ version: 1, expression: '\\frac{a}{b}' })).toEqual({ version: 1, expression: '\\frac{a}{b}' })
    for (const value of [{ version: 2, expression: 'x' }, { version: 1, expression: '' }, { version: 1, expression: 'x'.repeat(10_001) }]) expect(() => normalizeMath(value)).toThrow()
  })
  it('registers inline and block nodes under one persisted plugin and retains both sources', () => {
    const data = { version: 1 as const, expression: '\\sqrt{x}' }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createMathPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'math', version: 1 }], document: { type: 'doc', content: [createMathBlockNode(data), { type: 'paragraph', content: [createMathInlineNode(data)] }] } } } })
    try {
      controller.attachView(document.createElement('div'), { ariaLabel: 'Editor' })
      const projection = controller.getProjection()
      expect(projection.rich.plugins).toContainEqual({ id: 'math', version: 1 })
      expect(projection.rich.plugins).not.toContainEqual({ id: 'math-inline', version: 1 })
      expect(projection.markdown).toBe('$$\n\\sqrt{x}\n$$\n\n$\\sqrt{x}$')
      expect(projection.markdownLossy).toBe(false)
      expect(projection.rich.document).toMatchObject({ content: [{ type: 'mathBlock' }, { content: [{ type: 'mathInline', attrs: { data } }] }] })
    } finally { controller.destroy() }
  })
  it('writes source $$ lines cannot carry as a ```math fence and keeps inline source $…$ cannot carry lossy', () => {
    const block = (expression: string) => createMathBlockNode({ version: 1, expression })
    const inline = (expression: string) => ({ type: 'paragraph', content: [createMathInlineNode({ version: 1, expression })] })
    const project = (content: unknown[]) => {
      const doc = { type: 'doc', content } as MarkdownRichValue['document']
      const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createMathPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'math', version: 1 }], document: doc } } })
      try { controller.attachView(document.createElement('div'), { ariaLabel: 'Editor' }); return controller.getProjection() } finally { controller.destroy() }
    }
    expect(project([block('a\n\n  b $$ ```')])).toMatchObject({ markdown: '````math\na\n\n  b $$ ```\n````', markdownLossy: false })
    for (const expression of ['a\nb', 'cost $5', 'x\\', ' x']) expect(project([inline(expression)]).markdownLossy, expression).toBe(true)
    expect(project([inline('a\\$b \\\\')])).toMatchObject({ markdown: '$a\\$b \\\\$', markdownLossy: false })
  })
  it('turns pasted Markdown $…$ and $$…$$ into math nodes and keeps oversized source as literal text', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createMathPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    try {
      const host = document.createElement('div'); document.body.append(host)
      controller.attachView(host, { ariaLabel: 'Editor' })
      const huge = 'x'.repeat(10_001)
      // Obsidian note: display math with LaTeX thin spaces, then an inline formula over the module limit.
      const text = `$$\n\\int_0^1 x^2 \\, dx\n$$\n\nToo long $${huge}$ stays text.`
      const event = new Event('paste', { bubbles: true, cancelable: true })
      Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => type === 'text/plain' ? text : '', types: ['text/plain'], files: [], items: [] } })
      host.querySelector('.ProseMirror')!.dispatchEvent(event)
      const content = controller.getProjection().rich.document.content as { type: string; attrs?: { data?: unknown }; content?: { type: string; text?: string }[] }[]
      expect(content[0]).toEqual({ type: 'mathBlock', attrs: { data: { version: 1, expression: '\\int_0^1 x^2 \\, dx' } } })
      expect(content[1]!.content).toEqual([{ type: 'text', text: `Too long $${huge}$ stays text.` }])
    } finally { controller.destroy(); document.body.replaceChildren() }
  })
  it('renders native accessible MathML for inline and block math without stylesheets', async () => {
    for (const [runtime, display] of [[mathBlockRuntime, 'block'], [mathInlineRuntime, null]] as const) {
      const container = document.createElement('span')
      await runtime.render({ container, data: { version: 1, expression: '\\frac{1}{2}' }, document: { type: 'doc' }, signal: new AbortController().signal })
      const math = container.querySelector('math')
      expect(math).not.toBeNull()
      expect(math?.getAttribute('display')).toBe(display)
      expect(container.querySelector('link')).toBeNull()
    }
  })
  it('does not permit untrusted links and preserves invalid source as text', async () => {
    for (const expression of ['\\href{javascript:alert(1)}{click}', '\\frac{']) {
      const container = document.createElement('span')
      await mathInlineRuntime.render({ container, data: { version: 1, expression }, document: { type: 'doc' }, signal: new AbortController().signal })
      expect(container.querySelector('a,[href],[onclick]')).toBeNull()
      if (expression === '\\frac{') {
        expect(container.querySelector('[role=alert]')).not.toBeNull()
        expect(container.querySelector('code')?.textContent).toBe(expression)
      }
    }
  })
  describe('editing in place', () => {
    const open = (initial: unknown[] = [{ type: 'paragraph' }]) => {
      const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createMathPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: initial } as MarkdownRichValue['document'] } } })
      const host = document.createElement('div'); document.body.append(host)
      controller.attachView(host, { ariaLabel: 'Editor' })
      const editor = (controller as unknown as { editor: Editor }).editor
      editor.commands.focus('end')
      const type = (text: string) => { for (const char of text) {
        const { from, to } = editor.state.selection
        if (!editor.view.someProp('handleTextInput', (handle: any) => handle(editor.view, from, to, char))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
      } }
      const content = () => (controller.getProjection().rich.document.content as { type: string; content?: { type: string; text?: string; attrs?: { data?: { expression: string } } }[] }[])
      return { controller, editor, type, content, done: () => { controller.destroy(); document.body.replaceChildren() } }
    }
    it('turns typed $…$ into an inline formula and gives the text back on Backspace', () => {
      const { editor, type, content, done } = open()
      try {
        type('Plocha $\\pi r^2$ ')
        expect(content()[0]!.content).toMatchObject([{ type: 'text', text: 'Plocha ' }, { type: 'mathInline', attrs: { data: { expression: '\\pi r^2' } } }, { type: 'text', text: ' ' }])
        editor.commands.undoInputRule()
        expect(editor.getText()).toContain('Plocha $\\pi r^2$')
      } finally { done() }
    })
    it.each(['Za $5 a $10 ', 'Cena $5$ ', 'Je $ x$ ', 'Je $x $ ', 'a$x$ ', '$5 a $10, '])('leaves %j as text', text => {
      const { type, content, done } = open()
      try { type(text); expect(content()[0]!.content).toEqual([{ type: 'text', text }]) } finally { done() }
    })
    it('does not fire inside inline code', () => {
      const { editor, type, content, done } = open([{ type: 'paragraph', content: [{ type: 'text', text: 'k', marks: [{ type: 'code' }] }] }])
      try {
        editor.commands.focus('end'); type(' $x$ ')
        expect(content()[0]!.content!.some(node => node.type === 'mathInline')).toBe(false)
      } finally { done() }
    })
    it('turns $$ + Enter in an empty paragraph into a block formula', () => {
      const { editor, type, content, done } = open()
      try {
        type('$$')
        expect(editor.view.someProp('handleKeyDown', (handle: any) => handle(editor.view, new KeyboardEvent('keydown', { key: 'Enter' })))).toBe(true)
        expect(content()[0]).toMatchObject({ type: 'mathBlock' })
        editor.commands.undo()
        expect(content()[0]).toMatchObject({ type: 'paragraph' })
      } finally { done() }
    })
    it('updates the node live from the panel, keeps the last good render on a parse error and undoes', async () => {
      const container = document.createElement('div'); document.body.append(container)
      let data = { version: 1 as const, expression: 'x^2' }
      const base = { container, document: { type: 'doc' } as never, signal: new AbortController().signal, update: (next: typeof data) => { data = next } }
      const view = await mathBlockRuntime.render({ ...base, data }) as { update(context: unknown): void }
      const textarea = container.querySelector('textarea')!
      const alert = container.querySelector<HTMLElement>('.md-math-error')!
      textarea.value = '\\frac{a}{b}'; textarea.dispatchEvent(new Event('input'))
      expect(data.expression).toBe('\\frac{a}{b}')
      view.update({ ...base, data })
      expect(container.innerHTML).toContain('mfrac')
      textarea.value = '\\frac{a'; textarea.dispatchEvent(new Event('input'))
      view.update({ ...base, data })
      await new Promise(resolve => setTimeout(resolve, 20))
      expect(alert.hidden).toBe(false)
      expect(container.innerHTML).toContain('mfrac')
      view.update({ ...base, data: { version: 1, expression: 'x^2' } })
      expect(textarea.value).toBe('x^2')
      container.remove()
    })
  })
})
