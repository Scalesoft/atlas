// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Editor, type JSONContent } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import * as Y from 'yjs'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownPluginController } from '../core'
import { assistMarkersFor, createAssistPlugin, type AssistPluginOptions, type MarkdownAssistBlock, type MarkdownAssistSource, type MarkdownAssistSuggestion } from './index'

// jsdom has no layout: ProseMirror measures ranges when it scrolls the selection into view.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
const editors: Editor[] = []
beforeEach(() => { vi.useFakeTimers() })
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  editors.splice(0).forEach(editor => editor.destroy())
  document.body.replaceChildren()
  vi.useRealTimers(); vi.restoreAllMocks()
})

const settle = () => vi.advanceTimersByTimeAsync(0)
const paragraphs = (...texts: string[]): JSONContent => ({ type: 'doc', content: texts.map(text => text ? { type: 'paragraph', content: [{ type: 'text', text }] } : { type: 'paragraph' }) })

function mount(options: Partial<AssistPluginOptions> & { sources: AssistPluginOptions['sources'] }, content: JSONContent = paragraphs('')) {
  let editor!: Editor
  let pluginController!: MarkdownPluginController
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; pluginController = context.controller } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createAssistPlugin(options), capture],
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: content as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Dokument' })
  editor.view.focus()
  return { editor, controller, pluginController, host }
}
const key = (editor: Editor, key: string, init: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init })
  editor.view.dom.dispatchEvent(event)
  return event
}
const markers = () => [...document.querySelectorAll<HTMLButtonElement>('.md-assist-marker:not([data-leaving])')]
const listbox = () => document.querySelector<HTMLElement>('.md-assist-menu [role=listbox]')
const statusText = () => document.querySelector('[data-md-assist-status]')?.textContent ?? ''
/**
 * The block's own list (reasons, Delete, „Nenabízet“) opens at its marker. Where no marker shows yet, Alt+Enter (core's „Co s tím jde
 * dělat“, which lists the same suggestions) asks for the block's suggestions first.
 */
async function openAssistList(editor: Editor) {
  if (!markers().length) { key(editor, 'Enter', { altKey: true }); await settle(); key(editor, 'Escape'); await settle() }
  const marker = markers().at(-1)!
  marker.dispatchEvent(new PointerEvent('pointerdown')); marker.click(); await settle()
}
/** The textblock that contains `text` (position before it). */
const blockPos = (editor: Editor, text: string) => {
  let found = -1
  editor.state.doc.descendants((node, pos) => { if (found < 0 && node.isTextblock && node.textContent.includes(text)) found = pos; return found < 0 })
  return found
}
const caretInto = (editor: Editor, text: string) => editor.commands.setTextSelection(blockPos(editor, text) + 1)

const contactText = 'Kontaktujte Jana Nováka z provozu.'
function contactSource(apply = vi.fn((context: Parameters<MarkdownAssistSuggestion['apply']>[0]) =>
  context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! }))) {
  const suggest = vi.fn((block: MarkdownAssistBlock, _context: { signal: AbortSignal }): MarkdownAssistSuggestion[] => {
    const at = block.text.indexOf('Jana Nováka')
    if (at < 0) return []
    return [
      { id: `contact:${at}`, kind: 'contact.inline', title: 'Nahradit „Jana Nováka“ kontaktem', description: 'Jan Novák · Provoz', icon: 'user',
        range: block.rangeOf(at, at + 'Jana Nováka'.length), reason: 'V textu je celé jméno z adresáře.', apply },
      { id: 'card', kind: 'contact.card', title: 'Vložit kartu kontaktu pod odstavec', icon: 'users', apply: vi.fn(() => false) },
    ]
  })
  return { source: { id: 'contacts', suggest } satisfies MarkdownAssistSource, suggest, apply }
}

describe('without registration', () => {
  it('leaves an editor without the plugin untouched: no overlays, no panels, no keyboard', async () => {
    let editor!: Editor
    const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: paragraphs('') as never } } })
    controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Dokument' })
    editor.view.focus(); editor.commands.insertContent(contactText)
    expect(editor.extensionManager.extensions.map(extension => extension.name)).not.toContain('markdownAssist')
    expect(controller.panels.status).toHaveLength(0)
    const before = controller.getValue()
    // Alt+Enter is core's „Co s tím jde dělat“: without the module it has no AI rows and analyses nothing.
    key(editor, 'Enter', { altKey: true })
    await vi.advanceTimersByTimeAsync(5000)
    expect(document.querySelector('[data-testid=intentions] [role=option]')).toBeNull()
    key(editor, 'Escape')
    expect(document.querySelector('[class*="md-assist"]')).toBeNull()
    expect([...document.querySelectorAll('style')].some(style => style.textContent?.includes('md-assist'))).toBe(false)
    expect(controller.getValue()).toEqual(before)
  })
})

