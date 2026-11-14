import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, PluginKey, type Transaction } from '@tiptap/pm/state'
import { DecorationSet } from '@tiptap/pm/view'
import InvisibleCharacters from '@tiptap/extension-invisible-characters'

interface InvisibleState { visible: boolean; decorations: DecorationSet }
const visibleMeta = 'setInvisibleCharactersVisible'
const key = new PluginKey<InvisibleState>('markdownInvisibleCharacters')

/** The ranges a transaction changed, in its final document (upstream's getUpdatedRanges). */
function updatedRanges({ mapping }: Transaction): Array<[number, number]> {
  const ranges: Array<[number, number]> = []
  mapping.maps.forEach((map, index) => map.forEach((_oldStart, _oldEnd, start, end) => {
    const rest = mapping.slice(index + 1)
    ranges.push([rest.map(start), rest.map(end)])
  }))
  return ranges
}

/**
 * Upstream's invisible characters, decorated only while they are shown. Upstream adds a widget for every space of every changed
 * range even while hidden, and y-tiptap replaces the whole document on each remote update: on a 5000-block page every keystroke
 * of a colleague re-decorated every space of the page, more than half of the main thread under 5 writers (COL2-5).
 */
export const LazyInvisibleCharacters = InvisibleCharacters.extend({
  addProseMirrorPlugins() {
    const builders = [...this.options.builders].sort((a, b) => (a.priority > b.priority ? 1 : -1))
    const decorate = (from: number, to: number, doc: ProseMirrorNode, decorations: DecorationSet) =>
      builders.reduce((next, builder) => builder.createDecoration(from, to, doc, next), decorations)
    const whole = (doc: ProseMirrorNode) => decorate(0, doc.content.size, doc, DecorationSet.empty)
    const initial = this.options.visible
    return [new Plugin<InvisibleState>({
      key,
      state: {
        init: (_, state) => ({ visible: initial, decorations: initial ? whole(state.doc) : DecorationSet.empty }),
        apply: (tr, previous, _, state) => {
          const meta = tr.getMeta(visibleMeta) as boolean | undefined
          const visible = meta ?? previous.visible
          if (!visible) return previous.visible ? { visible, decorations: DecorationSet.empty } : previous
          if (!previous.visible) return { visible, decorations: whole(state.doc) }
          if (!tr.docChanged) return previous
          const decorations = updatedRanges(tr).reduce((next, [from, to]) => decorate(from, to, state.doc, next),
            previous.decorations.map(tr.mapping, tr.doc))
          return { visible, decorations }
        },
      },
      props: { decorations: state => key.getState(state)?.decorations },
    })]
  },
  addCommands() {
    return {
      showInvisibleCharacters: (visibility = true) => ({ dispatch, tr }) => { if (dispatch) tr.setMeta(visibleMeta, visibility); return true },
      hideInvisibleCharacters: () => ({ dispatch, tr }) => { if (dispatch) tr.setMeta(visibleMeta, false); return true },
      toggleInvisibleCharacters: () => ({ dispatch, tr, state }) => { if (dispatch) tr.setMeta(visibleMeta, !key.getState(state)?.visible); return true },
    }
  },
  onBeforeCreate() {
    this.storage.visibility = () => key.getState(this.editor.state)?.visible ?? false
  },
})
