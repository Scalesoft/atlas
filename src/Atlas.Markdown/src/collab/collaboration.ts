import Collaboration from '@tiptap/extension-collaboration'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import type { Plugin } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import { ySyncPluginKey } from '@tiptap/y-tiptap'

/**
 * Tiptap's Collaboration, except that a plugin registered later (a bubble menu, a drag handle, an agent's live write) does not rebuild
 * the document. Registering reconfigures the editor, which recreates every plugin view, and y-tiptap's sync view then renders the whole
 * document from Yjs again (`_forceRerender`: every node and node view new; on a 750 KB page a whole document of node views each time).
 * The document the view shows is still the one the binding rendered and kept in sync, so that rebuild is skipped. The first view, a
 * new view and a binding that knows no block yet render as before. (Tiptap wraps the undo plugin's view the same way.)
 */
export const KeepingCollaboration = Collaboration.extend({
  addProseMirrorPlugins() {
    const plugins = (this.parent?.() ?? []) as Plugin[]
    const sync = plugins.find(plugin => plugin.spec.key === ySyncPluginKey)
    const create = sync?.spec.view
    if (!sync || !create) return plugins
    let shown: { view: EditorView; doc: ProseMirrorNode } | null = null
    sync.spec.view = view => {
      const binding = ySyncPluginKey.getState(view.state)?.binding
      const keep = !!binding && shown?.view === view && shown.doc === view.state.doc && binding.mapping.size > 0
      if (keep) binding._forceRerender = () => {}
      let inner: ReturnType<typeof create>
      try { inner = create(view) } finally { if (keep) Reflect.deleteProperty(binding, '_forceRerender') }
      shown = { view, doc: view.state.doc }
      return {
        update: (updated, previous) => { inner.update?.(updated, previous); shown = { view, doc: view.state.doc } },
        destroy: () => inner.destroy?.(),
      }
    }
    return plugins
  },
})
