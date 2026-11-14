// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, inject, nextTick, provide, shallowReactive, shallowRef, type App } from 'vue'
import { Editor, Node as TiptapNode, type AnyExtension } from '@tiptap/core'
import Collaboration from '@tiptap/extension-collaboration'
import * as Y from 'yjs'
import { NodeViewWrapper, VueNodeViewRenderer, nodeViewProps } from '@tiptap/vue-3'
import PoolEditor from '../PoolEditor.vue'
import { projectEditor } from '../engine/projections'

interface ExposedEditor { editor: Editor; wordCount: number; charCount: number; dirty: boolean }
const apps: App[] = []
const documents: Y.Doc[] = []
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
})
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  documents.splice(0).forEach(doc => doc.destroy())
  vi.useRealTimers()
  vi.unstubAllGlobals()
  document.body.replaceChildren()
})

async function mount(overrides: { modelValue?: string; ariaLabel?: string; compact?: boolean; readonly?: boolean; profile?: 'rich' | 'markdown-safe'; collaborative?: boolean; extensions?: AnyExtension[]; onCreated?: (editor: Editor) => void } = {}) {
  const props = shallowReactive({ modelValue: 'Initial text', ariaLabel: 'Editor Markdown', compact: false, readonly: false, hideBlockHandle: true, ...overrides })
  const exposed = shallowRef<ExposedEditor | null>(null)
  const changed = vi.fn()
  const replaced = vi.fn()
  const created = vi.fn((editor: Editor) => overrides.onCreated?.(editor))
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp({ render: () => h(PoolEditor, {
    ...props, ref: exposed, onCreated: created, 'onUpdate:modelValue': changed, onContentReplaced: replaced,
  }) })
  apps.push(app)
  app.mount(host)
  await nextTick()
  await vi.advanceTimersByTimeAsync(0)
  await nextTick()
  return { props, exposed, editor: exposed.value!.editor, host, app, changed, replaced, created }
}

