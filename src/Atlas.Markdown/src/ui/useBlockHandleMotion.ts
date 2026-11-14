import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'
import type { Editor } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import type { Transaction } from '@tiptap/pm/state'
import { computePosition, type ComputePositionConfig } from '@floating-ui/dom'
import { markSectionHover, clearSectionHover, HOVER_DECO_PLUGIN_KEY } from '../features/drag/decorations'
import { blockRangeLabel, collectHoverSectionRanges } from '../features/drag/blockSelection'
import { dragStrings } from './strings/drag'

/**
 * Horizontal correction that keeps the block controls inside the editor surface. The controls sit left of the block
 * (`left-start`); a gutter narrower than the control column (a host that trims the padding, a very narrow viewport)
 * would push them past the surface's left edge. The returned shift is ≥ 0 and in client pixels.
 */
export function blockControlsInset(referenceLeft: number, floatingWidth: number, surfaceLeft: number): number {
  return Math.max(0, surfaceLeft - (referenceLeft - floatingWidth))
}

/** Owns handle motion, native-selection synchronization and every associated subscription. */
export function useBlockHandleMotion(editor: Editor, row: Ref<HTMLElement | null>) {
  const ownerDocument = editor.view.dom.ownerDocument
  const ownerWindow = ownerDocument.defaultView
  const requestFrame = (callback: FrameRequestCallback) => (ownerWindow ?? globalThis).requestAnimationFrame(callback)
  const cancelFrame = (id: number) => (ownerWindow ?? globalThis).cancelAnimationFrame(id)
  const DomNode = ownerWindow?.Node ?? globalThis.Node
  const DomElement = ownerWindow?.HTMLElement ?? globalThis.HTMLElement
  const handle = () => row.value?.parentElement ?? null
  let pressed = false
  let cancelled = false
  let frame = 0
  let hoverFrame = 0
  let hoverPending = false
  let contentPointerDown = false
  let pointerPosition: { x: number; y: number } | null = null
  let preserveHoverTarget = false
  let observer: ResizeObserver | undefined
  const rangeLabel = ref(dragStrings.moveBlockLabel)
  const positionConfig: Partial<ComputePositionConfig> = {
    placement: 'left-start',
    middleware: [{ name: 'block-motion', fn({ x, y, elements, rects }) {
      const surface = editor.view.dom.closest('.md-editor')
      if (surface) x += blockControlsInset(elements.reference.getBoundingClientRect().left, rects.floating.width, surface.getBoundingClientRect().left)
      if (!pressed) elements.floating.style.transform = `translate3d(${x}px, ${y}px, 0)`
      return { x: 0, y: 0 }
    } }],
  }

  function updateGeometry(recompute: unknown = true) {
    const element = handle()
    if (!element) return
    preserveHoverTarget = true
    element.dataset.motion = 'instant'
    if (recompute === false || frame) return
    frame = requestFrame(() => {
      frame = 0
      if (editor.isDestroyed || dragTargetPos < 0) return
      const anchor = editor.view.nodeDOM(dragTargetPos)
      if (anchor instanceof DomElement) void computePosition(anchor, element, positionConfig)
    })
  }
  function onPointerDown() {
    cancelHover()
    pressed = true
    cancelled = false
    const element = handle()
    if (element) element.dataset.motion = 'instant'
  }
  function releasePointer() { pressed = false }
  function onFocusOut() {
    if (!handle()?.contains(ownerDocument.activeElement)) releasePointer()
    hoverPending = true
    scheduleHover()
  }
  // Tracked from events: asking the browser for `:hover` forced a style recalculation on every keystroke.
  let overHandle = false
  function onHandleHover(event?: Event) { overHandle = event?.type === 'pointerenter'; hoverPending = true; scheduleHover() }
  function onTransaction({ transaction }: { transaction: Transaction }) {
    if (transaction.selectionSet) {
      rangeLabel.value = blockRangeLabel(editor.state, dragTargetPos)
      hoverPending = true
      scheduleHover()
    }
    if (!transaction.docChanged) return
    cancelHover()
    // Cancel an in-flight gesture rather than applying its old position to a new block.
    if (pressed) {
      cancelled = true
      pressed = false
      dragTargetPos = -1
    }
    // The grip sits at the target block's top-left: typing inside or after the block cannot move it, only an edit before it can.
    let editedBefore = false
    transaction.mapping.maps.forEach(map => map.forEach(oldStart => { if (oldStart < dragTargetPos) editedBefore = true }))
    updateGeometry(editedBefore)
  }
  function cancelHover() {
    if (hoverFrame) cancelFrame(hoverFrame)
    hoverFrame = 0
    hoverPending = false
  }
  function scheduleHover() {
    if (!hoverPending || hoverFrame || contentPointerDown) return
    hoverFrame = requestFrame(() => {
      hoverFrame = 0
      const e = editor
      if (e.isDestroyed || contentPointerDown || pressed) return
      // A metadata-only dispatch also redraws PM's selection. Native mouse
      // selection can be newer than editor.state until selectionchange runs.
      // This applies to clearing hover too. Leave it alone; selectionchange /
      // the resulting transaction retries us.
      const selection = e.view.dom.ownerDocument.getSelection()
      if (selection?.anchorNode && selection.focusNode && e.view.dom.contains(selection.anchorNode)
        && e.view.dom.contains(selection.focusNode)
        && (e.view.posAtDOM(selection.anchorNode, selection.anchorOffset) !== e.state.selection.anchor
          || e.view.posAtDOM(selection.focusNode, selection.focusOffset) !== e.state.selection.head)) return
      // Moving across document text only positions the grip. Preview the group
      // when the user actually enters the controls (or reaches them by keyboard).
      if (!overHandle && !handle()?.contains(ownerDocument.activeElement)) {
        hoverPending = false
        if (HOVER_DECO_PLUGIN_KEY.getState(e.state)?.find().length) clearSectionHover(e.view)
        return
      }
      hoverPending = false
      const node = dragTargetPos >= 0 ? e.state.doc.nodeAt(dragTargetPos) : null
      const ranges = node ? collectHoverSectionRanges(e, dragTargetPos, node) : []
      if (!node || !ranges.length) clearSectionHover(e.view)
      else markSectionHover(e.view, ranges, { headingFirst: node.type.name === 'heading' })
    })
  }
  function onContentPointerDown(event: PointerEvent) {
    if (!(event.target instanceof DomNode) || !editor.view.dom.contains(event.target)) return
    contentPointerDown = true
    if (hoverFrame) cancelFrame(hoverFrame)
    hoverFrame = 0
  }
  function onContentPointerUp() {
    contentPointerDown = false
    scheduleHover()
  }
  function onMouseMove(event: MouseEvent) {
    if (!handle()?.contains(event.target as Node | null)) overHandle = false
    const moved = !pointerPosition || pointerPosition.x !== event.clientX || pointerPosition.y !== event.clientY
    pointerPosition = { x: event.clientX, y: event.clientY }
    if (moved || event.buttons !== 0) {
      preserveHoverTarget = false
      return
    }
    // WebKit synthesizes mousemove at unchanged coordinates after scrolling/reflow.
    // Keep the active block anchored until the pointer actually moves; otherwise the
    // drag-handle plugin starts a glide to the block that moved under a stationary cursor.
    if (preserveHoverTarget && dragTargetPos >= 0 && event.target instanceof DomNode
      && editor.view.dom.contains(event.target)) event.stopPropagation()
  }
  onMounted(() => {
    ownerDocument.addEventListener('mousemove', onMouseMove, true)
    ownerDocument.addEventListener('pointerdown', onContentPointerDown, true)
    ownerDocument.addEventListener('pointerup', onContentPointerUp)
    ownerDocument.addEventListener('pointercancel', onContentPointerUp)
    ownerDocument.addEventListener('selectionchange', scheduleHover)
    ownerDocument.addEventListener('pointerup', releasePointer)
    ownerDocument.addEventListener('pointercancel', releasePointer)
    ownerDocument.addEventListener('dragend', releasePointer)
    ownerDocument.addEventListener('scroll', updateGeometry, true)
    ownerWindow?.addEventListener('resize', updateGeometry)
    editor.on('transaction', onTransaction)
    const Observer = ownerWindow?.ResizeObserver ?? globalThis.ResizeObserver
    observer = new Observer(updateGeometry)
    observer.observe(editor.view.dom)
    // Adding/removing the side plus changes the floating width, not the editor.
    // Reanchor even when the pointer stays still over the same block.
    if (row.value) observer.observe(row.value)
  })
  onBeforeUnmount(() => {
    ownerDocument.removeEventListener('mousemove', onMouseMove, true)
    ownerDocument.removeEventListener('pointerdown', onContentPointerDown, true)
    ownerDocument.removeEventListener('pointerup', onContentPointerUp)
    ownerDocument.removeEventListener('pointercancel', onContentPointerUp)
    ownerDocument.removeEventListener('selectionchange', scheduleHover)
    cancelHover()
    ownerDocument.removeEventListener('pointerup', releasePointer)
    ownerDocument.removeEventListener('pointercancel', releasePointer)
    ownerDocument.removeEventListener('dragend', releasePointer)
    ownerDocument.removeEventListener('scroll', updateGeometry, true)
    ownerWindow?.removeEventListener('resize', updateGeometry)
    editor.off('transaction', onTransaction)
    observer?.disconnect()
    if (frame) cancelFrame(frame)
    if (!editor.isDestroyed) clearSectionHover(editor.view)
  })

  let dragTargetPos = -1
  let dragTargetSize = 0
  // Reactive — drives the v-show / CSS gate that hides the block drag handle
  // when the cursor sits over a table. Tables ship their own row/col rails +
  // insert/delete chips in the same gutter; without this the drag-handle's
  // "+ ::" button stack overlaps the row chip area and the row chips end up
  // intercepting clicks meant for the drag-handle (and vice-versa). Whole-table
  // reorder is rare enough that disabling the handle for `table` blocks is a
  // cleaner win than fighting both for the same pixels.
  const dragTargetType = ref<string | null>(null)

  function onDragNodeChange({ pos, node }: { pos: number; node: ProseMirrorNode | null }) {
    if (pressed || (pos < 0 && handle()?.contains(ownerDocument.activeElement))) return
    const element = handle()
    if (element) element.dataset.motion = dragTargetPos >= 0 && pos >= 0 && pos !== dragTargetPos ? 'glide' : 'instant'
    if (element) element.inert = pos < 0 || node?.type.name === 'table'
    dragTargetPos = pos
    dragTargetSize = node?.nodeSize ?? 0
    dragTargetType.value = node?.type?.name ?? null
    rangeLabel.value = blockRangeLabel(editor.state, pos)
    // Drive the pre-drag section-hover tint so the user sees the unit the
    // handle will pick up. Heading → entire section (heading + content blocks
    // until the next same-or-shallower heading). Any other top-level block
    // tints just itself. pos = -1 means the handle was hidden (mouseleave) —
    // clear every mark.
    hoverPending = true
    scheduleHover()
  }

  return {
    positionConfig, dragTargetType, rangeLabel, onPointerDown, onFocusOut, onDragNodeChange, onHandleHover,
    releasePointer, cancelHover, updateGeometry,
    get cancelled() { return cancelled },
    get targetPos() { return dragTargetPos },
    get targetSize() { return dragTargetSize },
    resolveTarget() {
      if (dragTargetPos >= 0) return dragTargetPos
      try {
        const position = editor.state.selection.$from.depth ? editor.state.selection.$from.before(1) : editor.state.selection.from
        const node = editor.state.doc.nodeAt(position)
        if (node) { dragTargetPos = position; dragTargetSize = node.nodeSize }
      } catch { /* no top-level selection ancestor */ }
      return dragTargetPos
    },
  }
}
