import { Extension, posToDOMRect, type Editor } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { NodeSelection, Plugin, Selection, type EditorState } from '@tiptap/pm/state'
import { createSuggestionList, type MarkdownSuggestionItem } from '../suggestions/list'
import { createSuggestionPopup } from '../suggestions/popup'
import { foldSearchText } from '../suggestions/match'
import { chipToTextShortcut, convertMarkdownChipToText, markdownChipText } from '../chips/chipText'
import { formatMarkdownShortcut } from '../../core/keyboardShortcuts'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { blockActions, turnInto } from './turnInto'
import { intentionsStrings } from '../../ui/strings/intentions'

/*
 * „Co s tím jde dělat“ (intentions, IntelliJ style): Ctrl+Space, Alt+Enter, Ctrl+., Shift+F10, the menu key or the right button opens one menu of what can be done with what is under
 * the caret or the selection. Plugins contribute rows for the context (`MarkdownPluginInstance.intentions`, or
 * `registerMarkdownIntentions` from an extension); the menu is the editor's suggestion list at the caret, the keyboard stays in the
 * text: arrows, Enter, Esc, typing filters. A provider that answers later adds its rows when it answers. Core contributes
 * „Převést na text“ for a chip and, for a selected element, the buttons of its context toolbar (contextToolbar.ts: „Upravit“, its
 * conversions, its delete) and „Kopírovat“ / „Smazat“, so every block and chip has its actions here without code of its own;
 * everything else (contacts, issues, links, emoji, requirements, AI) comes from its own plugin.
 */

export interface MarkdownIntentionContext {
  readonly editor: Editor
  /** The selection; with an empty selection the word(s) at the caret (`from` = `to` at the caret when there is no word). */
  readonly from: number
  readonly to: number
  /** No text is selected. */
  readonly empty: boolean
  /** The selected text, or the word at the caret (inline nodes as U+FFFC). */
  readonly text: string
  /** A selected inline atom, or the one right before (else right after) the caret. */
  /** The menu was opened from the block (its handle): rows for the whole block, Změnit na…. */
  readonly block?: boolean
  readonly node?: ProseMirrorNode
  readonly nodePos?: number
  /** Aborted when the menu closes. */
  readonly signal: AbortSignal
}
export interface MarkdownIntention {
  readonly id: string
  readonly label: string
  readonly description?: string
  readonly icon?: string
  /** `ai` rows (the assist module's) go first when Alt+Enter opened the menu and last on Ctrl+Space. */
  readonly group?: string
  /** Lower first, as `MarkdownAction.priority`; default 0. */
  readonly priority?: number
  /** Extra words the filter matches (diacritics aside). */
  readonly keywords?: readonly string[]
  /** The current state (the block's own kind in Změnit na…): marked with a check, never preselected. */
  readonly checked?: boolean
  run(): void | Promise<void>
}
export type MarkdownIntentionProvider = (context: MarkdownIntentionContext) => readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]>

const providers = new WeakMap<Editor, Set<MarkdownIntentionProvider>>()
/** Adds rows to the menu of one editor; the returned function removes them. */
export function registerMarkdownIntentions(editor: Editor, provider: MarkdownIntentionProvider): () => void {
  let set = providers.get(editor)
  if (!set) providers.set(editor, set = new Set())
  set.add(provider)
  return () => { set!.delete(provider) }
}

/** The word around `pos` in its text block: from the space before it to the space after it. */
function wordAt(state: EditorState, pos: number): { from: number; to: number } {
  const $pos = state.doc.resolve(pos), parent = $pos.parent
  if (!parent.isTextblock) return { from: pos, to: pos }
  const text = parent.textBetween(0, parent.content.size, undefined, '￼'), offset = $pos.parentOffset
  let start = offset, end = offset
  while (start > 0 && !/[\s￼]/u.test(text[start - 1]!)) start--
  while (end < text.length && !/[\s￼]/u.test(text[end]!)) end++
  return { from: $pos.start() + start, to: $pos.start() + end }
}

