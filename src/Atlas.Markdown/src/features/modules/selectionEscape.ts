import { Extension } from '@tiptap/core'
import { NodeSelection, Plugin, Selection, TextSelection } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import { isMarkdownComposition } from './composition'

/** The caret an Esc in the text left (J4.1), while the block it selected stays selected. */
const carets = new WeakMap<EditorView, { selection: Selection; caret: number }>()

/**
 * J4.1, the document view's Esc in a block's text with nothing open: the top-level block around the caret is selected (a table keeps its
 * cell: Esc there arms Tab to leave). The second Esc gives the caret back where it was (SelectedElementEscape).
 */
export function selectBlockAtCaret(view: EditorView): boolean {
  const { selection, doc } = view.state
  // A text range inside one block too (J6.3: its bubble went with the Esc before).
  if (!view.editable || !(selection instanceof TextSelection) || !selection.$from.depth || !selection.$to.depth || selection.$from.before(1) !== selection.$to.before(1)) return false
  const pos = selection.$from.before(1), node = doc.nodeAt(pos)
  if (!node || node.type.name === 'table' || !NodeSelection.isSelectable(node)) return false
  view.dispatch(view.state.tr.setSelection(NodeSelection.create(doc, pos)))
  carets.set(view, { selection: view.state.selection, caret: selection.from })
  return true
}

/**
 * The caret back into the selected top-level element: where the Esc that selected the block left it, else into the block's text, or right
 * after an element without text of its own (a chart, a chip). False with no element selected.
 */
export function caretIntoSelected(view: EditorView): boolean {
  const { selection, doc } = view.state
  if (!(selection instanceof NodeSelection)) return false
  const remembered = carets.get(view)
  const inside = !selection.node.isAtom && Selection.findFrom(doc.resolve(selection.from + 1), 1, true)
  const caret = remembered?.selection === selection ? TextSelection.create(doc, remembered.caret)
    : inside && inside.from < selection.to ? inside : TextSelection.near(doc.resolve(selection.to))
  carets.delete(view)
  view.dispatch(view.state.tr.setSelection(caret).scrollIntoView())
  return true
}

/** A block selected whole whose text can take the caret (Esc, the grip), not a component (a chart: Enter opens it). */
export const selectedTextBlock = (view: EditorView) =>
  view.state.selection instanceof NodeSelection && !view.state.selection.$from.depth && !view.state.selection.node.isAtom

/**
 * P3's last step for every selected element (a block, a table, a chip): Esc that nothing before took (a menu, a field, a panel)
 * drops the selection and returns the caret (J4.1, J6.3): where the Esc that selected the block left it, else into the block's text, or
 * right after an element without text of its own (a chart, a chip). Also from a control of the element itself (a button the edit gave the
 * keyboard back to). The one mechanism for it: the ready-made view's keys only select the block and arm Esc-then-Tab.
 */
export const SelectedElementEscape = Extension.create({
  name: 'markdownSelectedElementEscape',
  // Last of all keymaps: a menu, the names list, the intentions menu take their Esc first, the ready-made view's keys (0.5) arm Tab.
  priority: 0.25,
  addProseMirrorPlugins() {
    const editor = this.editor
    // A field of the element (a source, a title) takes its own Esc (P3), also while an input method composes in it.
    const leave = (view: EditorView, event: KeyboardEvent) => {
      const target = event.target as Element
      if (event.key !== 'Escape' || !editor.isEditable || !(view.state.selection instanceof NodeSelection) || isMarkdownComposition(event, editor)
        || (target !== view.dom && target.matches?.('input, textarea, select, [contenteditable="true"]'))) return false
      caretIntoSelected(view)
      view.focus()
      return true
    }
    return [new Plugin({
      props: {
        // Inside ProseMirror's key handling: it prevents every Esc it sees (by key code 27), so a listener after it never could tell.
        handleKeyDown: leave,
        // A character typed on a block selected whole goes into its text where the caret was (as Enter does, KeyboardFirst.ts):
        // ProseMirror's own keypress would replace the whole block with it. Returning false lets the browser type it at the caret.
        handleKeyPress(view, event) {
          if (editor.isEditable && !event.ctrlKey && !event.metaKey && selectedTextBlock(view)) caretIntoSelected(view)
          return false
        },
      },
      view(view) {
        // A key in an element's own controls never reaches ProseMirror (its node view stops it); a prevented one was taken there.
        const fromControls = (event: KeyboardEvent) => {
          if (event.target !== view.dom && !event.defaultPrevented && leave(view, event)) event.preventDefault()
        }
        view.dom.addEventListener('keydown', fromControls)
        return { destroy: () => view.dom.removeEventListener('keydown', fromControls) }
      },
    })]
  },
})
