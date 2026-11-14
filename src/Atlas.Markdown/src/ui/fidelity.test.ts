// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import EditorSurface from './EditorSurface.vue'

const apps: App[] = []
const controllers: MarkdownEditorController[] = []
beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }) })
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals()
})
function create() {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'test.footer', apiVersion: 1,
    create: () => ({ onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: {
    format: 'atlas-rich', version: 1,
    document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Text' }] }] },
  } } })
  controllers.push(controller)
  return { controller, editor: () => editor }
}
function mount(render: () => ReturnType<typeof h>) {
  const host = document.createElement('div'); document.body.append(host)
  const app = createApp({ render }); apps.push(app); app.mount(host)
  return host
}

describe('footer fidelity subscriptions', () => {
  it('uses the cheap capability, including its false result, and never orders hidden-footer projections', async () => {
    const { controller, editor } = create()
    const project = vi.spyOn(controller, 'getProjection')
    const fidelity = vi.spyOn(controller, 'getMarkdownLossy')
    const variant = ref<'document' | 'compact'>('compact')
    const host = mount(() => h(EditorSurface, { controller, ariaLabel: 'Lazy footer', variant: variant.value }))
    await nextTick()
    expect(fidelity).not.toHaveBeenCalled()
    expect(project).not.toHaveBeenCalled()
    editor().commands.insertContent('More')
    await nextTick()
    expect(fidelity).not.toHaveBeenCalled()

    variant.value = 'document'; await nextTick()
    expect(host.querySelector('.paper-footer [role="status"]')?.textContent).toBe('Markdown')
    expect(fidelity).toHaveBeenCalledOnce()
    expect(project).not.toHaveBeenCalled()
    controller.setEditable(false); await nextTick(); fidelity.mockClear()
    controller.setValue({ format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Marked', marks: [{ type: 'underline' }] }] }],
    } })
    await nextTick()
    // Read-only, the footer holds only „Celá obrazovka“: no label, so nothing is projected.
    expect(host.querySelector('.paper-footer [role="status"]')).toBeNull()
    expect(fidelity).not.toHaveBeenCalled()
    expect(project).not.toHaveBeenCalled()
    controller.setEditable(true); await nextTick()
    expect(host.querySelector('.paper-footer [role="status"]')?.textContent).toBe('Rich dokument')
    expect(fidelity).toHaveBeenCalledOnce()
    expect(project).not.toHaveBeenCalled()
  })

  it('keeps older structural controllers working through the full-projection fallback', async () => {
    const { controller } = create()
    const project = vi.spyOn(controller, 'getProjection')
    // A structural host controller may omit the new optional capability.
    const legacy = new Proxy(controller, { get(target, property) {
      if (property === 'getMarkdownLossy') return undefined
      const value = Reflect.get(target, property, target)
      return typeof value === 'function' ? value.bind(target) : value
    } })
    const variant = ref<'document' | 'compact'>('compact')
    const host = mount(() => h(EditorSurface, { controller: legacy, ariaLabel: 'Legacy footer', variant: variant.value }))
    await nextTick()
    expect(project).not.toHaveBeenCalled()
    variant.value = 'document'; await nextTick()
    expect(host.querySelector('.paper-footer [role="status"]')?.textContent).toBe('Markdown')
    expect(project).toHaveBeenCalledOnce()
  })
})

describe('plugin toolbar icons', () => {
  it('draws every plugin action, never a letter or an emoji standing in for one', async () => {
    const toolbarPlugin = (id: string, icon: string): MarkdownPlugin => ({ id, apiVersion: 1,
      create: () => ({ actions: { toolbar: [{ id: `${id}.action`, label: id, icon, command: { type: 'toggle-bold' } }] } }) })
    const controller = createMarkdownEditorController({ profile: 'rich',
      plugins: [toolbarPlugin('attachments', 'svg:paperclip'), toolbarPlugin('youtrack', 'svg:ticket')],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
        document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    controllers.push(controller)
    const host = mount(() => h(EditorSurface, { controller, ariaLabel: 'Icons' }))
    await nextTick()
    const iconSlots = [...host.querySelectorAll('.md-plugin-action-icon')]
    expect(iconSlots.length).toBe(2)
    expect(iconSlots.filter(slot => slot.textContent?.trim())).toEqual([])
    expect(iconSlots.map(slot => slot.querySelector('svg')?.getAttribute('data-icon'))).toEqual(['paperclip', 'ticket'])
  })
})
