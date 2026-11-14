import { Extension, InputRule, type Editor, type JSONContent } from '@tiptap/core'
import { Plugin, PluginKey, NodeSelection, Selection, TextSelection } from '@tiptap/pm/state'
import { findWrapping } from '@tiptap/pm/transform'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { moveBlock } from '../features/drag/blockCommands'
import { blockRangeForHandle } from '../features/drag/blockSelection'
import { dispatchMarkdownHistoryOperation } from '../engine/historyBoundary'
import { MARKDOWN_BLOCK_REMOVAL_META, MARKDOWN_LEAVE_START_EVENT, MARKDOWN_SHORTCUT_HELP_EVENT } from '../core/keyboardShortcuts'
import { caretIntoSelected, selectBlockAtCaret, selectedTextBlock } from '../features/modules/selectionEscape'
import { isMarkdownComposition } from '../features/modules/composition'

/**
 * Keyboard-first authoring of a document view: one key set for blocks (move, duplicate, delete), Tab that never drops the
 * writer out of the text, and the Markdown a developer types (a pipe row is a table, `> [!tip]` a callout). The keys are
 * listed for the host's overview in `core/keyboardShortcuts.ts`.
 */
export { markdownKeyboardShortcuts, markdownTypingShortcuts, MARKDOWN_LEAVE_START_EVENT, MARKDOWN_SHORTCUT_HELP_EVENT, type MarkdownShortcut } from '../core/keyboardShortcuts'

const calloutVariants: Readonly<Record<string, string>> = { note: 'note', tip: 'tip', warning: 'warning', important: 'important', caution: 'caution' }

/** The top-level block with the caret, or a selected top-level component (chart, diagram…): its position and the caret's offset inside it. */
function caretBlock(editor: Editor) {
  const { selection } = editor.state
  if (selection instanceof NodeSelection && !selection.$from.depth) return { from: selection.from, node: selection.node, offset: 0, selected: true }
  if (!(selection instanceof TextSelection) || !selection.$from.depth) return undefined
  const from = selection.$from.before(1)
  return { from, node: editor.state.doc.nodeAt(from)!, offset: selection.from - from, selected: false }
}

/** The caret back at `offset` in the block at `at`, or the component there selected again. */
const reselect = (doc: Editor['state']['doc'], at: number, block: NonNullable<ReturnType<typeof caretBlock>>) =>
  block.selected ? NodeSelection.create(doc, at) : TextSelection.near(doc.resolve(Math.min(at + block.offset, doc.content.size)))

export function moveWithCaret(editor: Editor, direction: -1 | 1): boolean {
  const block = caretBlock(editor)
  if (!block) return false
  const moved = moveBlock(editor, block.from, direction)
  if (moved === null) return true // At the edge: the key is still ours, nothing else should move.
  editor.view.dispatch(editor.state.tr.setSelection(reselect(editor.state.doc, moved, block)).setMeta('addToHistory', false).scrollIntoView())
  return true
}

/** Where the block ends: a heading's block is its section, the range the grip tints and moves (a requirement's notes and facts go with it). */
const blockEnd = (editor: Editor, block: NonNullable<ReturnType<typeof caretBlock>>) =>
  block.node.type.name === 'heading' ? blockRangeForHandle(editor.state, block.from)?.to ?? block.from + block.node.nodeSize : block.from + block.node.nodeSize

export function duplicate(editor: Editor, direction: -1 | 1): boolean {
  const block = caretBlock(editor)
  if (!block) return false
  const end = blockEnd(editor, block), at = direction > 0 ? end : block.from
  // New node objects: `node.copy(node.content)` returns the node itself, and Yjs then maps two blocks to one node, so undo
  // rewrote the original and never removed the copy (J4.4).
  const copies: ProseMirrorNode[] = []
  editor.state.doc.slice(block.from, end).content.forEach(node => { copies.push(editor.schema.nodeFromJSON(node.toJSON())) })
  const tr = editor.state.tr.insert(at, copies)
  tr.setSelection(reselect(tr.doc, at, block)).scrollIntoView()
  dispatchMarkdownHistoryOperation(editor, tr)
  return true
}

