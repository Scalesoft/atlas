import { markRaw, onScopeDispose } from 'vue'
import { createMarkdownEditorController, getMarkdownContentCapability, type MarkdownContentCapability, type LocalControllerOptions, type MarkdownControllerOptions, type LocalMarkdownEditorController, type MarkdownEditorController, type MarkdownProfile, type MarkdownValueInput } from '../core'
import { clonePlainData } from '../core/plainData'
import { useControllerState } from './context'

/** Rich values are JSON; remove Vue proxies at every level before the core clones them. */
export function toPlainMarkdownValue<P extends MarkdownProfile>(value: MarkdownValueInput<P>): MarkdownValueInput<P> {
  // Descriptor traversal also works through Vue proxies. Object.entries/toRaw can
  // invoke accessors before core validation, and silently discard invalid fields.
  return clonePlainData(value)
}

export function useMarkdownEditor<P extends MarkdownProfile>(options: LocalControllerOptions<P>): { controller: LocalMarkdownEditorController<P>; state: ReturnType<typeof useControllerState>; content: MarkdownContentCapability<P> }
export function useMarkdownEditor<P extends MarkdownProfile>(options: MarkdownControllerOptions<P>): { controller: MarkdownEditorController<P>; state: ReturnType<typeof useControllerState>; content: MarkdownContentCapability<P> }
export function useMarkdownEditor<P extends MarkdownProfile>(options: MarkdownControllerOptions<P>) {
  const source = options.source.kind === 'local'
    ? { ...options.source, value: toPlainMarkdownValue(options.source.value) }
    : options.source
  const controller = markRaw(createMarkdownEditorController({ ...options, source }))
  const state = useControllerState(controller)
  onScopeDispose(() => controller.destroy())
  return { controller, state, content: getMarkdownContentCapability(controller)! }
}
