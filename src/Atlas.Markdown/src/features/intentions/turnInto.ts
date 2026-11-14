import type { Editor } from '@tiptap/core'
import { NodeRange, type Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Selection, type Transaction } from '@tiptap/pm/state'
import { findWrapping, liftTarget } from '@tiptap/pm/transform'
import { wrapRangeInList } from '@tiptap/pm/schema-list'
import { slashStrings } from '../../ui/strings/slash'
import { intentionsStrings } from '../../ui/strings/intentions'
import { dragStrings } from '../../ui/strings/drag'
import { formatMarkdownShortcut } from '../../core/keyboardShortcuts'
import { closeMarkdownHistory } from '../../engine/historyBoundary'
import { retypeList } from '../timed/timedList'
import { deleteBlock, duplicate, moveWithCaret } from '../../extensions/KeyboardFirst'
import type { MarkdownIntention, MarkdownIntentionProvider } from './intentions'

/*
 * The block's menu (J4.9): „Změnit na…“ (Notion's turn-into) and the block's own rows (Duplikovat, Posunout, Smazat), as rows of the
 * one action menu opened from the block handle. The block is the top-level block with the caret or the selected one; only text blocks
 * and their containers turn (a table, a chart or a chip offers its own rows). The current kind is listed checked and does nothing (§5.4).
 */
type Target = { id: string; label: string; icon: string; keywords: string[]; node: string; attrs?: Record<string, unknown>; item?: string }
const heading = (level: 1 | 2 | 3 | 4, label: string): Target => ({ id: `h${level}`, label, icon: `heading-${level}`, keywords: ['nadpis', `h${level}`, 'heading'], node: 'heading', attrs: { level } })
// The icons are the slash menu's for the same kinds (SlashCommands.ts).
const targets = (): Target[] => [
  { id: 'paragraph', label: 'Odstavec', icon: 'pilcrow', keywords: ['odstavec', 'text', 'paragraph'], node: 'paragraph' },
  heading(1, slashStrings.heading1Title), heading(2, slashStrings.heading2Title), heading(3, slashStrings.heading3Title), heading(4, slashStrings.heading4Title),
  { id: 'bullet', label: slashStrings.bulletListTitle, icon: 'list', keywords: ['odrazky', 'seznam', 'bullet'], node: 'bulletList', item: 'listItem' },
  { id: 'ordered', label: slashStrings.orderedListTitle, icon: 'list-ordered', keywords: ['cisla', 'ciselny', 'seznam', 'ordered'], node: 'orderedList', item: 'listItem' },
  { id: 'task', label: intentionsStrings.tasks, icon: 'list-checks', keywords: ['ukol', 'todo', 'checklist'], node: 'taskList', item: 'taskItem' },
  { id: 'timed', label: slashStrings.timedListTitle, icon: 'clock', keywords: ['cas', 'casova', 'datum'], node: 'timedList', item: 'timedItem' },
  { id: 'quote', label: slashStrings.blockquoteTitle, icon: 'quote', keywords: ['citace', 'quote'], node: 'blockquote' },
  { id: 'callout', label: slashStrings.calloutTitle, icon: 'callout', keywords: ['info', 'callout', 'pozor', 'varovani'], node: 'callout', attrs: { variant: 'note' } },
  { id: 'code', label: slashStrings.codeBlockTitle, icon: 'square-code', keywords: ['kod', 'code'], node: 'codeBlock' },
]
const LISTS = ['bulletList', 'orderedList', 'taskList', 'timedList']

/** The block's kind as a target id; undefined for a block that does not turn (a table, a chart). */
const kindOf = (node: ProseMirrorNode) => node.type.name === 'heading' ? `h${node.attrs.level}` : targets().find(target => target.node === node.type.name && !target.attrs?.level)?.id

/** Where the block with the caret (or the selected top-level block) starts. */
const blockStart = (editor: Editor) => { const { selection } = editor.state; return selection.$from.depth ? selection.$from.before(1) : selection.from }

/**
 * Lifts every text block of the region `from`…(size − `tail`) out of the lists, quotes and callouts around it, one level at a time; an
 * item with a nested list goes out with it, whose items follow. False when a block cannot leave (a table cell).
 */
function liftOut(tr: Transaction, from: number, tail: number): boolean {
  for (let guard = 0; guard < 1000; guard++) {
    let at = -1
    tr.doc.nodesBetween(from, tr.doc.content.size - tail, (node, pos) => {
      if (at < 0 && node.isTextblock && tr.doc.resolve(pos).depth) at = pos
      return at < 0 && !node.isTextblock
    })
    if (at < 0) return true
    const $in = tr.doc.resolve(at + 1)
    // The block alone; when its container cannot stay without it (an item whose nested list would come first), all of the container.
    const range = [$in.blockRange(), new NodeRange(tr.doc.resolve($in.start(-1)), tr.doc.resolve($in.end(-1)), $in.depth - 1)]
      .find(candidate => candidate && liftTarget(candidate) != null)
    if (!range) return false
    tr.lift(range, liftTarget(range)!)
  }
  return false
}

