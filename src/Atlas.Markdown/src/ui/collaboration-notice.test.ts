// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, shallowRef, type App } from 'vue'
import type { MarkdownEditorState } from '../core/types'
import CollaborationNotice from './CollaborationNotice.vue'

const apps: App[] = []
beforeEach(() => vi.useFakeTimers())
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren(); vi.useRealTimers() })
const initial: MarkdownEditorState = {
  contentRevision: 0, changeOrigin: 'local', lifecycle: 'starting', binding: 'collaborative',
  editable: false, focused: false, connection: 'connecting', documentSave: 'pending',
  projectionSave: 'pending', pendingChanges: 0, error: null, peers: [], link: null,
}
/** The card is deliberately withheld for the first moment of every interruption. */
const GRACE_MS = 1200

function mount(state = initial) {
  const current = shallowRef(state)
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render: () => h(CollaborationNotice, { state: current.value }) })
  apps.push(app); app.mount(host)
  return { host, current }
}
const card = (host: HTMLElement) => host.querySelector('[data-testid="collaboration-notice"]')
async function settle(): Promise<void> {
  vi.advanceTimersByTime(GRACE_MS)
  await nextTick()
}

it('says nothing at all while a short interruption resolves itself', async () => {
  const { host, current } = mount()
  expect(card(host)).toBeNull()
  vi.advanceTimersByTime(GRACE_MS - 1)
  await nextTick()
  expect(card(host)).toBeNull()
  current.value = { ...initial, connection: 'online', lifecycle: 'ready', editable: true, documentSave: 'saved' }
  await nextTick()
  vi.advanceTimersByTime(10_000)
  await nextTick()
  expect(card(host)).toBeNull()
})

it('explains initial unavailability and removes the warning when ready without stealing focus', async () => {
  const { host, current } = mount()
  await settle()
  expect(host.textContent).toContain('Zatím nelze upravovat')
  expect(host.querySelector('[role="status"]')?.getAttribute('aria-atomic')).toBe('true')
  current.value = { ...initial, connection: 'offline' }
  await settle()
  expect(host.textContent).toContain('Dokument se zatím nepodařilo načíst')
  const button = document.createElement('button'); document.body.append(button); button.focus()
  current.value = { ...initial, connection: 'online', lifecycle: 'ready', editable: true, documentSave: 'saved' }
  await nextTick()
  expect(card(host)).toBeNull()
  expect(document.activeElement).toBe(button)
})

it('keeps the card out of the document flow by nesting it in the zero-size live region', async () => {
  const { host, current } = mount({ ...initial, lifecycle: 'ready', connection: 'reconnecting' })
  const live = host.querySelector('.md-collaboration-live')
  expect(live).not.toBeNull()
  await settle()
  expect(card(host)?.parentElement).toBe(live)
  current.value = { ...initial, connection: 'online', lifecycle: 'ready', editable: true }
  await nextTick()
  // The live region itself is the only child either way, so nothing below it can move.
  expect(host.querySelector('.md-collaboration-live')).toBe(live)
  expect(host.firstElementChild).toBe(live)
  expect(live!.children).toHaveLength(0)
})

it('distinguishes an interrupted session and unconfirmed changes from initial loading', async () => {
  const { host, current } = mount({ ...initial, lifecycle: 'ready', connection: 'reconnecting', pendingChanges: 2 })
  await settle()
  expect(host.textContent).toContain('Úpravy jsou pozastavené')
  expect(host.textContent).toContain('Některé změny ještě server nepotvrdil')
  current.value = { ...current.value, pendingChanges: 0 }
  await nextTick()
  expect(host.textContent).not.toContain('Nezavírejte')
})

it('respects a host that explicitly permits offline editing', async () => {
  const { host } = mount({ ...initial, lifecycle: 'ready', connection: 'offline', editable: true })
  await settle()
  expect(host.textContent).toContain('bez spojení')
  expect(host.textContent).not.toContain('pozastaven')
  expect(host.textContent).not.toContain('Pouze čtení')
})

it('does not claim retry for fatal errors or render a warning for local and online readonly editors', async () => {
  const failure = { kind: 'collaboration', message: 'Unauthorized', recoverable: false } as const
  const { host, current } = mount({ ...initial, connection: 'failed', lifecycle: 'failed', error: failure })
  // A failure cannot resolve itself, so it is the one state that skips the grace period.
  await nextTick()
  expect(host.textContent).toContain('Dokument nelze upravovat')
  expect(host.textContent).not.toContain('Po obnovení')
  expect(host.querySelector('button')).toBeNull()
  current.value = { ...initial, binding: 'local', connection: 'local', lifecycle: 'ready' }
  await settle()
  expect(host.textContent).toBe('')
  current.value = { ...initial, connection: 'online', lifecycle: 'ready', documentSave: 'saved' }
  await settle()
  expect(host.textContent).toBe('')
})

it('stays silent for a destroyed session', async () => {
  const { host } = mount({ ...initial, lifecycle: 'destroyed', connection: 'offline' })
  await settle()
  expect(card(host)).toBeNull()
})
