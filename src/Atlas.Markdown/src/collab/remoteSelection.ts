import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { relativePositionToAbsolutePosition, ySyncPluginKey } from '@tiptap/y-tiptap'
import type * as Y from 'yjs'

interface SyncBinding {
  doc: Y.Doc
  type: Y.XmlFragment
  mapping: Map<unknown, unknown>
  prosemirrorView: { state: { doc: { content: { size: number } } } } | null
  beforeTransactionSelection: { anchor: Y.RelativePosition | null; head: Y.RelativePosition | null; absAnchor?: number; absHead?: number } | null
}

/**
 * Works around @tiptap/y-tiptap 3.0.9 (the latest release): after a remote change it checks the restored selection against
 * the absolute positions it took before the first Yjs transaction of a batch, resolved in the document as it is when each
 * transaction's change reaches ProseMirror. A second transaction of the batch, usually Yjs' formatting cleanup after a remote
 * delete, then resolves positions of the longer old document in the shorter new one and throws "Position N out of range",
 * which ends a collaborative session. Before every Yjs transaction the absolute positions are taken again from the relative
 * ones, in the document ProseMirror holds at that moment.
 */
export const CollaborationRemoteSelection = Extension.create({
  name: 'markdownCollaborationRemoteSelection',
  addProseMirrorPlugins() {
    return [new Plugin({
      key: new PluginKey('markdownCollaborationRemoteSelection'),
      view(view) {
        const binding = (ySyncPluginKey.getState(view.state) as { binding?: SyncBinding } | undefined)?.binding
        if (!binding) return {}
        const align = () => {
          const selection = binding.beforeTransactionSelection
          if (!selection || !binding.prosemirrorView) return
          const size = binding.prosemirrorView.state.doc.content.size
          const current = (relative: Y.RelativePosition | null, fallback: number | undefined) => Math.min(size, Math.max(0,
            (relative ? relativePositionToAbsolutePosition(binding.doc, binding.type, relative, binding.mapping as never) : null) ?? fallback ?? 0))
          selection.absAnchor = current(selection.anchor, selection.absAnchor)
          selection.absHead = current(selection.head, selection.absHead)
        }
        binding.doc.on('beforeTransaction', align)
        binding.doc.on('afterTransaction', align)
        return { destroy: () => { binding.doc.off('beforeTransaction', align); binding.doc.off('afterTransaction', align) } }
      },
    })]
  },
})
