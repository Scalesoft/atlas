import type { Node as PMNode } from '@tiptap/pm/model'
import type { Transaction } from '@tiptap/pm/state'
import { Mapping, ReplaceStep, StepMap, type Step } from '@tiptap/pm/transform'
import { isRemoteTransaction } from './recognition/gate'

/**
 * `transaction.mapping`, except that a collaborator's change covers only what really changed. y-tiptap applies a remote update as
 * one step replacing every changed top-level block whole, so a position inside such a block (an upload's target, a suggestion's
 * range, a picker's place) would map as deleted although the collaborator typed a character elsewhere in it. A step replacing the
 * whole document (an older binding, a server's echo) narrows the same way, whoever made it.
 * shortcut: one changed span per step, so a step changing two distant blocks still deletes the positions between them; upgrade to a
 * per-block diff or Yjs relative positions if that shows.
 */
export function markdownChangeMapping(transaction: Transaction): Mapping {
  const remote = isRemoteTransaction(transaction)
  if (!transaction.steps.some((step, index) => narrows(step, transaction.docs[index]!, remote))) return transaction.mapping
  return new Mapping(transaction.steps.map((step, index) => {
    const before = transaction.docs[index]!
    if (!narrows(step, before, remote)) return step.getMap()
    const { from, to, slice } = step as ReplaceStep
    // Within the step's own range: outside it nothing changed.
    const old = before.slice(from, to).content, start = old.findDiffStart(slice.content)
    if (start === null) return StepMap.empty
    let { a, b } = old.findDiffEnd(slice.content)!
    // Repeated content lets the end overtake the start; either reading holds (as prosemirror-view reads a DOM change).
    if (a < start) { b += start - a; a = start } else if (b < start) { a += start - b; b = start }
    return new StepMap([from + start, a - start, b - start])
  }))
}

/** A replace that deletes, closed under one parent, by a collaborator or of the whole document. */
const narrows = (step: Step, before: PMNode, remote: boolean) => step instanceof ReplaceStep && step.from < step.to
  && !step.slice.openStart && !step.slice.openEnd && before.resolve(step.from).sameParent(before.resolve(step.to))
  && (remote || (step.from === 0 && step.to === before.content.size))
