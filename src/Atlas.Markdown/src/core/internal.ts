import type { AnyExtension, Editor } from '@tiptap/core'
import type { MarkdownCollaborationAdapter, MarkdownDocumentSchema, MarkdownEditorController, MarkdownEditorState } from './types'

// The Vue renderer needs the engine for Tiptap UI plugins, but this bridge is not a package export.
const engines = new WeakMap<MarkdownEditorController, () => Editor | null>()
const viewExtensions = new WeakMap<MarkdownEditorController, AnyExtension[]>()
export function configureControllerView(controller: MarkdownEditorController, extensions: AnyExtension[]): void {
  if (controller.state.lifecycle !== 'idle' || editorForController(controller)) throw new Error('Configure editor UI before attaching its first view.')
  viewExtensions.set(controller, extensions)
}
export function extensionsForController(controller: MarkdownEditorController): AnyExtension[] {
  return viewExtensions.get(controller) ?? []
}
export function registerControllerEngine(controller: MarkdownEditorController, getEditor: () => Editor | null): void {
  engines.set(controller, getEditor)
}
export function editorForController(controller: MarkdownEditorController): Editor | null {
  return engines.get(controller)?.() ?? null
}

/** A key right after a native selection change (Shift+arrow, then Alt+F10) can come before ProseMirror's `selectionchange`: read it now. */
// shortcut: DOMObserver.flush is ProseMirror's internal API (it reads the DOM selection into the state); upgrade if prosemirror-view exposes one.
export function flushDomSelection(editor: Editor): void {
  (editor.view as unknown as { domObserver?: { flush(): void } }).domObserver?.flush()
}

export interface CollaborationSession {
  readonly extensions: AnyExtension[]
  isRemoteTransaction(): boolean
  attachEditor(editor: Editor): void
  detachEditor(): void
  destroy(): void
}
export type CollaborationUpdate = Partial<MarkdownEditorState> & {
  readonly hydrated?: boolean
  /** Effective server permission, kept separate from the host's readonly preference. */
  readonly writable?: boolean
  /**
   * The server's document is about to be applied, in the same task, to a session that may write: the view can turn editable while it
   * is still empty, so the content is drawn editable once instead of drawn read-only and then redrawn (perf review 2026-11-02).
   */
  readonly hydrating?: boolean
}
type SessionFactory = (publish: (state: CollaborationUpdate) => void, schema: MarkdownDocumentSchema) => CollaborationSession
const adapters = new WeakMap<MarkdownCollaborationAdapter, SessionFactory>()
export function registerCollaborationAdapter(factory: SessionFactory): MarkdownCollaborationAdapter {
  const adapter = Object.freeze({}) as MarkdownCollaborationAdapter
  adapters.set(adapter, factory)
  return adapter
}
export function claimCollaborationAdapter(adapter: MarkdownCollaborationAdapter): SessionFactory {
  const factory = adapters.get(adapter)
  if (!factory) throw new TypeError('Unknown or already used collaboration adapter; create one adapter per session.')
  adapters.delete(adapter)
  return factory
}