describe('markers', () => {
  it('analyses a finished block once and shows one marker at the current block', async () => {
    const { source, suggest } = contactSource()
    const { editor, host } = mount({ sources: [source] })
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1499)
    expect(suggest).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(suggest.mock.calls[0]![0]).toMatchObject({ trigger: 'idle', text: contactText, pos: 0 })
    await settle()
    expect(markers()).toHaveLength(1)
    expect(markers()[0]!.getAttribute('aria-label')).toBe('Návrh: Nahradit „Jana Nováka“ kontaktem')
    expect(markers()[0]!.tabIndex).toBe(-1)
    expect(markers()[0]!.type).toBe('button')
    // The marker lives outside the document; focus never moves when it appears.
    expect(host.querySelector('.md-assist-marker')).toBeNull()
    expect(document.activeElement).toBe(editor.view.dom)
    // Enter finishes the block: its unchanged text is not analysed again and the marker stays with it.
    key(editor, 'Enter')
    await vi.advanceTimersByTimeAsync(3000)
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(markers()).toHaveLength(1)
  })

  it('analyses on leaving a short block, removes the marker on edits and offers it again after idle', async () => {
    const { source, suggest } = contactSource()
    const { editor } = mount({ sources: [source] }, paragraphs('Volejte Jana Nováka', 'Další'))
    const analysedJana = () => suggest.mock.calls.filter(([block]) => block.text.includes('Jana'))
    caretInto(editor, 'Volejte'); editor.commands.insertContent('!')
    caretInto(editor, 'Další')
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(suggest.mock.calls[0]![0]).toMatchObject({ trigger: 'leave', text: '!Volejte Jana Nováka' })
    await settle()
    // The finished block keeps its marker while the caret stays in the block entered right after it.
    expect(markers()).toHaveLength(1)
    // Back in the block (leaving „Další“ analyses that one too): still one marker.
    caretInto(editor, 'Volejte'); await settle()
    expect(markers()).toHaveLength(1)
    editor.commands.insertContent(' dnes')
    await settle()
    expect(markers()).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(1500)
    expect(analysedJana()).toHaveLength(2)
    expect(analysedJana()[1]![0]).toMatchObject({ trigger: 'idle' })
    await settle()
    expect(markers()).toHaveLength(1)
  })

  it('analyses nothing while the caret only passes through existing text', async () => {
    const { source, suggest } = contactSource()
    const { editor } = mount({ sources: [source] }, paragraphs(contactText, 'Další', 'Třetí'))
    caretInto(editor, 'Další'); caretInto(editor, 'Třetí'); caretInto(editor, 'Kontaktujte'); caretInto(editor, 'Další')
    await vi.advanceTimersByTimeAsync(5000)
    expect(suggest).not.toHaveBeenCalled()
    expect(markers()).toHaveLength(0)
  })

  it('never analyses during composition, in readonly or after Shift+paste', async () => {
    const { source, suggest } = contactSource()
    const { editor, controller } = mount({ sources: [source] })
    editor.view.dom.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }))
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(5000)
    expect(suggest).not.toHaveBeenCalled()
    editor.view.dom.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }))
    await vi.advanceTimersByTimeAsync(5000)
    expect(suggest).toHaveBeenCalledTimes(1)
    suggest.mockClear()

    // Shift+paste stays literal and silent; an ordinary paste is analysed at once.
    key(editor, 'Enter')
    key(editor, 'Shift', { shiftKey: true })
    editor.view.pasteText('Volejte Jana Nováka', new Event('paste') as ClipboardEvent)
    editor.view.dom.dispatchEvent(new KeyboardEvent('keyup', { key: 'Shift', bubbles: true }))
    await settle()
    expect(suggest).not.toHaveBeenCalled()
    editor.view.pasteText(' a Jana Nováka', new Event('paste') as ClipboardEvent)
    await settle()
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(suggest.mock.calls[0]![0]).toMatchObject({ trigger: 'paste', pasted: expect.objectContaining({ from: expect.any(Number) }) })
    suggest.mockClear()

    controller.setEditable(false)
    await settle()
    expect(markers()).toHaveLength(0)
    editor.commands.setTextSelection(1)
    await vi.advanceTimersByTimeAsync(5000)
    expect(suggest).not.toHaveBeenCalled()
  })

  it('aborts a pending suggest when the block changes and ignores the late result', async () => {
    let release!: (value: MarkdownAssistSuggestion[]) => void
    const signals: AbortSignal[] = []
    const suggest = vi.fn((block: MarkdownAssistBlock, context: { signal: AbortSignal }) => {
      signals.push(context.signal)
      return new Promise<MarkdownAssistSuggestion[]>(resolve => { release = resolve })
    })
    const { editor } = mount({ sources: [{ id: 'slow', suggest }] })
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1500)
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(signals[0]!.aborted).toBe(false)
    editor.commands.insertContent(' Díky.')
    expect(signals[0]!.aborted).toBe(true)
    release([{ id: 'late', kind: 'late', title: 'Pozdní', icon: 'user', apply: () => true }])
    await settle()
    expect(markers()).toHaveLength(0)
  })

  it('keeps warning markers when the cursor leaves and shows the warning bar under the block', async () => {
    const example = vi.fn()
    const suggest = vi.fn((block: MarkdownAssistBlock): MarkdownAssistSuggestion[] => {
      const at = block.text.indexOf('Srv#2026!rack')
      return at < 0 ? [] : [{ id: 'pw', kind: 'secret.plaintext', title: 'Možné heslo v textu', description: 'Detekce běží jen ve vašem prohlížeči.',
        icon: 'triangle-alert', tone: 'warning', applyLabel: 'Přesunout do trezoru', applyIcon: 'lock', range: block.rangeOf(at, at + 13),
        apply: () => true, secondary: [{ id: 'example', label: 'Je to jen příklad', run: example }] }]
    })
    const { editor, host } = mount({ sources: [{ id: 'secrets', suggest }] }, paragraphs('Nouzový účet: admin, heslo Srv#2026!rack', 'Další'))
    caretInto(editor, 'Nouzový'); editor.commands.insertContent('Pozor! ')
    caretInto(editor, 'Další')
    await settle()
    expect(markers()).toHaveLength(1)
    expect(markers()[0]!.dataset.tone).toBe('warning')
    const bar = host.querySelector<HTMLElement>('.md-assist-warning')!
    expect(bar.getAttribute('role')).toBe('status')
    expect(bar.getAttribute('contenteditable')).toBe('false')
    expect(bar.textContent).toContain('Možné heslo v textu')
    expect(host.querySelector('.md-assist-tone-warning')?.textContent).toBe('Srv#2026!rack')
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await vi.advanceTimersByTimeAsync(3000)
    expect(markers()).toHaveLength(1)
    const button = [...bar.querySelectorAll('button')].find(item => item.textContent === 'Je to jen příklad')!
    button.click(); await settle()
    expect(example).toHaveBeenCalledTimes(1)
    // The decorations never reach the document.
    expect(JSON.stringify(editor.getJSON())).not.toContain('md-assist')
  })
})

