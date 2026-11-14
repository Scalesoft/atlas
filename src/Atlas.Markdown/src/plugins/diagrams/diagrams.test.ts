// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownModuleContext, type MarkdownPlugin } from '../../core'
import type { Editor } from '@tiptap/core'
import { createDiagramNode, createDiagramsPlugin, insertDiagram, normalizeDiagram, type MarkdownDiagramData } from './index'
import { diagramRuntime, naturalSizeSvg } from './runtime'

const mermaid = vi.hoisted(() => ({ initialize: vi.fn(), render: vi.fn() }))
vi.mock('mermaid', () => ({ default: mermaid }))
const data: MarkdownDiagramData = { version: 1, source: 'flowchart LR\n A --> B', caption: 'Postup' }
const controllers: MarkdownEditorController[] = []
const aborts: AbortController[] = []
const svg = '<svg xmlns="http://www.w3.org/2000/svg"><text>A</text></svg>'
const originalDecode = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'decode')
beforeAll(() => Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: async () => {} }))
afterAll(() => { if (originalDecode) Object.defineProperty(HTMLImageElement.prototype, 'decode', originalDecode); else Reflect.deleteProperty(HTMLImageElement.prototype, 'decode') })
async function mountedEditor() {
  mermaid.render.mockResolvedValue({ svg })
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createDiagramsPlugin(), capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: { type: 'doc', content: [createDiagramNode(data)] } } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Diagrams' })
  await vi.waitFor(() => expect(host.querySelector('img')).not.toBeNull())
  // A click on the drawing only selects the block; a click on its source (or Enter, or the toolbar's „Upravit“ button) opens the source panel in place.
  host.querySelector<HTMLElement>('.md-module')!.click()
  host.querySelector<HTMLElement>('.md-diagram-foot pre')!.click()
  const textarea = host.querySelector('textarea')!
  const input = (source: string) => { textarea.value = source; textarea.dispatchEvent(new Event('input', { bubbles: true })) }
  return { controller, editor, host, textarea, input }
}

afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); aborts.splice(0).forEach(abort => abort.abort()); document.body.replaceChildren(); vi.clearAllMocks() })

