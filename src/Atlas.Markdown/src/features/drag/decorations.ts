import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet, type EditorView } from '@tiptap/pm/view'
import { NodeRangeSelection } from '@tiptap/extension-node-range'

export const DRAGGING_SOURCE_ATTR = 'data-dragging-source'
const SECTION_HOVER_ATTR = 'data-section-hover'
const SECTION_HOVER_IDX_VAR = '--section-hover-idx'

export const SOURCE_DECO_PLUGIN_KEY = new PluginKey<DecorationSet>('dragPreviewSourceDeco')
export const HOVER_DECO_PLUGIN_KEY = new PluginKey<DecorationSet>('dragPreviewHoverDeco')

export interface SetSourceMeta {
  type: 'set-source'
  from: number
  to: number
}
export interface ClearSourceMeta {
  type: 'clear-source'
}
type SourceMeta = SetSourceMeta | ClearSourceMeta

interface SetHoverMeta {
  type: 'set-hover'
  /** When true, the first range is a heading and gets the deeper "head" tint. */
  headingFirst: boolean
  ranges: Array<{ from: number; to: number; idx: number }>
}
interface ClearHoverMeta {
  type: 'clear-hover'
}
type HoverMeta = SetHoverMeta | ClearHoverMeta

function buildSourceDecorations(doc: import('@tiptap/pm/model').Node, from: number, to: number): DecorationSet {
  if (to <= from) return DecorationSet.empty
  const decos: Decoration[] = []
  let pos = from
  while (pos < to) {
    const node = doc.nodeAt(pos)
    if (!node) break
    decos.push(
      Decoration.node(pos, pos + node.nodeSize, {
        [DRAGGING_SOURCE_ATTR]: 'true',
      }),
    )
    pos += node.nodeSize
  }
  return DecorationSet.create(doc, decos)
}

function buildHoverDecorations(
  doc: import('@tiptap/pm/model').Node,
  ranges: Array<{ from: number; to: number; idx: number }>,
  headingFirst: boolean,
): DecorationSet {
  if (ranges.length === 0) return DecorationSet.empty
  const decos: Decoration[] = ranges.map((r, i) =>
    Decoration.node(r.from, r.to, {
      [SECTION_HOVER_ATTR]: headingFirst && i === 0 ? 'head' : 'body',
      // Inline style is the only way to deliver a CSS custom property via
      // a Decoration's spec map.
      style: `${SECTION_HOVER_IDX_VAR}: ${r.idx};`,
    }),
  )
  return DecorationSet.create(doc, decos)
}


export function createDragDecorationPlugins() {
  return [
    new Plugin({
      key: new PluginKey('explicitBlockSelection'),
      props: { decorations(state) {
        if (!(state.selection instanceof NodeRangeSelection)) return DecorationSet.empty
        const decorations: Decoration[] = []
        state.doc.forEach((node, pos) => {
          if (pos >= state.selection.from && pos + node.nodeSize <= state.selection.to) {
            decorations.push(Decoration.node(pos, pos + node.nodeSize, { 'data-block-selected': 'true' }))
          }
        })
        return DecorationSet.create(state.doc, decorations)
      } },
    }),
    // Source-range decoration plugin: keeps `data-dragging-source` attached
    // to the dragged blocks across ProseMirror's redraw cycle. A plain DOM
    // attribute set imperatively gets wiped the next time PM re-renders
    // those nodes (decoration churn from NodeRangeSelection, doc transactions
    // emitted by other plugins, Vue reactive updates dispatching empty txs).
    // A Decoration.node attribute is part of PM's view state, so it survives.
    new Plugin<DecorationSet>({
      key: SOURCE_DECO_PLUGIN_KEY,
      state: {
        init: () => DecorationSet.empty,
        apply(tr, value) {
          const meta = tr.getMeta(SOURCE_DECO_PLUGIN_KEY) as SourceMeta | undefined
          if (meta?.type === 'set-source') {
            return buildSourceDecorations(tr.doc, meta.from, meta.to)
          }
          if (meta?.type === 'clear-source') {
            return DecorationSet.empty
          }
          return value.map(tr.mapping, tr.doc)
        },
      },
      props: {
        decorations(state) {
          return SOURCE_DECO_PLUGIN_KEY.getState(state) ?? DecorationSet.empty
        },
      },
    }),
    // Hover-range decoration plugin — same redraw-safety story as the
    // source plugin, but used for the pre-drag section tint. Each range
    // gets `data-section-hover="head"|"body"` plus an inline style with
    // `--section-hover-idx: N` so the CSS overlay can stagger its fade-in
    // from the handle position outward.
    new Plugin<DecorationSet>({
      key: HOVER_DECO_PLUGIN_KEY,
      state: {
        init: () => DecorationSet.empty,
        apply(tr, value) {
          const meta = tr.getMeta(HOVER_DECO_PLUGIN_KEY) as HoverMeta | undefined
          if (meta?.type === 'set-hover') {
            return buildHoverDecorations(tr.doc, meta.ranges, meta.headingFirst)
          }
          if (meta?.type === 'clear-hover') {
            return DecorationSet.empty
          }
          return value.map(tr.mapping, tr.doc)
        },
      },
      props: {
        decorations(state) {
          return HOVER_DECO_PLUGIN_KEY.getState(state) ?? DecorationSet.empty
        },
      },
    }),
  ]
}

/**
 * Mark the heading + its content blocks with `data-section-hover` and a
 * staggered `--section-hover-idx` so the CSS overlay can "spread" the tint
 * outward from the handle position. Implemented as ProseMirror Decorations
 * (via HOVER_DECO_PLUGIN_KEY meta) so the markers survive any redraws
 * triggered by selection changes, Vue reactive updates, or other plugins.
 */
export function markSectionHover(
  view: EditorView,
  ranges: Array<{ from: number; to: number }>,
  options: { headingFirst: boolean } = { headingFirst: false },
): void {
  if (ranges.length === 0) {
    clearSectionHover(view)
    return
  }
  try {
    view.dispatch(
      view.state.tr.setMeta(HOVER_DECO_PLUGIN_KEY, {
        type: 'set-hover',
        headingFirst: options.headingFirst,
        ranges: ranges.map((r, idx) => ({ from: r.from, to: r.to, idx })),
      } satisfies SetHoverMeta),
    )
  } catch {
    /* view may be detached during teardown */
  }
}

export function clearSectionHover(view: EditorView): void {
  try {
    view.dispatch(
      view.state.tr.setMeta(HOVER_DECO_PLUGIN_KEY, {
        type: 'clear-hover',
      } satisfies ClearHoverMeta),
    )
  } catch {
    /* view may be detached during teardown */
  }
}

export function clearDragSource(view: EditorView) {
  // A drop already ended the marker in its own transaction: no second round of every transaction listener.
  if (!SOURCE_DECO_PLUGIN_KEY.getState(view.state)?.find().length) return
  view.dispatch(view.state.tr.setMeta(SOURCE_DECO_PLUGIN_KEY, { type: 'clear-source' } satisfies ClearSourceMeta))
}
