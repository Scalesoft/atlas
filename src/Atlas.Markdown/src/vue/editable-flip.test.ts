// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import { createMarkdownEditorController } from '../core'
import MarkdownEditor from './MarkdownEditor.vue'
import type { LocalMarkdownEditorController, MarkdownRichValue } from '../core'
import { editorForController } from '../core/internal'
import { createMathBlockNode, createMathPlugin } from '../plugins/math'
import { mathBlockRuntime } from '../plugins/math/runtime'

const apps: App[] = []
beforeEach(() => vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }))
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

// 100 blocks: text, headings, lists, a table, an image and math modules (a NodeView with a lazily loaded runtime).
function page(): MarkdownRichValue {
  const text = (value: string) => [{ type: 'text', text: value }]
  const content: unknown[] = []
  for (let i = 0; content.length < 100; i++) {
    content.push({ type: 'heading', attrs: { level: 2 }, content: text(`Oddíl ${i}`) })
    content.push({ type: 'paragraph', content: text(`Odstavec ${i} s textem.`) })
    content.push({ type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: text(`Bod ${i}`) }] }] })
    if (i % 5 === 0) content.push(createMathBlockNode({ version: 1, expression: `x^${i}` }))
    if (i % 10 === 0) content.push({ type: 'image', attrs: { src: `https://example.com/${i}.png`, alt: `Obrázek ${i}` } })
    if (i % 10 === 5) content.push({ type: 'table', content: [{ type: 'tableRow', content: [
      { type: 'tableCell', content: [{ type: 'paragraph', content: text('A') }] }, { type: 'tableCell', content: [{ type: 'paragraph', content: text('B') }] }] }] })
  }
  return { format: 'atlas-rich', version: 1, plugins: [{ id: 'math', version: 1 }], document: { type: 'doc', content: content.slice(0, 100) } } as MarkdownRichValue
}

describe('editability is a flag, not a mode (contract J1.2)', () => {
  // Before: each flip mounted or unmounted the editor controls (block handle, bubble menus), whose 4 plugin registrations recreated every
  // plugin view (y-sync re-rendered the document from Yjs), and re-rendered each module block.
  it('flips 10× without new node views, plugin views or plugin registrations, and the controls stay mounted', async () => {
    let controller!: LocalMarkdownEditorController<'rich'>
    const readonly = ref(false), plugins = [createMathPlugin()]
    const host = document.createElement('div'); document.body.append(host)
    const app = createApp({ render: () => h(MarkdownEditor, { profile: 'rich', initialValue: page(), mode: 'revision', readonly: readonly.value, plugins,
      ariaLabel: 'Stránka', onCreated: (value: unknown) => { controller = value as LocalMarkdownEditorController<'rich'> } }) })
    apps.push(app); app.mount(host)
    await nextTick(); await new Promise(resolve => setTimeout(resolve, 0)); await nextTick()
    const editor = editorForController(controller)!
    expect(editor.state.doc.childCount).toBe(100)
    const blocks = () => { const doms: Node[] = []; editor.state.doc.forEach((_node, offset) => { doms.push(editor.view.nodeDOM(offset)!) }); return doms }
    const pluginViews = () => [...(editor.view as unknown as { pluginViews: object[] }).pluginViews]
    const before = blocks(), viewsBefore = pluginViews()
    // The math blocks render lazily; all of them drawn before the flips.
    await vi.waitFor(() => expect(host.querySelectorAll('.md-math')).toHaveLength(host.querySelectorAll('[data-md-module]').length), { timeout: 5000 })
    const register = vi.spyOn(editor, 'registerPlugin'), unregister = vi.spyOn(editor, 'unregisterPlugin')
    const renders = vi.spyOn(mathBlockRuntime, 'render')

    const started = performance.now()
    for (let i = 0; i < 10; i++) { readonly.value = !readonly.value; await nextTick() }
    const elapsed = (performance.now() - started) / 10
    await new Promise(resolve => setTimeout(resolve, 0)); await nextTick()

    expect(editor.isEditable).toBe(true)
    expect(register).not.toHaveBeenCalled(); expect(unregister).not.toHaveBeenCalled()
    expect(pluginViews().every((view, index) => view === viewsBefore[index])).toBe(true)
    expect(blocks().every((dom, index) => dom === before[index])).toBe(true)
    expect(renders).not.toHaveBeenCalled()
    // Counted above; the time is a coarse guard against a rebuild sneaking back (jsdom, no layout).
    expect(elapsed).toBeLessThan(16)
  })

  it('builds a read-only view read-only: no contenteditable flip over the drawn document', () => {
    const writes: [Element, string][] = []
    const setAttribute = Element.prototype.setAttribute
    vi.spyOn(Element.prototype, 'setAttribute').mockImplementation(function (this: Element, name: string, value: string) {
      if (name === 'contenteditable') writes.push([this, value])
      return setAttribute.call(this, name, value)
    })
    const controller = createMarkdownEditorController({ profile: 'rich', editable: false, plugins: [createMathPlugin()], source: { kind: 'local', value: page() } })
    try {
      const host = document.createElement('div'); document.body.append(host)
      controller.attachView(host, { ariaLabel: 'Stránka' })
      const dom = editorForController(controller)!.view.dom
      expect(writes.filter(([element]) => element === dom).map(([, value]) => value)).toEqual(['false'])
      expect(host.querySelector('.ProseMirror')!.getAttribute('aria-readonly')).toBe('true')
    } finally { controller.destroy() }
  })
})