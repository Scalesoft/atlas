// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { Editor, Extension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { PluginKey } from '@tiptap/pm/state'
import Suggestion from '@tiptap/suggestion'
import { createMarkdownSuggestionRenderer, shouldShowMarkdownSuggestion } from './renderer'
import { activeMarkdownPopover, registerMarkdownPopover } from '../modules/popoverInteraction'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
function setup(source?: { search: (query: string, options: { signal: AbortSignal }) => Promise<string[]> }) {
  const key = new PluginKey('testSuggestion'), command = vi.fn()
  const host = document.createElement('div'); document.body.append(host)
  const extension = Extension.create({ name: 'testSuggestion', addProseMirrorPlugins() {
    return [Suggestion<string, string>({ editor: this.editor, pluginKey: key, char: '@', shouldShow: shouldShowMarkdownSuggestion, items: () => source ? [] : ['Alice', 'Bob'],
      render: createMarkdownSuggestionRenderer({ pluginKey: key, label: 'People', mapItem: value => ({ id: value, title: value }), source: source && { ...source, debounceMs: 0 } }),
      command: ({ editor, range, props }) => { command(props); editor.chain().deleteRange(range).insertContent(props).run() },
    })]
  } })
  const editor = new Editor({ element: host, extensions: [StarterKit, extension], content: '<p></p>' }); editors.push(editor)
  editor.view.focus()
  return { editor, key, command, root: editor.view.dom }
}
async function open(editor: Editor, text = '@a') {
  editor.commands.insertContent(text)
  await vi.waitFor(() => expect(editor.view.dom.getAttribute('aria-expanded')).toBe('true'))
}

it('takes the active row with Tab like Enter (P8), moves up with Shift+Tab, never leaving the editor, and Escape keeps the typed text', async () => {
  const { editor, root, key, command } = setup(); await open(editor)
  expect(document.getElementById(root.getAttribute('aria-controls')!)?.getAttribute('role')).toBe('listbox')
  const active = () => document.getElementById(root.getAttribute('aria-activedescendant')!)?.textContent
  expect(active()).toContain('Alice')
  const press = (init: KeyboardEventInit) => { const event = new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }); root.dispatchEvent(event); return event }
  press({ key: 'ArrowDown' }); expect(active()).toContain('Bob')
  expect(press({ key: 'Tab', shiftKey: true }).defaultPrevented).toBe(true); expect(active()).toContain('Alice')
  expect(command).not.toHaveBeenCalled()
  expect(press({ key: 'Tab' }).defaultPrevented).toBe(true)
  expect(command).toHaveBeenCalledWith('Alice'); expect(editor.getText()).toBe('Alice'); expect(document.activeElement).toBe(root)
  await open(editor, ' @a')
  press({ key: 'Escape' })
  expect(editor.getText()).toBe('Alice @a'); expect(key.getState(editor.state)?.active).toBe(false)
  expect(root.hasAttribute('aria-controls')).toBe(false); expect(document.querySelector('[role=listbox]')).toBeNull()
  await open(editor, ' @b'); expect(document.querySelector('[role=listbox]')).not.toBeNull()
})

it('picks the best item of what is typed now when Enter arrives before the list caught up', async () => {
  const names = ['Alice', 'Bob', 'Bobek']
  const matching = (query: string) => names.filter(name => name.toLowerCase().startsWith(query.toLowerCase()))
  const key = new PluginKey('nowSuggestion'), command = vi.fn()
  const host = document.createElement('div'); document.body.append(host)
  const extension = Extension.create({ name: 'nowSuggestion', addProseMirrorPlugins() {
    return [Suggestion<string, string>({ editor: this.editor, pluginKey: key, char: '@', items: async ({ query }) => matching(query),
      render: createMarkdownSuggestionRenderer({ pluginKey: key, label: 'People', mapItem: value => ({ id: value, title: value }), itemsNow: matching,
        command: (item, { editor, range }) => { command(item); editor.chain().deleteRange(range).insertContent(item).run() } }),
      command: () => { throw new Error('the renderer runs picks with the live range') } })]
  } })
  const editor = new Editor({ element: host, extensions: [StarterKit, extension], content: '<p></p>' }); editors.push(editor)
  editor.view.focus()
  await open(editor, '@b')
  await vi.waitFor(() => expect(document.querySelectorAll('[role=option]')).toHaveLength(2))
  // Typing and Enter in one go: the list still shows the rows of „b“ (or is loading) when Enter comes.
  editor.view.dispatch(editor.state.tr.insertText('obe'))
  const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }); editor.view.dom.dispatchEvent(enter)
  expect(enter.defaultPrevented).toBe(true)
  expect(command).toHaveBeenCalledWith('Bobek')
  expect(editor.getText()).toBe('Bobek')
})

