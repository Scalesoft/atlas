import { Extension, type Editor } from '@tiptap/core'
import { Fragment, type Node as ProseMirrorNode } from '@tiptap/pm/model'
import { NodeSelection, Plugin, PluginKey, TextSelection, type EditorState, type Transaction } from '@tiptap/pm/state'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { formatMarkdownShortcut } from '../../core/keyboardShortcuts'
import { markdownChangeMapping } from '../../core/changeMapping'

/*
 * Chips back to text, one behaviour for every inline chip (a contact, a YouTrack issue, a page reference, an emoji…):
 * - right after an automatic conversion (`markMarkdownAutoConversion`) Backspace acts as deleting the character that triggered it:
 *   the typed text comes back without it, the caret at its end; the conversion is its own undo step, so Ctrl+Z undoes it and not the
 *   typing; typing the trigger again makes the chip again;
 * - any time later Ctrl+Shift+Space on a selected chip (or with the caret right after it) and „Převést na text“ in its toolbar turn
 *   it into its visible text (`registerMarkdownChipText`), leaving no hidden data behind; that text is remembered as turned back on
 *   purpose (`isMarkdownRevertedText`), so a recognizer does not make it a chip again; that memory is the editor session's (owner R1:
 *   no mark in the document), and editing that text forgets it, so the edited words may be offered again.
 */

const texts = new Map<string, (node: ProseMirrorNode) => string>()
/** How an inline atom reads as plain text when it is turned back into text: what it shows (the name, the issue key, the emoji). */
export function registerMarkdownChipText(nodeName: string, text: (node: ProseMirrorNode) => string): void { texts.set(nodeName, text) }
/** The registered text of a chip, or undefined for a node that is no chip. */
export const markdownChipText = (node: ProseMirrorNode | null | undefined): string | undefined => (node ? texts.get(node.type.name)?.(node) : undefined)

/** The shortcut that turns the chip into text, as `markdownKeyboardShortcuts` lists it. */
export const CHIP_TO_TEXT_KEYS = ['Mod', 'Shift', 'Space'] as const
export const chipToTextShortcut = () => formatMarkdownShortcut(CHIP_TO_TEXT_KEYS)

/** An automatic conversion; `revert` also undoes what it wrote besides the text (a requirement's date), into the same step. */
export interface MarkdownAutoConversion { from: number; to: number; text: string; revert?: (tr: Transaction) => void }
type Conversion = MarkdownAutoConversion
const conversionMeta = 'markdownAutoConversion'
const conversionKey = new PluginKey<Conversion | null>('markdownAutoConversion')
interface Range { from: number; to: number }
interface Reverted extends Range { text: string }
const revertedKey = new PluginKey<readonly Reverted[]>('markdownRevertedText')

/** Whether `from`–`to` overlaps a chip's text turned back on purpose: a recognizer leaves it text. */
export const isMarkdownRevertedText = (state: EditorState, from: number, to: number): boolean =>
  !!revertedKey.getState(state)?.some(range => range.from < to && from < range.to)

/** Remembers `from`–`to` of `tr`'s document as text left so on purpose („Není to on“): no recognizer offers or converts it again. */
export const rememberMarkdownRevertedText = (tr: Transaction, from: number, to: number): Transaction =>
  tr.setMeta(revertedKey, { from, to, text: tr.doc.textBetween(from, to, ' ', '￼') } satisfies Reverted)

/**
 * Marks `tr` as an automatic conversion. `from`–`to` is everything it left that Backspace replaces: the chip, and the character that
 * triggered it when that stays after it (the space after a name or an issue key). `text` is what was typed, without that character.
 * The conversion becomes an undo step of its own; right after it (nothing typed, the caret not moved), Backspace with the caret at
 * `to` acts as deleting the trigger: `text` comes back with the caret at its end. Typing the trigger again converts it again, through
 * the plugin's own path.
 */