describe('list and apply', () => {
  async function openList(sourceApply?: ReturnType<typeof contactSource>['apply']) {
    const fixture = contactSource(sourceApply)
    const mounted = mount({ sources: [fixture.source] })
    mounted.editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1500)
    await settle()
    await openAssistList(mounted.editor)
    return { ...mounted, ...fixture }
  }

  it('the marker lists suggestions, Enter applies as one undo step, Tab and Escape apply nothing', async () => {
    const { editor, controller, apply } = await openList()
    expect(listbox()).not.toBeNull()
    expect(listbox()!.getAttribute('aria-label')).toBe('Návrhy pro tento odstavec')
    expect(document.querySelector('.md-assist-menu [role=status]')?.textContent).toBe('2 návrhy pro tento odstavec.')
    expect([...listbox()!.querySelectorAll('[role=option]')].map(row => row.textContent)).toEqual([
      'Nahradit „Jana Nováka“ kontaktemJan Novák · Provoz', 'Vložit kartu kontaktu pod odstavec'])
    expect(document.activeElement).toBe(editor.view.dom)
    expect(document.querySelector('.md-assist-menu')?.textContent).toContain('Proč tento návrh?')
    expect(document.querySelector('.md-assist-menu')?.textContent).toContain('Nenabízet v této stránce')

    const tab = key(editor, 'Tab')
    expect(tab.defaultPrevented).toBe(false)
    await settle()
    expect(listbox()).toBeNull()
    expect(apply).not.toHaveBeenCalled()

    await openAssistList(editor)
    const escape = key(editor, 'Escape')
    expect(escape.defaultPrevented).toBe(true)
    await settle()
    expect(listbox()).toBeNull()
    expect(apply).not.toHaveBeenCalled()
    expect(editor.getText()).toBe(contactText)
    expect(markers()).toHaveLength(1)

    await openAssistList(editor)
    key(editor, 'Enter'); await settle()
    expect(apply).toHaveBeenCalledTimes(1)
    expect(editor.getText()).toBe('Kontaktujte Jan Novák z provozu.')
    expect(listbox()).toBeNull()
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).toBe(contactText)
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).toBe('')
  })

  it('re-validates the span on apply and reports a stale proposal in the status panel', async () => {
    const { editor, apply } = await openList()
    expect(listbox()).not.toBeNull()
    // A collaborator rewrites the span while the list is open.
    const from = blockPos(editor, 'Jana') + 1 + contactText.indexOf('Jana')
    editor.view.dispatch(editor.state.tr.insertText('Petra Malá', from, from + 'Jana Nováka'.length).setMeta('y-sync$', { isChangeOrigin: true }))
    await settle()
    key(editor, 'Enter'); await settle()
    expect(apply).not.toHaveBeenCalled()
    expect(statusText()).toBe('Návrh už neodpovídá textu.')
    expect(document.querySelector('[data-md-assist-status]')?.getAttribute('role')).toBe('status')
    expect(editor.getText()).toBe('Kontaktujte Petra Malá z provozu.')
  })

  it('retires an applied suggestion and its alternatives for the same words, also when the text stays', async () => {
    // Two actions on one span (link the words, or replace them) and one suggestion elsewhere in the block. Linking
    // keeps the block's text, so the block's entry survives the write; the applied row and its alternative go anyway.
    const text = 'Nastavte VPN a pak monitoring.'
    const vpn = text.indexOf('VPN')
    const watch = text.indexOf('monitoring')
    const link = vi.fn((context: Parameters<MarkdownAssistSuggestion['apply']>[0]) => {
      context.editor.commands.setTextSelection(context.range!)
      return context.dispatch({ type: 'set-link', href: 'https://wiki.example.com/vpn' })
    })
    const replace = vi.fn(() => false)
    const other = vi.fn((context: Parameters<MarkdownAssistSuggestion['apply']>[0]) => {
      context.editor.commands.setTextSelection(context.range!)
      return context.dispatch({ type: 'set-link', href: 'https://wiki.example.com/monitoring' })
    })
    const source: MarkdownAssistSource = {
      id: 'links',
      suggest: block => block.text === text ? [
        { id: 'link', kind: 'link', title: 'Propojit „VPN“', icon: 'link', range: block.rangeOf(vpn, vpn + 3), apply: link },
        { id: 'replace', kind: 'link', title: 'Nahradit názvem stránky', icon: 'file-text', range: block.rangeOf(vpn, vpn + 3), apply: replace },
        { id: 'other', kind: 'link', title: 'Propojit „monitoring“', icon: 'link', range: block.rangeOf(watch, watch + 10), apply: other },
      ] : [],
    }
    const onApplied = vi.fn()
    const { editor, controller } = mount({ sources: [source], onApplied })
    editor.commands.insertContent(text)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    await openAssistList(editor)
    key(editor, 'Enter'); await settle()
    expect(link).toHaveBeenCalledTimes(1)
    expect(onApplied).toHaveBeenCalledWith(expect.objectContaining({ id: 'link' }))
    expect(editor.getText()).toBe(text)
    // The marker stays for the suggestion elsewhere in the block, and its list no longer offers the applied span.
    expect(markers()).toHaveLength(1)
    expect(markers()[0]!.getAttribute('aria-label')).toBe('Návrh: Propojit „monitoring“')
    await openAssistList(editor)
    expect([...listbox()!.querySelectorAll('[role=option]')].map(row => row.textContent)).toEqual(['Propojit „monitoring“'])
    key(editor, 'Enter'); await settle()
    expect(other).toHaveBeenCalledTimes(1)
    expect(replace).not.toHaveBeenCalled()
    expect(markers()).toHaveLength(0)
    // Each write was its own undo step, and undoing never brings a retired suggestion back by itself.
    controller.dispatch({ type: 'undo' })
    expect(editor.isActive('link', { href: 'https://wiki.example.com/monitoring' })).toBe(false)
    expect(markers()).toHaveLength(0)
  })

  it('filters its list by typing and never types into the page while it is open (J4.9)', async () => {
    const text = 'VPN a monitoring'
    const vpn = vi.fn(() => false), watch = vi.fn(() => false)
    const source: MarkdownAssistSource = { id: 'links', suggest: block => block.text === text ? [
      { id: 'vpn', kind: 'link', title: 'Propojit „VPN“', icon: 'link', range: block.rangeOf(0, 3), apply: vpn },
      { id: 'watch', kind: 'link', title: 'Propojit „monitoring“', icon: 'link', range: block.rangeOf(6, 16), apply: watch },
    ] : [] }
    const { editor } = mount({ sources: [source] })
    editor.commands.insertContent(text)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    await openAssistList(editor)
    const rows = () => [...listbox()!.querySelectorAll('[role=option]')].map(row => row.textContent)
    for (const letter of 'moni') expect(key(editor, letter).defaultPrevented).toBe(true)
    expect(rows()).toEqual(['Propojit „monitoring“'])
    expect(editor.getText()).toBe(text)
    key(editor, 'Backspace'); key(editor, 'Backspace'); key(editor, 'Backspace'); key(editor, 'Backspace')
    expect(rows()).toHaveLength(2)
    expect(editor.getText()).toBe(text)
    for (const letter of 'moni') key(editor, letter)
    key(editor, 'Enter'); await settle()
    // The filtered row is the suggestion it shows, not the first of the unfiltered list.
    expect(watch).toHaveBeenCalledTimes(1)
    expect(vpn).not.toHaveBeenCalled()
  })

  it('does not offer a dismissed suggestion again in the session', async () => {
    const onDismissed = vi.fn()
    const fixture = contactSource()
    const { editor } = mount({ sources: [fixture.source], onDismissed })
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    await openAssistList(editor)
    const remove = key(editor, 'Delete')
    expect(remove.defaultPrevented).toBe(true)
    await settle()
    expect(onDismissed).toHaveBeenCalledWith(expect.objectContaining({ id: 'contact:12' }), 'suggestion')
    expect(editor.getText()).toBe(contactText)
    expect([...listbox()!.querySelectorAll('[role=option]')]).toHaveLength(1)
    // „Nenabízet v této stránce“ suppresses the kind of the active row.
    const suppress = [...document.querySelectorAll<HTMLButtonElement>('.md-assist-menu button')].find(button => button.textContent === 'Nenabízet v této stránce')!
    suppress.click(); await settle()
    expect(onDismissed).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'contact.card' }), 'kind')
    expect(markers()).toHaveLength(0)
    // The same text analysed again offers neither the dismissed id nor the suppressed kind.
    editor.commands.insertContent('x'); editor.commands.deleteRange({ from: editor.state.selection.from - 1, to: editor.state.selection.from })
    await vi.advanceTimersByTimeAsync(1500); await settle()
    expect(fixture.suggest).toHaveBeenCalledTimes(2)
    expect(markers()).toHaveLength(0)
  })
})

