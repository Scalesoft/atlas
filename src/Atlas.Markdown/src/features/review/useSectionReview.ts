import { computed, onScopeDispose, ref, shallowRef } from 'vue'
import type { MarkdownRichValue } from '../../core/types'
import type { MarkdownReviewAccess, MarkdownReviewSection, MarkdownSectionDecision, MarkdownSectionDocument, MarkdownSectionReviewSource } from './types'
import { reviewStrings } from '../../ui/strings/review'

interface Draft { value: MarkdownRichValue; revision: string; generation: number; section: MarkdownReviewSection }
const emptyAccess: MarkdownReviewAccess = { canRead: false, canContribute: false, canEdit: false, canReview: false }

/** Serializes this client's writes; other sections can still be edited on other clients.
 * Remote changes arrive through the host's push channel (source.watch); nothing polls.
 * Conflicting same-section drafts are retained for recovery, never silently replaced. */
export function useSectionReview(source: MarkdownSectionReviewSource) {
  const document = shallowRef<MarkdownSectionDocument>(), access = shallowRef(emptyAccess)
  const drafts = shallowRef(new Map<string, Draft>()), failures = shallowRef(new Map<string, string>())
  const error = ref(''), busy = ref(false), loading = ref(true)
  const lifetime = new AbortController()
  const timers = new Map<string, ReturnType<typeof setTimeout>>()
  let generation = 0, epoch = 0, reads = 0, disposed = false, queue = Promise.resolve(), queued = 0, changePending = false
  const dirty = computed(() => drafts.value.size > 0)
  const status = computed(() => {
    const strings = reviewStrings
    if (loading.value) return strings.loadingServerState
    if (busy.value) return strings.waitingForServerConfirmation
    if (failures.value.size) return strings.sectionsSavePaused
    if (error.value) return strings.serverStateVerifyFailed
    if (dirty.value) return strings.unsavedSectionChanges
    if (!document.value) return strings.serverStateNotVerifiedYet
    return document.value.sections.some(section => section.status !== 'approved')
      ? strings.draftsSavedNotPublished : strings.publishedVersionLoaded
  })
  const setFailure = (id: string, message?: string) => { const next = new Map(failures.value); if (message) next.set(id, message); else next.delete(id); failures.value = next }
  function enqueue(operation: () => Promise<void>) {
    queued++; busy.value = true; epoch++
    const result = queue.then(async () => { if (!disposed) await operation() }).catch(cause => {
      if (!disposed) error.value = cause instanceof Error ? cause.message : reviewStrings.operationFailed
    }).finally(() => {
      queued--
      if (disposed) return
      busy.value = queued > 0; epoch++
      // A push that arrived while our own writes were in flight must not be lost.
      if (!busy.value && changePending) { changePending = false; void refresh() }
    })
    queue = result
    return result
  }
  async function refresh() {
    if (busy.value || disposed) return
    const current = epoch, read = ++reads
    let overtaken = false
    try {
      const next = await source.read(lifetime.signal)
      if (disposed || read !== reads) return
      if (current !== epoch) { overtaken = true; return }
      document.value = next.document; access.value = next.access; error.value = ''
      for (const [id, draft] of drafts.value) {
        if (next.document.sections.find(section => section.id === id)?.revision !== draft.revision)
          setFailure(id, reviewStrings.sectionChangedRemotely)
      }
    } catch (cause) {
      if (disposed || read !== reads) return
      if (current !== epoch) { overtaken = true; return }
      error.value = cause instanceof Error ? cause.message : reviewStrings.documentLoadFailed
    } finally {
      if (!disposed) loading.value = false
      // A local write raced this read, so its result was discarded. A failed write would otherwise leave the
      // pushed revision unseen; read again once the queue has settled.
      if (overtaken) onRemoteChange(null)
    }
  }
  function edit(id: string, value: MarkdownRichValue) {
    const section = document.value?.sections.find(section => section.id === id)
    if (!section) return
    const previous = drafts.value.get(id)
    drafts.value = new Map(drafts.value).set(id, { value, revision: previous?.revision ?? section.revision, generation: ++generation, section: previous?.section ?? section })
    clearTimeout(timers.get(id))
    timers.set(id, setTimeout(() => { timers.delete(id); if (!failures.value.has(id)) void flush(id, false) }, 600))
  }
  function flush(id: string, ready = true) {
    clearTimeout(timers.get(id)); timers.delete(id)
    return enqueue(async () => {
      const section = document.value?.sections.find(section => section.id === id)
      const draft = drafts.value.get(id)
      if (!section || (!draft && (!ready || section.status !== 'draft'))) return
      if (failures.value.has(id)) return
      try {
        const result = await source.save(id, draft?.revision ?? section.revision, draft?.value ?? section.working, ready, lifetime.signal)
        if (disposed) return
        document.value = result
        const latest = drafts.value.get(id), acknowledged = result.sections.find(section => section.id === id)
        if (latest && acknowledged) {
          const next = new Map(drafts.value)
          if (latest.generation === draft?.generation) next.delete(id)
          else next.set(id, { ...latest, revision: acknowledged.revision })
          drafts.value = next
        }
        setFailure(id)
      } catch (cause) {
        if (!disposed) setFailure(id, cause instanceof Error ? cause.message : reviewStrings.saveFailed)
      }
    })
  }
  function decide(id: string, decision: MarkdownSectionDecision) {
    // Capture the revision at the click, not after queued work or a refresh.
    const revision = document.value?.sections.find(section => section.id === id)?.revision
    return enqueue(async () => {
      if (!revision || drafts.value.has(id)) return
      const result = await source.decide(id, revision, decision, lifetime.signal)
      if (!disposed) { document.value = result; error.value = '' }
    })
  }
  function add(after: string | null, value: MarkdownRichValue) {
    const id = crypto.randomUUID().replaceAll('-', '')
    return enqueue(async () => { const result = await source.add(id, after, value, lifetime.signal); if (!disposed) { document.value = result; error.value = '' } })
  }
  function retry(id: string) { setFailure(id); return flush(id) }
  async function discard(id: string) {
    clearTimeout(timers.get(id)); timers.delete(id)
    const next = new Map(drafts.value); next.delete(id); drafts.value = next; setFailure(id)
    await refresh()
  }
  function onRemoteChange(revision: string | null) {
    if (disposed) return
    if (revision !== null && revision === document.value?.revision) return
    if (busy.value) { changePending = true; return }
    void refresh()
  }
  // Subscribe before the first read so no commit can fall between the snapshot and the subscription.
  source.watch(onRemoteChange, lifetime.signal)
  void refresh()
  onScopeDispose(() => { disposed = true; lifetime.abort(); for (const timer of timers.values()) clearTimeout(timer) })
  return { document, access, drafts, failures, error, busy, loading, dirty, status, refresh, edit, flush, decide, add, retry, discard }
}