describe('diagram module', () => {
  it('rejects executable/configuration source and oversize documents but accepts an empty live source', () => {
    for (const source of ['%%{init: {securityLevel:"loose"}}%%\ngraph LR\nA-->B', '---\nconfig: {}\n---\ngraph LR\nA-->B', '<img src=x onerror=alert(1)>', 'x'.repeat(20_001)]) {
      expect(() => normalizeDiagram({ ...data, source })).toThrow()
    }
    expect(normalizeDiagram(data)).toEqual(data)
    expect(normalizeDiagram({ ...data, source: '' }).source).toBe('')
    expect(createDiagramsPlugin().schemaVersion).toBe(2)
    expect(() => normalizeDiagram({ ...data, version: 2 })).toThrow()
  })
  it('migrates version one source without changing content and marks markdown fallback as lossy', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createDiagramsPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 1 }], document: { type: 'doc', content: [createDiagramNode(data)] } } } })
    try {
      controller.attachView(document.createElement('div'), { ariaLabel: 'Editor' })
      expect(controller.getProjection().rich.document).toMatchObject({ content: [{ attrs: { data } }] })
      expect(controller.getValue().plugins).toEqual([{ id: 'diagrams', version: 2 }])
      expect(controller.getProjection().markdownLossy).toBe(true)
      expect(controller.getProjection().markdown).toContain('A --> B')
      expect(controller.dispatch(insertDiagram({ ...data, source: '<script>bad</script>' }))).toBe(false)
    } finally { controller.destroy() }
  })
  it('writes a diagram without a caption as a ```mermaid fence longer than any backtick run in its source', () => {
    const source = 'flowchart LR\n  A["```"] --> B'
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createDiagramsPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: { type: 'doc', content: [createDiagramNode({ ...data, source, caption: '' })] } } } })
    try {
      controller.attachView(document.createElement('div'), { ariaLabel: 'Editor' })
      expect(controller.getProjection()).toMatchObject({ markdown: `\`\`\`\`mermaid\n${source}\n\`\`\`\``, markdownLossy: false })
    } finally { controller.destroy() }
  })
  it('uses editor theme and strict SVG image rendering while retaining accessible readonly source', async () => {
    mermaid.render.mockResolvedValue({ svg })
    const container = document.createElement('div'); document.body.append(container)
    const abort = new AbortController(); aborts.push(abort)
    await diagramRuntime.render({ container, data, document: { type: 'doc' }, signal: abort.signal })
    await vi.waitFor(() => expect(container.querySelector('img')).not.toBeNull())
    expect(mermaid.initialize).toHaveBeenCalledWith(expect.objectContaining({ securityLevel: 'strict', htmlLabels: false, maxTextSize: 20_000 }))
    expect(mermaid.initialize).toHaveBeenCalledWith(expect.objectContaining({ theme: 'base', themeVariables: expect.objectContaining({ primaryColor: '#e3f4f7', primaryTextColor: '#102f36', fontSize: '14px' }) }))
    expect(container.querySelector('img')?.src).toMatch(/^data:image\/svg\+xml/)
    expect(container.querySelector('img')?.alt).toBe('Postup')
    expect(container.querySelector('.md-diagram-visual svg')).toBeNull()
    expect(container.querySelector('details')?.textContent).toContain(data.source)
    expect(container.querySelector<HTMLElement>('.md-diagram-source-editor')!.hidden).toBe(true)
    expect(document.body.children).toHaveLength(1)
  })
  it('gives the image the natural size of the diagram instead of the column width', () => {
    const sized = naturalSizeSvg('<svg xmlns="http://www.w3.org/2000/svg" width="100%" viewBox="0 0 262.5 70" style="max-width: 262.5px;"><g/></svg>', window)
    const svg = new DOMParser().parseFromString(sized, 'image/svg+xml').documentElement
    expect([svg.getAttribute('width'), svg.getAttribute('height'), svg.getAttribute('style') ?? '']).toEqual(['263', '70', ''])
    expect(naturalSizeSvg(svg.outerHTML.replace(/viewBox="[^"]*"/, ''), window)).not.toContain('viewBox')
  })
  it('preserves readable source on errors and does not paint after unmount', async () => {
    const container = document.createElement('div'); document.body.append(container)
    const abort = new AbortController(); aborts.push(abort)
    const context: MarkdownModuleContext<MarkdownDiagramData> = { container, data, document: { type: 'doc' }, signal: abort.signal }
    mermaid.render.mockRejectedValue(new Error('Syntax error'))
    await diagramRuntime.render(context)
    await vi.waitFor(() => expect(container.querySelector('[role=status]')?.textContent).toContain('Syntax error'))
    expect(container.querySelector('pre')?.textContent).toBe(data.source)
    let resolve!: (value: { svg: string }) => void
    mermaid.render.mockImplementation(() => new Promise(yes => { resolve = yes }))
    await diagramRuntime.render(context)
    await vi.waitFor(() => expect(resolve).toBeTypeOf('function'))
    abort.abort(); resolve({ svg: '<svg></svg>' })
    await vi.waitFor(() => expect(document.body.children).toHaveLength(1))
    expect(container.querySelector('img')).toBeNull()
  })
  it('edits source inline without remounting, preserves focus and groups typing for undo/redo', async () => {
    const { host, textarea, input, editor } = await mountedEditor()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    expect(host.querySelector<HTMLElement>('.md-diagram-source-editor')!.hidden).toBe(false)
    expect(document.activeElement).toBe(textarea)
    input(`${data.source}\n B --> C`)
    input(`${data.source}\n B --> CD`)
    expect(host.querySelector('textarea')).toBe(textarea); expect(document.activeElement).toBe(textarea)
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe(`${data.source}\n B --> CD`)
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }))
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe(data.source); expect(textarea.value).toBe(data.source)
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true }))
    expect(textarea.value).toBe(`${data.source}\n B --> CD`)
  })
  it('keeps last valid image and invalid syntax, rejecting security directives without losing local text', async () => {
    const { host, textarea, input, editor } = await mountedEditor()
    const originalImage = host.querySelector('img')
    mermaid.render.mockRejectedValue(new Error('Incomplete Mermaid syntax'))
    input('flowchart LR\n BROKEN[')
    await vi.waitFor(() => expect(host.querySelector('.md-diagram-status[role=status]')!.textContent).toContain('poslední platný náhled'))
    await vi.waitFor(() => expect(host.querySelectorAll('[role=status]')).toHaveLength(1))
    expect(host.querySelector('img')).toBe(originalImage)
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe('flowchart LR\n BROKEN[')
    expect(textarea.value).toBe('flowchart LR\n BROKEN[')
    input('%%{init: {securityLevel:"loose"}}%%')
    expect(textarea.value).toContain('securityLevel')
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe('flowchart LR\n BROKEN[')
    expect(host.querySelector('.md-diagram-status[role=status]')!.textContent).toContain('konfigurační direktivy')
  })
  it('stores empty source and closes editing on readonly without accepting late inputs', async () => {
    const { controller, host, textarea, input, editor } = await mountedEditor()
    input('')
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe('')
    await vi.waitFor(() => expect(host.querySelector('img')).toBeNull())
    controller.setEditable(false)
    await vi.waitFor(() => expect(host.querySelector<HTMLElement>('.md-diagram-source-editor')!.hidden).toBe(true))
    textarea.value = 'flowchart LR\n Evil --> Saved'; textarea.dispatchEvent(new Event('input'))
    expect(editor.state.doc.firstChild!.attrs.data.source).toBe('')
  })
  it('leaves composing Escape and undo to the input method', async () => {
    const { host, textarea, editor } = await mountedEditor()
    const undo = vi.spyOn(editor.commands, 'undo')
    textarea.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }))
    for (const key of ['Escape', 'z']) {
      const event = new KeyboardEvent('keydown', { key, ctrlKey: key === 'z', bubbles: true, cancelable: true })
      textarea.dispatchEvent(event)
      expect(event.defaultPrevented).toBe(false)
    }
    expect(undo).not.toHaveBeenCalled()
    expect(host.querySelector<HTMLElement>('.md-diagram-source-editor')!.hidden).toBe(false)
    textarea.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }))
    textarea.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(host.querySelector<HTMLElement>('.md-diagram-source-editor')!.hidden).toBe(true)
    // Opened by a block click: Escape returns to the editor with the diagram still selected.
    expect(document.activeElement).toBe(editor.view.dom); expect(editor.state.selection.from).toBe(0)
  })
  it('discards old rendering results after a newer source has been saved', async () => {
    const { host, input } = await mountedEditor()
    const pending: { source: string; resolve: (value: { svg: string }) => void }[] = []
    mermaid.render.mockImplementation((_id, source: string) => new Promise(resolve => { pending.push({ source, resolve }) }))
    input('flowchart LR\n Old --> Result')
    await vi.waitFor(() => expect(pending).toHaveLength(1))
    input('flowchart LR\n New --> Result')
    pending[0]!.resolve({ svg: '<svg><text>Old</text></svg>' })
    await vi.waitFor(() => expect(pending).toHaveLength(2))
    expect(decodeURIComponent(host.querySelector('img')!.src)).not.toContain('>Old<')
    pending[1]!.resolve({ svg: '<svg><text>New</text></svg>' })
    await vi.waitFor(() => expect(decodeURIComponent(host.querySelector('img')!.src)).toContain('>New<'))
  })
  it('repaints for changed theme tokens without editing source or losing the inline editor', async () => {
    const { host, textarea, editor } = await mountedEditor()
    const before = editor.getJSON()
    // jsdom does not inherit CSS custom properties; set the computed runtime token explicitly.
    host.querySelector<HTMLElement>('.md-diagram')!.parentElement!.style.setProperty('--accent-soft', '#ddeeff')
    host.classList.add('updated-theme')
    await vi.waitFor(() => expect(mermaid.initialize).toHaveBeenLastCalledWith(expect.objectContaining({ themeVariables: expect.objectContaining({ primaryColor: '#ddeeff' }) })))
    expect(editor.getJSON()).toEqual(before)
    expect(host.querySelector('textarea')).toBe(textarea)
    expect(document.activeElement).toBe(textarea)
  })
  // A board's pan or zoom restyles an ancestor (transform) on every frame: that is no theme change and reads nothing; a token set on
  // an ancestor in a burst of restyles is read once, after the burst has settled.
  it('ignores ancestor restyles that are no theme and reads a token change once after its burst', async () => {
    const { host } = await mountedEditor()
    const parent = host.querySelector<HTMLElement>('.md-diagram')!.parentElement!
    await vi.waitFor(() => expect(mermaid.initialize).toHaveBeenCalled())
    // Whatever the mounting itself scheduled has settled first.
    await new Promise(resolve => setTimeout(resolve, 300))
    const painted = vi.mocked(mermaid.initialize).mock.calls.length
    // Reading the theme inserts a probe into the diagram (diagramTheme): none may come.
    const probes: Node[] = []
    const watching = new MutationObserver(records => records.forEach(record => probes.push(...record.addedNodes)))
    watching.observe(host, { childList: true, subtree: true })
    for (let frame = 0; frame < 10; frame++) {
      host.style.transform = `translate(${frame}px, 0)`
      await new Promise(resolve => setTimeout(resolve, 10))
    }
    await new Promise(resolve => setTimeout(resolve, 300))
    watching.disconnect()
    expect(probes).toEqual([])
    // jsdom keeps no custom property in a style attribute's text: the theme changes by a class, as an app's theme switch does.
    parent.style.setProperty('--accent-soft', '#ccddee')
    host.classList.add('next-theme')
    for (let frame = 0; frame < 10; frame++) {
      host.style.transform = `translate(${frame}px, 1px)`
      await new Promise(resolve => setTimeout(resolve, 10))
    }
    expect(mermaid.initialize).toHaveBeenCalledTimes(painted)
    await vi.waitFor(() => expect(mermaid.initialize).toHaveBeenLastCalledWith(expect.objectContaining({ themeVariables: expect.objectContaining({ primaryColor: '#ccddee' }) })))
    await new Promise(resolve => setTimeout(resolve, 300))
    expect(mermaid.initialize).toHaveBeenCalledTimes(painted + 1)
  })
})
