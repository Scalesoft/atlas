// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import MarkdownEditor from './MarkdownEditor.vue'
import { getMarkdownContentCapability, type LocalMarkdownEditorController, type MarkdownRichValue } from '../core'
import { editorForController } from '../core/internal'

const apps: App[] = []
const rich = (text: string): MarkdownRichValue => ({ format: 'atlas-rich', version: 1,
  document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } })
beforeEach(() => vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} }))
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals() })
function mount(render: () => ReturnType<typeof h>) {
  const host = document.createElement('div'); document.body.append(host)
  const app = createApp({ render }); apps.push(app); app.mount(host)
  return host
}

describe('explicit Vue revision mode', () => {
  it('emits lightweight events without value reads or full JSON exports during typing', async () => {
    let controller!: LocalMarkdownEditorController<'rich'>
    const changed = vi.fn(), update = vi.fn(), oldChange = vi.fn()
    mount(() => h(MarkdownEditor, { profile: 'rich', mode: 'revision', initialValue: rich('Text'), ariaLabel: 'Revision editor',
      onCreated: value => { controller = value as LocalMarkdownEditorController<'rich'> }, onContentChange: changed,
      'onUpdate:modelValue': update, onChange: oldChange }))
    await nextTick()
    const editor = editorForController(controller)!, read = vi.spyOn(controller, 'getValue')
    const json = vi.spyOn(Object.getPrototypeOf(editor.state.doc), 'toJSON')
    editor.view.dispatch(editor.state.tr.insertText('A', 1)); await nextTick()
    expect(changed).toHaveBeenCalledOnce()
    expect(changed.mock.calls[0]![0]).toMatchObject({ revision: 1, origin: 'local' })
    expect(update).not.toHaveBeenCalled(); expect(oldChange).not.toHaveBeenCalled()
    expect(read).not.toHaveBeenCalled(); expect(json).not.toHaveBeenCalled()
    expect(getMarkdownContentCapability(controller)!.getSnapshot().value.document).toMatchObject({ content: [{ content: [{ text: 'AText' }] }] })
  })

  it('treats initialValue as an initial value and replaces only through the explicit controller API', async () => {
    let controller!: LocalMarkdownEditorController<'rich'>
    const initial = ref(rich('Initial')), changes = vi.fn()
    mount(() => h(MarkdownEditor, { profile: 'rich', mode: 'revision', initialValue: initial.value, ariaLabel: 'Explicit replacement',
      onCreated: value => { controller = value as LocalMarkdownEditorController<'rich'> }, onContentChange: changes }))
    await nextTick()
    initial.value = rich('Ignored new initial'); await nextTick()
    expect(controller.getValue().document).toMatchObject({ content: [{ content: [{ text: 'Initial' }] }] })
    expect(changes).not.toHaveBeenCalled()
    controller.setValue(rich('Explicit')); await nextTick()
    expect(controller.getValue().document).toMatchObject({ content: [{ content: [{ text: 'Explicit' }] }] })
    expect(changes).toHaveBeenCalledOnce()
  })

  it('retains deep mutable external v-model edits in the default mode', async () => {
    let controller!: LocalMarkdownEditorController<'rich'>
    const value = ref(rich('Before')), update = vi.fn((next: string | MarkdownRichValue) => { value.value = next as MarkdownRichValue })
    mount(() => h(MarkdownEditor, { profile: 'rich', modelValue: value.value, ariaLabel: 'Compatible model',
      'onUpdate:modelValue': update, onCreated: next => { controller = next as LocalMarkdownEditorController<'rich'> } }))
    await nextTick()
    const editor = editorForController(controller)!
    editor.view.dispatch(editor.state.tr.insertText('A', 1)); await nextTick()
    expect(update).toHaveBeenCalledOnce()
    const text = (value.value.document.content as Array<{ content: Array<{ text: string }> }>)[0]!.content[0]!
    text.text = 'Nested host edit'; await nextTick()
    expect(controller.getValue().document).toMatchObject({ content: [{ content: [{ text: 'Nested host edit' }] }] })
    expect(update).toHaveBeenCalledOnce()
  })

  it('shows a persistent accessible content failure in a local editor', async () => {
    let controller!: LocalMarkdownEditorController<'rich'>
    const host = mount(() => h(MarkdownEditor, { profile: 'rich', mode: 'revision', initialValue: rich('Text'), ariaLabel: 'Failed local editor',
      onCreated: next => { controller = next as LocalMarkdownEditorController<'rich'> } }))
    await nextTick()
    const editor = editorForController(controller)!
    Object.assign(editor.state.doc.firstChild!.attrs, { textAlign: 'invalid' })
    editor.view.dispatch(editor.state.tr.setMeta('validate', true)); await nextTick()
    expect(host.querySelector('[data-testid="editor-content-error"]')?.getAttribute('role')).toBe('alert')
    expect(host.querySelector('[data-testid="editor-content-error"]')?.textContent).toContain('Úpravy jsou pozastavené')
    expect(controller.state.editable).toBe(false)
  })
})
