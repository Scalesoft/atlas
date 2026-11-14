import { inject, onScopeDispose, shallowRef, type InjectionKey } from 'vue'
import type { MarkdownEditorController } from '../core'

export const editorContext: InjectionKey<MarkdownEditorController> = Symbol('Atlas.Markdown')

export function useEditorContext(): MarkdownEditorController {
  const controller = inject(editorContext)
  if (!controller) throw new Error('MarkdownEditorContent requires a MarkdownEditorRoot.')
  return controller
}

/** Observes a borrowed controller; only its creator owns destruction. */
export function useControllerState(controller: MarkdownEditorController) {
  const state = shallowRef(controller.state)
  const unsubscribe = controller.subscribe(next => { state.value = next })
  onScopeDispose(unsubscribe)
  return state
}
