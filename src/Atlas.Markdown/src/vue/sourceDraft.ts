import { toValue, type ComputedRef, type Ref } from 'vue'
import type { MarkdownRichValue } from '../core'
import type { MarkdownSourceDiagnostic, MarkdownSourceProtectedRange, MarkdownSourceSession } from '../source'
import type { MarkdownSourceDraftOptions, MarkdownSourceDraftStatus, MarkdownSourceNotice, MarkdownSourceSaveResult } from './useMarkdownSourceDraft'

// The page source draft's behaviour, loaded on the first `open()` (docs/page-source-editor.md). The
// eager `useMarkdownSourceDraft` shell owns the reactive state; this module drives it.
export interface SourceDraftState {
  text: Ref<string>
  baseText: Ref<string>
  protectedBlocks: Ref<readonly MarkdownSourceProtectedRange[]>
  diagnostics: Ref<readonly MarkdownSourceDiagnostic[]>
  status: Ref<MarkdownSourceDraftStatus>
  notice: Ref<MarkdownSourceNotice | undefined>
  stale: Ref<boolean>
  busy: Ref<boolean>
  revision: Ref<number>
  /** Only a moved base blocks Apply; with 'live-edits' the same draft may simply be retried. */
  staleReason: Ref<'changed' | 'conflict' | 'live-edits' | undefined>
  /**
   * The reader works in the source editor (focus inside, reported by `MarkdownSourcePanel`). A clean
   * draft then does not follow a moved base, which would reset their cursor, scroll and undo; it
   * follows when they leave, and turns stale if they start editing first.
   */
  viewActive: Ref<boolean>
  dirty: ComputedRef<boolean>
  canApply: ComputedRef<boolean>
}
export interface SourceDraftController {
  open(): Promise<boolean>
  apply(): Promise<boolean>
  discard(): void
  reload(): Promise<boolean>
  close(): void
  reconcile(): void
}

const loadSource = () => import('../source')

// Rich values are immutable snapshots, so each one is serialized at most once (the base is compared on
// every host change, a large page must not be re-stringified per keystroke of a collaborator).
const canonicalCache = new WeakMap<object, string>()
/** Key-order independent JSON identity: a server round trip may reorder keys of the same page. */
function canonical(value: MarkdownRichValue): string {
  let key = canonicalCache.get(value)
  if (key === undefined) {
    key = JSON.stringify(value, (_key, item: unknown) => item && typeof item === 'object' && !Array.isArray(item)
      ? Object.fromEntries(Object.entries(item as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)))
      : item)
    canonicalCache.set(value, key)
  }
  return key
}
const sameValue = (left: MarkdownRichValue | undefined, right: MarkdownRichValue | undefined) =>
  left === right || (!!left && !!right && canonical(left) === canonical(right))