/** The block at `from` turned into `target` in one undo step; its text stays, only its kind and containers change. */
export function turnBlockInto(editor: Editor, from: number, target: Pick<Target, 'node' | 'attrs' | 'item'>): boolean {
  const { schema } = editor.state, type = schema.nodes[target.node], top = editor.state.doc.nodeAt(from)
  if (!type || !top || !editor.isEditable) return false
  const tail = editor.state.doc.content.size - from - top.nodeSize
  const done = editor.chain().command(({ tr, commands }) => {
    const end = () => tr.doc.content.size - tail
    tr.setSelection(Selection.near(tr.doc.resolve(from)))
    if (target.item && top.type.isInGroup('list')) {
      // Another kind of list keeps its items and their nesting.
      if (!(target.node === 'timedList' ? commands.toggleTimedList() : retypeList(tr, LISTS, target.node, target.item))) return false
    } else {
      if (!liftOut(tr, from, tail)) return false
      tr.setBlockType(from, end(), type.isTextblock ? type : schema.nodes.paragraph!, type.isTextblock ? target.attrs : null)
      // The wrap is done on `tr` itself: a command here would read the chain's state from before the lift.
      const range = new NodeRange(tr.doc.resolve(from), tr.doc.resolve(end()), 0)
      if (target.item) {
        // A timed list is a bullet list retyped and stamped (toggleTimedList), each block its own item.
        if (!wrapRangeInList(tr, range, target.node === 'timedList' ? schema.nodes.bulletList! : type)) return false
        tr.setSelection(Selection.near(tr.doc.resolve(from)))
        if (target.node === 'timedList' && !commands.toggleTimedList()) return false
      } else if (!type.isTextblock) {
        const wrapping = findWrapping(range, type, target.attrs)
        if (!wrapping) return false
        tr.wrap(range, wrapping)
      }
    }
    tr.setSelection(Selection.near(tr.doc.resolve(from)))
    closeMarkdownHistory(tr)
    return true
  }).run()
  // Closes the undo step after it too (dispatchMarkdownHistoryOperation).
  if (done) editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
  return done
}

/** Only the block's own menu (the handle) lists them: over a plain caret Ctrl+Space keeps to what the text under it offers. */
export const turnInto: MarkdownIntentionProvider = ({ editor, block }) => {
  const from = blockStart(editor), top = editor.state.doc.nodeAt(from), kind = top && kindOf(top)
  if (!block || !editor.isEditable || !kind) return []
  return targets().filter(target => editor.schema.nodes[target.node]).map((target): MarkdownIntention => ({
    id: `core.turn-into.${target.id}`, label: intentionsStrings.turnInto(target.label), icon: `svg:${target.icon}`, priority: -3, keywords: ['zmenit', 'prevest', ...target.keywords],
    ...target.id === kind ? { checked: true } : {},
    run: () => { if (target.id !== kind) turnBlockInto(editor, from, target); editor.view.focus() },
  }))
}

/** The block's own rows (J4.9), the keys' commands (KeyboardFirst.ts) for the block with the caret; the focus back in the text after. */
export const blockActions: MarkdownIntentionProvider = ({ editor, block }) => {
  if (!block || !editor.isEditable) return []
  const row = (id: string, label: string, keys: string[], icon: string, priority: number, action: () => boolean): MarkdownIntention =>
    ({ id, label, description: formatMarkdownShortcut(keys), icon: `svg:${icon}`, priority, run: () => { action(); editor.view.focus() } })
  return [
    // Harmless rows first (the first one is preselected, J4.9): Změnit na… (−3), these, then plugin rows such as Kopírovat odkaz (0), Smazat last.
    row('core.block.duplicate', intentionsStrings.duplicate, ['Alt', 'Shift', 'ArrowDown'], 'copy', -2, () => duplicate(editor, 1)),
    row('core.block.up', dragStrings.moveUpLabel, ['Mod', 'Shift', 'ArrowUp'], 'arrow-up', -1, () => moveWithCaret(editor, -1)),
    row('core.block.down', dragStrings.moveDownLabel, ['Mod', 'Shift', 'ArrowDown'], 'arrow-down', -1, () => moveWithCaret(editor, 1)),
    row('core.delete', intentionsStrings.delete, ['Mod', 'Shift', 'Backspace'], 'trash-2', 5, () => deleteBlock(editor)),
  ]
}