export function deleteBlock(editor: Editor): boolean {
  const block = caretBlock(editor)
  if (!block) return false
  const { doc } = editor.state
  const tr = editor.state.tr, end = blockEnd(editor, block)
  let blocks = 0
  doc.nodesBetween(block.from, end, () => { blocks++; return false })
  tr.setMeta(MARKDOWN_BLOCK_REMOVAL_META, blocks)
  if (block.from === 0 && end === doc.content.size) tr.replaceWith(0, doc.content.size, editor.schema.nodes.paragraph!.create())
  else tr.delete(block.from, end)
  const after = Math.min(block.from, tr.doc.content.size)
  tr.setSelection(TextSelection.near(tr.doc.resolve(after))).scrollIntoView()
  dispatchMarkdownHistoryOperation(editor, tr)
  return true
}

/** `|Jméno|Telefon|` and Enter: a table with those header cells and one empty row, the caret in its first cell. */
function pipeRowToTable(editor: Editor): boolean {
  const { selection, schema } = editor.state
  const { $from } = selection
  if (!selection.empty || !schema.nodes.table || $from.parent.type.name !== 'paragraph' || $from.parentOffset !== $from.parent.content.size) return false
  if ($from.depth > 1 && $from.node($from.depth - 1).type.spec.tableRole) return false
  const match = /^\|(.+)\|\s*$/u.exec($from.parent.textContent)
  const cells = match?.[1]!.split('|').map(cell => cell.trim())
  if (!cells || cells.length < 2) return false
  const cell = (type: string, text: string): JSONContent => ({ type, content: [{ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] }] })
  const table: JSONContent = { type: 'table', content: [
    { type: 'tableRow', content: cells.map(text => cell('tableHeader', text)) },
    { type: 'tableRow', content: cells.map(() => cell('tableCell', '')) },
  ] }
  const from = $from.before(), to = $from.after()
  const tr = editor.state.tr.replaceWith(from, to, schema.nodeFromJSON(table))
  // Header row, then the first cell of the body row: table > row > cell > paragraph.
  const headerRow = tr.doc.nodeAt(from + 1)!
  tr.setSelection(TextSelection.near(tr.doc.resolve(from + 1 + headerRow.nodeSize + 3))).scrollIntoView()
  dispatchMarkdownHistoryOperation(editor, tr)
  return true
}

/** Enter on a selected component without an edit of its own goes into its toolbar, like Tab; Escape returns. A block
 *  module with `edit` takes Enter first and opens that edit (contextToolbar.ts `onActivate`). */
const toolbarButton = (editor: Editor) => [...editor.view.dom.ownerDocument.querySelectorAll<HTMLElement>('.md-context-toolbar:not([hidden]) button:not([disabled])')]
  .find(element => !element.closest('[hidden]'))
function openSelectedComponent(editor: Editor): boolean {
  const { selection } = editor.state
  if (!(selection instanceof NodeSelection) || !selection.node.isAtom || selection.node.isInline) return false
  const button = toolbarButton(editor)
  if (!button) return false
  button.focus({ preventScroll: true })
  return true
}

const TABBABLE = 'a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[contenteditable="true"],[tabindex]:not([tabindex="-1"])'
/** The first focusable element after `root` in document order, outside it, shown and not taken out of the tab order. */
function tabbableAfter(root: HTMLElement): HTMLElement | undefined {
  return [...root.ownerDocument.querySelectorAll<HTMLElement>(TABBABLE)].find(element => !root.contains(element)
    && !!(root.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING) && element.tabIndex >= 0 && !element.closest('[hidden],[inert]')
    && element.getClientRects().length > 0)
}

/** The first (forward) or last control of the top-level block next to the selected one: a card's, a chart's button (J6.2(4)).
 *  Only controls of an atom or of a node view's non-editable island count, not links in the block's text. */
