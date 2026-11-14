import type { Editor } from '@tiptap/core'
import type { EditorState, Transaction } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import * as Y from 'yjs'
import { createRuntimeDocumentDataValidator } from '../../engine/runtimeDocumentData'
import { validateBuiltinDocument } from '../../engine/builtinDocumentValidation'

export interface CanonicalUpdateBatch { readonly update: Uint8Array; readonly snapshot: Uint8Array }
export interface CanonicalUpdateCollectorOptions {
  readonly doc: Y.Doc
  readonly isRemoteOrigin: (origin: unknown) => boolean
  /** Called synchronously with owned bytes, before controller transaction subscribers. */
  readonly onBatch: (batch: CanonicalUpdateBatch) => void
  readonly onError: (error: unknown) => void
}
export interface CanonicalUpdateCollector { attachEditor(editor: Editor): void; detachEditor(): void; destroy(): void }
interface Boundary { readonly depth: number; readonly updates: Uint8Array[]; sealed: boolean; applied: boolean }

/** Opt-in V2 collector. It changes no provider or V1 behavior until explicitly attached.
 * PM dispatch includes apply/append; view.updateState completes Yjs synchronization before
 * Tiptap emits its transaction event. Raw Yjs work uses afterAllTransactions instead.
 */
export function createCanonicalUpdateCollector(options: CanonicalUpdateCollectorOptions): CanonicalUpdateCollector {
  let destroyed = false, failed = false, depth = 0, active: Boundary | null = null
  let detach: (() => void) | null = null, attachedEditor: Editor | null = null
  let raw: Uint8Array[] = []
  const validate = createRuntimeDocumentDataValidator()
  const fail = (error: unknown) => {
    if (failed || destroyed) return
    failed = true
    try { options.onError(error) } catch { /* Observer failure cannot resume this collector. */ }
  }
  const emit = (updates: Uint8Array[]) => {
    if (!updates.length || destroyed || failed) return
    try {
      const batch = Object.freeze({ update: Y.mergeUpdates(updates), snapshot: Y.encodeStateAsUpdate(options.doc) })
      options.onBatch(batch)
    } catch (error) { fail(error) }
  }
  const seal = (boundary: Boundary, state: EditorState) => {
    if (boundary.sealed) return
    boundary.sealed = true
    try { validate(state.doc); state.doc.check(); validateBuiltinDocument(state.doc) }
    catch (error) { fail(error); return }
    const updates = boundary.updates.splice(0)
    emit(updates)
  }
  const update = (bytes: Uint8Array, origin: unknown) => {
    if (destroyed || failed) return
    try {
      if (options.isRemoteOrigin(origin)) return
      const owned = bytes.slice()
      if (active && !active.sealed) active.updates.push(owned)
      else raw.push(owned)
    } catch (error) { fail(error) }
  }
  const completed = () => {
    if (destroyed || failed || (active && !active.sealed)) return
    const updates = raw; raw = []
    emit(updates)
  }
  options.doc.on('update', update)
  options.doc.on('afterAllTransactions', completed)

  const detachEditor = () => { detach?.(); detach = null; attachedEditor = null }
  return {
    attachEditor(editor) {
      if (destroyed || failed) throw new Error('Canonical collector is closed.')
      if (attachedEditor === editor) return
      detachEditor()
      const view = editor.view
      const dispatch = view.props.dispatchTransaction
      if (typeof dispatch !== 'function') throw new TypeError('Canonical collection requires an initialized editor dispatch boundary.')
      const updateState = view.updateState
      const wrappedDispatch = (transaction: Transaction) => {
        if (destroyed || failed) { dispatch.call(view, transaction); return }
        const previous = active
        const boundary: Boundary = previous && !previous.sealed ? previous : { depth: depth + 1, updates: [], sealed: false, applied: false }
        active = boundary; depth++
        try {
          dispatch.call(view, transaction)
          // Filtered transactions must not conceal raw Yjs mutation inside an apply hook.
          if (depth === boundary.depth && !boundary.sealed && boundary.updates.length) {
            fail(new Error('Yjs changed in a PM dispatch that did not install an accepted state.'))
          }
        } catch (error) { fail(error); throw error }
        finally { depth--; active = previous }
      }
      const wrappedUpdate = function (this: EditorView, state: EditorState) {
        updateState.call(this, state)
        if (!destroyed && !failed && active && !active.sealed && depth === active.depth) {
          active.applied = true
          seal(active, state)
        }
      }
      view.setProps({ dispatchTransaction: wrappedDispatch })
      view.updateState = wrappedUpdate
      attachedEditor = editor
      detach = () => {
        if (view.updateState === wrappedUpdate) view.updateState = updateState
        if (!view.isDestroyed && view.props.dispatchTransaction === wrappedDispatch) view.setProps({ dispatchTransaction: dispatch })
      }
    },
    detachEditor,
    destroy() {
      if (destroyed) return
      destroyed = true
      detachEditor()
      options.doc.off('update', update)
      options.doc.off('afterAllTransactions', completed)
      raw = []
    },
  }
}