describe('marker and block', () => {
  it('links each marker to its textblock: data-md-assist-pos follows the block, assistMarkersFor finds it', async () => {
    const { source } = contactSource()
    const list = { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Úvod' }] }, { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph' }] }] }] }
    const { editor } = mount({ sources: [source] }, list)
    const listPos = editor.state.doc.child(0).nodeSize
    editor.commands.setTextSelection(listPos + 3)
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    expect(markers()).toHaveLength(1)
    const marker = markers()[0]!
    const paragraph = blockPos(editor, 'Kontaktujte')
    expect(paragraph).toBe(listPos + 2)
    expect(marker.getAttribute('data-md-assist-pos')).toBe(String(paragraph))
    // The textblock itself, or any block that contains it (the list the block grip addresses).
    expect(assistMarkersFor(editor, paragraph)).toEqual([marker])
    expect(assistMarkersFor(editor, listPos)).toEqual([marker])
    expect(assistMarkersFor(editor, 0)).toEqual([])
    expect(assistMarkersFor(editor, -5)).toEqual([])
    // A collaborator's edit above moves the block; the link moves with it.
    editor.view.dispatch(editor.state.tr.insertText('Velký ', 1).setMeta('y-sync$', { isChangeOrigin: true }))
    await settle()
    expect(markers()[0]).toBe(marker)
    expect(marker.getAttribute('data-md-assist-pos')).toBe(String(paragraph + 6))
    expect(assistMarkersFor(editor, listPos + 6)).toEqual([marker])
    expect(assistMarkersFor(editor, listPos)).toEqual([])
  })

  it('answers no markers for an editor without the plugin', () => {
    const editor = new Editor({ element: document.body.appendChild(document.createElement('div')), extensions: [StarterKit] })
    editors.push(editor)
    expect(assistMarkersFor(editor, 0)).toEqual([])
  })

  it('keeps the marker inside the editor and reads its offset from --md-assist-marker-offset', async () => {
    const { source } = contactSource()
    const { editor, host } = mount({ sources: [source] })
    const rect = (left: number, right: number) => () => new DOMRect(left, 40, right - left, 24)
    host.classList.add('md-editor')
    host.getBoundingClientRect = rect(100, 900)
    editor.commands.insertContent(contactText)
    const block = () => editor.view.nodeDOM(0) as HTMLElement
    block().getBoundingClientRect = rect(300, 880)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    const marker = markers()[0]!
    const left = () => Number.parseFloat(marker.style.left)
    // jsdom has no layout: the marker measures 0 px, so its left edge is the block's left minus the offset.
    expect(left()).toBe(300 - 32)
    // Geometry is re-read on layout events (autoUpdate), never in a transaction whose block stayed put (J12.5).
    editor.view.dom.style.setProperty('--md-assist-marker-offset', '72px')
    editor.commands.setTextSelection(3); await settle()
    expect(left()).toBe(300 - 32)
    window.dispatchEvent(new Event('resize')); await settle()
    expect(left()).toBe(300 - 72)
    // Never left of the editor's own bounds (a host sidebar sits there), nor over the block's grip: right of the block instead.
    block().getBoundingClientRect = rect(130, 880)
    window.dispatchEvent(new Event('resize')); await settle()
    expect(left()).toBe(880 + 4)
    // …and still inside the editor when the block reaches its right edge.
    block().getBoundingClientRect = rect(130, 900)
    window.dispatchEvent(new Event('resize')); await settle()
    expect(left()).toBe(900 - 4)
  })
})