it('Escape consumes only the active suggestion and a following Escape propagates', async () => {
  const { editor, root, key } = setup(); await open(editor)
  const listener = vi.fn(); document.addEventListener('keydown', listener)
  root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(listener).not.toHaveBeenCalled(); expect(key.getState(editor.state)?.active).toBe(false)
  root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(listener).toHaveBeenCalledOnce(); document.removeEventListener('keydown', listener)
})

it('never opens local suggestions in response to a remote Yjs transaction', async () => {
  const { editor, key } = setup()
  editor.view.dispatch(editor.state.tr.insertText('@a').setMeta('y-sync$', { isChangeOrigin: true }))
  await Promise.resolve()
  expect(key.getState(editor.state)?.active).toBe(false)
  expect(document.querySelector('[role=listbox]')).toBeNull()
  editor.commands.insertContent(' @b')
  await vi.waitFor(() => expect(document.querySelector('[role=listbox]')).not.toBeNull())
})

it('shares exclusive ownership with block panels and preserves suggestions in a different editor', async () => {
  const first = setup(), second = setup(); await open(first.editor); await open(second.editor)
  expect(document.querySelectorAll('[role=listbox]')).toHaveLength(2)
  const close = vi.fn(); const release = registerMarkdownPopover(first.editor, { anchor: first.root, key: 'panel', close })
  expect(first.key.getState(first.editor.state)?.active).toBe(false)
  expect(second.key.getState(second.editor.state)?.active).toBe(true)
  expect(activeMarkdownPopover(first.editor)?.key).toBe('panel')
  expect(document.querySelectorAll('[role=listbox]')).toHaveLength(1)
  release()
})

it('cancels pending work on readonly and never mounts a late result after destruction', async () => {
  let resolve: (items: string[]) => void = () => undefined
  let signal: AbortSignal | undefined
  const { editor, root, key } = setup({ search: (_query, options) => { signal = options.signal; return new Promise(done => { resolve = done }) } })
  await open(editor); await vi.waitFor(() => expect(signal).toBeDefined())
  editor.setEditable(false)
  await vi.waitFor(() => expect(document.querySelector('[role=listbox]')).toBeNull())
  expect(signal?.aborted).toBe(true); expect(key.getState(editor.state)?.active).toBe(false)
  resolve(['Late']); await Promise.resolve(); expect(root.hasAttribute('aria-controls')).toBe(false)
  editor.destroy(); expect(document.querySelector('.md-suggestion-popup')).toBeNull()
})

it('inserts in an independent undo operation and outside dismissal leaves focus alone', async () => {
  const { editor, root } = setup(); await open(editor)
  root.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(editor.getText()).toBe('Alice'); editor.commands.undo(); expect(editor.getText()).toBe('@a')
  editor.commands.insertContent(' @b'); await vi.waitFor(() => expect(root.hasAttribute('aria-controls')).toBe(true))
  const outside = document.createElement('button'); document.body.append(outside); outside.focus()
  outside.dispatchEvent(new Event('pointerdown', { bubbles: true }))
  expect(document.activeElement).toBe(outside); expect(document.querySelector('[role=listbox]')).toBeNull()
})
