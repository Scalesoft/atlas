// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { createMarkdownEditorController, type LocalMarkdownEditorController } from '../core'
import { editorForController } from '../core/internal'
import EditorSurface from './EditorSurface.vue'

const apps: App[] = []
const controllers: LocalMarkdownEditorController<'rich'>[] = []
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

function mount() {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: {
    format: 'atlas-rich', version: 1,
    document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Original' }] }] },
  } } })
  controllers.push(controller)
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render: () => h(EditorSurface, { controller, ariaLabel: 'Input errors' }) })
  apps.push(app); app.mount(host)
  return { controller, host }
}

describe('input rejection status', () => {
  it('shows a real rejected paste only in its editor and preserves editable content', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
    vi.stubGlobal('ClipboardEvent', class extends Event {})
    const first = mount(), second = mount()
    await nextTick()
    const value = first.controller.getValue()
    const revision = first.controller.state.contentRevision
    const row = `<tr>${'<td><p>cell</p></td>'.repeat(30)}</tr>`
    editorForController(first.controller)!.view.pasteHTML(`<table>${row.repeat(34)}</table>`)
    await nextTick()
    expect(first.host.querySelector('[role="alert"]')?.textContent).toContain('Obsah nelze vložit')
    expect(second.host.querySelector('[role="alert"]')).toBeNull()
    expect(first.controller.getValue()).toEqual(value)
    expect(first.controller.state.contentRevision).toBe(revision)
    expect(first.controller.state.lifecycle).toBe('ready')
    expect(first.controller.state.editable).toBe(true)
    editorForController(first.controller)!.commands.insertContent('Still editing ')
    expect(first.controller.getProjection().text).toContain('Still editing')
  })
})
