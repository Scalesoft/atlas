import type { EditorView } from '@tiptap/pm/view'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { clearDragSource, HOVER_DECO_PLUGIN_KEY, SOURCE_DECO_PLUGIN_KEY, type SetSourceMeta } from './decorations'
import { stopEdgeScroll } from './edgeScroll'
import { createDragCard } from './dragCard'

type Cleanup = (updateDecorations?: boolean) => void
const activeDocuments = new WeakMap<Document, Cleanup>()
const activeViews = new WeakMap<EditorView, Cleanup>()

/** Owns the native drag image and listeners for a single document's active gesture. */
export function startDragSourceMarker(
  view: EditorView,
  blocks: HTMLElement[],
  event: DragEvent,
  selectionRange?: { from: number; to: number },
): void {
  if (!blocks.length || view.isDestroyed) return
  const doc = view.dom.ownerDocument
  const win = doc.defaultView!
  activeDocuments.get(doc)?.()
  const range = selectionRange && selectionRange.to > selectionRange.from ? selectionRange : undefined
  // One transaction for both markers and the selection: each one re-runs every transaction listener while the drag starts.
  const tr = view.state.tr.setMeta(HOVER_DECO_PLUGIN_KEY, { type: 'clear-hover' })
  if (range) tr.setMeta(SOURCE_DECO_PLUGIN_KEY, { type: 'set-source', ...range } satisfies SetSourceMeta)
    .setSelection(NodeRangeSelection.create(tr.doc, range.from, range.to))
  view.dispatch(tr)
  // The caller may hide the event's dataTransfer from later listeners (BlockHandle: Tiptap's dragHandler); `onStart` keeps it.
  const transfer = event.dataTransfer

  let card: ReturnType<typeof createDragCard> | undefined
  let nativeImage: HTMLCanvasElement | undefined
  if (event.dataTransfer) {
    card = createDragCard(view, blocks)
    card.move(event.clientX || 0, event.clientY || 0)
    nativeImage = doc.createElement('canvas')
    nativeImage.width = 1; nativeImage.height = 1
    Object.assign(nativeImage.style, { position: 'fixed', left: '-10px', top: '-10px', pointerEvents: 'none' })
    nativeImage.setAttribute('aria-hidden', 'true')
    doc.body.append(nativeImage)
  }
  const onOver = (event: DragEvent) => card?.move(event.clientX, event.clientY)
  const onKey = (event: KeyboardEvent) => {
    if (event.key !== 'Escape') return
    event.preventDefault(); event.stopImmediatePropagation(); cleanup()
  }

  // Tiptap's handle is outside view.dom. After the handle's own listeners (which may have
  // narrowed the selection), this document-bubble callback restores the range and installs our image last.
  const onStart = (next: DragEvent) => {
    const dataTransfer = next === event ? transfer : next.dataTransfer
    if (!dataTransfer || view.isDestroyed) return
    if (nativeImage) {
      try { dataTransfer.setDragImage(nativeImage, 0, 0) } catch { /* browser retains its default image */ }
    }
    if (!range) return
    try {
      const { selection } = view.state
      if (!(selection instanceof NodeRangeSelection) || selection.from !== range.from || selection.to !== range.to)
        view.dispatch(view.state.tr.setSelection(NodeRangeSelection.create(view.state.doc, range.from, range.to)))
      view.dragging = { slice: view.state.doc.slice(range.from, range.to), move: true }
    } catch { /* selection changed before native dragstart; retain Tiptap's selection */ }
  }
  let finished = false
  const cleanup: Cleanup = (updateDecorations = true) => {
    if (finished) return
    finished = true
    stopEdgeScroll(view)
    doc.removeEventListener('dragstart', onStart)
    doc.removeEventListener('dragend', onEnd, true)
    doc.removeEventListener('drop', onEnd)
    doc.removeEventListener('dragover', onOver, true)
    win.removeEventListener('keydown', onKey, true)
    card?.destroy(); nativeImage?.remove()
    view.dragging = null
    activeDocuments.delete(doc)
    activeViews.delete(view)
    if (updateDecorations && !view.isDestroyed) {
      clearDragSource(view)
    }
  }
  const onEnd = () => cleanup()
  activeDocuments.set(doc, cleanup)
  activeViews.set(view, cleanup)
  doc.addEventListener('dragstart', onStart, { once: true })
  doc.addEventListener('dragover', onOver, true)
  win.addEventListener('keydown', onKey, true)
  doc.addEventListener('dragend', onEnd, true)
  // The editor's drop handler still needs the mapped source decorations.
  doc.addEventListener('drop', onEnd)
}

/** Plugin destruction must not dispatch while ProseMirror is tearing down its views. */
export function stopDragSourceMarker(view: EditorView): void {
  activeViews.get(view)?.(false)
}

/** Cancel a live gesture, including its redraw-safe source decorations. */
export function cancelDragSourceMarker(view: EditorView): void {
  activeViews.get(view)?.()
}
