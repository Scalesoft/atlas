import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from 'vue'
import type { MarkdownHistoryEntry, MarkdownHistorySnapshot, MarkdownHistorySource } from './types'
import { createHistorySnapshotCache } from './snapshotCache'
import { historyStrings } from '../../ui/strings/history'

export function useDocumentHistory(source: Readonly<Ref<MarkdownHistorySource>>) {
  const entries = shallowRef<readonly MarkdownHistoryEntry[]>([])
  const selectedId = ref('')
  const snapshot = shallowRef<MarkdownHistorySnapshot>()
  const loading = ref(false), loadingSnapshot = ref(false), error = ref('')
  const cursor = ref<string>()
  const selected = computed(() => entries.value.find(entry => entry.id === selectedId.value))
  let cache = createHistorySnapshotCache(source.value)
  let pageRequest: AbortController | undefined
  let generation = 0, selection = 0, disposed = false
  let timer: ReturnType<typeof setTimeout> | undefined
  let failedPage: boolean | undefined

  async function load(older = false) {
    if (loading.value || (older && !cursor.value)) return
    const current = generation
    loading.value = true; error.value = ''
    const controller = new AbortController(); pageRequest = controller
    try {
      const page = await source.value.page({ cursor: older ? cursor.value : undefined, limit: 100, signal: controller.signal })
      if (disposed || current !== generation) return
      if (older && page.nextCursor && page.nextCursor === cursor.value) throw new Error('History cursor did not advance.')
      const previous = older ? entries.value : []
      const ids = new Set(previous.map(entry => entry.id))
      const additions: MarkdownHistoryEntry[] = []
      let lastTime = previous.length ? Date.parse(previous.at(-1)!.timestamp) : Infinity
      for (const entry of page.entries) {
        const time = Date.parse(entry.timestamp)
        if (!entry.id || !Number.isFinite(time)) throw new Error('Invalid history checkpoint.')
        if (ids.has(entry.id)) continue
        if (time > lastTime) throw new Error('History checkpoints must be newest first.')
        ids.add(entry.id); additions.push(entry); lastTime = time
      }
      if (!additions.length && page.nextCursor) throw new Error('History page contains no new checkpoints.')
      entries.value = [...previous, ...additions]
      cursor.value = page.nextCursor ?? undefined
      failedPage = undefined
      if (!older) selectedId.value = entries.value[0]?.id ?? ''
    } catch (cause) {
      if (!disposed && current === generation && !controller.signal.aborted) { failedPage = older; error.value = cause instanceof Error ? cause.message : historyStrings.historyLoadFailed }
    } finally { if (current === generation) loading.value = false }
  }

  async function readSnapshot(id: string) {
    const current = generation, request = ++selection
    loadingSnapshot.value = true; error.value = ''
    try {
      const next = await cache.get(id)
      if (!disposed && current === generation && request === selection && selectedId.value === id) snapshot.value = next
    } catch (cause) {
      if (!disposed && current === generation && request === selection) error.value = cause instanceof Error ? cause.message : historyStrings.snapshotLoadFailed
    } finally { if (current === generation && request === selection) loadingSnapshot.value = false }
  }
  watch(selectedId, id => {
    clearTimeout(timer); selection++; snapshot.value = undefined
    loadingSnapshot.value = !!id
    // Scrubbing remains immediate; IO starts after a short pause, only for the selected checkpoint.
    if (id) timer = setTimeout(() => { void readSnapshot(id) }, 90)
  })
  watch(source, () => {
    generation++; selection++; clearTimeout(timer); pageRequest?.abort(); cache.dispose()
    cache = createHistorySnapshotCache(source.value); entries.value = []; snapshot.value = undefined
    selectedId.value = ''; cursor.value = undefined; loading.value = false; failedPage = undefined
    void load()
  }, { immediate: true })
  onScopeDispose(() => { disposed = true; generation++; selection++; clearTimeout(timer); pageRequest?.abort(); cache.dispose() })
  return { entries, selectedId, selected, snapshot, loading, loadingSnapshot, error, cursor, load,
    retry: () => failedPage !== undefined ? load(failedPage) : selectedId.value && !snapshot.value ? readSnapshot(selectedId.value) : load() }
}
