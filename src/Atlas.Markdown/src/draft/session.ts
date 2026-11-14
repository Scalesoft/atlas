import type { MarkdownContentStamp, MarkdownEditorController } from '../core/types'
import { getMarkdownContentCapability } from '../core/contentCapability'
import { cloneMarkdownDraftRecord, normalizeMarkdownDraftScope } from './data'
import { MarkdownDraftError, type MarkdownDraftRecord, type MarkdownDraftScope, type MarkdownDraftState, type MarkdownDraftStore } from './types'

export interface MarkdownDraftOptions {
  readonly controller: MarkdownEditorController
  readonly store: MarkdownDraftStore
  readonly scope: MarkdownDraftScope
  readonly delayMs?: number
  readonly maxWaitMs?: number
}
export interface MarkdownDraft {
  getState(): MarkdownDraftState
  subscribe(listener: (state: MarkdownDraftState) => void): () => void
  /** Capture now; resolve only after this captured value's atomic store commit. */
  checkpoint(): Promise<MarkdownDraftRecord>
  destroy(): void
}
const stamp = (value: MarkdownContentStamp): MarkdownContentStamp => Object.freeze({ sessionId: value.sessionId, revision: value.revision })
const same = (left: MarkdownContentStamp, right: MarkdownContentStamp | null) => !!right && left.sessionId === right.sessionId && left.revision === right.revision

/** Opt-in periodic local snapshots. This is neither an every-edit journal nor host/server save acknowledgement. */
export function createMarkdownDraft(options: MarkdownDraftOptions): MarkdownDraft {
  const { controller, store } = options
  const content = getMarkdownContentCapability(controller)
  if (!content || controller.state.binding !== 'local' || ['failed', 'destroyed'].includes(controller.state.lifecycle)) {
    throw new MarkdownDraftError('invalid', 'Drafts require an active local controller with snapshot capability.')
  }
  const scope = normalizeMarkdownDraftScope(options.scope)
  const delay = options.delayMs ?? 1000, maximum = options.maxWaitMs ?? 5000
  if (!Number.isSafeInteger(delay) || delay < 0 || !Number.isSafeInteger(maximum) || maximum < Math.max(1, delay) || maximum > 60_000) {
    throw new MarkdownDraftError('invalid', 'Invalid draft checkpoint timing.')
  }
  const branchId = crypto.randomUUID()
  let state: MarkdownDraftState = Object.freeze({ branchId, status: 'pending', current: stamp(content.getStamp()), persisted: null, error: null })
  let destroyed = false, failed = false, revision = 0
  let tail: Promise<unknown> = Promise.resolve()
  let timer: ReturnType<typeof setTimeout> | undefined, deadline: ReturnType<typeof setTimeout> | undefined
  const listeners = new Set<(state: MarkdownDraftState) => void>()
  const publish = (patch: Partial<MarkdownDraftState>) => {
    if (destroyed) return
    state = Object.freeze({ ...state, ...patch })
    const published = state
    for (const listener of [...listeners]) { if (destroyed) break; try { listener(published) } catch { /* Host observers do not alter commit ownership. */ } }
  }
  const clear = () => { clearTimeout(timer); clearTimeout(deadline); timer = deadline = undefined }
  const fail = (cause: unknown) => {
    failed = true; clear()
    publish({ status: 'failed', error: cause instanceof Error ? cause.message : String(cause) })
  }
  const checkpoint = (): Promise<MarkdownDraftRecord> => {
    if (destroyed) return Promise.reject(new MarkdownDraftError('closed', 'Draft session is closed.'))
    clear()
    let captured: MarkdownDraftRecord
    try {
      if (controller.state.binding !== 'local' || ['failed', 'destroyed'].includes(controller.state.lifecycle)) throw new MarkdownDraftError('invalid', 'The local controller cannot export a draft.')
      // Export and detach before awaiting any previous write or calling host observers.
      captured = cloneMarkdownDraftRecord({ formatVersion: 1, scope, branchId, revision: 1, capturedAt: Date.now(), schema: controller.schema, snapshot: content.getSnapshot() })
    } catch (cause) { fail(cause); return Promise.reject(cause) }
    const work = tail.then(async () => {
      const record = cloneMarkdownDraftRecord({ ...captured, revision: revision + 1 })
      // A custom host store receives its own copy; even a mutable implementation cannot
      // change the identity/value used to acknowledge this session's committed snapshot.
      await store.write(cloneMarkdownDraftRecord(record), revision)
      revision = record.revision
      if (controller.state.lifecycle === 'failed') {
        // A good older snapshot may finish after live content validation fails. Its
        // completion must never turn the now-failed editor back into "persisted".
        failed = true
        publish({ persisted: stamp(record.snapshot), status: 'failed', error: controller.state.error?.message ?? 'The editor content failed validation.' })
      } else {
        failed = false
        publish({ persisted: stamp(record.snapshot), status: same(state.current, record.snapshot) ? 'persisted' : 'pending', error: null })
      }
      return record
    })
    tail = work.catch(fail)
    publish({ status: 'pending', error: null })
    return work
  }
  const schedule = () => {
    if (destroyed || failed) return
    clearTimeout(timer)
    const run = () => { void checkpoint().catch(() => {}) }
    timer = setTimeout(run, delay)
    deadline ??= setTimeout(run, maximum)
  }
  const stopContent = content.subscribe(change => {
    publish({ current: stamp(change), status: failed ? 'failed' : 'pending' })
    schedule()
  })
  let stopController = () => {}
  const destroy = () => {
    if (destroyed) return
    clear(); stopContent(); stopController()
    state = Object.freeze({ ...state, status: 'closed' }); destroyed = true
    listeners.clear()
    // Captured, detached queued writes may finish. Store ownership belongs to the host.
  }
  stopController = controller.subscribe(next => {
    if (next.lifecycle === 'destroyed') destroy()
    else if (next.lifecycle === 'failed') fail(new Error(next.error?.message ?? 'The editor content failed validation.'))
  })
  schedule()
  return {
    getState: () => state,
    subscribe(listener) {
      if (destroyed) throw new MarkdownDraftError('closed', 'Draft session is closed.')
      listeners.add(listener)
      try { listener(state) } catch { /* Observer isolation. */ }
      return () => { listeners.delete(listener) }
    },
    checkpoint,
    destroy,
  }
}
