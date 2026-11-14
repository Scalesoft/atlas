import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'

/**
 * The top-level block that holds the caret carries `data-md-caret`, and content.css never skips its rendering. The page's blocks
 * use `content-visibility: auto`, and Chrome does not move the caret past text typed into a block it has not rendered yet: the first
 * character after Enter landed behind the caret ("Blok B" became "lok B", block-moves e2e; 30 of 30 in plain ProseMirror).
 */
export const CaretBlock = Extension.create({
  name: 'markdownCaretBlock',
  addProseMirrorPlugins() {
    return [new Plugin({
      key: new PluginKey('markdownCaretBlock'),
      props: {
        decorations: ({ doc, selection: { $head } }) => {
          if (!$head.depth) return null
          const from = $head.before(1)
          return DecorationSet.create(doc, [Decoration.node(from, from + $head.node(1).nodeSize, { 'data-md-caret': '' })])
        },
      },
    })]
  },
})