export function markMarkdownAutoConversion(tr: Transaction, conversion: MarkdownAutoConversion): Transaction {
  return closeMarkdownHistory(tr).setMeta(conversionMeta, { ...conversion } satisfies Conversion)
}

/** The registered chip at `pos` as its text, one undo step; the caret after the text. */
export function convertMarkdownChipToText(editor: Editor, pos: number): boolean {
  const node = editor.state.doc.nodeAt(pos), text = markdownChipText(node)
  if (!node || text === undefined || !editor.isEditable) return false
  const tr = rememberMarkdownRevertedText(editor.state.tr.replaceWith(pos, pos + node.nodeSize, text ? editor.schema.text(text, node.marks) : Fragment.empty), pos, pos + text.length)
  dispatchMarkdownHistoryOperation(editor, tr.setSelection(TextSelection.create(tr.doc, pos + text.length)))
  editor.view.focus()
  return true
}

/** The chip a key acts on: the selected one, else the one right before the caret. */
function chipAt(editor: Editor): number | undefined {
  const { selection } = editor.state
  if (selection instanceof NodeSelection) return markdownChipText(selection.node) === undefined ? undefined : selection.from
  const before = selection.empty ? selection.$from.nodeBefore : null
  return before && markdownChipText(before) !== undefined ? selection.from - before.nodeSize : undefined
}

export const ChipText = Extension.create({
  name: 'markdownChipText',
  // Before every other Backspace (lists, blocks, the editor's own, tiptap's input-rule undo).
  priority: 1100,
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin<Conversion | null>({
      key: conversionKey,
      state: {
        init: () => null,
        apply(tr, previous) {
          const marked = tr.getMeta(conversionMeta) as Conversion | undefined
          if (marked) return marked
          // Only right after it: any edit or caret move forgets the conversion.
          return previous && !tr.docChanged && !tr.selectionSet ? previous : null
        },
      },
      // The end of the conversion's undo step: what is typed next is a step of its own.
      appendTransaction: (transactions, _old, state) => transactions.some(tr => tr.getMeta(conversionMeta))
        ? closeMarkdownHistory(state.tr).setMeta('addToHistory', false) : null,
      props: {
        handleKeyDown(view, event) {
          if (event.isComposing || !editor.isEditable) return false
          const mod = event.ctrlKey || event.metaKey
          if (mod && event.shiftKey && !event.altKey && (event.code === 'Space' || event.key === ' ')) {
            const pos = chipAt(editor)
            return pos !== undefined && convertMarkdownChipToText(editor, pos)
          }
          const conversion = conversionKey.getState(view.state)
          if (event.key !== 'Backspace' || mod || event.altKey || event.shiftKey || !conversion?.text) return false
          const { selection, doc } = view.state
          if (!selection.empty || selection.from !== conversion.to || conversion.to > doc.content.size) return false
          // As deleting the character that made the chip: the typed text without it, the caret at its end.
          const marks = doc.nodeAt(conversion.from)?.marks ?? doc.resolve(conversion.from).marks()
          const tr = view.state.tr.replaceWith(conversion.from, conversion.to, view.state.schema.text(conversion.text, marks))
          conversion.revert?.(tr)
          dispatchMarkdownHistoryOperation(editor, tr.setSelection(TextSelection.create(tr.doc, conversion.from + conversion.text.length)))
          return true
        },
      },
    }), new Plugin<readonly Reverted[]>({
      key: revertedKey,
      state: {
        init: () => [],
        apply(tr, ranges) {
          const added = tr.getMeta(revertedKey) as Reverted | undefined
          // Text typed right before or after stays outside; an edit inside (other words now) forgets it.
          const mapping = markdownChangeMapping(tr)
          const mapped = !tr.docChanged ? ranges : ranges.map(range => ({ ...range, from: mapping.map(range.from, 1), to: mapping.map(range.to, -1) }))
            .filter(range => range.from < range.to && tr.doc.textBetween(range.from, range.to, ' ', '￼') === range.text)
          return added && added.from < added.to ? [...mapped, added] : mapped
        },
      },
    })]
  },
})
