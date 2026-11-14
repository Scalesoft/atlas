import type { Editor } from '@tiptap/core'
import { findTablePosition, runTableAxisCommand, tableAxisRange } from './commands'
import { moveColumn, reorderedTable } from './columnOrder'
import { createTableAxisDragPreview } from './axisDragPreview'
import { moveRow, reorderedRows } from './rowOrder'
import { canMoveBlocks } from '../drag/movePolicy'
import { registerMarkdownOverlayForElement } from '../../core/overlay'
import { dragTargetTolerance } from '../drag/motion'

/** One pointer gesture per view, with a click threshold and complete cancellation. */
export function createTableAxisDragSession(editor: Editor, onFinish: () => void, axis: 'column' | 'row' = 'column') {
  let cancel: (() => void) | null = null
  const animations = new Set<Animation>()
  const stopAnimations = () => { animations.forEach(animation => animation.cancel()); animations.clear() }

  function start(event: PointerEvent, table: HTMLTableElement, from: number, handle: HTMLElement) {
    if (event.button !== 0 || !canMoveBlocks(editor)) return
    cancel?.()
    stopAnimations()
    const tablePos = findTablePosition(editor.state.doc, table, editor.view)
    const model = tablePos >= 0 ? editor.state.doc.nodeAt(tablePos) : null
    // Rails are physical header cells. A spanning header does not represent one column.
    if (!model || (axis === 'column' && Array.from(table.rows[0].cells).some(cell => cell.colSpan !== 1))) return
    const reorder = axis === 'column' ? reorderedTable : reorderedRows
    const commitMove = axis === 'column' ? moveColumn : moveRow
    const items = axis === 'column' ? Array.from(table.rows[0].cells) : Array.from(table.rows)
    const range = tableAxisRange(editor.state, tablePos, axis, from)
    from = range.from
    const count = range.count
    const destinations = new Set(items.flatMap((_, to) => reorder(model, from, to, count) ? [to] : []))
    if (!destinations.size) return
    const doc = table.ownerDocument
    const win = doc.defaultView!
    let preview: ReturnType<typeof createTableAxisDragPreview> | undefined
    let releaseOverlay: (() => void) | undefined
    let to = from
    let inside = true
    try { handle.setPointerCapture(event.pointerId) } catch { /* synthetic pointer */ }

    const move = (next: PointerEvent) => {
      if (next.pointerId !== event.pointerId) return
      const dx = axis === 'column' ? next.clientX - event.clientX : next.clientY - event.clientY
      if (!preview) {
        if (Math.abs(dx) < 5) return
        if (count === 1) runTableAxisCommand(editor, tablePos, axis, from, 'select')
        preview = createTableAxisDragPreview(editor, table, tablePos, from, axis, count)
        releaseOverlay = registerMarkdownOverlayForElement(editor.view.dom, handle)
      }
      next.preventDefault()
      const items = preview.items
      const bounds = table.getBoundingClientRect()
      inside = next.clientY >= bounds.top - 72 && next.clientY <= bounds.bottom + 48
        && next.clientX >= bounds.left - 48 && next.clientX <= bounds.right + 48
      let candidate = items.filter((rect, index) => (index < from || index >= from + count) && (axis === 'column' ? next.clientX > rect.left + rect.width / 2 : next.clientY > rect.top + rect.height / 2)).length
      const others = items.filter((_, index) => index < from || index >= from + count)
      if (Math.abs(candidate - to) === 1) {
        const crossed = others[Math.min(candidate, to)]
        const pointer = axis === 'column' ? next.clientX : next.clientY
        const midpoint = crossed && (axis === 'column' ? crossed.left + crossed.width / 2 : crossed.top + crossed.height / 2)
        const band = crossed ? dragTargetTolerance(axis === 'column' ? crossed.width : crossed.height) : 0
        if (midpoint !== undefined && Math.abs(pointer - midpoint) < band) candidate = to
      }
      to = inside && destinations.has(candidate) ? candidate : from
      preview.update(to, next.clientX, next.clientY)
    }
    const suppressClick = (click: MouseEvent) => {
      click.preventDefault()
      click.stopImmediatePropagation()
    }
    const finish = (commit: boolean, animate = true) => {
      if (!cancel) return
      cancel = null
      doc.removeEventListener('pointermove', move, true)
      doc.removeEventListener('pointerup', up, true)
      doc.removeEventListener('pointercancel', abort, true)
      doc.removeEventListener('keydown', keydown, true)
      doc.removeEventListener('scroll', abort, true)
      win.removeEventListener('blur', abort)
      win.removeEventListener('resize', abort)
      handle.removeEventListener('lostpointercapture', abort)
      try { handle.releasePointerCapture(event.pointerId) } catch { /* already released */ }
      releaseOverlay?.()
      if (preview) {
        // Browsers synthesize a click after pointerup. Never let it reselect the old index.
        handle.addEventListener('click', suppressClick, { capture: true, once: true })
        const before = preview.clear()
        const moved = commit && inside && commitMove(editor, tablePos, from, to, count)
        if (animate && !editor.isDestroyed && editor.isEditable) {
          for (const animation of preview.settle(before, moved ? to : from)) {
            animations.add(animation)
            void animation.finished.then(() => animations.delete(animation), () => animations.delete(animation))
          }
        }
        if (commit && !editor.isDestroyed) editor.view.focus()
        onFinish()
      }
    }
    const up = (next: PointerEvent) => { if (next.pointerId === event.pointerId) finish(true) }
    const abort = () => finish(false, false)
    const keydown = (key: KeyboardEvent) => {
      if (key.key === 'Escape') { key.preventDefault(); key.stopImmediatePropagation(); finish(false) }
    }
    cancel = abort
    doc.addEventListener('pointermove', move, true)
    doc.addEventListener('pointerup', up, true)
    doc.addEventListener('pointercancel', abort, true)
    doc.addEventListener('keydown', keydown, true)
    doc.addEventListener('scroll', abort, true)
    win.addEventListener('blur', abort)
    win.addEventListener('resize', abort)
    handle.addEventListener('lostpointercapture', abort)
  }
  const reset = () => { cancel?.(); stopAnimations() }
  return { start, cancel: reset, destroy: reset, get active() { return cancel !== null } }
}