export function createSourceDraftController(state: SourceDraftState, options: MarkdownSourceDraftOptions): SourceDraftController {
  const { text, baseText, protectedBlocks, diagnostics, status, notice, stale, busy, revision, staleReason, viewActive, dirty, canApply } = state
  let session: MarkdownSourceSession | undefined
  let base: MarkdownRichValue | undefined
  let generation = 0
  let pendingOpen: Promise<boolean> | undefined
  let saving: AbortController | undefined
  // The page before our own successful save, while the host has not shown the saved one yet.
  let superseded: MarkdownRichValue | undefined
  let refresh: ReturnType<typeof setTimeout> | undefined

  const current = () => toValue(options.value)
  const create = async (value: MarkdownRichValue) => (await loadSource()).createMarkdownSourceSession({ value, plugins: toValue(options.plugins) })
  function markStale(reason: 'changed' | 'conflict' | 'live-edits') {
    // A moved base outranks a retryable failure.
    if (reason === 'live-edits' && staleReason.value) return
    stale.value = true; staleReason.value = reason
  }
  function install(next: MarkdownSourceSession, value: MarkdownRichValue) {
    session?.destroy()
    session = next; base = value
    baseText.value = next.text; text.value = next.text; protectedBlocks.value = next.protectedBlocks
    stale.value = false; staleReason.value = undefined
    status.value = 'ready'
    revision.value++
    reconcile()
  }
  // The value may have moved while a session was being built or a save was in flight. A clean draft
  // follows it (debounced: someone may be typing in the document) unless the reader is in the editor;
  // a dirty one becomes stale.
  function reconcile() {
    clearTimeout(refresh)
    if (status.value !== 'ready' || busy.value) return
    const value = current()
    if (sameValue(value, base)) { superseded = undefined; return }
    if (superseded && sameValue(value, superseded)) return
    superseded = undefined
    if (dirty.value) {
      // A conflict already explains the moved base; keep its message.
      if (staleReason.value !== 'conflict') notice.value = { kind: 'changed' }
      markStale('changed')
    }
    else if (!viewActive.value) refresh = setTimeout(() => { if (!dirty.value && !viewActive.value && !sameValue(current(), base)) void regenerate() }, 250)
  }
  async function regenerate(value = current()): Promise<boolean> {
    const token = ++generation
    const draftAtStart = text.value
    try {
      const next = await create(value)
      if (token !== generation) { next.destroy(); return false }
      // The user typed while it was built: keep the draft on its old base and say so.
      if (text.value !== draftAtStart && text.value !== baseText.value) {
        next.destroy(); markStale('changed'); notice.value = { kind: 'changed' }
        return false
      }
      install(next, value)
      return true
    } catch {
      if (token === generation) notice.value = { kind: 'unavailable' }
      return false
    }
  }

  function open(): Promise<boolean> {
    if (status.value === 'ready') return Promise.resolve(true)
    if (pendingOpen) return pendingOpen
    status.value = 'loading'; notice.value = undefined
    // Opening the source is the user's intent: fetch the editor while the session is built.
    loadSource().then(source => source.prefetchSourceEditor(), () => { /* create() reports it */ })
    const token = ++generation
    const value = current()
    const opening = pendingOpen = create(value).then(next => {
      if (token !== generation) { next.destroy(); return false }
      install(next, value)
      return true
    }, () => {
      if (token === generation) status.value = 'error'
      return false
    }).finally(() => { if (pendingOpen === opening) pendingOpen = undefined })
    return opening
  }

  async function apply(): Promise<boolean> {
    if (!canApply.value || !session) return false
    const result = session.apply(text.value)
    diagnostics.value = result.diagnostics
    if (!result.ok) { notice.value = { kind: 'invalid' }; return false }
    if (!result.changed) {
      // Same page, other spelling (e.g. whitespace): nothing to save.
      baseText.value = text.value
      notice.value = { kind: 'unchanged' }
      return true
    }
    const token = generation
    const submitted = text.value
    const controller = saving = new AbortController()
    busy.value = true; notice.value = undefined
    let outcome: Exclude<MarkdownSourceSaveResult, void>
    try {
      outcome = (await options.save(result.value, submitted, { signal: controller.signal })) ?? { ok: true }
    } catch (error) {
      const failure = error as { status?: unknown; message?: unknown } | undefined
      outcome = { ok: false, reason: failure?.status === 409 ? 'conflict' : 'error', message: typeof failure?.message === 'string' ? failure.message : undefined }
    } finally {
      if (saving === controller) saving = undefined
      busy.value = false
    }
    // Closed (tab gone, permission lost) while the request ran: nothing is left to update.
    if (controller.signal.aborted || token !== generation) return false
    if (outcome.ok) {
      superseded = base
      notice.value = { kind: 'saved' }
      // The saved page is the new base. It regenerates the text, so the editor starts a fresh history.
      await regenerate(result.value)
      notice.value = { kind: 'saved' }
      return true
    }
    if (outcome.reason !== 'error') markStale(outcome.reason)
    notice.value = { kind: outcome.reason, detail: outcome.message }
    reconcile()
    return false
  }

  function discard() {
    // A save in flight owns the text it submitted; its outcome decides what happens next.
    if (status.value !== 'ready' || busy.value) return
    text.value = baseText.value
    diagnostics.value = []; notice.value = undefined
    revision.value++
    const value = current()
    if (!sameValue(value, base) && !(superseded && sameValue(value, superseded))) void regenerate(value)
    else if (staleReason.value === 'live-edits') { stale.value = false; staleReason.value = undefined }
  }

  async function reload(): Promise<boolean> {
    if (status.value !== 'ready' || busy.value) return false
    diagnostics.value = []; notice.value = undefined
    return regenerate()
  }

  function close() {
    generation++; clearTimeout(refresh); superseded = undefined
    saving?.abort(); saving = undefined
    session?.destroy(); session = undefined; base = undefined; pendingOpen = undefined
    text.value = ''; baseText.value = ''; protectedBlocks.value = []; diagnostics.value = []
    status.value = 'closed'; notice.value = undefined
    stale.value = false; staleReason.value = undefined; busy.value = false
  }

  return { open, apply, discard, reload, close, reconcile }
}
