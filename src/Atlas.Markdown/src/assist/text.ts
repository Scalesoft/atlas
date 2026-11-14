import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import type { EditorState } from '@tiptap/pm/state'
import type { Mapping } from '@tiptap/pm/transform'
import type { MarkdownAssistRange } from './types'

const OBJECT = '￼'

/** One character per position: hard break `\n`, any other inline leaf U+FFFC. */
export function blockText(node: ProseMirrorNode): string {
  return node.textBetween(0, node.content.size, undefined, leaf => leaf.type.name === 'hardBreak' ? '\n' : OBJECT)
}
/** The same convention for any range (blocks joined by `\n`). */
export function rangeText(doc: ProseMirrorNode, range: MarkdownAssistRange): string {
  return doc.textBetween(range.from, range.to, '\n', leaf => leaf.type.name === 'hardBreak' ? '\n' : OBJECT)
}

/** Position before the textblock that holds the selection head, or null outside text. */
export function caretBlock(state: EditorState): number | null {
  const { $from, $to } = state.selection
  if (!$from.parent.isTextblock || !$to.sameParent($from)) return null
  return $from.before()
}

export function textblockAt(doc: ProseMirrorNode, pos: number): ProseMirrorNode | null {
  if (pos < 0 || pos >= doc.content.size) return null
  const node = doc.nodeAt(pos)
  return node?.isTextblock ? node : null
}

/** Maps a range so that an insertion at either edge counts as a change of the span. */
export function mapRange(mapping: Mapping, range: MarkdownAssistRange): MarkdownAssistRange | null {
  const from = mapping.mapResult(range.from, -1), to = mapping.mapResult(range.to, 1)
  if ((from.deleted && to.deleted && range.from !== range.to) || to.pos < from.pos) return null
  return { from: from.pos, to: to.pos }
}

/** The new-document range touched by a sequence of step maps. */
export function changedRange(mapping: Mapping): MarkdownAssistRange | null {
  let changed: MarkdownAssistRange | null = null
  for (const map of mapping.maps) {
    if (changed) changed = { from: map.map(changed.from, -1), to: map.map(changed.to, 1) }
    map.forEach((_oldStart, _oldEnd, newStart, newEnd) => {
      changed = changed ? { from: Math.min(changed.from, newStart), to: Math.max(changed.to, newEnd) } : { from: newStart, to: newEnd }
    })
  }
  return changed
}

/** Stable short hash (FNV-1a) — never stored with the text it describes. */
export function hash(text: string): string {
  let value = 0x811c9dc5
  for (let index = 0; index < text.length; index++) { value ^= text.charCodeAt(index); value = Math.imul(value, 0x01000193) }
  return (value >>> 0).toString(36)
}

export type DiffPart = { readonly kind: 'same' | 'removed' | 'added'; readonly text: string }

/** Word diff (longest common subsequence over words and the spaces between them). */
export function diffWords(before: string, after: string): DiffPart[] {
  const split = (text: string) => text.match(/\s+|[^\s]+/g) ?? []
  const left = split(before), right = split(after)
  // Long inputs fall back to „everything replaced“ instead of an O(n·m) table.
  if (left.length * right.length > 250_000) return [{ kind: 'removed', text: before }, { kind: 'added', text: after }].filter(part => part.text) as DiffPart[]
  const table = Array.from({ length: left.length + 1 }, () => new Uint32Array(right.length + 1))
  for (let i = left.length - 1; i >= 0; i--) {
    for (let j = right.length - 1; j >= 0; j--) table[i]![j] = left[i] === right[j] ? table[i + 1]![j + 1]! + 1 : Math.max(table[i + 1]![j]!, table[i]![j + 1]!)
  }
  const parts: DiffPart[] = []
  const push = (kind: DiffPart['kind'], text: string) => {
    const last = parts.at(-1)
    if (last?.kind === kind) parts[parts.length - 1] = { kind, text: last.text + text }
    else parts.push({ kind, text })
  }
  let i = 0, j = 0
  while (i < left.length || j < right.length) {
    if (i < left.length && j < right.length && left[i] === right[j]) { push('same', left[i]!); i++; j++ }
    else if (j < right.length && (i >= left.length || table[i]![j + 1]! >= table[i + 1]![j]!)) { push('added', right[j]!); j++ }
    else { push('removed', left[i]!); i++ }
  }
  return parts
}

export const countWords = (text: string) => (text.match(/[^\s]+/g) ?? []).length
