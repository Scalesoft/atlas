// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { NodeSelection } from '@tiptap/pm/state'
import { createMarkdownContextToolbar, openMarkdownContextPopover } from './contextToolbar'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
function setup() {
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr><p>Text</p>' }); editors.push(editor)
  const anchor = host.querySelector('hr')!
  const toolbar = createMarkdownContextToolbar({ editor, anchor, getPos: () => 0, label: 'Komponenta' })
  return { editor, anchor, toolbar }
}

describe('component contextual controls', () => {
  it('reads the editor theme only when the toolbar shows, never while it is created hidden (perf review 2026-11-03)', () => {
    const host = document.createElement('div'); document.body.append(host)
    const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr><p>Text</p>' }); editors.push(editor)
    editor.view.dom.style.setProperty('--accent', 'rgb(1, 2, 3)')
    const computed = vi.spyOn(window, 'getComputedStyle')
    const toolbar = createMarkdownContextToolbar({ editor, anchor: host.querySelector('hr')!, getPos: () => 0, label: 'Komponenta' })
    expect(computed).not.toHaveBeenCalled()
    expect(toolbar.element.hidden).toBe(true)
    editor.commands.setNodeSelection(0); editor.view.focus(); toolbar.refresh()
    expect(toolbar.element.hidden).toBe(false)
    expect(toolbar.element.style.getPropertyValue('--accent')).toBe('rgb(1, 2, 3)')
  })
  it('runs the component action on a click it did not handle itself and on Enter/F2 while selected', () => {
    const host = document.createElement('div'); document.body.append(host)
    const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr><p>Text</p>' }); editors.push(editor)
    const anchor = host.querySelector('hr')!, onActivate = vi.fn()
    createMarkdownContextToolbar({ editor, anchor, getPos: () => 0, label: 'Komponenta', onActivate })
    const handled = (event: Event) => event.preventDefault()
    document.addEventListener('click', handled, { capture: true, once: true })
    anchor.click()
    expect(onActivate).not.toHaveBeenCalled(); expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    anchor.click(); expect(onActivate).toHaveBeenCalledOnce()
    const press = (key: string, extra: KeyboardEventInit = {}) => { const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...extra }); editor.view.dom.dispatchEvent(event); return event }
    const before = editor.getJSON()
    expect(press('Enter').defaultPrevented).toBe(true); expect(press('F2').defaultPrevented).toBe(true)
    expect(onActivate).toHaveBeenCalledTimes(3); expect(editor.getJSON()).toEqual(before)
    press('Enter', { shiftKey: true }); press('Enter', { isComposing: true }); expect(onActivate).toHaveBeenCalledTimes(3)
    editor.commands.setTextSelection(3); press('F2'); expect(onActivate).toHaveBeenCalledTimes(3)
    editor.commands.setNodeSelection(0); editor.setEditable(false); press('Enter'); anchor.click()
    expect(onActivate).toHaveBeenCalledTimes(3)
  })
  it('enters a selected component toolbar with Tab, skips disabled actions and returns with Escape', () => {
    const { editor, toolbar } = setup()
    const first = toolbar.addButton({ label: 'First', onClick() {} })
    toolbar.addButton({ label: 'Disabled', disabled: true, onClick() {} })
    const last = toolbar.addButton({ label: 'Last', onClick() {} })
    editor.commands.setNodeSelection(0); editor.view.focus()
    const before = editor.getJSON()
    const press = (target: HTMLElement, key: string, extra: KeyboardEventInit = {}) => {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...extra })
      target.dispatchEvent(event); return event
    }
    expect(press(editor.view.dom, 'Tab', { isComposing: true }).defaultPrevented).toBe(false)
    expect(press(editor.view.dom, 'Tab').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(first)
    press(first, 'ArrowRight'); expect(document.activeElement).toBe(last)
    press(last, 'Home'); expect(document.activeElement).toBe(first)
    press(first, 'End'); expect(document.activeElement).toBe(last)
    press(last, 'Escape'); expect(document.activeElement).toBe(editor.view.dom)
    expect(editor.getJSON()).toEqual(before)
    expect(editor.commands.undo()).toBe(false)
    editor.setEditable(false)
    expect(press(editor.view.dom, 'Tab').defaultPrevented).toBe(false)
    toolbar.destroy()
  })
  it('undoes from a toolbar button and from a panel button with Ctrl+Z, redoes with Ctrl+Y; a panel field keeps its own (P6)', () => {
    const { editor, toolbar } = setup()
    const append = () => editor.commands.insertContentAt(editor.state.doc.content.size, '<p>Více</p>')
    const button = toolbar.addButton({ label: 'Přidat', onClick: append })
    editor.commands.setNodeSelection(0); editor.view.focus()
    const press = (target: HTMLElement, key: string, extra: KeyboardEventInit) => { const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...extra }); target.dispatchEvent(event); return event }
    button.focus(); button.click()
    expect(editor.getText()).toContain('Více')
    expect(press(button, 'z', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(editor.getText()).not.toContain('Více')
    press(button, 'y', { ctrlKey: true })
    expect(editor.getText()).toContain('Více')
    let field!: HTMLInputElement, choice!: HTMLButtonElement
    openMarkdownContextPopover({ editor, anchor: button, title: 'Panel', render(panel) {
      field = document.createElement('input'); choice = document.createElement('button'); choice.onclick = append; panel.append(field, choice)
    } })
    expect(press(field, 'z', { ctrlKey: true }).defaultPrevented).toBe(false)
    choice.click()
    expect(editor.getText().match(/Více/g)).toHaveLength(2)
    expect(press(choice, 'z', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(editor.getText().match(/Více/g)).toHaveLength(1)
  })
  it('allows an explicitly readonly reader panel while retaining the editable default', () => {
    const { editor } = setup(); editor.setEditable(false)
    const trigger = document.createElement('button'); document.body.append(trigger)
    const render = vi.fn()
    openMarkdownContextPopover({ editor, anchor: trigger, title: 'Editing', render })
    expect(render).not.toHaveBeenCalled()
    const close = openMarkdownContextPopover({ editor, anchor: trigger, title: 'Search', readonly: true, render })
    expect(render).toHaveBeenCalledOnce()
    expect(document.querySelector('[aria-label="Search"]')).not.toBeNull()
    close()
  })
  it('opens a headerless accessible menu, navigates enabled items and returns focus on Escape', () => {
    const { editor, anchor, toolbar } = setup(); anchor.click()
    const trigger = toolbar.addButton({ label: 'Možnosti', onClick(button) {
      openMarkdownContextPopover({ editor, anchor: button, title: 'Možnosti komponenty', variant: 'menu', render(container) {
        for (const label of ['První', 'Nedostupné', 'Poslední']) {
          const item = document.createElement('button'); item.textContent = label; item.setAttribute('role', 'menuitem')
          item.disabled = label === 'Nedostupné'; container.append(item)
        }
      } })
    } })
    trigger.focus(); trigger.click()
    const menu = document.querySelector<HTMLElement>('[role=menu][aria-label="Možnosti komponenty"]')!
    expect(menu.querySelector('.md-context-popover-heading, .md-context-dismiss')).toBeNull()
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    const items = menu.querySelectorAll<HTMLButtonElement>('[role=menuitem]')
    expect(document.activeElement).toBe(items[0])
    items[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(items[2]); expect(items[0]!.tabIndex).toBe(-1)
    items[2]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(items[0])
    items[0]!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(menu.isConnected).toBe(false); expect(document.activeElement).toBe(trigger)
    expect(trigger.getAttribute('aria-expanded')).toBe('false')
  })

  it('closes a menu on Tab and outside interaction without changing the document', () => {
    const { editor, anchor } = setup(); anchor.click()
    const trigger = document.createElement('button'); document.body.append(trigger)
    const before = editor.getJSON()
    const open = () => { trigger.focus(); openMarkdownContextPopover({ editor, anchor: trigger, title: 'Volby', variant: 'menu', render(container) {
      const item = document.createElement('button'); item.setAttribute('role', 'menuitem'); container.append(item)
    } }) }
    open()
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }))
    expect(document.querySelector('[role=menu]')).toBeNull(); expect(document.activeElement).toBe(trigger)
    open()
    editor.view.dom.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    expect(document.querySelector('[role=menu]')).toBeNull(); expect(editor.getJSON()).toEqual(before)
  })

  it('shows for the selected node only, retains selection on toolbar interaction and hides on outside focus', async () => {
    const { editor, anchor, toolbar } = setup()
    const action = vi.fn()
    const button = toolbar.addButton({ label: 'Nastavení', icon: 'settings', onClick: action })
    expect(button.querySelector('svg')!.getAttribute('data-icon')).toBe('settings-2')
    expect(toolbar.addButton({ label: 'Odstranit', icon: 'trash', onClick: action }).querySelector('svg')!.getAttribute('data-icon')).toBe('trash-2')
    expect(toolbar.addButton({ label: 'Odkaz', icon: 'link', onClick: action }).querySelector('svg')!.getAttribute('stroke-width')).toBe('1.75')
    expect(toolbar.element.hidden).toBe(true)
    anchor.click()
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(false))
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    button.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true })); button.click()
    expect(action).toHaveBeenCalledWith(button)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(editor.getHTML()).not.toContain('Nastavení')
    const elsewhere = document.createElement('input'); document.body.append(elsewhere); elsewhere.focus()
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(true))
    editor.commands.setTextSelection(2); editor.view.focus()
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(true))
    editor.commands.setNodeSelection(0)
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(false))
    editor.setEditable(false)
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(true))
    button.click(); expect(action).toHaveBeenCalledTimes(1)
  })

  it('keeps node selection while a nonmodal panel has focus, closes on Escape, and releases all portals on destroy', async () => {
    const { editor, anchor, toolbar } = setup()
    const release = vi.fn()
    const button = toolbar.addButton({ label: 'Nastavení', onClick(button) {
      openMarkdownContextPopover({ editor, anchor: button, title: 'Nastavení komponenty', render(container) {
        const field = document.createElement('input'); container.append(field); field.focus(); return release
      } })
    } })
    anchor.click(); button.click()
    await vi.waitFor(() => expect(toolbar.element.hidden).toBe(false))
    const popup = document.querySelector<HTMLElement>('[role=dialog]')!
    expect(popup.querySelector('.md-context-popover-heading, .md-context-dismiss')).toBeNull()
    expect(popup.getAttribute('aria-label')).toBe('Nastavení komponenty')
    expect(button.getAttribute('aria-haspopup')).toBe('dialog')
    expect(button.getAttribute('aria-expanded')).toBe('true')
    expect(popup.getAttribute('aria-modal')).toBeNull()
    expect(popup.getAttribute('data-md-overlay-for')).toBe(toolbar.element.getAttribute('data-md-overlay-for'))
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    popup.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(popup.isConnected).toBe(false); expect(release).toHaveBeenCalledTimes(1)
    expect(document.activeElement).toBe(editor.view.dom)
    button.click(); editor.destroy()
    expect(document.querySelector('[data-md-overlay-for]')).toBeNull(); expect(release).toHaveBeenCalledTimes(2)
  })

  it('lets Escape through to a field that handles it itself (data-md-own-escape within the panel)', () => {
    const { editor, anchor } = setup(); anchor.click()
    const own = vi.fn((event: KeyboardEvent) => event.preventDefault())
    openMarkdownContextPopover({ editor, anchor, title: 'Kontakt', render(container) {
      const group = document.createElement('label'); group.dataset.mdOwnEscape = ''
      const field = document.createElement('input'); field.addEventListener('keydown', own); group.append(field); container.append(group, document.createElement('textarea')); field.focus()
    } })
    const popup = document.querySelector<HTMLElement>('[role=dialog]')!
    const escape = (target: Element) => target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    escape(popup.querySelector('input')!)
    expect(own).toHaveBeenCalledOnce(); expect(popup.isConnected).toBe(true)
    // A mark outside the panel does not count.
    document.body.dataset.mdOwnEscape = ''; escape(popup.querySelector('textarea')!); delete document.body.dataset.mdOwnEscape
    expect(popup.isConnected).toBe(false)
  })

  it('closes on outside pointer without moving focus back, and cleans an aborted panel once', () => {
    const { editor, anchor } = setup(); anchor.click()
    const abort = new AbortController(); const release = vi.fn()
    const close = openMarkdownContextPopover({ editor, anchor, title: 'Options', signal: abort.signal, render(container) {
      const field = document.createElement('input'); container.append(field); field.focus(); return release
    } })
    const outside = document.createElement('button'); document.body.append(outside); outside.focus()
    outside.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    expect(document.querySelector('[role=dialog]')).toBeNull(); expect(document.activeElement).toBe(outside)
    abort.abort(); close(); expect(release).toHaveBeenCalledOnce()
  })

  it('gives the keyboard back to the opener after a mouse click outside that went nowhere and selected nothing, never after a touch', async () => {
    const { editor } = setup()
    const opener = document.createElement('button'); document.body.append(opener)
    const page = document.createElement('p'); page.textContent = 'Prostý text stránky'; document.body.append(page)
    const clickOutside = async (pointerType: string, select = false) => {
      opener.focus()
      openMarkdownContextPopover({ editor, anchor: opener, title: 'Závisí na', render(container) { const field = document.createElement('input'); field.id = 'dependency'; field.name = 'dependency'; container.append(field); field.focus() } })
      expect(document.activeElement?.tagName).toBe('INPUT')
      page.dispatchEvent(Object.assign(new Event('pointerdown', { bubbles: true }), { pointerType }))
      expect(document.querySelector('[role=dialog]')).toBeNull()
      ;(document.activeElement as HTMLElement | null)?.blur()
      if (select) document.getSelection()!.selectAllChildren(page); else document.getSelection()!.removeAllRanges()
      page.dispatchEvent(new Event('pointerup', { bubbles: true }))
      await new Promise(resolve => setTimeout(resolve, 10))
      return document.activeElement
    }
    expect(await clickOutside('mouse')).toBe(opener)
    expect(await clickOutside('mouse', true)).toBe(document.body)
    expect(await clickOutside('touch')).toBe(document.body)
  })

  it('forgets the release it waited for when the press outside is cancelled', async () => {
    const { editor } = setup()
    const opener = document.createElement('button'); document.body.append(opener); opener.focus()
    openMarkdownContextPopover({ editor, anchor: opener, title: 'Závisí na', render(container) { const field = document.createElement('input'); container.append(field); field.focus() } })
    document.body.dispatchEvent(Object.assign(new Event('pointerdown', { bubbles: true }), { pointerType: 'mouse' }))
    ;(document.activeElement as HTMLElement | null)?.blur(); document.getSelection()!.removeAllRanges()
    document.body.dispatchEvent(new Event('pointercancel', { bubbles: true }))
    document.body.dispatchEvent(new Event('pointerup', { bubbles: true }))
    await new Promise(resolve => setTimeout(resolve, 10))
    expect(document.activeElement).toBe(document.body)
  })

  it('a popover at a chip in the text: a second click on the chip closes it, a press elsewhere in the text or the focus back in the editor too', async () => {
    const { editor } = setup()
    const chip = { getBoundingClientRect: () => new DOMRect(10, 10, 40, 16) }
    const open = () => openMarkdownContextPopover({ editor, anchor: editor.view.dom, positionAnchor: chip, title: 'Stav', render(container) { const field = document.createElement('input'); container.append(field); field.focus() } })
    const press = (clientX: number) => editor.view.dom.querySelector('p')!.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX, clientY: 15 }))
    const shown = () => document.querySelector('[role=dialog]:not([data-leaving])')
    open(); expect(shown()).not.toBeNull()
    // The chip's press keeps it, its click (the chip opens it again) closes it.
    press(20); expect(shown()).not.toBeNull()
    open(); expect(shown()).toBeNull()
    open(); press(200); expect(shown()).toBeNull()
    open(); editor.view.dom.focus(); await vi.waitFor(() => expect(shown()).toBeNull())
  })

  it('lets a closed popover leave as a copy without the id, name or label target of its fields', async () => {
    const { editor } = setup()
    const opener = document.createElement('button'); document.body.append(opener)
    const style = document.createElement('style'); style.textContent = '[data-leaving] { animation-name: md-popover-leave; animation-duration: 1s }'; document.head.append(style)
    const close = openMarkdownContextPopover({ editor, anchor: opener, title: 'Závisí na', render(container) {
      container.innerHTML = '<label for="dependency">Závisí na</label><input id="dependency" name="dependency">'
    } })
    const panel = document.querySelector<HTMLElement>('[role=dialog]')!
    await vi.waitFor(() => expect(panel.dataset.positioned).toBe('true'))
    close()
    try {
      expect(document.querySelector('[data-leaving] input')).not.toBeNull()
      expect(document.querySelectorAll('[data-leaving] [id], [data-leaving] [name], [data-leaving] [for], [data-leaving][id]')).toHaveLength(0)
    } finally { style.remove(); document.querySelector('[data-leaving]')?.remove() }
  })

  it('leaves a field in progress before a close without a click (it saves as on a blur), but not on Escape', async () => {
    const { editor, anchor } = setup(); anchor.click()
    const blurred = vi.fn()
    const open = () => openMarkdownContextPopover({ editor, anchor, title: 'Kontakt', render(container) {
      const field = document.createElement('input'); field.addEventListener('blur', () => blurred(field.isConnected)); container.append(field); field.focus()
    } })
    open()
    editor.setEditable(false); await Promise.resolve(); await Promise.resolve()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    expect(blurred).toHaveBeenCalledExactlyOnceWith(true)
    editor.setEditable(true); anchor.click(); blurred.mockClear()
    open()
    document.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(document.querySelector('[role=dialog]')).toBeNull()
    expect(blurred).not.toHaveBeenCalled()
  })

  // UX PR-9 (projector review): the panel sits at the end of <body>, so Tab past its last control left it for <body>.
  it('keeps Tab and Shift+Tab inside a panel, wrapping between its first and last control', () => {
    const { editor, anchor } = setup(); anchor.click()
    let first!: HTMLInputElement, last!: HTMLButtonElement
    openMarkdownContextPopover({ editor, anchor, title: 'Nastavení', render(container) {
      first = document.createElement('input'); const hidden = document.createElement('button'); hidden.hidden = true
      last = document.createElement('button'); last.textContent = 'Použít'; container.append(first, last, hidden)
    } })
    const tab = (target: HTMLElement, shiftKey = false) => { const event = new KeyboardEvent('keydown', { key: 'Tab', shiftKey, bubbles: true, cancelable: true }); target.dispatchEvent(event); return event }
    expect(document.activeElement).toBe(first)
    last.focus()
    expect(tab(last).defaultPrevented).toBe(true); expect(document.activeElement).toBe(first)
    expect(tab(first, true).defaultPrevented).toBe(true); expect(document.activeElement).toBe(last)
    // Between the ends Tab stays the browser's.
    expect(tab(first).defaultPrevented).toBe(false)
  })

  it('toggles the same trigger closed and discards its draft without rendering a replacement', () => {
    const { editor, anchor, toolbar } = setup(); anchor.click()
    const release = vi.fn(), render = vi.fn((container: HTMLElement) => { container.append(document.createElement('input')); return release })
    const trigger = toolbar.addButton({ label: 'Nastavení', onClick(button) {
      openMarkdownContextPopover({ editor, anchor: button, title: 'Nastavení', render })
    } })
    const before = editor.getJSON()
    trigger.click()
    document.querySelector('input')!.value = 'Uncommitted draft'
    trigger.click()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    expect(trigger.getAttribute('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(trigger)
    expect(release).toHaveBeenCalledOnce(); expect(render).toHaveBeenCalledOnce()
    expect(editor.getJSON()).toEqual(before)
    trigger.click()
    expect(document.querySelector('input')!.value).toBe('')
  })

  it('keeps an inspector alive across the browser microtask between input blur and button focus', async () => {
    const { anchor, editor, toolbar } = setup(); anchor.click()
    const apply = vi.fn()
    const trigger = toolbar.addButton({ label: 'Upravit', onClick(button) {
      openMarkdownContextPopover({ editor, anchor: button, title: 'Upravit komponentu', render(container, close) {
        const input = document.createElement('input')
        const save = document.createElement('button'); save.textContent = 'Použít'
        save.onclick = () => { apply(input.value); close() }
        container.append(input, save)
      } })
    } })
    trigger.click()
    const panel = document.querySelector<HTMLElement>('[role=dialog]')!
    const input = panel.querySelector('input')!, save = panel.querySelector('.md-context-popover-body button') as HTMLButtonElement
    input.value = 'New value'; input.blur()
    expect(document.activeElement).toBe(document.body)
    // Browsers run microtasks before focusin during the same native focus transfer.
    await Promise.resolve(); await Promise.resolve()
    expect(panel.isConnected).toBe(true); expect(toolbar.element.hidden).toBe(false)
    save.focus(); save.click()
    expect(apply).toHaveBeenCalledWith('New value'); expect(panel.isConnected).toBe(false)
  })

  it('focuses custom forms, replaces previous panels, and closes when the selected node changes', async () => {
    const { editor, anchor, toolbar } = setup(); anchor.click()
    const release = vi.fn()
    const render = (container: HTMLElement) => { container.append(document.createElement('input')); return release }
    openMarkdownContextPopover({ editor, anchor, title: 'First', render })
    openMarkdownContextPopover({ editor, anchor, title: 'Second', render })
    expect(document.querySelectorAll('[role=dialog]')).toHaveLength(1)
    expect(document.querySelector('[role=dialog]')?.getAttribute('aria-label')).toBe('Second')
    expect(document.activeElement).toBe(document.querySelector('[role=dialog] input'))
    expect(release).toHaveBeenCalledTimes(1)
    editor.commands.setTextSelection(2)
    await vi.waitFor(() => expect(document.querySelector('[role=dialog]')).toBeNull())
    expect(release).toHaveBeenCalledTimes(2)
    editor.commands.setNodeSelection(0); editor.view.focus(); toolbar.refresh()
    openMarkdownContextPopover({ editor, anchor: toolbar.element, title: 'Detached', render })
    toolbar.destroy()
    await vi.waitFor(() => expect(document.querySelector('[role=dialog]')).toBeNull())
    expect(release).toHaveBeenCalledTimes(3)
  })

  it('keeps a panel attached to its node when unrelated edits shift its position, and closes on removal', async () => {
    const { editor, anchor } = setup(); anchor.click()
    openMarkdownContextPopover({ editor, anchor, title: 'Mapped', render(container) { container.append(document.createElement('input')) } })
    editor.view.dispatch(editor.state.tr.insert(0, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Earlier'))))
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(document.querySelector('[role=dialog]')).not.toBeNull()
    editor.commands.deleteSelection()
    await vi.waitFor(() => expect(document.querySelector('[role=dialog]')).toBeNull())
  })
})
