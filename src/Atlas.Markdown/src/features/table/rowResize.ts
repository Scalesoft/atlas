import type { Editor } from '@tiptap/core'
import { findTablePosition } from './commands'

/** One cancellable gesture per editor; document changes invalidate its captured row position. */
export function createRowResizeSession(editor: Editor, onFinish: () => void) {
  let cancel: (() => void) | null = null

  function start(event: PointerEvent, table: HTMLTableElement, row: HTMLTableRowElement, index: number, handle: HTMLElement) {
    cancel?.()
    if (!editor.isEditable || editor.isDestroyed) return
    const tablePos = findTablePosition(editor.state.doc, table, editor.view)
    const tableNode = tablePos >= 0 ? editor.state.doc.nodeAt(tablePos) : null
    if (!tableNode || index < 0 || index >= tableNode.childCount) return
    let rowPos = tablePos + 1
    for (let i = 0; i < index; i++) rowPos += tableNode.child(i).nodeSize

    const startHeight = row.offsetHeight
    const originalHeight = row.style.height
    let height = startHeight
    const doc = table.ownerDocument
    try { handle.setPointerCapture(event.pointerId) } catch { /* unsupported by synthetic pointers */ }
    doc.body.classList.add('tt-row-resizing')

    const move = (next: PointerEvent) => {
      if (next.pointerId !== event.pointerId) return
      height = Math.max(24, startHeight + next.clientY - event.clientY)
      row.style.height = `${height}px`
    }
    const finish = (commit: boolean) => {
      if (!cancel) return
      cancel = null
      doc.body.classList.remove('tt-row-resizing')
      handle.removeEventListener('pointermove', move)
      handle.removeEventListener('pointerup', up)
      handle.removeEventListener('pointercancel', abort)
      handle.removeEventListener('lostpointercapture', abort)
      try { handle.releasePointerCapture(event.pointerId) } catch { /* capture already released */ }
      row.style.height = originalHeight
      if (commit && height !== startHeight && !editor.isDestroyed && editor.isEditable) {
        editor.chain().setRowHeightAt(rowPos, height).run()
      }
      onFinish()
    }
    const up = (next: PointerEvent) => { if (next.pointerId === event.pointerId) finish(true) }
    const abort = () => finish(false)
    cancel = abort
    handle.addEventListener('pointermove', move)
    handle.addEventListener('pointerup', up)
    handle.addEventListener('pointercancel', abort)
    handle.addEventListener('lostpointercapture', abort)
  }

  return { start, cancel: () => cancel?.(), get active() { return cancel !== null } }
}
