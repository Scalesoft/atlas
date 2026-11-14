import type { Editor } from '@tiptap/core'
import { TableMap } from '@tiptap/pm/tables'
import { columnOrder } from './columnOrder'
import { createDragCard } from '../drag/dragCard'
import { dragMotion } from '../drag/motion'
import { tableStrings } from '../../ui/strings/table'

/** Only temporary DOM presentation; the document changes once, on drop. */
export function createTableAxisDragPreview(editor: Editor, table: HTMLTableElement, tablePos: number, from: number, axis: 'column' | 'row' = 'column', count = 1) {
  const doc = table.ownerDocument
  const model = editor.state.doc.nodeAt(tablePos)!
  const map = TableMap.get(model)
  const vertical = axis === 'row'
  const coordinate = vertical ? 'top' : 'left'
  const size = vertical ? 'height' : 'width'
  const translate = vertical ? 'translateY' : 'translateX'
  const items = (vertical ? Array.from(table.rows) : Array.from(table.rows[0].cells)).map(cell => cell.getBoundingClientRect())
  const entries: Array<{ element: HTMLElement; index: number }> = []
  model.forEach((row, rowOffset) => row.forEach((_cell, cellOffset) => {
    const pos = rowOffset + cellOffset + 1
    const element = editor.view.nodeDOM(tablePos + 1 + pos) as HTMLElement
    entries.push({ element, index: map.findCell(pos)[vertical ? 'top' : 'left'] })
  }))
  const overlay = table.parentElement!.querySelector<HTMLElement>('.tt-table-controls')!
  const chips = Array.from(overlay.querySelectorAll<HTMLElement>(vertical ? '.tt-table-row-chip' : '.tt-table-col-chip'))
  const guide = doc.createElement('div')
  guide.className = `tt-${axis}-drop-guide`
  guide.dataset.testid = `${axis}-drop-guide`
  guide.setAttribute('aria-hidden', 'true')
  overlay.append(guide)
  overlay.dataset[`${axis}Dragging`] = 'true'
  const host = table.closest('.md-editor')
  host?.setAttribute('data-table-dragging', '')
  const isSource = (index: number) => index >= from && index < from + count
  chips.forEach((chip, index) => { chip.dataset[`${axis}Drag`] = isSource(index) ? 'source' : 'neighbor' })
  const label = doc.createElement('span')
  label.className = 'tt-axis-drop-label'
  label.textContent = vertical ? tableStrings.rowQuantityLabel(count) : tableStrings.columnQuantityLabel(count)
  guide.append(label)
  const omit = new Set<Element>(table.querySelectorAll('colgroup'))
  if (vertical) Array.from(table.rows).forEach((row, index) => { if (!isSource(index)) omit.add(row) })
  else entries.forEach(({ element, index }) => { if (!isSource(index)) omit.add(element) })
  const card = createDragCard(editor.view, [table], { label: label.textContent!, omit,
    width: vertical ? table.getBoundingClientRect().width : items.slice(from, from + count).reduce((sum, item) => sum + item.width, 0),
  })
  const moving = new Map<HTMLElement, Animation>()
  const motion = dragMotion(table)
  const reduced = motion.reduced
  let previousTarget = -1

  function update(to: number, x: number, y: number) {
    card.move(x, y)
    const order = columnOrder(items.length, from, to, count)
    const offsets: number[] = []
    let left = items[0][coordinate]
    for (const col of order) { offsets[col] = left - items[col][coordinate]; left += items[col][size] }
    for (const { element, index } of entries) {
      if (isSource(index) ? previousTarget !== -1 : previousTarget === to) continue
      // Web Animations do not mutate editable DOM attributes. Inline transforms
      // would be reparsed and removed by ProseMirror's DOM observer.
      if (!element.animate) continue
      const current = doc.defaultView!.getComputedStyle(element).transform
      moving.get(element)?.cancel()
      moving.set(element, element.animate([
        { transform: current === 'none' ? `${translate}(0)` : current },
        { transform: `${translate}(${isSource(index) ? 0 : offsets[index]}px)`, opacity: isSource(index) ? .25 : 1 },
      ], { duration: reduced ? 0 : motion.duration, easing: motion.easing, fill: 'forwards' }))
    }
    previousTarget = to
    chips.forEach((chip, index) => { chip.style.transform = `${translate}(${isSource(index) ? 0 : offsets[index]}px)` })
    guide.style[coordinate] = `${items[from][coordinate] - table.getBoundingClientRect()[coordinate] + offsets[from]}px`
    guide.style[size] = `${items.slice(from, from + count).reduce((sum, item) => sum + item[size], 0)}px`
    guide.dataset.destination = String(to)
  }

  function clear() {
    const before = entries.map(({ element, index }) => ({ index, rect: element.getBoundingClientRect(), opacity: doc.defaultView!.getComputedStyle(element).opacity }))
    moving.forEach(animation => animation.cancel())
    moving.clear()
    for (const chip of chips) { delete chip.dataset[`${axis}Drag`]; chip.style.transform = '' }
    delete overlay.dataset[`${axis}Dragging`]
    host?.removeAttribute('data-table-dragging')
    guide.remove()
    card.destroy()
    return before
  }

  /** FLIP the final layout into place, including a cancelled drag returning home. */
  function settle(before: ReturnType<typeof clear>, to: number): Animation[] {
    if (dragMotion(table).reduced) return []
    const animations: Animation[] = []
    const next = editor.state.doc.nodeAt(tablePos)
    if (!next || next.type.name !== 'table') return animations
    const order = columnOrder(vertical ? map.height : map.width, from, to, count)
    const nextMap = TableMap.get(next)
    const used = new Set<number>()
    let entry = 0
    model.forEach((row, rowOffset, rowIndex) => row.forEach((_cell, cellOffset) => {
      const rect = map.findCell(rowOffset + cellOffset + 1)
      const newCol = vertical ? rect.left : order.indexOf(rect.left)
      const newRow = vertical ? order.indexOf(rowIndex) : rowIndex
      const position = nextMap.map[newRow * nextMap.width + newCol]
      const previous = before[entry++]
      if (used.has(position)) return
      used.add(position)
      const element = editor.view.nodeDOM(tablePos + 1 + position) as HTMLElement | null
      if (!element?.animate) return
      const dx = previous.rect[coordinate] - element.getBoundingClientRect()[coordinate]
      if (Math.abs(dx) < .5 && Number(previous.opacity) >= .99) return
      animations.push(element.animate([{ transform: `${translate}(${dx}px)`, opacity: previous.opacity }, { transform: `${translate}(0)`, opacity: 1 }], {
        duration: motion.duration, easing: motion.easing,
      }))
    }))
    return animations
  }
  return { items, update, clear, settle }
}
