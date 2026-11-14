// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { createApp, h, nextTick, shallowRef, type App } from 'vue'
import type { MarkdownEditorState } from '../core/types'
import ConnectionDot from './ConnectionDot.vue'

const apps: App[] = []
afterEach(() => { apps.splice(0).forEach(app => app.unmount()); document.body.replaceChildren() })
const online: MarkdownEditorState = {
  contentRevision: 2, changeOrigin: 'local', lifecycle: 'ready', binding: 'collaborative',
  editable: true, focused: false, connection: 'online', documentSave: 'saved',
  projectionSave: 'current', pendingChanges: 0, error: null, peers: [],
  link: { latencyMs: 42, drops: 0, stalled: false },
}
function mount(state = online) {
  const current = shallowRef(state)
  const host = document.createElement('div')
  document.body.append(host)
  const app = createApp({ render: () => h(ConnectionDot, { state: current.value }) })
  apps.push(app); app.mount(host)
  return { host, current, dot: () => host.querySelector('[data-testid="connection-dot"]') }
}

it('stays out of the way of a document nobody else is editing', () => {
  expect(mount({ ...online, binding: 'local', connection: 'local', link: null }).dot()).toBeNull()
  expect(mount({ ...online, lifecycle: 'destroyed' }).dot()).toBeNull()
})

it('keeps the same element and the same box through every connection state', async () => {
  const { current, dot } = mount()
  const element = dot()
  expect(element?.getAttribute('data-level')).toBe('live')
  for (const [state, level] of [
    [{ ...online, connection: 'reconnecting', editable: false }, 'reconnecting'],
    [{ ...online, connection: 'offline', editable: false }, 'offline'],
    [{ ...online, link: { latencyMs: 4200, drops: 3, stalled: false } }, 'weak'],
    [online, 'live'],
  ] as const) {
    current.value = state
    await nextTick()
    expect(dot()).toBe(element)
    expect(element?.getAttribute('data-level')).toBe(level)
  }
})

it('names the current state for assistive technology', async () => {
  const { host, current } = mount()
  const button = host.querySelector('button')!
  expect(button.getAttribute('aria-label')).toBe('Stav spolupráce: Živě synchronizováno')
  current.value = { ...online, connection: 'offline', editable: false }
  await nextTick()
  expect(button.getAttribute('aria-label')).toBe('Stav spolupráce: Server není dostupný')
})

it('reveals the detail on keyboard focus, not only on hover', async () => {
  const { host } = mount()
  const button = host.querySelector('button')!
  const detail = host.querySelector('[data-testid="connection-detail"]') as HTMLElement
  expect(detail.style.display).toBe('none')
  expect(button.getAttribute('aria-expanded')).toBe('false')
  expect(button.getAttribute('aria-controls')).toBe(detail.id)
  button.dispatchEvent(new FocusEvent('focus'))
  await nextTick()
  expect(detail.style.display).toBe('')
  expect(button.getAttribute('aria-expanded')).toBe('true')
  button.dispatchEvent(new FocusEvent('blur'))
  await nextTick()
  expect(detail.style.display).toBe('none')
})

it('keeps the detail open after a click, for longer than a hover lasts', async () => {
  const { host } = mount()
  const dot = host.querySelector('[data-testid="connection-dot"]')!
  const detail = host.querySelector('[data-testid="connection-detail"]') as HTMLElement
  dot.dispatchEvent(new MouseEvent('mouseenter'))
  host.querySelector('button')!.click()
  dot.dispatchEvent(new MouseEvent('mouseleave'))
  await nextTick()
  expect(detail.style.display).toBe('')
  host.querySelector('button')!.click()
  await nextTick()
  expect(detail.style.display).toBe('none')
})

it('reports measurements a person can act on', async () => {
  const { host, current } = mount({ ...online, pendingChanges: 4, peers: [{ clientId: 1, name: 'Bob', color: '#123456' }] })
  const detail = host.querySelector('[data-testid="connection-detail"]')!
  expect(detail.textContent).toContain('42 ms')
  expect(detail.textContent).toContain('bez výpadku')
  expect(detail.textContent).toContain('Bob')
  current.value = { ...online, link: { latencyMs: 2400, drops: 1, stalled: false } }
  await nextTick()
  expect(detail.textContent).toContain('2.4 s')
  expect(detail.textContent).toContain('1 výpadek')
  expect(detail.textContent).toContain('nikdo další')
  current.value = { ...online, link: { latencyMs: null, drops: 3, stalled: false } }
  await nextTick()
  expect(detail.textContent).toContain('zatím neměřeno')
  expect(detail.textContent).toContain('3 výpadky')
  current.value = { ...online, link: { latencyMs: null, drops: 7, stalled: false } }
  await nextTick()
  expect(detail.textContent).toContain('7 výpadků')
})

it('dismisses focused and hovered details with Escape and a second click', async () => {
  const { host, dot } = mount()
  const button = host.querySelector('button')!
  dot()!.dispatchEvent(new MouseEvent('mouseenter'))
  button.focus()
  button.click()
  await nextTick()
  button.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  await nextTick()
  expect(button.getAttribute('aria-expanded')).toBe('false')
  button.click()
  await nextTick()
  expect(button.getAttribute('aria-expanded')).toBe('true')
  button.click()
  await nextTick()
  expect(button.getAttribute('aria-expanded')).toBe('false')
})

it('dismisses pinned details on outside interaction and reopens on new focus', async () => {
  const { host } = mount()
  const button = host.querySelector('button')!
  button.focus()
  button.click()
  await nextTick()
  document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
  await nextTick()
  expect(button.getAttribute('aria-expanded')).toBe('false')
  button.blur()
  button.focus()
  await nextTick()
  expect(button.getAttribute('aria-expanded')).toBe('true')
})
