import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { effectScope, nextTick } from 'vue'
import { useSectionReview } from './useSectionReview'
import { canEditSection, sectionActions, type MarkdownReviewAccess, type MarkdownReviewSection, type MarkdownSectionDocument, type MarkdownSectionReviewSource } from './types'
const content = (text: string) => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } } as const)
const author: MarkdownReviewAccess = { canRead: true, canContribute: true, canEdit: false, canReview: false }
const reviewer: MarkdownReviewAccess = { ...author, canContribute: false, canReview: true }
const editor: MarkdownReviewAccess = { ...author, canEdit: true }
const section = (id = 'a'): MarkdownReviewSection => ({ id, revision: 'r1', working: content('Original'), approved: content('Original'), author: 'alice', status: 'approved', deleted: false })
const document = (...sections: MarkdownReviewSection[]): MarkdownSectionDocument => ({ revision: crypto.randomUUID(), sections, approvedOrder: sections.map(section => section.id) })
const scopes: ReturnType<typeof effectScope>[] = []
function api() {
  return { history: { page: vi.fn(), snapshot: vi.fn() },
    read: vi.fn<MarkdownSectionReviewSource['read']>(async () => ({ document: document(section(), section('b')), access: author })),
    save: vi.fn<MarkdownSectionReviewSource['save']>(async (id, revision, value, ready) => document({ ...section(id), working: value, revision: revision + 'x', status: ready ? 'pending' : 'draft' }, section('b'))),
    add: vi.fn<MarkdownSectionReviewSource['add']>(), decide: vi.fn<MarkdownSectionReviewSource['decide']>(),
    watch: vi.fn<MarkdownSectionReviewSource['watch']>(),
  } satisfies MarkdownSectionReviewSource
}
function mount(source = api()) { const scope = effectScope(); scopes.push(scope); return { source, scope, state: scope.run(() => useSectionReview(source))! } }
function pushed(source: ReturnType<typeof api>) { return source.watch.mock.calls[0]![0] }
beforeEach(() => vi.useFakeTimers())
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); vi.useRealTimers() })
async function settle() { await nextTick(); await vi.advanceTimersByTimeAsync(0) }
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (reason?: unknown) => void
  const promise = new Promise<T>((next, fail) => { resolve = next; reject = fail })
  return { promise, resolve, reject }
}

it('does not report saved or published content after an initial read failure', async () => {
  const source = api(); source.read.mockRejectedValueOnce(new Error('Offline'))
  const { state } = mount(source)
  expect(state.status.value).toBe('Načítám stav serveru…'); await settle()
  expect(state.status.value).toBe('Stav serveru se nepodařilo ověřit')
  await state.refresh(); expect(state.status.value).toBe('Publikovaná verze načtena')
})
it('distinguishes drafts, server acknowledgements, publication, and failed decisions without a dirty draft', async () => {
  const { state, source } = mount(); await settle()
  state.edit('a', content('Draft')); expect(state.status.value).toBe('Neuložené změny sekcí')
  await state.flush('a'); expect(state.status.value).toBe('Návrhy uloženy; nejsou publikované')
  const delayed = deferred<MarkdownSectionDocument>()
  source.decide.mockImplementationOnce(() => delayed.promise)
  const deciding = state.decide('a', 'approve'); await settle()
  expect(state.dirty.value).toBe(false); expect(state.status.value).toBe('Čekám na potvrzení serveru…')
  delayed.resolve(document(section())); await deciding
  expect(state.status.value).toBe('Publikovaná verze načtena')
  source.decide.mockRejectedValueOnce(new Error('Lost response'))
  await state.decide('a', 'delete')
  expect(state.status.value).toBe('Stav serveru se nepodařilo ověřit')
})
it('a failed save remains visible even after a successful server refresh', async () => {
  const { state, source } = mount(); await settle()
  source.save.mockRejectedValueOnce(new Error('Conflict'))
  state.edit('a', content('Local')); await state.flush('a'); await state.refresh()
  expect(state.status.value).toBe('Uložení sekcí pozastaveno')
  expect(state.drafts.value.get('a')?.value).toEqual(content('Local'))
})

