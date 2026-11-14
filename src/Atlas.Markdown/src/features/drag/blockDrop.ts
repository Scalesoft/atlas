import type { EditorView } from '@tiptap/pm/view'
import type { Editor } from '@tiptap/core'
import { canMoveBlocks } from './movePolicy'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { Selection } from '@tiptap/pm/state'
import { SOURCE_DECO_PLUGIN_KEY, type ClearSourceMeta } from './decorations'
import { dragTargetTolerance } from './motion'
import { dragStrings } from '../../ui/strings/drag'

export interface BlockDropTarget { position: number; y: number; left: number; width: number; label: string }

export function blockDragRange(view: EditorView): { from: number; to: number } | null {
  const marks = SOURCE_DECO_PLUGIN_KEY.getState(view.state)?.find() ?? []
  if (!marks.length) return null
  const from = Math.min(...marks.map(mark => mark.from))
  const to = Math.max(...marks.map(mark => mark.to))
  return view.state.doc.resolve(from).depth === 0 && view.state.doc.resolve(to).depth === 0 ? { from, to } : null
}

/** A top-level block's untransformed box, relative to the editor's top edge, so scrolling leaves it valid. */
export interface BlockRect { from: number; to: number; top: number; bottom: number; element: HTMLElement }

/** Layout reads for a whole drag: once at its start, again only after a resize, a redraw or a document change. */
export function measureBlocks(view: EditorView): BlockRect[] {
  const blocks: BlockRect[] = []
  const win = view.dom.ownerDocument.defaultView!
  const origin = view.dom.getBoundingClientRect().top
  let leading: number | undefined
  view.state.doc.forEach((node, from) => {
    const element = view.nodeDOM(from)
    if (!(element instanceof win.HTMLElement)) return
    const rect = element.getBoundingClientRect()
    // A block a folded heading or a filter hides has no box (0, 0). It joins the visible block before it, so the guide
    // never sits at the window's top and a drop after a folded section lands after its hidden tail, never inside it.
    // Hidden blocks at the very start join the first visible one: "insert at start" lands before them.
    if (!rect.width && !rect.height) {
      const previous = blocks.at(-1)
      if (previous) previous.to = from + node.nodeSize
      else leading ??= from
      return
    }
    const start = !blocks.length && leading !== undefined ? leading : from
    // Preview transforms must not move the hit areas, otherwise a stationary
    // pointer repeatedly flips the target as the two neighbours move apart.
    const transform = win.getComputedStyle(element).transform
    const shift = !transform || transform === 'none' ? 0 : new win.DOMMatrixReadOnly(transform).m42
    blocks.push({ from: start, to: from + node.nodeSize, top: rect.top - shift - origin, bottom: rect.bottom - shift - origin, element })
  })
  return blocks
}

/** The first block whose middle lies below `y` (`blocks.length` past the last one): a binary search over cached rects. */
export function blockIndexAt(blocks: ReadonlyArray<{ top: number; bottom: number }>, y: number): number {
  let low = 0, high = blocks.length
  while (low < high) {
    const middle = (low + high) >> 1
    if (y < (blocks[middle]!.top + blocks[middle]!.bottom) / 2) high = middle
    else low = middle + 1
  }
  return low
}

/** Use model boundaries even over text, atom nodes, or the margins between them. */
export function resolveBlockDrop(view: EditorView, clientY: number, previousPosition?: number, blocks: readonly BlockRect[] = measureBlocks(view)): BlockDropTarget | null {
  const source = blockDragRange(view)
  if (!source || !blocks.length) return null
  // The only layout read per pointer move: the editor's own box, which follows any scrolling.
  const rect = view.dom.getBoundingClientRect()
  const y = clientY - rect.top
  let index = blockIndexAt(blocks, y)
  // A small dead band around the switching threshold absorbs hand tremor.
  const previousIndex = previousPosition === blocks.at(-1)?.to ? blocks.length
    : blocks.findIndex(block => block.from === previousPosition)
  if (previousIndex >= 0 && Math.abs(index - previousIndex) === 1) {
    const crossed = blocks[Math.min(index, previousIndex)]!
    if (Math.abs(y - (crossed.top + crossed.bottom) / 2) < dragTargetTolerance(crossed.bottom - crossed.top)) index = previousIndex
  }
  const before = blocks[index - 1]
  const after = blocks[index]
  const position = after?.from ?? before!.to
  if (position >= source.from && position <= source.to) return null
  const boundary = before && after ? (before.bottom + after.top) / 2 : after?.top ?? before!.bottom
  const nextNode = after ? view.state.doc.nodeAt(after.from) : null
  const label = !before ? dragStrings.insertAtStart : !after ? dragStrings.insertAtEnd
    : nextNode?.type.name === 'heading' ? dragStrings.beforeSectionLabel(nextNode.textContent.slice(0, 50)) : dragStrings.insertHere
  return { position, y: boundary + rect.top, left: rect.left, width: rect.width, label }
}

/** Moves the dragged blocks to `position` as one undo step; returns where they landed. */
export function commitBlockDrop(editor: Editor, position: number): { from: number; to: number } | null {
  if (!canMoveBlocks(editor)) return null
  const view = editor.view
  const source = blockDragRange(view)
  if (!source || !view.editable || position >= source.from && position <= source.to) return null
  const content = view.state.doc.slice(source.from, source.to).content
  const tr = view.state.tr.delete(source.from, source.to)
  const destination = tr.mapping.map(position)
  tr.insert(destination, content)
  // The caret at the moved block, not a block selection: Tiptap's drop handler then dispatches no selection restore of its own.
  // One transaction also ends the source marker and hides the handle (no repositioning, which measures the whole redrawn page).
  // No scrollIntoView: the blocks land under the pointer, and scrolling to them would measure the redrawn page in the drop.
  tr.setSelection(Selection.near(tr.doc.resolve(destination)))
    .setMeta(SOURCE_DECO_PLUGIN_KEY, { type: 'clear-source' } satisfies ClearSourceMeta)
    .setMeta('hideDragHandle', true)
  dispatchMarkdownHistoryOperation(editor, tr)
  return { from: destination, to: destination + content.size }
}
