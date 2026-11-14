import { getCurrentInstance, type AppContext, type ComponentPublicInstance } from 'vue'
import type { Editor } from '@tiptap/core'
import type { Editor as VueEditor } from '@tiptap/vue-3'

type VueNodeViewHost = Editor & Pick<VueEditor, 'contentComponent' | 'appContext'>

/** The Vue node-view renderer needs a host context, independently of who constructs the engine. */
export function useTiptapVueContext() {
  const instance = getCurrentInstance()
  if (!instance) throw new Error('Vue editor context must be created during component setup.')
  // Vue's public instance type omits provides; Tiptap EditorContent uses this same internal field
  // to preserve the parent component's injection chain when mounting node views independently.
  const context = instance as typeof instance & { provides: AppContext['provides']; ctx: ComponentPublicInstance }

  /** `redraw`: the view already exists, so its node views are built again with the context (before the view is built, they need not). */
  function attach(editor: Editor, redraw = true): () => void {
    const host = editor as VueNodeViewHost
    host.contentComponent = context
    host.appContext = { ...context.appContext, provides: context.provides }
    if (redraw) editor.createNodeViews()
    return () => {
      if (host.contentComponent !== context) return
      host.contentComponent = null
      host.appContext = null
    }
  }
  return { attach }
}
