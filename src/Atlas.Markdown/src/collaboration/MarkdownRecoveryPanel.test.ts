// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, reactive } from 'vue'
import MarkdownRecoveryPanel from './MarkdownRecoveryPanel.vue'
import type { JournalRecord } from '../collab/recovery/durableJournal'
const mock = vi.hoisted(() => ({ prepare: vi.fn() }))
vi.mock('../collab/recovery/durablePreview', () => ({ prepareDurableRecovery: mock.prepare }))
const uuid = '11111111-2222-4333-8444-555555555555'
const scope = { serverId: 's', tenantId: 't', accountId: 'a', documentId: 'd' }
const schema = { version: 2 as const, profile: 'rich' as const, plugins: [] }
function record(): JournalRecord { return { version: 2, scope, branchId: uuid, owner: uuid, epoch: uuid, schema, fragment: 'default', revision: 1, sequence: 0, snapshot: new Uint8Array([0, 0]), pending: [] } }
const cleanups: Array<() => void> = []
afterEach(() => { cleanups.splice(0).forEach(fn => fn()); vi.clearAllMocks() })
const flush = async () => { await Promise.resolve(); await nextTick(); await Promise.resolve(); await nextTick() }
function mount(list = vi.fn(async () => [record()])) {
  const props = reactive({ store: { list, read: vi.fn(), write: vi.fn(), discard: vi.fn(), close: vi.fn() }, scope: { ...scope }, schema, plugins: [] as any[] })
  const element = document.createElement('div'); document.body.append(element)
  const app = createApp({ render: () => h(MarkdownRecoveryPanel, props) }); app.mount(element)
  cleanups.push(() => { app.unmount(); element.remove() })
  const button = (text: string) => [...element.querySelectorAll('button')].find(button => button.textContent?.includes(text))!
  return { props, element, button }
}
describe('local recovery panel boundaries', () => {
  it('rejects a custom store returning branches belonging to another account', async () => {
    const f = mount(vi.fn(async () => [{ ...record(), scope: { ...scope, accountId: 'foreign' } }]))
    await flush(); expect(f.element.querySelector('[role="alert"]')?.textContent).toContain('jiného účtu')
    expect(f.element.textContent).not.toContain(uuid.slice(0, 8))
  })
  it('copies custom store metadata before a later host mutation', async () => {
    const row = record(), f = mount(vi.fn(async () => [row]))
    await flush(); (row as any).branchId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
    mock.prepare.mockReturnValue(new Promise(() => {})); f.button('Prohlédnout').click(); await flush()
    expect(mock.prepare.mock.calls[0][0].branchId).toBe(uuid)
  })
  it('allows cancelling a preview while the selected store read is still pending', async () => {
    mock.prepare.mockReturnValue(new Promise(() => {})); const f = mount(); await flush(); f.button('Prohlédnout').click(); await flush()
    const signal: AbortSignal = mock.prepare.mock.calls[0][0].signal
    const cancel = f.button('Zavřít náhled'); expect(cancel.disabled).toBe(false); cancel.click(); await flush()
    expect(signal.aborted).toBe(true); expect(f.element.textContent).not.toContain('Vybraná větev')
  })
  it('cancels a pending preview when host plugin configuration changes', async () => {
    mock.prepare.mockReturnValue(new Promise(() => {})); const f = mount(); await flush(); f.button('Prohlédnout').click(); await flush()
    const signal: AbortSignal = mock.prepare.mock.calls[0][0].signal
    f.props.plugins = [{ id: 'other' }]; await flush()
    expect(signal.aborted).toBe(true); expect(f.element.textContent).not.toContain('Vybraná větev')
  })
  it('does not publish an obsolete account list after the host switches accounts', async () => {
    let finish!: (rows: JournalRecord[]) => void
    const first = new Promise<JournalRecord[]>(resolve => { finish = resolve })
    const list = vi.fn().mockReturnValueOnce(first).mockResolvedValue([]), f = mount(list)
    f.props.scope = { ...scope, accountId: 'new' }; await flush(); finish([record()]); await flush()
    expect(f.element.textContent).not.toContain(uuid.slice(0, 8))
  })
})
