// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import { connectionPresentation } from './connectionPresentation'
import { useConnectionCard } from './useConnectionCard'
import type { MarkdownEditorState } from '../core/types'

const base: MarkdownEditorState = {
  contentRevision: 1, changeOrigin: 'local', lifecycle: 'ready', binding: 'collaborative',
  editable: true, focused: false, connection: 'online', documentSave: 'saved',
  projectionSave: 'current', pendingChanges: 0, error: null, peers: [],
  link: { latencyMs: 20, drops: 0, stalled: false },
}
let scope: ReturnType<typeof effectScope> | undefined
beforeEach(() => vi.useFakeTimers())
afterEach(() => { scope?.stop(); scope = undefined; vi.useRealTimers() })

function mount(graceMs = 1200) {
  const state = ref<MarkdownEditorState>(base)
  scope = effectScope()
  const visible = scope.run(() => useConnectionCard(() => connectionPresentation(state.value), { graceMs }))!
  return { state, visible }
}

it('hides the card while the session is healthy', () => {
  const { visible } = mount()
  expect(visible.value).toBe(false)
})

it('never shows a card for an interruption shorter than the grace period', async () => {
  const { state, visible } = mount(1200)
  state.value = { ...base, connection: 'reconnecting', editable: false }
  await nextTick()
  vi.advanceTimersByTime(1199)
  expect(visible.value).toBe(false)
  state.value = base
  await nextTick()
  vi.advanceTimersByTime(5000)
  expect(visible.value).toBe(false)
})

it('shows the card once the interruption outlasts the grace period', async () => {
  const { state, visible } = mount(1200)
  state.value = { ...base, connection: 'reconnecting', editable: false }
  await nextTick()
  vi.advanceTimersByTime(1200)
  expect(visible.value).toBe(true)
})

it('does not restart the clock when one interruption turns into a worse one', async () => {
  const { state, visible } = mount(1200)
  state.value = { ...base, connection: 'reconnecting', editable: false }
  await nextTick()
  vi.advanceTimersByTime(900)
  state.value = { ...base, connection: 'offline', editable: false }
  await nextTick()
  vi.advanceTimersByTime(300)
  expect(visible.value).toBe(true)
})

it('hides the card the moment the session recovers', async () => {
  const { state, visible } = mount(1200)
  state.value = { ...base, connection: 'offline', editable: false }
  await nextTick()
  vi.advanceTimersByTime(1200)
  expect(visible.value).toBe(true)
  state.value = base
  await nextTick()
  expect(visible.value).toBe(false)
})

it('shows an unrecoverable failure without waiting', async () => {
  const { state, visible } = mount(1200)
  state.value = { ...base, lifecycle: 'failed', editable: false, error: { kind: 'collaboration', message: 'Unauthorized', recoverable: false } }
  await nextTick()
  expect(visible.value).toBe(true)
})
