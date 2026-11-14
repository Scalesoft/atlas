import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import { createDragDecorationPlugins, clearDragSource, SOURCE_DECO_PLUGIN_KEY } from '../features/drag/decorations'
import { updateEdgeScroll, stopEdgeScroll } from '../features/drag/edgeScroll'
import { cancelDragSourceMarker, stopDragSourceMarker } from '../features/drag/sourceSession'
import { blockDragRange, commitBlockDrop, measureBlocks, resolveBlockDrop, type BlockRect } from '../features/drag/blockDrop'
import { createBlockDropPreview } from '../features/drag/blockDropPreview'

export { markSectionHover, clearSectionHover } from '../features/drag/decorations'
export { startDragSourceMarker } from '../features/drag/sourceSession'

/**
 * A block drag hit-tests the pointer's height alone, so it drops over the text and over the gutter beside it, where the grips
 * stand (the paper's padding, outside view.dom).
 */
function inDropZone(view: EditorView, target: EventTarget | null): boolean {
  return target instanceof view.dom.ownerDocument.defaultView!.Node && (view.dom.closest('.paper-inner') ?? view.dom).contains(target)
}

/** Whole-block gestures share one model boundary for preview and commit. */
export const DragPreview = Extension.create({
  name: 'dragPreview',
  addProseMirrorPlugins() {
    const editor = this.editor
    let preview: ReturnType<typeof createBlockDropPreview> | undefined
    let pointerY: number | null = null
    let targetPosition: number | undefined
    // The blocks' geometry for the current drag; scrolling keeps it valid (it is relative to the editor).
    let blocks: BlockRect[] | undefined
    return [
      ...createDragDecorationPlugins(),
      new Plugin({
        key: new PluginKey('dragPreview'),
        view: view => {
          let cancellationPending = false
          let frame = 0
          preview = createBlockDropPreview(view)
          const document = view.dom.ownerDocument
          const win = document.defaultView!
          const clear = () => {
            pointerY = null; targetPosition = undefined; blocks = undefined
            redraws?.disconnect()
            if (frame) { win.cancelAnimationFrame(frame); frame = 0 }
            preview?.clear(); stopEdgeScroll(view)
          }
          // One hit test and one preview write per frame, however many drag and scroll events arrive in it.
          const render = () => {
            frame = 0
            if (pointerY === null || !blockDragRange(view)) return
            updateEdgeScroll(view, pointerY)
            const target = resolveBlockDrop(view, pointerY, targetPosition, geometry())
            targetPosition = target?.position
            preview?.update(target, geometry())
          }
          const refresh = () => { if (pointerY !== null && !frame) frame = win.requestAnimationFrame(render) }
          const remeasure = () => { if (blocks) { blocks = undefined; refresh() } }
          // A redraw replaces top-level elements without changing the document: the cached boxes would point at detached ones.
          // Watched only while a drag holds boxes, not for every edit of the editor's life.
          const redraws = typeof win.MutationObserver === 'function' ? new win.MutationObserver(remeasure) : undefined
          // A block can change its height with no document change (a NodeView hydrating, an image or a font loading, a
          // chart replacing its skeleton) and the editor's width with the window: the editor's own box changes with them.
          const resizes = typeof win.ResizeObserver === 'function' ? new win.ResizeObserver(remeasure) : undefined
          resizes?.observe(view.dom)
          const geometry = () => {
            if (!blocks) { blocks = measureBlocks(view); redraws?.observe(view.dom, { childList: true }) }
            return blocks
          }
          // Ahead of the view's own listeners: ProseMirror's drop cursor maps every event to a document position
          // (`posAtCoords`) and measures it, although a block drag hides that cursor and draws its own guide.
          const dragover = (event: DragEvent) => {
            if (!view.editable || !blockDragRange(view)) return
            if (!inDropZone(view, event.target)) {
              // Out of the editor through its gutter, where no dragleave of the editor's own fires.
              if (pointerY !== null) { pointerY = null; targetPosition = undefined; preview?.clear(); stopEdgeScroll(view) }
              return
            }
            event.stopPropagation()
            event.preventDefault()
            if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
            pointerY = event.clientY
            refresh()
          }
          // Capture, as dragover: a drop over a NodeView that stops events (a block's header, a module's buttons) never reaches
          // the view's handlers, although the guide promised a move there. No stopPropagation: the bubble cleanups still run.
          const drop = (event: DragEvent) => {
            if (!view.editable || !inDropZone(view, event.target) || !blockDragRange(view)) return
            event.preventDefault()
            // The same untransformed boxes as the preview, before it is cleared.
            const target = resolveBlockDrop(view, event.clientY, targetPosition, geometry())
            preview?.clear(true)
            stopEdgeScroll(view)
            const landed = target && commitBlockDrop(editor, target.position)
            view.dragging = null
            clearDragSource(view)
            if (!landed) return
            preview?.settle(landed.from, landed.to)
            // The focus left on the grip would keep Ctrl+Z from the editor: the caret is at the moved block, undo takes the move back.
            // Once the redrawn page has been laid out and painted (the second frame): focusing measures it, in the drop a second layout.
            win.requestAnimationFrame(() => win.requestAnimationFrame(() => { if (!view.isDestroyed) view.focus() }))
          }
          document.addEventListener('dragover', dragover, true)
          document.addEventListener('drop', drop, true)
          document.addEventListener('dragend', clear, true)
          document.addEventListener('drop', clear)
          document.addEventListener('scroll', refresh, true)
          return {
            update(_view, previous) {
              view.dom.closest('.md-editor')?.toggleAttribute('data-block-dragging', view.dom.dataset.blockDragging === 'true')
              if (!view.editable && blockDragRange(view) && !cancellationPending) {
                cancellationPending = true
                // Do not dispatch while ProseMirror is updating its plugin views.
                queueMicrotask(() => {
                  cancellationPending = false
                  if (!view.isDestroyed) cancelDragSourceMarker(view)
                })
              }
              if (!view.editable || !blockDragRange(view)) clear()
              else if (previous.doc !== view.state.doc) remeasure()
            },
            destroy() {
              view.dom.closest('.md-editor')?.removeAttribute('data-block-dragging')
              clear()
              preview?.destroy()
              stopDragSourceMarker(view)
              resizes?.disconnect()
              document.removeEventListener('dragover', dragover, true)
              document.removeEventListener('drop', drop, true)
              document.removeEventListener('dragend', clear, true)
              document.removeEventListener('drop', clear)
              document.removeEventListener('scroll', refresh, true)
            },
          }
        },
        props: {
          attributes: state => ({ 'data-block-dragging': String((SOURCE_DECO_PLUGIN_KEY.getState(state)?.find().length ?? 0) > 0) }),
          handleDOMEvents: {
            // Other drags (text, files); a block drag is handled by the document listener above and never arrives here.
            dragover: (view, event) => { updateEdgeScroll(view, event.clientY); return false },
            dragleave: (view, event) => {
              if (event.relatedTarget instanceof view.dom.ownerDocument.defaultView!.Node && view.dom.contains(event.relatedTarget)) return false
              // A block drag goes on in the gutter; the document listener ends it once the pointer leaves the editor.
              if (blockDragRange(view) && inDropZone(view, event.relatedTarget)) return false
              stopEdgeScroll(view)
              pointerY = null
              targetPosition = undefined
              preview?.clear()
              return false
            },
          },
        },
      }),
    ]
  },
})