describe('avatar', () => {
  async function rows(avatar: MarkdownAssistSuggestion['avatar']) {
    const source: MarkdownAssistSource = { id: 'people', suggest: block => block.text === contactText ? [
      { id: 'contact', kind: 'contact.inline', title: 'Nahradit kontaktem', icon: 'user', avatar, apply: () => false }] : [] }
    const { editor } = mount({ sources: [source] })
    editor.commands.insertContent(contactText)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    await openAssistList(editor)
    const icon = listbox()!.querySelector<HTMLElement>('[role=option] .ic')!
    // The marker keeps the component's icon; only the row shows the person.
    expect(markers().at(-1)!.querySelector('.md-assist-marker-icon svg')).not.toBeNull()
    return icon
  }

  it('shows initials instead of the icon, trimmed to three characters', async () => {
    const icon = await rows({ initials: '  JNovák ' })
    expect(icon.classList.contains('md-assist-avatar')).toBe(true)
    expect(icon.textContent).toBe('JNo')
    expect(icon.querySelector('svg')).toBeNull()
    expect(icon.getAttribute('aria-hidden')).toBe('true')
  })

  it('shows a safe image and falls back to the initials when it fails', async () => {
    const icon = await rows({ initials: 'JN', src: 'https://example.com/avatars/jn.png' })
    const image = icon.querySelector('img')!
    expect(image.getAttribute('src')).toBe('https://example.com/avatars/jn.png')
    expect(image.alt).toBe('')
    expect(icon.classList.contains('md-assist-avatar')).toBe(true)
    image.dispatchEvent(new Event('error'))
    expect(icon.querySelector('img')).toBeNull()
    expect(icon.textContent).toBe('JN')
  })

  it('ignores an unsafe image URL and keeps the icon without initials', async () => {
    const icon = await rows({ src: 'javascript:alert(1)' })
    expect(icon.querySelector('img')).toBeNull()
    expect(icon.classList.contains('md-assist-avatar')).toBe(false)
    expect(icon.querySelector('svg')).not.toBeNull()
  })

  it('ignores an image URL with credentials and shows the initials', async () => {
    const icon = await rows({ initials: 'PK', src: 'https://user:secret@example.com/a.png' })
    expect(icon.querySelector('img')).toBeNull()
    expect(icon.textContent).toBe('PK')
  })
})

