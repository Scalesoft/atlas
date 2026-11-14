// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { MarkdownEditor } from '../vue'
import { connectionPresentation } from '../ui/connectionPresentation'
import { createSimulatedCollaboration, type SimulatedLinkState } from './simulator'
import type { MarkdownEditorController, MarkdownEditorState } from '../core'

let app: App | undefined
afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren(); vi.useRealTimers() })

async function mount(initial?: SimulatedLinkState) {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  const simulation = createSimulatedCollaboration({ initial, peers: [{ clientId: 7, name: 'Bob', color: '#336699' }] })
  const states: MarkdownEditorState[] = []
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({
    render: () => h(MarkdownEditor, {
      collaboration: simulation.adapter, profile: 'rich', ariaLabel: 'Simulace',
      onCreated: (controller: MarkdownEditorController) => controller.subscribe(state => states.push(state)),
    }),
  })
  app.mount(host); await nextTick()
  return { simulation, host, latest: () => states.at(-1)! }
}

it('publishes a healthy session as soon as the editor attaches', async () => {
  const { latest } = await mount()
  expect(latest().connection).toBe('online')
  expect(latest().editable).toBe(true)
  expect(latest().peers).toHaveLength(1)
  expect(connectionPresentation(latest()).level).toBe('live')
})

it.each([
  ['slow', 'weak'], ['stalled', 'weak'], ['not-saving', 'weak'],
  ['reconnecting', 'reconnecting'], ['offline', 'offline'], ['connecting', 'connecting'], ['failed', 'failed'],
] as const)('drives %s into the %s presentation', async (state, level) => {
  const { simulation, latest } = await mount()
  simulation.apply(state)
  await nextTick()
  expect(connectionPresentation(latest()).level).toBe(level)
})

it('pauses editing while interrupted and resumes on recovery', async () => {
  const { simulation, latest } = await mount()
  simulation.apply('offline')
  await nextTick()
  expect(latest().editable).toBe(false)
  simulation.apply('live')
  await nextTick()
  expect(latest().editable).toBe(true)
})

it('keeps a read-only grant online and uninterrupted', async () => {
  const { simulation, latest } = await mount()
  simulation.apply('read-only')
  await nextTick()
  expect(latest().connection).toBe('online')
  expect(latest().editable).toBe(false)
  expect(connectionPresentation(latest()).card).toBe(false)
})

it('counts every drop so the detail panel can report reliability', async () => {
  const { simulation, latest } = await mount()
  simulation.apply('offline'); simulation.apply('live'); simulation.apply('reconnecting')
  await nextTick()
  expect(latest().link?.drops).toBe(2)
})

it('flaps between healthy and interrupted until it is stopped', async () => {
  vi.useFakeTimers()
  const { simulation, latest } = await mount()
  const stop = simulation.flap(700)
  vi.advanceTimersByTime(700); await nextTick()
  expect(latest().connection).toBe('reconnecting')
  vi.advanceTimersByTime(700); await nextTick()
  expect(latest().connection).toBe('online')
  vi.advanceTimersByTime(700); await nextTick()
  expect(latest().connection).toBe('reconnecting')
  stop(); await nextTick()
  expect(latest().connection).toBe('online')
  vi.advanceTimersByTime(5000); await nextTick()
  expect(latest().connection).toBe('online')
})

it('ignores further states once it is destroyed', async () => {
  const { simulation, latest } = await mount()
  simulation.destroy()
  simulation.apply('offline')
  await nextTick()
  expect(latest().connection).toBe('online')
})
