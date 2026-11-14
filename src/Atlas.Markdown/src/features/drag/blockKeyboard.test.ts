// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { enterMarkdownBlockKeyboardControls, markdownBlockKeyboardControls, registerMarkdownBlockKeyboardHook } from './blockKeyboard'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function setup() {
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<p>První</p><p>Druhý</p>' }); editors.push(editor)
  const grip = document.createElement('div'); grip.tabIndex = 0; document.body.append(grip)
  const button = (label: string) => { const element = document.createElement('button'); element.tabIndex = -1; element.textContent = label; document.body.append(element); return element }
  const block = { pos: 0, node: editor.state.doc.firstChild! }
  return { editor, grip, button, block }
}
const press = (target: HTMLElement, key: string, init: KeyboardEventInit = {}) => { const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }); target.dispatchEvent(event); return event }

describe('block keyboard hooks', () => {
  it('registers per editor, rejects a duplicate id and a bad hook, and unregisters', () => {
    const { editor, button, block } = setup(), control = button('Návrh')
    const release = registerMarkdownBlockKeyboardHook(editor, { id: 'assist', controls: () => [control] })
    expect(() => registerMarkdownBlockKeyboardHook(editor, { id: 'assist', controls: () => [] })).toThrow(/Duplicate/)
    expect(() => registerMarkdownBlockKeyboardHook(editor, { id: '', controls: () => [] })).toThrow(TypeError)
    expect(markdownBlockKeyboardControls(editor, block)).toEqual([control])
    release(); release()
    expect(markdownBlockKeyboardControls(editor, block)).toEqual([])
    registerMarkdownBlockKeyboardHook(editor, { id: 'assist', controls: () => [control] })()
  })
  it('asks the hook for the block each time and skips failing hooks, detached and disabled controls', () => {
    const { editor, button, block } = setup(), ok = button('Ok'), detached = button('Odpojeno'), disabled = button('Vypnuto'); detached.remove(); disabled.disabled = true
    const controls = vi.fn((_block: unknown) => [ok, detached, disabled, ok])
    registerMarkdownBlockKeyboardHook(editor, { id: 'broken', controls: () => { throw new Error('boom') } })
    registerMarkdownBlockKeyboardHook(editor, { id: 'one', controls })
    expect(markdownBlockKeyboardControls(editor, block)).toEqual([ok])
    expect(controls).toHaveBeenCalledWith(block)
  })
  it('does nothing without controls so Tab keeps its default', () => {
    const { editor, grip, block } = setup()
    expect(enterMarkdownBlockKeyboardControls(editor, block, { grip })).toBe(false)
    registerMarkdownBlockKeyboardHook(editor, { id: 'empty', controls: () => null })
    expect(enterMarkdownBlockKeyboardControls(editor, block, { grip })).toBe(false)
  })
  it('walks the controls with Tab, continues through `after` and walks back with Shift+Tab to the grip', () => {
    const { editor, grip, button, block } = setup(), first = button('Jedna'), second = button('Dva'), after = vi.fn(() => true)
    registerMarkdownBlockKeyboardHook(editor, { id: 'a', controls: () => [first] })
    registerMarkdownBlockKeyboardHook(editor, { id: 'b', controls: () => [second] })
    grip.focus()
    expect(enterMarkdownBlockKeyboardControls(editor, block, { grip, after })).toBe(true)
    expect(document.activeElement).toBe(first)
    expect(press(first, 'Tab').defaultPrevented).toBe(true); expect(document.activeElement).toBe(second)
    expect(press(second, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true); expect(document.activeElement).toBe(first)
    press(first, 'Tab'); expect(after).not.toHaveBeenCalled()
    expect(press(second, 'Tab').defaultPrevented).toBe(true); expect(after).toHaveBeenCalledOnce()
    // The path ended: Tab on a control is no longer intercepted.
    expect(press(second, 'Tab').defaultPrevented).toBe(false)
    enterMarkdownBlockKeyboardControls(editor, block, { grip })
    expect(press(first, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true); expect(document.activeElement).toBe(grip)
  })
  it('lets the browser continue when `after` does not take focus, and leaves other keys and modifiers alone', () => {
    const { editor, grip, button, block } = setup(), only = button('Jedna')
    registerMarkdownBlockKeyboardHook(editor, { id: 'a', controls: () => [only] })
    enterMarkdownBlockKeyboardControls(editor, block, { grip, after: () => false })
    expect(press(only, 'ArrowDown').defaultPrevented).toBe(false)
    expect(press(only, 'Tab', { ctrlKey: true }).defaultPrevented).toBe(false)
    expect(press(only, 'Tab').defaultPrevented).toBe(false)
  })
  it('returns focus to the editor on Escape, consumes it and ends the path', () => {
    const { editor, grip, button, block } = setup(), only = button('Jedna'), outer = vi.fn()
    document.addEventListener('keydown', outer)
    registerMarkdownBlockKeyboardHook(editor, { id: 'a', controls: () => [only] })
    enterMarkdownBlockKeyboardControls(editor, block, { grip })
    const escape = press(only, 'Escape')
    expect(escape.defaultPrevented).toBe(true); expect(outer).not.toHaveBeenCalled()
    expect(editor.view.dom.contains(document.activeElement)).toBe(true)
    expect(press(only, 'Escape').defaultPrevented).toBe(false); document.removeEventListener('keydown', outer)
  })
  it('goes on with the redrawn control that takes the focus back after a redraw removed the focused one', () => {
    const { editor, grip, button, block } = setup(), outer = vi.fn()
    // A redraw replaces a plugin's controls (its widgets) with new elements.
    const draw = () => [button('Jedna'), button('Dva')]
    let controls = draw()
    registerMarkdownBlockKeyboardHook(editor, { id: 'a', controls: () => controls.filter(control => control.isConnected) })
    enterMarkdownBlockKeyboardControls(editor, block, { grip })
    expect(document.activeElement).toBe(controls[0])
    const old = controls[0]!
    old.dispatchEvent(new FocusEvent('focusout', { relatedTarget: null })); controls.forEach(control => control.remove())
    controls = draw(); controls[0]!.focus()
    expect(press(controls[0]!, 'Tab').defaultPrevented).toBe(true); expect(document.activeElement).toBe(controls[1])
    document.addEventListener('keydown', outer)
    expect(press(controls[1]!, 'Escape').defaultPrevented).toBe(true); expect(outer).not.toHaveBeenCalled()
    document.removeEventListener('keydown', outer)
    expect(press(old, 'Tab').defaultPrevented).toBe(false)
  })
  it('ends the path when focus leaves the controls and respects a key the plugin already handled', () => {
    const { editor, grip, button, block } = setup(), first = button('Jedna'), second = button('Dva'), elsewhere = button('Jinde')
    registerMarkdownBlockKeyboardHook(editor, { id: 'a', controls: () => [first, second] })
    first.addEventListener('keydown', event => event.preventDefault(), { once: true })
    enterMarkdownBlockKeyboardControls(editor, block, { grip })
    press(first, 'Tab'); expect(document.activeElement).toBe(first)
    elsewhere.focus()
    expect(press(first, 'Tab').defaultPrevented).toBe(false)
  })
})
