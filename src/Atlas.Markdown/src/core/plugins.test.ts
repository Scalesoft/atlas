// @vitest-environment jsdom
import { Node } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, defineMarkdownCommand, migrateMarkdownValue, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from './index'

const controllers: MarkdownEditorController[] = []
afterEach(() => {
  for (const controller of controllers.splice(0)) controller.destroy()
  document.body.replaceChildren()
})

const serialized = vi.fn()
const Badge = Node.create({
  name: 'testBadge', group: 'inline', inline: true, atom: true,
  addAttributes: () => ({ label: { default: '' } }),
  parseHTML: () => [{ tag: 'span[data-badge]' }],
  renderHTML: ({ node }) => ['span', { 'data-badge': '' }, node.attrs.label],
  renderText: ({ node }) => node.attrs.label,
  addStorage: () => ({ markdown: { serialize(state: { write(text: string): void }, node: { attrs: { label: string } }) {
    serialized()
    state.write(node.attrs.label)
  } } }),
})
const insertBadge = defineMarkdownCommand<{ label: string }>({
  id: 'test.insert-badge',
  apply: args => ({ commands }) => commands.insertContent({ type: 'testBadge', attrs: args }),
  active: editor => editor.isActive('testBadge'),
})
const plugin: MarkdownPlugin = {
  id: 'test.badge', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
  create: () => ({
    extensions: [Badge.configure({})], commands: [insertBadge],
    actions: { slash: [{ id: 'test.badge', label: 'Badge', command: insertBadge({ label: 'Hello' }) }] },
    markdownLossy(document) {
      let found = false
      document.descendants(node => { found ||= node.type.name === 'testBadge' })
      return found
    },
  }),
}
const rich = (text = ''): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: {
  type: 'doc', content: [{ type: 'paragraph', ...(text ? { content: [{ type: 'text', text }] } : {}) }],
} })
function mount(value = rich(), plugins: readonly MarkdownPlugin[] = [plugin]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value } })
  controllers.push(controller)
  const host = document.createElement('div')
  document.body.append(host)
  const detach = controller.attachView(host, { ariaLabel: 'Plugin editor' })
  return { controller, host, detach }
}