function neighbourControl(editor: Editor, forward: boolean): HTMLElement | undefined {
  const { selection, doc } = editor.state
  if (!(selection instanceof NodeSelection) || selection.$from.depth) return undefined
  const index = selection.$from.index(0) + (forward ? 1 : -1)
  if (index < 0 || index >= doc.childCount) return undefined
  const atom = doc.child(index).isAtom
  const dom = editor.view.nodeDOM(forward ? selection.to : selection.from - doc.child(index).nodeSize)
  if (!(dom instanceof HTMLElement)) return undefined
  const controls = [...dom.querySelectorAll<HTMLElement>(TABBABLE)].filter(element => element.tabIndex >= 0 && element.getAttribute('contenteditable') !== 'true'
    && (atom || element.closest('[contenteditable]')?.getAttribute('contenteditable') === 'false') && !element.closest('[hidden],[inert]') && element.getClientRects().length > 0)
  return forward ? controls[0] : controls.at(-1)
}
/** Tab from a selected block: the neighbour's control takes focus; forward without one, focus leaves the editor (Esc, Tab: the help's
 *  „Opustit editor“; the controls in the text are out of the Tab order, Alt+F10 reaches them); else the key stays in the editor. */
const toNeighbour = (editor: Editor, forward: boolean) => {
  const control = neighbourControl(editor, forward) ?? (forward && editor.state.selection instanceof NodeSelection && !editor.state.selection.$from.depth ? tabbableAfter(editor.view.dom) : undefined)
  control?.focus()
  return true
}
/**
 * Ctrl+End / Ctrl+Home from a table cell: the browser keeps the caret in the cell (an editing island of its own), so the editor takes
 * it to the document's end or start as everywhere else; with Shift the selection grows there (F5). Elsewhere the browser's own, except
 * a plain Ctrl+Home: a caret the browser puts on the document's first text position shortly after the editor gained focus reads to
 * ProseMirror as the browser's reset after focus and is put back (the caret stayed where it was, the arrows started from there).
 */
function toDocumentEdge(editor: Editor, forward: boolean, extend: boolean): boolean {
  const { selection, doc } = editor.state
  let ours = !forward && !extend
  for (let depth = selection.$head.depth; depth > 0 && !ours; depth--) ours = /cell/i.test(String(selection.$head.node(depth).type.spec.tableRole ?? ''))
  if (!ours) return false
  const edge = forward ? Selection.atEnd(doc) : Selection.atStart(doc)
  editor.view.dispatch(editor.state.tr.setSelection(extend ? TextSelection.between(selection.$anchor, edge.$head) : edge).scrollIntoView())
  return true
}
/** The top-level block a selection selects whole (Esc, the grip): Delete removes it, a heading with its section. */
const selectedBlock = (editor: Editor) => editor.state.selection instanceof NodeSelection && !editor.state.selection.$from.depth

/** The caret (no selection) in the document's first block, a text block of the top level: the host's field above is the way back. */
function inFirstBlock(editor: Editor) {
  const { selection, doc } = editor.state
  return selection.empty && selection.$from.depth === 1 && selection.$from.index(0) === 0 && !!doc.firstChild?.isTextblock
}
/** A bubbling, cancelable request to the host; true when the host took it. */
const ask = (editor: Editor, type: string) => !editor.view.dom.dispatchEvent(new (editor.view.dom.ownerDocument.defaultView?.CustomEvent ?? CustomEvent)(type, { bubbles: true, cancelable: true }))

export interface KeyboardFirstOptions {
  /** A compact field (a form's description) gives Tab back to the page, where it walks the form. */
  compact: () => boolean
}