function contextOf(editor: Editor, signal: AbortSignal, block = false): MarkdownIntentionContext {
  const { state } = editor, { selection } = state
  if (selection instanceof NodeSelection) {
    return { editor, from: selection.from, to: selection.to, empty: true, text: '', signal, block, ...selection.node.isInline ? { node: selection.node, nodePos: selection.from } : {} }
  }
  const empty = selection.empty, range = empty ? wordAt(state, selection.from) : { from: selection.from, to: selection.to }
  const before = empty ? selection.$from.nodeBefore : null, after = empty ? selection.$from.nodeAfter : null
  const atom = before?.isInline && before.isAtom ? { node: before, nodePos: selection.from - before.nodeSize }
    : after?.isInline && after.isAtom ? { node: after, nodePos: selection.from } : {}
  // The block's menu is about the block, not a chip its first word happens to be.
  return { editor, ...range, block, empty, text: state.doc.textBetween(range.from, range.to, '\n', '￼'), signal, ...block ? {} : atom }
}

/** Core's own row: a registered chip back to its text. */
const chipToText: MarkdownIntentionProvider = context => context.node && context.nodePos !== undefined && markdownChipText(context.node) !== undefined
  ? [{ id: 'core.chip-to-text', label: intentionsStrings.chipToText, description: chipToTextShortcut(), icon: 'svg:pilcrow', keywords: ['text', 'cip'],
    run: () => { convertMarkdownChipToText(context.editor, context.nodePos!) } }]
  : []

/** The selected element's delete row, whichever runs it (core, or the element's toolbar trash): one word per kind of element (P12). */
export function deleteIntention(editor: Editor, run: () => void): MarkdownIntention {
  const { selection } = editor.state
  return { id: 'core.delete', label: selection instanceof NodeSelection && selection.node.isInline ? intentionsStrings.remove : intentionsStrings.delete,
    description: formatMarkdownShortcut(['Delete']), icon: 'svg:trash-2', priority: 2, run }
}

/**
 * Core's rows for any selected element (a block, a table, a chip), after the element's own (its toolbar's buttons, contextToolbar.ts):
 * copy it, and delete it unless its toolbar's own delete took the id first.
 */
const selectedElement: MarkdownIntentionProvider = ({ editor }) => editor.state.selection instanceof NodeSelection ? [
  // ProseMirror's own copy handler writes the selected node, as Ctrl+C does.
  { id: 'core.copy', label: intentionsStrings.copy, description: formatMarkdownShortcut(['Mod', 'C']), icon: 'svg:copy', priority: 1,
    run: () => { editor.view.focus(); try { editor.view.dom.ownerDocument.execCommand('copy') } catch { /* no clipboard here */ } } },
  deleteIntention(editor, () => { if (editor.isEditable) { dispatchMarkdownHistoryOperation(editor, editor.state.tr.deleteSelection().scrollIntoView()); editor.view.focus() } }),
] : []

/** An open menu: `key` takes a key while it is open (true when the menu used it), `close` closes it. */
export interface MarkdownIntentionsMenu { close(): void; key(event: KeyboardEvent): boolean }
const open = new WeakMap<Editor, MarkdownIntentionsMenu>()

/** Opens the menu at the caret or the selection; false where it cannot open (a read-only editor). */
export function openMarkdownIntentions(editor: Editor, { point, block, ...options }: { filter?: string; aiFirst?: boolean; point?: { x: number; y: number }; /** From the block handle: adds Změnit na…. */ block?: boolean } = {}): boolean {
  if (editor.isDestroyed || !editor.isEditable) return false
  open.get(editor)?.close()
  const abort = new AbortController(), context = contextOf(editor, abort.signal, block)
  const rows = [chipToText, ...(providers.get(editor) ?? []), turnInto, blockActions, selectedElement].flatMap(provider => { try { return [provider(context)] } catch { return [] } })
  // At the pointer for a right click, else at the caret or the selection.
  const rect = () => { if (point) return new DOMRect(point.x, point.y, 0, 0); try { return posToDOMRect(editor.view, context.from, context.to) } catch { return editor.view.dom.getBoundingClientRect() } }
  const doc = editor.view.dom.ownerDocument
  // While it is open the menu takes the keys wherever the focus is (the grip, a toolbar button), before any other listener, and text
  // that comes without a key (an input method, dictation, an automated `insertText`) filters it too: none of it lands in the page.
  const keys = (event: KeyboardEvent) => {
    if (event.isComposing || !menu.key(event)) return
    event.preventDefault(); event.stopPropagation()
    if (event.key === 'Escape' && !editor.isDestroyed) editor.view.focus()
  }
  const text = (event: InputEvent) => {
    if (event.inputType !== 'insertText' || !event.data || event.isComposing) return
    event.preventDefault(); event.stopPropagation()
    for (const key of event.data) menu.key(new KeyboardEvent('keydown', { key }))
  }
  const menu = openMarkdownIntentionsMenu({ document: doc, anchor: rect, contextElement: editor.view.dom, editor, rows, ...options,
    onClose: () => {
      abort.abort(); if (open.get(editor) === menu) open.delete(editor)
      doc.removeEventListener('keydown', keys, true); doc.removeEventListener('beforeinput', text as EventListener, true)
    } })
  doc.addEventListener('keydown', keys, true); doc.addEventListener('beforeinput', text as EventListener, true)
  open.set(editor, menu)
  return true
}

