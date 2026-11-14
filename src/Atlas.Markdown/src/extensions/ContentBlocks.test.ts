// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type LocalMarkdownEditorController, type MarkdownPlugin } from '../core'

// jsdom has no layout: ProseMirror scrolls to the caret when the editor holds the focus (a closed popover gives it back, P7).
Range.prototype.getClientRects ??= () => [] as unknown as DOMRectList
Range.prototype.getBoundingClientRect ??= () => new DOMRect()
const controllers: LocalMarkdownEditorController<'rich'>[] = []
function mount(type: 'callout') {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'blocks-test', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: {
    format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type, attrs: type === 'callout' ? { variant: 'note' } : { title: 'Podrobnosti' }, content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Upravitelný obsah' }] }] }] },
  } } })
  controllers.push(controller)
  const container = document.createElement('div'); document.body.append(container); controller.attachView(container, { ariaLabel: 'Bloky' })
  return { editor, controller, container, data: () => (controller.getProjection().rich.document as JSONContent).content![0]! }
}
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
// jsdom has no layout: an edit that returns the focus to the editor lets ProseMirror scroll its selection with the range geometry API.
beforeAll(() => {
  Range.prototype.getClientRects ??= () => [new DOMRect()] as unknown as DOMRectList
  Range.prototype.getBoundingClientRect ??= () => new DOMRect()
})