export const KeyboardFirst = Extension.create<KeyboardFirstOptions>({
  name: 'markdownKeyboardFirst',
  // The last word on Tab: lists, tables, code blocks and a host's blocks (Atlas's requirement TabGuard, priority 1) come first.
  // Below 1 but not 0, which tiptap reads as its default 100.
  priority: 0.5,
  addOptions: () => ({ compact: () => false }),
  addKeyboardShortcuts() {
    const editor = this.editor
    // Escape, then Tab leaves before any extension's Tab (lists, tables, code blocks): see the plugin below.
    // A selected component lends Tab to its toolbar (contextToolbar.ts); a block without one (a paragraph the grip selected) goes on
    // to the next block's first control, or keeps Tab (J6.2(4)).
    const tab = () => !this.options.compact() && !(editor.state.selection instanceof NodeSelection && toolbarButton(editor)) && toNeighbour(editor, true)
    // Back from a selected block: the previous block's last control, or stays in the editor: the browser's would go to the page around it (J6.2).
    // In the first block of the text it goes to the host's field above (the page title), as Up on the first line does.
    const shiftTab = () => !this.options.compact() && (inFirstBlock(editor) && ask(editor, MARKDOWN_LEAVE_START_EVENT) || toNeighbour(editor, false))
    // Ctrl+Shift+digit: a Czech keyboard types digits with Shift, and its Ctrl+Alt is AltGr, so Ctrl+Alt+1 never arrives.
    const headings = Object.fromEntries([1, 2, 3, 4, 5, 6].map(level => [`Mod-Shift-${level}`, () => editor.commands.toggleHeading({ level: level as 1 })]))
    return {
      ...headings,
      'Mod-Shift-0': () => editor.commands.setParagraph(),
      Tab: tab,
      'Shift-Tab': shiftTab,
      'Mod-Shift-ArrowUp': () => moveWithCaret(editor, -1),
      'Mod-Shift-ArrowDown': () => moveWithCaret(editor, 1),
      'Alt-Shift-ArrowUp': () => duplicate(editor, -1),
      'Alt-Shift-ArrowDown': () => duplicate(editor, 1),
      // The board's keys too (Ctrl+D, Delete on what is selected); the browser's bookmark never gets Ctrl+D in the text.
      'Mod-d': () => duplicate(editor, 1),
      'Mod-End': () => toDocumentEdge(editor, true, false),
      'Mod-Home': () => toDocumentEdge(editor, false, false),
      'Mod-Shift-End': () => toDocumentEdge(editor, true, true),
      'Mod-Shift-Home': () => toDocumentEdge(editor, false, true),
      'Mod-Shift-Backspace': () => deleteBlock(editor),
      'Mod-/': () => ask(editor, MARKDOWN_SHORTCUT_HELP_EVENT),
      // Up on the first line of the text: the host's field above (the page title) takes the caret.
      ArrowUp: () => !this.options.compact() && inFirstBlock(editor) && editor.view.endOfTextblock('up') && ask(editor, MARKDOWN_LEAVE_START_EVENT),
    }
  },
  addStorage: () => ({ leaving: false }),
  addProseMirrorPlugins() {
    const storage = this.storage, options = this.options, editor = this.editor
    return [new Plugin({
      key: new PluginKey('markdownKeyboardFirstEscape'),
      // Esc from a button inside a top-level block (a chart's, an OpenAPI card's) leaves that block selected, as Esc from its toolbar does (J6.3):
      // the second Esc then gives the caret back (core's SelectedElementEscape).
      view: view => {
        const escape = (event: KeyboardEvent) => {
          const target = event.target
          if (event.key !== 'Escape' || event.defaultPrevented || !view.editable || !(target instanceof Element) || target === view.dom
            || !target.closest('button, [role="button"], a[href]') || target.closest('[data-md-own-escape]') || !view.dom.contains(target)) return
          let child: Element | null = target
          while (child && child.parentElement !== view.dom) child = child.parentElement
          if (!child) return
          const pos = view.posAtDOM(view.dom, Array.prototype.indexOf.call(view.dom.childNodes, child))
          const node = view.state.doc.nodeAt(pos)
          if (!node || !NodeSelection.isSelectable(node)) return
          event.preventDefault(); event.stopPropagation()
          view.dispatch(view.state.tr.setSelection(NodeSelection.create(view.state.doc, pos)))
          view.focus()
        }
        view.dom.addEventListener('keydown', escape)
        // A chip drawn before the text could be written (a reader's, a session still connecting) leaves the Tab order with it (chip.ts).
        let editable = view.editable
        return {
          update: next => {
            if (next.editable === editable) return
            editable = next.editable
            next.dom.querySelectorAll<HTMLElement>('a.md-chip').forEach(chip => { if (editable) chip.tabIndex = -1; else chip.removeAttribute('tabindex') })
          },
          destroy: () => view.dom.removeEventListener('keydown', escape),
        }
      },
      props: {
        // Escape that nothing else took (no menu, no panel) arms the next Tab to leave the editor; any other key disarms it.
        handleKeyDown(view, event) {
          if (event.key !== 'Escape') {
            if (event.key !== 'Tab' && event.key !== 'Shift') storage.leaving = false
            return false
          }
          // J4.1: Esc in the text selects the block around the caret (not in a compact field); a selected one gives the caret back:
          // core's SelectedElementEscape, after these keys.
          // Either way the next Tab leaves the editor (the help's „Escape, Tab“), whatever the block or its neighbour holds; a selected
          // block's toolbar is Tab's when the block was selected otherwise (a click, the grip: J4.5).
          storage.leaving = true
          return !options.compact() && !isMarkdownComposition(event, editor) && selectBlockAtCaret(view)
        },
        // The armed Tab skips every keymap (a list would nest, a table walk its cells) and keeps its default: focus moves on.
        handleDOMEvents: {
          keydown(view, event) {
            if (event.key !== 'Tab' || !storage.leaving || event.ctrlKey || event.altKey || event.metaKey) return false
            storage.leaving = false
            // Forward, the browser's next stop would be a control inside the text (a table's scroll area, an image handle,
            // a contact's phone link): the first one after the whole editor takes focus instead (UX ED-16).
            const next = event.shiftKey ? undefined : tabbableAfter(view.dom)
            if (next) { event.preventDefault(); next.focus() }
            return true
          },
        },
      },
    })]
  },
})