/**
 * The block's menu (J4.9) for the top-level block at `position` (Enter on the grip, the selected blocks' „Změnit na…“): the caret into
 * its text (an atom selected), the focus in the editor, whose keys drive the menu (typing filters, Enter runs the first row).
 */
export function openMarkdownBlockMenu(editor: Editor, position: number): boolean {
  const node = editor.state.doc.nodeAt(position)
  if (editor.isDestroyed || !editor.isEditable || !node) return false
  // An atom is selected even when no caret may select it (`selectable: false`, Atlas's Přehled): near it would be the next block.
  editor.view.dispatch(editor.state.tr.setSelection(node.isAtom ? NodeSelection.create(editor.state.doc, position) : Selection.near(editor.state.doc.resolve(position))))
  editor.view.focus()
  return openMarkdownIntentions(editor, { block: true })
}

/**
 * The same menu where there is no caret: at `anchor`, with the rows a host gives (each list now or when its promise answers), the
 * keys routed to `key` by the host while it is open (the focus stays where it was). A board item uses it (P2: one menu everywhere).
 */
export function openMarkdownIntentionsMenu(options: {
  document: Document
  /** Where the menu stands, read again as it moves. */
  anchor: () => DOMRect
  /** The element whose theme the menu takes and whose scrolling it follows. */
  contextElement?: Element
  editor?: Editor
  rows: readonly (readonly MarkdownIntention[] | Promise<readonly MarkdownIntention[]>)[]
  filter?: string
  aiFirst?: boolean
  onClose?: () => void
}): MarkdownIntentionsMenu {
  const doc = options.document
  let rows: MarkdownIntention[] = [], filter = options.filter ?? '', pending = 0, closed = false
  const list = createSuggestionList({ document: doc, label: intentionsStrings.label, heading: intentionsStrings.label, testId: 'intentions', emptyText: intentionsStrings.empty,
    onSelect: id => {
      const row = shown().find(item => item.id === id)
      close()
      // A row's failure is its own to report; the menu just closes.
      try { void Promise.resolve(row?.run()).catch(() => undefined) } catch { /* see above */ }
    } })
  list.element.classList.add('md-intentions-menu')
  const heading = list.element.querySelector<HTMLElement>('.md-suggestion-heading')
  const ai = (row: MarkdownIntention) => (row.group === 'ai' ? (options.aiFirst ? -1 : 1) : 0)
  const matches = (row: MarkdownIntention) => {
    const words = foldSearchText(filter).split(/\s+/u).filter(Boolean), text = foldSearchText([row.label, row.description, ...(row.keywords ?? [])].join(' '))
    return words.every(word => text.includes(word))
  }
  const shown = () => rows.filter(matches).sort((a, b) => ai(a) - ai(b) || (a.priority ?? 0) - (b.priority ?? 0))
  const render = () => {
    if (closed) return
    // The menu's name is its listbox label; the visible heading only says what the typed filter is.
    if (heading) { heading.textContent = filter ? intentionsStrings.filtered(filter) : ''; heading.hidden = !filter }
    const items = shown()
    if (!items.length && pending) { list.setStatus('loading'); return }
    list.update(items.map((row): MarkdownSuggestionItem => ({ id: row.id, title: row.label, ...row.description ? { description: row.description } : {}, ...row.icon ? { icon: row.icon } : {}, ...row.checked ? { checked: true } : {} })))
  }
  const add = (more: readonly MarkdownIntention[] | undefined) => {
    const ids = new Set(rows.map(row => row.id))
    rows = [...rows, ...(Array.isArray(more) ? more : []).filter(row => row && typeof row.id === 'string' && typeof row.label === 'string' && typeof row.run === 'function' && !ids.has(row.id))]
  }
  for (const answer of options.rows) {
    if (!(answer instanceof Promise)) { add(answer); continue }
    pending++
    answer.then(add, () => undefined).finally(() => { pending--; render() })
  }
  const popup = createSuggestionPopup({ editor: options.editor, content: list.element, contextElement: options.contextElement, getReferenceRect: options.anchor, onDismiss: () => close() })
  function close() {
    if (closed) return
    closed = true
    popup.destroy(); list.destroy()
    options.onClose?.()
  }
  const state: MarkdownIntentionsMenu = {
    close,
    key(event) {
      if (event.key === 'Escape') { close(); return true }
      if (event.key === 'Tab') return list.onKeyDown(new KeyboardEvent('keydown', { key: event.shiftKey ? 'ArrowUp' : 'ArrowDown' })) || true
      if (['ArrowUp', 'ArrowDown', 'Home', 'End', 'Enter'].includes(event.key)) return list.onKeyDown(event) || true
      // Typing filters the rows; Backspace takes the filter back, and with none left closes the menu and deletes as usual.
      if (event.key === 'Backspace' && filter) { filter = filter.slice(0, -1); render(); return true }
      if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey && (filter || event.key !== ' ')) { filter += event.key; render(); return true }
      if (!['Shift', 'Control', 'Alt', 'Meta'].includes(event.key)) close()
      return false
    },
  }
  render()
  return state
}