it.each(['approved', 'draft', 'pending'] as const)('author and editor editing rights for %s', status => {
  const current = { ...section(), status }
  expect(canEditSection(current, 'alice', author)).toBe(true)
  expect(canEditSection(current, 'bob', author)).toBe(status === 'approved')
  expect(canEditSection(current, 'bob', editor)).toBe(true)
  expect(canEditSection(current, 'reviewer', reviewer)).toBe(false)
  expect(canEditSection(current, 'alice', { ...editor, canRead: false })).toBe(false)
})
it.each(['draft', 'pending'] as const)('role-specific actions for %s', status => {
  const current = { ...section(), status }
  expect(sectionActions(current, 'alice', author)).toEqual(['withdraw', 'delete'])
  expect(sectionActions(current, 'bob', author)).toEqual([])
  expect(sectionActions(current, 'reviewer', reviewer)).toEqual(status === 'pending' ? ['approve', 'reject'] : ['reject'])
  expect(sectionActions(current, 'editor', editor)).toEqual(['withdraw', 'delete'])
})
it('a deletion proposal can be reviewed or withdrawn, but not deleted again', () => {
  const current = { ...section(), status: 'pending' as const, deleted: true }
  expect(sectionActions(current, 'alice', author)).toEqual(['withdraw'])
  expect(sectionActions(current, 'reviewer', reviewer)).toEqual(['approve', 'reject'])
})
it('aggregates typing into one autosave checkpoint', async () => {
  const { state, source } = mount(); await settle()
  state.edit('a', content('a')); await vi.advanceTimersByTimeAsync(300); state.edit('a', content('ab')); await vi.advanceTimersByTimeAsync(601)
  expect(source.save).toHaveBeenCalledTimes(1); expect(source.save.mock.calls[0]![2]).toEqual(content('ab')); expect(source.save.mock.calls[0]![3]).toBe(false)
})
it('leaving the section immediately flushes and submits before the debounce', async () => {
  const { state, source } = mount(); await settle(); state.edit('a', content('Text')); await state.flush('a')
  expect(source.save.mock.calls[0]!.slice(0, 4)).toEqual(['a', 'r1', content('Text'), true]); expect(state.dirty.value).toBe(false)
})
it('no-op focus/blur does not create a review proposal', async () => {
  const { state, source } = mount(); await settle(); await state.flush('a'); expect(source.save).not.toHaveBeenCalled()
})
it('typing during an in-flight save stays local and uses the acknowledged revision next', async () => {
  const { state, source } = mount(); await settle(); const delayed = deferred<MarkdownSectionDocument>()
  source.save.mockImplementationOnce(() => delayed.promise)
  state.edit('a', content('First')); const saving = state.flush('a', false); await settle()
  state.edit('a', content('First plus more'))
  delayed.resolve(document({ ...section(), revision: 'r2', working: content('First'), status: 'draft' }, section('b'))); await saving
  expect(state.drafts.value.get('a')?.value).toEqual(content('First plus more'))
  await state.flush('a'); expect(source.save.mock.calls[1]!.slice(0, 4)).toEqual(['a', 'r2', content('First plus more'), true])
})
it('failed saves retain the exact draft and suppress automatic retry storms', async () => {
  const { state, source } = mount(); await settle(); source.save.mockRejectedValue(new Error('conflict'))
  state.edit('a', content('Local')); await state.flush('a'); expect(state.failures.value.get('a')).toBe('conflict')
  state.edit('a', content('Local more')); await vi.advanceTimersByTimeAsync(1000); expect(source.save).toHaveBeenCalledTimes(1)
  expect(state.drafts.value.get('a')?.value).toEqual(content('Local more'))
})
it('refresh preserves drafts even when another client removes that section', async () => {
  const { state, source } = mount(); await settle(); state.edit('a', content('Keep this'))
  source.read.mockResolvedValueOnce({ document: document(section('b')), access: author }); await state.refresh()
  expect(state.drafts.value.get('a')?.value).toEqual(content('Keep this')); expect(state.failures.value.has('a')).toBe(true)
  expect(state.drafts.value.get('a')?.section.id).toBe('a')
})
it('a late read cannot overwrite an acknowledged mutation and is repeated against the current server state', async () => {
  const { state, source } = mount(); await settle(); const delayed = deferred<Awaited<ReturnType<MarkdownSectionReviewSource['read']>>>()
  source.read.mockImplementationOnce(() => delayed.promise)
  source.read.mockResolvedValue({ document: document({ ...section(), revision: 'r1x', working: content('New'), status: 'pending' }, section('b')), access: author })
  const refresh = state.refresh()
  state.edit('a', content('New')); await state.flush('a'); delayed.resolve({ document: document(section()), access: author }); await refresh; await settle()
  expect(state.document.value?.sections[0]?.working).toEqual(content('New'))
  expect(source.read).toHaveBeenCalledTimes(3)
})
it('decisions capture the clicked revision and never approve a local unsubmitted draft', async () => {
  const { state, source } = mount(); await settle(); state.edit('a', content('Unsaved')); await state.decide('a', 'approve')
  expect(source.decide).not.toHaveBeenCalled()
})
it('subscribes once with the lifetime signal before the first read', async () => {
  const { source } = mount(); await settle()
  expect(source.watch).toHaveBeenCalledTimes(1); expect(source.watch.mock.calls[0]![1].aborted).toBe(false)
  expect(source.watch.mock.invocationCallOrder[0]!).toBeLessThan(source.read.mock.invocationCallOrder[0]!)
})
it('a pushed revision triggers exactly one read', async () => {
  const { source } = mount(); await settle()
  pushed(source)('r-remote'); await settle()
  expect(source.read).toHaveBeenCalledTimes(2)
})
it('a push carrying the revision already on screen is ignored', async () => {
  const { state, source } = mount(); await settle()
  pushed(source)(state.document.value!.revision); await settle()
  expect(source.read).toHaveBeenCalledTimes(1)
})
it('a resync push (null) always reads', async () => {
  const { source } = mount(); await settle()
  pushed(source)(null); await settle()
  expect(source.read).toHaveBeenCalledTimes(2)
})
it('a push during a queued write is applied after the queue drains', async () => {
  const { state, source } = mount(); await settle(); const delayed = deferred<MarkdownSectionDocument>()
  source.save.mockImplementationOnce(() => delayed.promise)
  state.edit('a', content('Local')); const saving = state.flush('a'); await settle()
  pushed(source)('r-remote'); await settle()
  expect(source.read).toHaveBeenCalledTimes(1)
  delayed.resolve(document({ ...section(), revision: 'r2', working: content('Local'), status: 'pending' }, section('b'))); await saving; await settle()
  expect(source.read).toHaveBeenCalledTimes(2)
})
it('a pushed read overtaken by an in-flight local write is repeated after that write fails', async () => {
  const { state, source } = mount(); await settle()
  const delayedRead = deferred<Awaited<ReturnType<MarkdownSectionReviewSource['read']>>>()
  source.read.mockImplementationOnce(() => delayedRead.promise)
  pushed(source)('r-remote'); await settle()
  const delayedSave = deferred<MarkdownSectionDocument>(); source.save.mockImplementationOnce(() => delayedSave.promise)
  state.edit('a', content('Local')); const saving = state.flush('a'); await settle()
  delayedRead.resolve({ document: document(section(), section('b')), access: author }); await settle()
  expect(source.read).toHaveBeenCalledTimes(2)
  delayedSave.reject(new Error('conflict')); await saving; await settle()
  expect(source.read).toHaveBeenCalledTimes(3)
})
it('a pushed read overtaken by a write that already settled is repeated immediately', async () => {
  const { state, source } = mount(); await settle()
  const delayedRead = deferred<Awaited<ReturnType<MarkdownSectionReviewSource['read']>>>()
  source.read.mockImplementationOnce(() => delayedRead.promise)
  pushed(source)('r-remote'); await settle()
  source.save.mockRejectedValueOnce(new Error('conflict'))
  state.edit('a', content('Local')); await state.flush('a'); await settle()
  delayedRead.resolve({ document: document(section(), section('b')), access: author }); await settle()
  expect(source.read).toHaveBeenCalledTimes(3)
})
it('unmount aborts the subscription, requests and delayed writes', async () => {
  const { state, source, scope } = mount(); await settle(); const signal = source.read.mock.calls[0]?.[0]
  state.edit('a', content('Local')); scope.stop(); await vi.advanceTimersByTimeAsync(30000)
  expect(signal?.aborted).toBe(true); expect(source.watch.mock.calls[0]![1].aborted).toBe(true)
  expect(source.save).not.toHaveBeenCalled(); expect(source.read).toHaveBeenCalledTimes(1)
})