describe('context controls for editable content blocks', () => {
  it('edits the callout type where it is shown: a click on its label opens the menu, a second one closes it (P1)', async () => {
    const { container, editor, data } = mount('callout')
    await Promise.resolve()
    expect(container.querySelector('select, button')).toBeNull()
    const label = container.querySelector<HTMLElement>('.md-callout-label')!
    const menu = () => document.querySelector<HTMLElement>('[role=menu][aria-label="Typ informačního boxu"]')
    label.click()
    const toolbar = document.querySelector<HTMLElement>('[role=toolbar][aria-label="Informační box"]')!
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(menu()?.tagName).toBe('DIV')
    label.click()
    expect(menu()).toBeNull()
    // The toolbar's „Upravit“ leads to the same menu at the label, and a second press on it closes the menu.
    const upravit = toolbar.querySelector<HTMLButtonElement>('[aria-label="Upravit"]')!
    upravit.click()
    expect(label.getAttribute('aria-expanded')).toBe('true')
    upravit.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, cancelable: true })); upravit.click()
    expect(menu()).toBeNull()
    upravit.click()
    const warning = Array.from(menu()!.querySelectorAll('button')).find(button => button.textContent === 'Upozornění')!
    warning.click()
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(toolbar.hidden).toBe(false)
    expect(data().attrs!.variant).toBe('warning')
    expect(label.textContent).toBe('Upozornění')
    expect(document.querySelector('[role=dialog]')).toBeNull()
    expect(data().content![0]!.content![0]!.text).toBe('Upravitelný obsah')
    editor.commands.undo()
    expect(data().attrs!.variant).toBe('note')
  })
  it('lets the user edit callout content without showing its structural toolbar', async () => {
    const { editor, container, data } = mount('callout')
    await Promise.resolve()
    container.querySelector<HTMLElement>('.md-callout-label')!.click()
    editor.commands.setTextSelection(2)
    editor.view.dispatch(editor.state.tr.insertText('Nový '))
    await Promise.resolve()
    expect(data().content![0]!.content![0]!.text).toBe('Nový Upravitelný obsah')
    expect(document.querySelector<HTMLElement>('[role=toolbar][aria-label="Informační box"]')!.hidden).toBe(true)
    expect(container.querySelector<HTMLElement>('.md-callout-label')!.contentEditable).toBe('false')
    expect(container.querySelector('.md-callout p')!.closest('[contenteditable=false]')).toBeNull()
  })
  it('keeps its label out of a reader\'s Tab order (§7)', async () => {
    const { editor, container } = mount('callout')
    const label = container.querySelector<HTMLElement>('.md-callout-label')!
    expect(label.tabIndex).toBe(0)
    editor.setEditable(false)
    await vi.waitFor(() => expect(label.tabIndex).toBe(-1))
    editor.setEditable(true)
    await vi.waitFor(() => expect(label.tabIndex).toBe(0))
  })
  it('removes an empty paragraph after it on Backspace, the caret at the end of its text (J4.3)', () => {
    const { editor, controller } = mount('callout')
    editor.commands.insertContentAt(editor.state.doc.content.size, [{ type: 'paragraph' }, { type: 'paragraph', content: [{ type: 'text', text: 'Dál' }] }])
    const types = () => (controller.getValue().document as JSONContent).content!.map(node => node.type)
    expect(types()).toEqual(['callout', 'paragraph', 'paragraph'])
    editor.commands.setTextSelection(editor.state.doc.child(0).nodeSize + 1)
    editor.view.someProp('handleKeyDown', handle => handle(editor.view, new KeyboardEvent('keydown', { key: 'Backspace' })))
    expect(types()).toEqual(['callout', 'paragraph'])
    expect(editor.state.selection.$from.parent.textContent).toBe('Upravitelný obsah')
    expect(editor.state.selection.$from.parentOffset).toBe('Upravitelný obsah'.length)
  })
  it('opens the type menu from the keyboard: Enter on the label, Enter or F2 on the selected box', async () => {
    const { editor, container } = mount('callout')
    await vi.waitFor(() => expect(editor.isInitialized).toBe(true))
    const menu = () => document.querySelector<HTMLElement>('[role=menu][aria-label="Typ informačního boxu"]')
    const escape = () => document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    editor.commands.setTextSelection(2)
    container.querySelector<HTMLElement>('.md-callout-label')!.focus()
    container.querySelector<HTMLElement>('.md-callout-label')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(menu()).not.toBeNull()
    escape()
    expect(menu()).toBeNull()
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[role=toolbar][aria-label="Informační box"]')!.hidden).toBe(false))
    for (const key of ['Enter', 'F2']) {
      editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
      expect(menu(), key).not.toBeNull()
      escape()
    }
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
  })
  it('cleans up active controls on destruction and blocks readonly structural edits', async () => {
    const { container, controller, data } = mount('callout')
    await Promise.resolve()
    // A click on the label opens the type menu.
    container.querySelector<HTMLElement>('.md-callout-label')!.click()
    const warning = Array.from(document.querySelectorAll<HTMLButtonElement>('[role=menu] button')).find(button => button.textContent === 'Upozornění')!
    controller.setEditable(false)
    warning.click()
    expect(data().attrs!.variant).toBe('note')
    await Promise.resolve()
    expect(document.querySelector('[role=menu]')).toBeNull()
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('[role=toolbar]')!.hidden).toBe(true))
    controller.destroy()
    expect(document.querySelector('[role=toolbar], [role=menu], [role=dialog]')).toBeNull()
  })
  it.each(['callout'] as const)('isolates rapid %s settings changes from each other and surrounding text edits', async type => {
    const { editor, data } = mount(type)
    await Promise.resolve()
    vi.spyOn(Date, 'now').mockReturnValue(1000)
    editor.view.dispatch(editor.state.tr.insertText('Before ', 2))
    for (const index of [0, 1]) {
      document.querySelector<HTMLButtonElement>('[aria-label="Upravit"]')!.click()
      const label = ['Tip', 'Upozornění'][index]
      Array.from(document.querySelectorAll<HTMLButtonElement>('[role=menu] button')).find(button => button.textContent === label)!.click()
    }
    const attrs = () => type === 'callout' ? data().attrs!.variant : data().attrs!.title
    editor.view.dispatch(editor.state.tr.insertText('After ', 2))
    editor.commands.undo()
    expect(attrs()).toBe(type === 'callout' ? 'warning' : 'Second')
    expect(editor.state.doc.textContent).toBe('Before Upravitelný obsah')
    editor.commands.undo()
    expect(attrs()).toBe(type === 'callout' ? 'tip' : 'First')
    expect(editor.state.doc.textContent).toBe('Before Upravitelný obsah')
    editor.commands.undo()
    expect(attrs()).toBe(type === 'callout' ? 'note' : 'Podrobnosti')
    expect(editor.state.doc.textContent).toBe('Before Upravitelný obsah')
    // 'Before ' ends in a space: the word boundary closes its step, so its text goes in one more undo.
    editor.commands.undo()
    editor.commands.undo()
    expect(editor.state.doc.textContent).toBe('Upravitelný obsah')
  })
})