/**
 * P2, every entry point of the one menu: Ctrl+Space on Windows and macOS (⌘Space is Spotlight there; macOS may take Ctrl+Space for
 * input sources, Alt+Enter then), Alt+Enter, Ctrl+. (⌘.), Shift+F10 and the menu key; the right button is the view's `contextmenu`.
 */
const opensMarkdownIntentions = (event: KeyboardEvent) => {
  const plain = !event.ctrlKey && !event.metaKey && !event.altKey
  return (event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey && (event.code === 'Space' || event.key === ' '))
    || (event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && event.key === 'Enter')
    || ((event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && (event.key === '.' || event.code === 'Period'))
    || (plain && event.shiftKey && event.key === 'F10') || (plain && !event.shiftKey && event.key === 'ContextMenu')
}

export const Intentions = Extension.create({
  name: 'markdownIntentions',
  // Before the lists' Tab, the editor's Escape and every Enter while the menu is open.
  priority: 1200,
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({
      props: {
        handleKeyDown(_view, event) {
          if (event.isComposing) return false
          const menu = open.get(editor)
          if (menu) return menu.key(event)
          return opensMarkdownIntentions(event) && openMarkdownIntentions(editor, { aiFirst: event.altKey })
        },
        handleDOMEvents: {
          contextmenu(view, event) {
            // Windows sends the menu key's own contextmenu on its release, after the keydown opened the menu: no second menu, no browser's.
            if (open.get(editor)) { event.preventDefault(); return true }
            // Shift + right button, a link's or an image's own menu (open in a new tab, save the picture) stay the browser's, as on the board.
            const target = event.target as Element | null
            if (event.shiftKey || !editor.isEditable || target?.closest?.('a[href]:not(.md-chip), img, input, textarea, select')) return false
            // The element or the word under the pointer first; a click inside the selection (or on the selected element) keeps it.
            const { selection } = view.state
            const onSelected = selection instanceof NodeSelection && !!target && !!view.nodeDOM(selection.from)?.contains(target)
            let at: ReturnType<typeof view.posAtCoords> = null
            try { if (!onSelected) at = view.posAtCoords({ left: event.clientX, top: event.clientY }) } catch { /* no layout: the selection stays */ }
            if (at && !(!selection.empty && selection.from <= at.pos && at.pos <= selection.to)) {
              const node = at.inside >= 0 ? view.state.doc.nodeAt(at.inside) : null
              if (node?.isAtom && NodeSelection.isSelectable(node)) editor.commands.setNodeSelection(at.inside)
              else editor.commands.setTextSelection(at.pos)
            }
            event.preventDefault()
            view.focus()
            return openMarkdownIntentions(editor, { point: { x: event.clientX, y: event.clientY } })
          },
        },
      },
      view: () => ({ destroy: () => open.get(editor)?.close() }),
    })]
  },
})
