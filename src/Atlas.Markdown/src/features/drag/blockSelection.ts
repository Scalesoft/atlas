import type { Editor } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import { AllSelection, NodeSelection, type EditorState } from '@tiptap/pm/state'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { dragStrings } from '../../ui/strings/drag'

const selectionAnchors = new WeakMap<Editor, { from: number; to: number; selection: EditorState['selection'] }>()

/** One source range for hover, keyboard reorder and native grip dragging. */
export function blockRangeForHandle(state: EditorState, position: number): { from: number; to: number } | null {
  const { doc, selection } = state
  if (position < 0 || position >= doc.content.size || doc.resolve(position).depth !== 0) return null
  const node = doc.nodeAt(position)
  if (!node) return null
  if (selection instanceof AllSelection) return { from: 0, to: doc.content.size }
  if (!selection.empty && selection instanceof NodeRangeSelection) {
    const start = selection.$from.depth ? selection.$from.before(1) : selection.from
    // Ending at the beginning of the next paragraph does not select its content.
    const end = !selection.$to.depth ? selection.to
      : selection.to === selection.$to.start(1) ? selection.$to.before(1) : selection.$to.after(1)
    const first = doc.nodeAt(start)
    if (first && start <= position && position < end) return { from: start, to: end }
  }
  return { from: position, to: node.type.name === 'heading'
    ? computeSectionEnd(doc, position, node.attrs.level ?? 1) : position + node.nodeSize }
}

/** A deliberate grip selection, independent of a text selection left by editing. */
export function selectBlockRange(editor: Editor, position: number, extend = false, section = true, forceRange = false): void {
  const { doc, selection } = editor.state
  if (!editor.isEditable || position < 0 || position >= doc.content.size || doc.resolve(position).depth !== 0) return
  const node = doc.nodeAt(position)!
  const end = section && node.type.name === 'heading' ? computeSectionEnd(doc, position, node.attrs.level ?? 1) : position + node.nodeSize
  let from = position, to = end
  const saved = selectionAnchors.get(editor)
  let anchor = { from, to }
  if (extend && (selection instanceof NodeRangeSelection || selection instanceof NodeSelection)) {
    anchor = saved?.selection === selection ? saved : { from: selection.from, to: selection.to }
    from = Math.min(from, anchor.from)
    to = Math.max(to, anchor.to)
  }
  // A table is one element too (allowTableNodeSelection): its grip selects it whole, with the shared look, menu and Esc step. A callout
  // is text: its grip selects it as a block, with the block toolbar (move, Změnit na…); its type stays on its label.
  const component = node.isAtom || node.type.name === 'table'
  editor.view.dispatch(editor.state.tr.setSelection(!extend && !forceRange && component && NodeSelection.isSelectable(node)
    ? NodeSelection.create(doc, position) : NodeRangeSelection.create(doc, from, to, 0)))
  selectionAnchors.set(editor, { ...anchor, selection: editor.state.selection })
}

export function blockRangeLabel(state: EditorState, position: number): string {
  const range = blockRangeForHandle(state, position)
  if (!range) return dragStrings.moveBlockLabel
  const count = state.doc.slice(range.from, range.to).content.childCount
  const first = state.doc.nodeAt(range.from)
  const section = first?.type.name === 'heading' && range.to === computeSectionEnd(state.doc, range.from, first.attrs.level ?? 1)
  const quantity = dragStrings.blockCountLabel(count)
  return section && count > 1 ? dragStrings.moveSectionLabel(quantity) : dragStrings.moveQuantityLabel(quantity)
}

/**
 * Tint the same selected siblings or heading section that the grip will move.
 */
export function collectHoverSectionRanges(
  e: Editor,
  pos: number,
  node: { type: { name: string }; attrs?: { level?: number }; nodeSize: number },
): Array<{ from: number; to: number }> {
  const out: Array<{ from: number; to: number }> = []
  const range = blockRangeForHandle(e.state, pos)
  if (range) {
    let cur = range.from
    while (cur < range.to) {
      const child = e.state.doc.nodeAt(cur)
      if (!child) break
      out.push({ from: cur, to: cur + child.nodeSize })
      cur += child.nodeSize
    }
    return out
  }
  out.push({ from: pos, to: pos + node.nodeSize })
  return out
}

/**
 * Returns the position where the section starting at `headingPos` ends —
 * i.e. the position of the next top-level heading at level <= `level`, or
 * the end of the document. Used to drag a heading + its content as a unit.
 */
export function computeSectionEnd(doc: Node, headingPos: number, level: number): number {
  let pos = 0
  let foundOurHeading = false
  for (let i = 0; i < doc.content.childCount; i++) {
    const child = doc.content.child(i)
    const childStart = pos
    if (foundOurHeading && child.type.name === 'heading') {
      const childLevel = child.attrs.level ?? 99
      if (childLevel <= level) return childStart
    }
    if (childStart === headingPos) foundOurHeading = true
    pos = childStart + child.nodeSize
  }
  return doc.content.size
}

export function collectSourceBlocks(e: Editor, dragTargetPos: number, range?: { from: number; to: number }): HTMLElement[] {
  const sel = range ?? e.state.selection
  const out: HTMLElement[] = []
  // The given range, else a NodeRangeSelection: every top-level block it covers.
  if (range || sel instanceof NodeRangeSelection) {
    let pos = sel.from
    while (pos < sel.to) {
      const node = e.state.doc.nodeAt(pos)
      if (!node) break
      const dom = e.view.nodeDOM(pos)
      if (dom instanceof HTMLElement) out.push(dom)
      pos += node.nodeSize
    }
    return out
  }
  // NodeSelection covers a single block.
  if (sel instanceof NodeSelection) {
    const dom = e.view.nodeDOM(sel.from)
    if (dom instanceof HTMLElement) out.push(dom)
    return out
  }
  // Fallback to the hovered block tracked by the drag handle.
  if (dragTargetPos >= 0) {
    const dom = e.view.nodeDOM(dragTargetPos)
    if (dom instanceof HTMLElement) out.push(dom)
  }
  return out
}