/** Enter before the core keymap: a pipe row becomes a table, a selected component opens its controls, a selected text block takes the caret back. */
export const KeyboardFirstEnter = Extension.create({
  name: 'markdownKeyboardFirstEnter',
  priority: 1000,
  addKeyboardShortcuts() {
    const editor = this.editor
    // Enter on a block selected whole (Esc, the grip) goes back into its text, as in Notion (J4.2); the core keymap added a paragraph.
    const intoText = () => selectedTextBlock(editor.view) && caretIntoSelected(editor.view)
    // Backspace and Delete on a block selected whole before the core keymap, which would delete a heading alone and leave its section
    // (a requirement's title went, its lines stayed: J4.2).
    const remove = () => selectedBlock(editor) && deleteBlock(editor)
    return { Enter: () => openSelectedComponent(editor) || intoText() || pipeRowToTable(editor), Backspace: remove, Delete: remove }
  },
  addInputRules() {
    const editor = this.editor
    if (!editor.schema.nodes.callout) return []
    // `[!tip] ` at the start of a quote turns the quote into that callout; at the start of a paragraph it wraps it.
    return [new InputRule({
      find: /^\[!(note|tip|warning|important|caution)\]\s$/iu,
      handler: ({ state, range, match }) => {
        const callout = state.schema.nodes.callout!
        const attrs = { variant: calloutVariants[match[1]!.toLowerCase()]! }
        const tr = state.tr.delete(range.from, range.to)
        const $at = tr.doc.resolve(tr.mapping.map(range.from))
        const quote = $at.depth >= 2 && $at.node(-1).type.name === 'blockquote' && $at.index(-1) === 0 ? $at.before(-1) : undefined
        if (quote !== undefined) { tr.setNodeMarkup(quote, callout, attrs); return }
        const blockRange = $at.blockRange()
        const wrapping = blockRange && findWrapping(blockRange, callout, attrs)
        if (wrapping) tr.wrap(blockRange!, wrapping)
      },
    })]
  },
})