describe('legacy PoolEditor compatibility on the shared controller', () => {
  it('exposes one raw editor and preserves synchronous counters and structural dirty state', async () => {
    const { editor, exposed, created, changed } = await mount()
    expect(editor).toBeInstanceOf(Editor)
    expect(created).toHaveBeenCalledExactlyOnceWith(editor)
    expect(exposed.value!.wordCount).toBe(2)
    expect(exposed.value!.charCount).toBe(12)
    expect(exposed.value!.dirty).toBe(false)
    editor.commands.insertContent('New ')
    await nextTick()
    expect(exposed.value!.wordCount).toBe(3)
    expect(exposed.value!.charCount).toBe(16)
    expect(exposed.value!.dirty).toBe(true)
    await vi.advanceTimersByTimeAsync(119)
    expect(changed).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(changed).toHaveBeenCalledExactlyOnceWith('New Initial text')
  })

  it('ignores a parent echo during typing but applies an external reset and cancels the stale emit', async () => {
    const { editor, props, changed, replaced, exposed } = await mount()
    editor.commands.insertContent('A')
    await vi.advanceTimersByTimeAsync(120)
    const echo = changed.mock.calls[0][0]
    editor.commands.insertContent('B')
    const current = editor.getText()
    props.modelValue = echo
    await nextTick()
    expect(editor.getText()).toBe(current)
    expect(replaced).not.toHaveBeenCalled()
    props.modelValue = 'External replacement'
    await nextTick()
    expect(editor.getText()).toBe('External replacement')
    expect(exposed.value!.dirty).toBe(false)
    expect(replaced).toHaveBeenCalledExactlyOnceWith(editor)
    await vi.advanceTimersByTimeAsync(120)
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('flushes pending Markdown on blur and on unmount without duplicate emissions', async () => {
    const { editor, changed, app } = await mount()
    editor.commands.insertContent('First ')
    editor.view.dom.dispatchEvent(new FocusEvent('blur'))
    expect(changed).toHaveBeenCalledExactlyOnceWith('First Initial text')
    await vi.advanceTimersByTimeAsync(120)
    expect(changed).toHaveBeenCalledTimes(1)
    editor.commands.insertContent('Second ')
    app.unmount()
    apps.splice(apps.indexOf(app), 1)
    expect(changed).toHaveBeenLastCalledWith('First Second Initial text')
    expect(changed).toHaveBeenCalledTimes(2)
    expect(editor.isDestroyed).toBe(true)
    await vi.advanceTimersByTimeAsync(120)
    expect(changed).toHaveBeenCalledTimes(2)
  })

  it('publishes compact input on the next timer tick and applies readonly changes to the same editor', async () => {
    const { editor, props, changed, exposed } = await mount({ compact: true })
    editor.commands.insertContent('Quick ')
    await vi.advanceTimersByTimeAsync(0)
    expect(changed).toHaveBeenCalledExactlyOnceWith('Quick Initial text')
    props.readonly = true
    await nextTick()
    expect(editor.isEditable).toBe(false)
    props.readonly = false
    await nextTick()
    expect(editor.isEditable).toBe(true)
    expect(exposed.value!.editor).toBe(editor)
  })

  it('applies an accessible label initially and updates it without rebuilding the editor', async () => {
    const { editor, props } = await mount({ ariaLabel: 'Popisek požadavku' })
    expect(editor.view.dom.getAttribute('aria-label')).toBe('Popisek požadavku')
    props.ariaLabel = 'Podrobnosti požadavku'
    await nextTick()
    expect(editor.view.dom.getAttribute('aria-label')).toBe('Podrobnosti požadavku')
  })

  it('accepts Markdown for rich legacy editors and keeps rich-only edits while emitting a lossy projection', async () => {
    const { editor, props, changed } = await mount({ profile: 'rich', modelValue: '**Initial** text' })
    expect(editor.getHTML()).toContain('<strong>Initial</strong>')
    editor.commands.setTextSelection({ from: 1, to: 8 })
    editor.commands.toggleUnderline()
    await vi.advanceTimersByTimeAsync(120)
    expect(typeof changed.mock.calls[0][0]).toBe('string')
    expect(editor.getHTML()).toContain('<u>')
    expect(projectEditor(editor).markdownLossy).toBe(true)
    props.modelValue = '## Replacement'
    await nextTick()
    expect(editor.getHTML()).toContain('<h2>Replacement</h2>')
  })

  it('keeps externally supplied custom nodes and their serializers in the shared schema', async () => {
    const issue = TiptapNode.create({
      name: 'legacyIssue', group: 'block', atom: true,
      addAttributes: () => ({ key: { default: 'APP-1' } }),
      parseHTML: () => [{ tag: 'legacy-issue' }],
      renderHTML: ({ node }) => ['legacy-issue', { 'data-key': node.attrs.key }, node.attrs.key],
      addStorage: () => ({ markdown: { serialize: (state: { write(text: string): void; closeBlock(node: unknown): void }, node: { attrs: { key: string } }) => {
        state.write(node.attrs.key)
        state.closeBlock(node)
      } } }),
    })
    const { editor, changed } = await mount({ profile: 'rich', extensions: [issue], modelValue: 'Before' })
    editor.commands.insertContent({ type: 'legacyIssue', attrs: { key: 'APP-42' } })
    await vi.advanceTimersByTimeAsync(120)
    expect(editor.schema.nodes.legacyIssue).toBeDefined()
    expect(editor.view.dom.querySelector('legacy-issue')?.textContent).toBe('APP-42')
    expect(changed).toHaveBeenLastCalledWith(expect.stringContaining('APP-42'))
    expect(projectEditor(editor).rich.document).toEqual(editor.getJSON())
  })

  it('leaves collaborative seeding and Y.Doc ownership with the consumer and ignores inbound Markdown', async () => {
    const doc = new Y.Doc()
    documents.push(doc)
    const extension = Collaboration.configure({ document: doc })
    const { editor, props, changed, app } = await mount({
      collaborative: true, extensions: [extension], modelValue: 'Do not seed from model',
      onCreated: raw => { expect(raw.getText()).toBe(''); raw.commands.setContent('Shared seed') },
    })
    expect(editor.getText()).toBe('Shared seed')
    expect(editor.extensionManager.extensions.some(extension => extension.name === 'undoRedo')).toBe(false)
    props.modelValue = 'Stale parent snapshot'
    await nextTick()
    expect(editor.getText()).toBe('Shared seed')
    await vi.advanceTimersByTimeAsync(120)
    expect(changed).toHaveBeenLastCalledWith('Shared seed')
    const second = await mount({ collaborative: true, extensions: [Collaboration.configure({ document: doc })], modelValue: 'Also ignored' })
    expect(second.editor.getText()).toBe('Shared seed')
    editor.commands.insertContent('Remote ')
    expect(second.editor.getText()).toBe(editor.getText())
    expect(second.editor.getText()).toContain('Remote ')
    app.unmount()
    apps.splice(apps.indexOf(app), 1)
    expect(doc.isDestroyed).toBe(false)
    second.editor.commands.insertContent('Still editable ')
    expect(second.editor.getText()).toContain('Still editable ')
  })

  it('renders Vue node views with the consumer injection context using the same raw editor', async () => {
    const view = defineComponent({
      props: nodeViewProps,
      setup(props) {
        const label = inject<string>('issue-label')
        return () => h(NodeViewWrapper, { 'data-testid': 'vue-issue' }, `${label}: ${props.node.attrs.key}`)
      },
    })
    const issue = TiptapNode.create({
      name: 'vueIssue', group: 'block', atom: true,
      addAttributes: () => ({ key: { default: 'APP-7' } }),
      parseHTML: () => [{ tag: 'vue-issue' }],
      renderHTML: () => ['vue-issue'],
      addNodeView: () => VueNodeViewRenderer(view),
      addStorage: () => ({ markdown: { serialize: (state: { write(text: string): void; closeBlock(node: unknown): void }, node: { attrs: { key: string } }) => {
        state.write(node.attrs.key)
        state.closeBlock(node)
      } } }),
    })
    let editor!: Editor
    const host = document.createElement('div')
    document.body.appendChild(host)
    const app = createApp({
      setup() {
        provide('issue-label', 'Ticket')
        return () => h(PoolEditor, { modelValue: 'Before', extensions: [issue], hideBlockHandle: true, onCreated: value => { editor = value } })
      },
    })
    apps.push(app)
    app.mount(host)
    await nextTick()
    await vi.advanceTimersByTimeAsync(0)
    editor.commands.insertContent({ type: 'vueIssue', attrs: { key: 'APP-42' } })
    await nextTick()
    expect(host.querySelector('[data-testid="vue-issue"]')?.textContent).toBe('Ticket: APP-42')
    expect(editor).toBeInstanceOf(Editor)
  })
})