describe('caret after apply', () => {
  /** The author finished the contact paragraph (Enter) and types the next one; the marker stays at the finished block. */
  async function finishedBlock(suggestion: Partial<MarkdownAssistSuggestion> = {}, apply?: MarkdownAssistSuggestion['apply']) {
    const write = apply ?? ((context: Parameters<MarkdownAssistSuggestion['apply']>[0]) => context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! }))
    const source: MarkdownAssistSource = { id: 'contacts', suggest: block => {
      const at = block.text.indexOf('Jana Nováka')
      return at < 0 ? [] : [{ id: 'contact', kind: 'contact.inline', title: 'Nahradit kontaktem', icon: 'user', range: block.rangeOf(at, at + 11), apply: write, ...suggestion }]
    } }
    const mounted = mount({ sources: [source] })
    mounted.editor.commands.insertContent(contactText)
    key(mounted.editor, 'Enter')
    mounted.editor.commands.insertContent('Další')
    await settle()
    expect(markers()).toHaveLength(1)
    const caret = mounted.editor.state.selection.from
    const transactions: { selection: boolean; doc: boolean; history: unknown }[] = []
    mounted.editor.on('transaction', ({ transaction }) => transactions.push({ selection: transaction.selectionSet, doc: transaction.docChanged, history: transaction.getMeta('addToHistory') }))
    return { ...mounted, caret, transactions }
  }
  const apply = async (editor: Editor) => { await openAssistList(editor); key(editor, 'Enter'); await settle() }

  it('puts the caret back where the author types when it was outside the block, without a history entry', async () => {
    const { editor, controller, caret, transactions } = await finishedBlock()
    await apply(editor)
    expect(editor.getText()).toContain('Kontaktujte Jan Novák z provozu.')
    // „Jan Novák“ is two characters shorter than „Jana Nováka“: the caret is mapped through the write.
    expect(editor.state.selection.from).toBe(caret - 2)
    expect(editor.state.selection.empty).toBe(true)
    const restore = transactions.filter(item => item.selection && !item.doc).at(-1)!
    expect(restore.history).toBe(false)
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).toContain(contactText)
  })

  it('keeps the author bookmark when a mark command prepares the suggestion selection before dispatch', async () => {
    const { editor, controller, caret } = await finishedBlock({}, context => {
      context.editor.commands.setTextSelection(context.range!)
      return context.dispatch({type:'set-link',href:'https://example.com/person'})
    })
    await apply(editor)
    expect(editor.state.selection.from).toBe(caret)
    editor.commands.insertContent(' dál')
    expect(editor.state.doc.child(1).textContent).toBe('Další dál')
    controller.dispatch({type:'undo'}); expect(editor.state.doc.child(1).textContent).toBe('Další')
    controller.dispatch({type:'undo'}); expect(editor.state.doc.firstChild!.child(0).marks).toEqual([])
  })

  it('leaves the caret to the write with caret: "follow"', async () => {
    const { editor, caret } = await finishedBlock({ caret: 'follow' })
    await apply(editor)
    expect(editor.state.selection.from).toBeLessThan(caret - 2)
    expect(editor.state.selection.$from.parent.textContent).toBe('Kontaktujte Jan Novák z provozu.')
  })

  it('is harmless when the host restores the caret itself', async () => {
    let restored = 0
    const { editor, caret, transactions } = await finishedBlock({}, context => {
      const before = context.editor.state.selection.from
      const size = context.editor.state.doc.content.size
      const written = context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! })
      context.editor.commands.setTextSelection(before + context.editor.state.doc.content.size - size); restored++
      return written
    })
    const count = transactions.length
    await apply(editor)
    expect(restored).toBe(1)
    expect(editor.state.selection.from).toBe(caret - 2)
    // After the host's own restore, the module dispatches no further selection change.
    const after = transactions.slice(count)
    const hostRestore = after.map(item => item.selection && !item.doc).lastIndexOf(true)
    expect(after.slice(hostRestore + 1).some(item => item.selection)).toBe(false)
  })

  it('returns the caret to where the author kept typing while an asynchronous apply was running', async () => {
    let release!: () => void
    const { editor } = await finishedBlock({}, async context => {
      await new Promise<void>(resolve => { release = resolve })
      return context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! })
    })
    await apply(editor)
    // A lookup is still running; the author types on in the next paragraph and moves the caret with the keyboard.
    editor.commands.insertContent(' a dál')
    editor.commands.setTextSelection(editor.state.selection.from - 3)
    const caret = editor.state.selection.from
    release(); await settle()
    expect(editor.getText()).toContain('Kontaktujte Jan Novák z provozu.')
    expect(editor.state.selection.from).toBe(caret - 2)
  })

  it('keeps a caret the author moved into the block while the apply was running', async () => {
    let release!: () => void
    const { editor } = await finishedBlock({}, async context => {
      await new Promise<void>(resolve => { release = resolve })
      return context.dispatch({ type: 'paste-markdown', text: 'Jan Novák' }, { replaceRange: context.range! })
    })
    await apply(editor)
    // The author clicks at the start of the finished paragraph while a picker is open.
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 2)).setMeta('pointer', true))
    release(); await settle()
    expect(editor.getText()).toContain('Kontaktujte Jan Novák z provozu.')
    expect(editor.state.selection.$from.parent.textContent).toBe('Kontaktujte Jan Novák z provozu.')
  })
})

