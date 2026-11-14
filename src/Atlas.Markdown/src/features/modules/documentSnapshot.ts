import type { Node } from '@tiptap/pm/model'
import type { MarkdownReadonlyJSONContent } from '../../core/plugins/types'
import { clonePlainData, deepFreezePlainData } from '../../core/plainData'

const snapshots = new WeakMap<object, { document: Node; value: MarkdownReadonlyJSONContent }>()
/** One detached snapshot per editor state, shared only after copying its attrs. */
export function moduleDocumentSnapshot(document: Node, state: object): MarkdownReadonlyJSONContent {
  const cached = snapshots.get(state)
  const existing = cached?.document === document ? cached.value : undefined
  if (existing) return existing
  const value: MarkdownReadonlyJSONContent = deepFreezePlainData(clonePlainData(document.toJSON()))
  snapshots.set(state, { document, value })
  return value
}
