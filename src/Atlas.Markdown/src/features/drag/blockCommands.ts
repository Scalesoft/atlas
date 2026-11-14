import type { Editor } from '@tiptap/core'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import { blockRangeForHandle, computeSectionEnd } from './blockSelection'
import { canMoveBlocks } from './movePolicy'

export function moveBlock(editor: Editor, activePos: number, dir: -1 | 1): number | null {
  if (!canMoveBlocks(editor)) return null
  const e = editor
  const doc = e.state.doc
  const range = blockRangeForHandle(e.state, activePos)
  if (!range) return null
  activePos = range.from
  const node = doc.nodeAt(activePos)
  if (!node) return null

  // Find the top-level block index of the hovered node.
  let curIdx = -1
  let curPos = 0
  for (let i = 0; i < doc.content.childCount; i++) {
    if (curPos === activePos) { curIdx = i; break }
    curPos += doc.content.child(i).nodeSize
  }
  if (curIdx < 0) return null

  const sectionEnd = range.to
  const targetIdx = dir > 0 ? doc.resolve(sectionEnd).index(0) : curIdx - 1
  if (targetIdx < 0 || targetIdx >= doc.content.childCount) return null

  // Compute target position to insert at.
  let targetInsertPos = 0
  for (let i = 0; i < targetIdx; i++) targetInsertPos += doc.content.child(i).nodeSize
  if (dir < 0 && node.type.name === 'heading') {
    let previousPos = 0
    for (let i = 0; i < curIdx; i++) {
      const previous = doc.content.child(i)
      if (previous.type.name === 'heading' && previous.attrs.level <= node.attrs.level
        && computeSectionEnd(doc, previousPos, previous.attrs.level) === activePos) {
        targetInsertPos = previousPos
        break
      }
      previousPos += previous.nodeSize
    }
  }
  if (dir > 0) {
    const next = doc.content.child(targetIdx)
    targetInsertPos = next.type.name === 'heading'
      ? computeSectionEnd(doc, targetInsertPos, next.attrs.level ?? 1)
      : targetInsertPos + next.nodeSize
  }

  const tr = e.state.tr
  const sliceSize = sectionEnd - activePos
  const content = doc.slice(activePos, sectionEnd).content
  // Move = delete then insert at adjusted position.
  tr.delete(activePos, activePos + sliceSize)
  const adjustedTarget = targetInsertPos > activePos ? targetInsertPos - sliceSize : targetInsertPos
  tr.insert(adjustedTarget, content)
  tr.setSelection(NodeRangeSelection.create(tr.doc, adjustedTarget, adjustedTarget + sliceSize))
  dispatchMarkdownHistoryOperation(e, tr.scrollIntoView())
  return adjustedTarget
}

/** A grip always drags whole blocks; inline text dragging stays inside the editor. */
export function prepareBlockDrag(e: Editor, dragTargetPos: number): { from: number; to: number } | undefined {
  if (!canMoveBlocks(e)) return
  const range = blockRangeForHandle(e.state, dragTargetPos)
  if (!range) return
  const { from, to } = range
  e.view.dispatch(e.state.tr.setSelection(NodeRangeSelection.create(e.state.doc, from, to)))
  return { from, to }
}
