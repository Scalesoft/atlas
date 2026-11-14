import { getChangedRanges } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet, type EditorView } from '@tiptap/pm/view'
import type { CodeHighlighter, HastNode } from './code'

/** Highlight spans of one code block, as upstream's LowlightPlugin draws them. */
function blockDecorations(lowlight: CodeHighlighter, node: ProseMirrorNode, pos: number): Decoration[] {
  const language = node.attrs.language as string | null
  const tree = language && lowlight.registered(language) ? lowlight.highlight(language, node.textContent) : lowlight.highlightAuto(node.textContent)
  const decorations: Decoration[] = []
  let from = pos + 1
  const walk = (nodes: readonly HastNode[], classes: readonly string[]) => {
    for (const child of nodes) {
      if (child.type === 'text') {
        const to = from + (child.value?.length ?? 0)
        if (classes.length) decorations.push(Decoration.inline(from, to, { class: classes.join(' ') }))
        from = to
      } else if (child.type === 'element') {
        const own = Array.isArray(child.properties?.className) ? child.properties.className.filter((name): name is string => typeof name === 'string') : []
        walk(child.children ?? [], [...classes, ...own])
      }
    }
  }
  walk(tree.children, [])
  return decorations
}

const key = new PluginKey<DecorationSet>('codeHighlighting')
/** The node decoration that marks a block as highlighted (plan 3.6-G): only blocks near the viewport are, see `requestCodeHighlighting`. */
const HIGHLIGHTED = 'codeHighlighted'
const marker = (node: ProseMirrorNode, pos: number) => Decoration.node(pos, pos + node.nodeSize, {}, { [HIGHLIGHTED]: true })
/** Whether `decorations` (a node view's own) still mark its block as highlighted; a replaced block loses the mark and asks again. */
export const codeHighlighted = (decorations: readonly Decoration[]) => decorations.some(decoration => decoration.spec[HIGHLIGHTED])

const requests = new WeakMap<EditorView, Set<() => number | undefined>>()
/** Highlights the block at `getPos` (its node view came near the viewport), batched with the others asking in this task. */
export function requestCodeHighlighting(view: EditorView, getPos: () => number | undefined) {
  let pending = requests.get(view)
  if (pending) { pending.add(getPos); return }
  requests.set(view, pending = new Set([getPos]))
  // Never inside the view update that created or updated the node view.
  queueMicrotask(() => {
    requests.delete(view)
    const positions = [...pending].map(position => position()).filter((position): position is number => position !== undefined)
    if (!view.isDestroyed && positions.length) view.dispatch(view.state.tr.setMeta(key, positions).setMeta('addToHistory', false))
  })
}

/**
 * Upstream's LowlightPlugin scans the whole document on every transaction and highlights every code block again on a
 * keystroke in one of them (UX EP2-3: ~12 ms a key on the biggest real page, more with many blocks). This one maps the
 * spans and highlights again only the code blocks a change touched. Loaded with the highlighter, never eagerly.
 * `lazy` (with the code node view): a block is highlighted only once its view asks (`requestCodeHighlighting`), so a page
 * opens without tokenizing code below the fold; without a node view everything is highlighted from the start.
 */
export function incrementalHighlighting(name: string, lowlight: CodeHighlighter, lazy = false): Plugin<DecorationSet> {
  const all = (doc: ProseMirrorNode) => {
    const decorations: Decoration[] = []
    doc.descendants((node, pos) => { if (node.type.name === name) { decorations.push(...blockDecorations(lowlight, node, pos)); return false } return true })
    return DecorationSet.create(doc, decorations)
  }
  return new Plugin<DecorationSet>({
    key,
    state: {
      init: (_, { doc }) => lazy ? DecorationSet.empty : all(doc),
      apply(transaction, set) {
        const requested = transaction.getMeta(key) as number[] | undefined
        if (!transaction.docChanged && !requested) return set
        let next = set.map(transaction.mapping, transaction.doc)
        // A block's own decorations: not the neighbour's marker that ends where this block starts.
        const own = (pos: number, node: ProseMirrorNode) => next.find(pos, pos + node.nodeSize).filter(decoration => decoration.from >= pos && decoration.to <= pos + node.nodeSize)
        const touched = new Map<number, ProseMirrorNode>()
        for (const { newRange } of transaction.docChanged ? getChangedRanges(transaction) : []) {
          transaction.doc.nodesBetween(newRange.from, Math.min(newRange.to, transaction.doc.content.size), (node, pos) => {
            if (node.type.name !== name) return true
            // A lazy block not highlighted (yet, or no longer: its node view asks again) only drops stale spans.
            if (!lazy || own(pos, node).some(decoration => decoration.spec[HIGHLIGHTED])) touched.set(pos, node)
            else next = next.remove(own(pos, node))
            return false
          })
        }
        for (const pos of requested ?? []) { const node = transaction.doc.nodeAt(pos); if (node?.type.name === name) touched.set(pos, node) }
        for (const [pos, node] of touched) next = next.remove(own(pos, node)).add(transaction.doc, [...lazy ? [marker(node, pos)] : [], ...blockDecorations(lowlight, node, pos)])
        return next
      },
    },
    props: { decorations: state => key.getState(state) },
  })
}
