import { Extension, type Editor } from '@tiptap/core'
import { Plugin, PluginKey, type EditorState, type Transaction } from '@tiptap/pm/state'
import type { DecorationSource } from '@tiptap/pm/view'

export const outlineHighlights = new WeakMap<Editor, {
  map(transaction: Transaction): void
  decorations(state: EditorState): DecorationSource
}>()

/** Stable view hook; highlight implementation stays in the lazy outline chunk. */
export const OutlineHighlight = Extension.create({
  name: 'markdownOutlineHighlight',
  addProseMirrorPlugins() {
    const editor = this.editor
    return [new Plugin({
      key: new PluginKey('markdownOutlineHighlight'),
      state: { init: () => null, apply(tr) { outlineHighlights.get(editor)?.map(tr); return null } },
      props: { decorations: state => outlineHighlights.get(editor)?.decorations(state) ?? null },
    })]
  },
})