it('shows a source notice at the marker and in Alt+Enter without offering a document write', async () => {
  const source: MarkdownAssistSource = { id: 'diagnostic', suggest: block => [{
    id: 'unverified-issue', kind: 'youtrack.unverified', title: 'Úkol PX2-1234 se nepodařilo ověřit',
    description: 'Zkontrolujte klíč, nastavení integrace nebo oprávnění.', icon: 'ticket',
    informational: true, range: block.rangeOf(13, 21),
  }] }
  const { editor, controller } = mount({ sources: [source] }, paragraphs('viz youtrack PX2-1234'))
  const before = controller.getValue()
  await openAssistList(editor)
  expect(markers()).toHaveLength(1)
  expect(markers()[0]!.getAttribute('aria-label')).toContain('nepodařilo ověřit')
  expect(listbox()?.textContent).toContain('Zkontrolujte klíč, nastavení integrace nebo oprávnění.')
  key(editor, 'Enter'); await settle()
  expect(listbox()).toBeNull()
  expect(controller.getValue()).toEqual(before)
})
describe('collaboration', () => {
  it('creates no transaction and no Yjs update while suggesting', async () => {
    const document_ = new Y.Doc()
    const { source, suggest } = contactSource()
    const instance = createAssistPlugin({ sources: [source] }).create({ profile: 'rich', signal: new AbortController().signal })
    const host = document.createElement('div'); document.body.append(host)
    const editor = new Editor({ element: host, extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document: document_ }), ...instance.extensions!] })
    editors.push(editor)
    editor.view.focus()
    editor.commands.insertContent(contactText)
    await settle()
    const updates = vi.fn(); document_.on('update', updates)
    const transactions = vi.fn(); editor.on('transaction', transactions)
    await vi.advanceTimersByTimeAsync(1500); await settle()
    expect(suggest).toHaveBeenCalledTimes(1)
    expect(markers()).toHaveLength(1)
    expect(transactions).not.toHaveBeenCalled()
    expect(updates).not.toHaveBeenCalled()
    document_.destroy()
  })
})
it('selection submenu is host-owned, validates availability, and delegates to one shared menu', async () => {
  const run = vi.fn()
  let available = true
  const { editor, controller } = mount({ sources: [], selectionMenu: { label: 'AI', icon: 'sparkles', available: () => available,
    actions: [{ id: 'shorten', label: 'Zkrátit', run }] } }, paragraphs('Selected text'))
  editor.commands.setTextSelection({ from: 1, to: 9 })
  const action = controller.actions.selection.find(action => action.id === 'assist.selection')!
  expect(action.hideWhenDisabled).toBe(true)
  expect(controller.query(action.command).enabled).toBe(true)
  expect(controller.dispatch(action.command)).toBe(true)
  await settle()
  const menu = document.querySelector('[role=menu][aria-label="AI"]')!
  expect(menu).not.toBeNull()
  menu.querySelector<HTMLButtonElement>('button')!.click()
  expect(run).toHaveBeenCalledOnce()
  expect(run.mock.calls[0]![0].range).toEqual({ from: 1, to: 9 })
  expect(document.querySelector('[role=menu][aria-label="AI"]')).toBeNull()
  available = false
  expect(controller.query(action.command).enabled).toBe(false)
  expect(controller.dispatch(action.command)).toBe(false)
})
it('offers the host empty action only while editable and applicable, outside document content', async () => {
  const run = vi.fn()
  const { editor, controller } = mount({ sources: [], emptyAction: { label: 'Začít s osnovou', available: editor => !editor.state.doc.textContent.trim(), run } })
  await settle()
  const button = document.querySelector<HTMLButtonElement>('[data-md-assist-empty]')!
  expect(button.textContent).toBe('Začít s osnovou')
  expect(controller.getValue().document.content).toEqual([{ type: 'paragraph' }])
  button.click(); expect(run).toHaveBeenCalledOnce()
  editor.commands.insertContent('Some text'); await settle()
  expect(document.querySelector('[data-md-assist-empty]')).toBeNull()
  editor.commands.clearContent(); await settle(); controller.setEditable(false); await settle()
  expect(document.querySelector('[data-md-assist-empty]')).toBeNull()
})
it('reports displayed suggestions once per session, never again when the list reopens', async () => {
  const onShown = vi.fn()
  const source = contactSource()
  const { editor } = mount({ sources: [source.source], onShown }, paragraphs(contactText))
  await openAssistList(editor)
  expect(onShown.mock.calls.map(call => call[0].id)).toEqual(expect.arrayContaining(['contact:12', 'card']))
  const count = onShown.mock.calls.length
  key(editor, 'Escape'); await settle(); await openAssistList(editor)
  expect(onShown).toHaveBeenCalledTimes(count)
})
it('shows fast rules while another source is pending, and never revives them from a late response', async () => {
  let resolve!: (value: readonly MarkdownAssistSuggestion[]) => void
  const fast = contactSource(vi.fn(context => { context.editor.commands.setTextSelection(context.range!); return context.dispatch({ type: 'set-link', href: 'https://example.com' }) }))
  const slow: MarkdownAssistSource = { id: 'slow', suggest: () => new Promise(done => { resolve = done }) }
  const { editor } = mount({ sources: [fast.source, slow] }, paragraphs(contactText))
  await openAssistList(editor)
  expect(markers()).toHaveLength(1)
  expect(listbox()!.textContent).toContain('kontaktem')
  key(editor, 'Enter'); await settle()
  expect(editor.getText()).toBe(contactText)
  resolve([{ id: 'late', kind: 'contact.inline', title: 'Late duplicate', icon: 'user', range: { from: 13, to: 24 }, apply: () => false }]); await settle()
  expect(markers()).toHaveLength(1)
  expect(markers()[0]!.getAttribute('aria-label')).toContain('kartu kontaktu')
})

it('mounts the host icon on an empty action and releases it on removal', async () => {
  const cleanup = vi.fn(), renderIcon = vi.fn((container: HTMLElement) => { container.textContent = 'host icon'; return cleanup })
  const { editor } = mount({ sources: [], emptyAction: { label: 'Outline', renderIcon, available: editor => !editor.state.doc.textContent.trim(), run: vi.fn() } })
  await settle()
  expect(document.querySelector('[data-md-assist-empty]')!.textContent).toContain('host icon')
  expect(renderIcon).toHaveBeenCalledOnce()
  editor.commands.insertContent('Written'); await settle()
  expect(cleanup).toHaveBeenCalledOnce()
})
