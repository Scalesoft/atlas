// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { createContactsPlugin } from './index'
import { offerPeople } from './names'

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

describe('the name recognition loads after the first render (P17)', () => {
  it('asks the host for its people at the start and underlines a written name once the recognition is in', async () => {
    const known = vi.fn(async () => [{ name: 'Jana Nováková', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }])
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin({ known })],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Volala Jana Nováková.' }] }] } as never } } })
    controllers.push(controller)
    const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Editor' })
    // Before any timer: the request is out while the editor's first render is on screen, the recognition not yet loaded.
    await Promise.resolve(); await Promise.resolve()
    expect(known).toHaveBeenCalledOnce()
    expect(host.querySelector('.md-recognition-candidate')).toBeNull()
    // The scan runs a while after the load, never in the first render.
    await vi.waitFor(() => expect(host.querySelector('.md-recognition-candidate')?.textContent).toBe('Jana Nováková'), { timeout: 3000 })
    expect(known).toHaveBeenCalledOnce()
  })

  const jana = { name: 'Jana Nováková', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
  function mount(text: string) {
    const known = vi.fn(async () => [jana])
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin({ known })],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } as never } } })
    controllers.push(controller)
    const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Editor' })
    const editor = editorForController(controller)!; editor.view.focus()
    return { host, editor }
  }
  const key = (target: HTMLElement, name: string) => target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }))

  it('gives an open people list its keys before the recognition has loaded (an unresolved chip clicked at once)', () => {
    const { editor } = mount('Volala Jana.')
    const pick = vi.fn(), before = editor.state.doc.toJSON()
    // Synchronously after the first render: the idle load has not run yet.
    expect(offerPeople(editor, { rect: () => new DOMRect(), people: [{ contact: jana }], pick })).toBe(true)
    key(editor.view.dom, 'Enter')
    expect(pick).toHaveBeenCalledWith(jana)
    // Enter went to the list, not to the paragraph.
    expect(editor.state.doc.toJSON()).toEqual(before)
  })

  it('keeps the name index when the plugin view is rebuilt, so a typed name still becomes a chip at once', async () => {
    const { host, editor } = mount('Píše Jana Nováková')
    await vi.waitFor(() => expect(host.querySelector('.md-recognition-candidate')).not.toBeNull(), { timeout: 3000 })
    // A changed plugin set (bubble menus, an editable flip) rebuilds every plugin view; then the idle passes.
    editor.registerPlugin(new Plugin({}))
    await new Promise(resolve => setTimeout(resolve, 50))
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    editor.view.dispatch(editor.state.tr.insertText(' '))
    // The chip is its own root transaction once the keystroke has landed (E0): one microtask, no scan.
    await Promise.resolve()
    const chips: string[] = []; editor.state.doc.descendants(node => { if (node.type.name === 'contactInline') chips.push((node.attrs.data as { contacts: { name: string }[] }).contacts[0]!.name) })
    expect(chips).toEqual(['Jana Nováková'])
  })
})