describe('public plugin contract', () => {
  it('uses the configured schema for loading, replacement and loss-aware roundtrips', () => {
    const { controller, host } = mount()
    expect(controller.dispatch(insertBadge({ label: 'Issue-42' }))).toBe(true)
    const value = controller.getValue()
    expect(value.plugins).toEqual([{ id: 'test.badge', version: 1 }])
    expect(controller.getProjection()).toMatchObject({ markdown: 'Issue-42', text: 'Issue-42', markdownLossy: true })
    expect(host.querySelector('[data-badge]')?.textContent).toBe('Issue-42')
    const reopened = mount(value)
    expect(reopened.controller.getValue()).toEqual(value)
    reopened.controller.setValue(rich('Replacement'))
    reopened.controller.setValue(value)
    expect(reopened.host.querySelector('[data-badge]')?.textContent).toBe('Issue-42')
  })

  it('rejects missing or incompatible plugin schemas atomically', () => {
    const { controller } = mount()
    controller.dispatch(insertBadge({ label: 'Retain me' }))
    const value = controller.getValue()
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value } })).toThrow(/missing plugin/)
    expect(() => controller.setValue({ ...value, plugins: [{ id: plugin.id, version: 2 }] })).toThrow(/newer|schema/i)
    expect(controller.getValue()).toEqual(value)
  })

  it('runs ordered plugin data migrations before validating the current schema', () => {
    const oldValue: MarkdownRichValue = {
      format: 'atlas-rich', version: 1, plugins: [{ id: plugin.id, version: 1 }],
      document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'testBadge', attrs: { label: 'Legacy' } }] }] },
    }
    const pluginV2: MarkdownPlugin = {
      ...plugin,
      schemaVersion: 2,
      migrations: [{ from: 1, to: 2, migrate(document) {
        const result = structuredClone(document)
        const badge = result.content?.[0]?.content?.[0]
        if (badge?.type === 'testBadge') badge.attrs = { ...badge.attrs, label: `v2:${String(badge.attrs?.label)}` }
        return result
      } }],
    }

    const migrated = migrateMarkdownValue(oldValue, 'rich', [pluginV2])

    expect(migrated.plugins).toEqual([{ id: plugin.id, version: 2 }])
    expect((migrated.document.content as Array<{ content: Array<{ attrs: { label: string } }> }>)[0].content[0].attrs.label).toBe('v2:Legacy')
    expect((oldValue.document.content as Array<{ content: Array<{ attrs: { label: string } }> }>)[0].content[0].attrs.label).toBe('Legacy')
  })

  it('checks query without mutation and replaces a slash range in one undo step', () => {
    const { controller, host } = mount(rich('/badge'))
    const listener = vi.fn()
    controller.subscribe(listener)
    const revision = controller.state.contentRevision
    const command = insertBadge({ label: 'Chosen' })
    const options = { replaceRange: { from: 1, to: 7 } }
    expect(controller.query(command, options).enabled).toBe(true)
    expect(listener).toHaveBeenCalledTimes(1)
    expect(controller.state.contentRevision).toBe(revision)
    expect(controller.dispatch(command, options)).toBe(true)
    expect(host.textContent).toBe('Chosen')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(host.textContent).toBe('/badge')
  })

  it('never applies unregistered, forged, readonly or out-of-range commands', () => {
    const { controller } = mount(rich('Keep'))
    const otherFactory = defineMarkdownCommand({ id: insertBadge.id, apply: () => () => true })
    const initial = controller.getValue()
    for (const command of [otherFactory(), { type: 'plugin-command', id: insertBadge.id } as ReturnType<typeof insertBadge>]) {
      expect(controller.query(command).enabled).toBe(false)
      expect(controller.dispatch(command, { replaceRange: { from: 1, to: 5 } })).toBe(false)
    }
    expect(controller.dispatch(insertBadge({ label: 'No' }), { replaceRange: { from: -1, to: 100 } })).toBe(false)
    controller.setEditable(false)
    expect(controller.dispatch(insertBadge({ label: 'No' }))).toBe(false)
    expect(controller.getValue()).toEqual(initial)
  })

  it('evaluates slash availability against the replaced document without dispatching it', () => {
    const insertIntoEmpty = defineMarkdownCommand({ id: 'test.empty-only', apply: () => ({ tr, commands }) =>
      tr.selection.$from.parent.content.size === 0 && commands.insertContent('Inserted'),
    })
    const { controller, host } = mount(rich('/empty'), [{ id: 'empty-only', apiVersion: 1, create: () => ({ commands: [insertIntoEmpty] }) }])
    const options = { replaceRange: { from: 1, to: 7 } }
    const original = controller.getValue()
    expect(controller.query(insertIntoEmpty()).enabled).toBe(false)
    expect(controller.query(insertIntoEmpty(), options).enabled).toBe(true)
    expect(controller.getValue()).toEqual(original)
    expect(controller.dispatch(insertIntoEmpty(), options)).toBe(true)
    expect(host.textContent).toBe('Inserted')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(host.textContent).toBe('/empty')
  })

  it('takes command arguments by value', () => {
    const { controller, host } = mount()
    const args = { label: 'Captured' }
    const command = insertBadge(args)
    args.label = 'Changed'
    controller.dispatch(command)
    expect(host.textContent).toBe('Captured')
  })

  it('does not serialize Markdown when only the rich value is requested', () => {
    const { controller } = mount()
    controller.dispatch(insertBadge({ label: 'Large rich value' }))
    serialized.mockClear()
    controller.getValue()
    controller.getValue()
    expect(serialized).not.toHaveBeenCalled()
    controller.getProjection()
    controller.getProjection()
    expect(serialized).toHaveBeenCalledTimes(1)
  })

  it('isolates plugin state and distinguishes engine lifetime from attached views', () => {
    const created = vi.fn()
    const attached = vi.fn()
    const detached = vi.fn()
    const destroyed = vi.fn()
    const signals: AbortSignal[] = []
    const lifecycle: MarkdownPlugin = { id: 'lifecycle', apiVersion: 1, create: ({ signal }) => {
      signals.push(signal)
      return {
        onCreate: context => { expect(Object.isFrozen(context)).toBe(true); created(); return destroyed },
        onAttach: context => { expect(Object.isFrozen(context)).toBe(true); attached(); return detached },
      }
    } }
    const first = mount(rich(), [lifecycle])
    const second = mount(rich(), [lifecycle])
    first.detach()
    expect(detached).toHaveBeenCalledTimes(1)
    expect(signals.every(signal => !signal.aborted)).toBe(true)
    first.controller.attachView(first.host, { ariaLabel: 'Reattached' })
    expect(created).toHaveBeenCalledTimes(2)
    expect(attached).toHaveBeenCalledTimes(3)
    first.controller.destroy()
    expect(destroyed).toHaveBeenCalledTimes(1)
    expect(signals.map(signal => signal.aborted)).toEqual([true, false])
    expect(second.controller.state.lifecycle).toBe('ready')
  })

  it('validates dependency order, duplicate IDs and extension collisions before attaching', () => {
    const order: string[] = []
    const a: MarkdownPlugin = { id: 'a', apiVersion: 1, requires: ['b'], create: () => { order.push('a'); return {} } }
    const b: MarkdownPlugin = { id: 'b', apiVersion: 1, create: () => { order.push('b'); return {} } }
    mount(rich(), [a, b])
    expect(order).toEqual(['b', 'a'])
    expect(() => mount(rich(), [a])).toThrow(/requires b/)
    expect(() => mount(rich(), [b, b])).toThrow(/Duplicate plugin/)
    expect(() => mount(rich(), [a, { ...b, requires: ['a'] }])).toThrow(/Cyclic/)
    expect(() => mount(rich(), [plugin, { ...plugin, id: 'other' }])).toThrow(/Duplicate command/)
    expect(() => mount(rich(), [{ ...plugin, create: () => ({ extensions: [Node.create({ name: 'paragraph' })] }) }])).toThrow(/Duplicate content extension/)
    expect(() => createMarkdownEditorController({ profile: 'markdown-safe', plugins: [plugin], source: { kind: 'local', value: '' } })).toThrow(/does not support/)
  })

  it('cleans every hook even when one plugin throws during destruction', () => {
    const cleanup = vi.fn()
    const bad: MarkdownPlugin = { id: 'bad', apiVersion: 1, create: () => ({ onAttach: () => () => { throw new Error('Cleanup') } }) }
    const good: MarkdownPlugin = { id: 'good', apiVersion: 1, create: () => ({ onCreate: () => cleanup }) }
    const { controller, host } = mount(rich(), [bad, good])
    expect(() => controller.destroy()).toThrow(/cleanup/)
    expect(cleanup).toHaveBeenCalledTimes(1)
    expect(controller.state.lifecycle).toBe('destroyed')
    expect(host.textContent).toBe('')
  })
})
