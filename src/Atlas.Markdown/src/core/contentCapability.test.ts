// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Node } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownEditorController, getMarkdownContentCapability } from './index'
import { editorForController, registerCollaborationAdapter } from './internal'
import type { MarkdownEditorController, MarkdownPlugin, MarkdownRichValue } from './index'

const controllers: MarkdownEditorController[] = []
const rich = (text = 'Original'): MarkdownRichValue => ({ format: 'atlas-rich', version: 1,
  document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] } })
function mount(plugins: MarkdownPlugin[] = [], value = rich()) {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  const detach = controller.attachView(host, { ariaLabel: 'Content capability' })
  return { controller, editor: editorForController(controller)!, capability: getMarkdownContentCapability(controller)!, detach }
}
afterEach(() => {
  for (const controller of controllers.splice(0)) controller.destroy()
  document.body.replaceChildren(); vi.restoreAllMocks()
})

describe('optional revision and snapshot capability', () => {
  it('does not invent capabilities for structural external controllers', () => {
    expect(getMarkdownContentCapability({} as MarkdownEditorController)).toBeNull()
  })

  it('emits one lightweight event per accepted batch and exports only when requested', () => {
    const { controller, editor, capability } = mount()
    const prototype = Object.getPrototypeOf(editor.state.doc)
    const json = vi.spyOn(prototype, 'toJSON'), getValue = vi.spyOn(controller, 'getValue')
    const events = vi.fn(), stop = capability.subscribe(events)
    editor.registerPlugin(new Plugin({ appendTransaction: (transactions, _old, state) => transactions.some(tr => tr.getMeta('append'))
      ? state.tr.insertText('B', 1) : null }))
    editor.view.dispatch(editor.state.tr.insertText('A', 1).setMeta('append', true))
    editor.view.dispatch(editor.state.tr.setMeta('selection-only', true))
    expect(events).toHaveBeenCalledOnce()
    expect(events.mock.calls[0]![0]).toEqual({ ...capability.getStamp(), origin: 'local' })
    expect(getValue).not.toHaveBeenCalled(); expect(json).not.toHaveBeenCalled()
    expect(capability.getSnapshot().value.document).toMatchObject({ content: [{ content: [{ text: 'BAOriginal' }] }] })
    expect(json).toHaveBeenCalled()
    stop()
    editor.view.dispatch(editor.state.tr.insertText('C', 1))
    expect(events).toHaveBeenCalledOnce()
  })

  it('keeps event order under reentrant edits and gives later readers the current identity', () => {
    const { editor, capability } = mount()
    const revisions: number[] = []
    capability.subscribe(change => { if (change.revision === 1) editor.view.dispatch(editor.state.tr.insertText('B', 1)) })
    capability.subscribe(change => revisions.push(change.revision))
    editor.view.dispatch(editor.state.tr.insertText('A', 1))
    expect(revisions).toEqual([1, 2])
    const snapshot = capability.getSnapshot()
    expect(snapshot.revision).toBe(2)
    expect(snapshot.value.document).toMatchObject({ content: [{ content: [{ text: 'BAOriginal' }] }] })
  })

  it('returns detached deeply frozen snapshots and distinct session identities', () => {
    const first = mount(), second = mount()
    const snapshot = first.capability.getSnapshot()
    expect(snapshot.sessionId).not.toBe(second.capability.getStamp().sessionId)
    expect(Object.isFrozen(snapshot.value.document.content)).toBe(true)
    expect(() => Object.assign(snapshot.value.document, { type: 'bad' })).toThrow()
    first.editor.view.dispatch(first.editor.state.tr.insertText('New ', 1))
    expect(snapshot.revision).toBe(0)
    expect(JSON.stringify(snapshot.value)).not.toContain('New ')
    expect(first.capability.getSnapshot().revision).toBe(1)
  })

  it('tracks detached replacement and keeps snapshot identity across view remount', () => {
    const { controller, capability, detach } = mount()
    const sessionId = capability.getStamp().sessionId
    detach(); controller.setValue(rich('Detached'))
    expect(capability.getSnapshot()).toMatchObject({ sessionId, revision: 1, value: rich('Detached') })
    const host = document.createElement('div'); document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Remount' })
    expect(capability.getSnapshot().sessionId).toBe(sessionId)
  })

  it('never labels a stale detached collaborative projection as a current snapshot', () => {
    const adapter = registerCollaborationAdapter(publish => ({ extensions: [], isRemoteTransaction: () => true,
      attachEditor: () => publish({ hydrated: true, writable: true }), detachEditor() {}, destroy() {} }))
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    const capability = getMarkdownContentCapability(controller)!
    expect(() => capability.getSnapshot()).toThrow(/Attach a collaborative/)
    const host = document.createElement('div'); document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Collaboration' })
    capability.getSnapshot(); detach()
    expect(() => capability.getSnapshot()).toThrow(/Attach a collaborative/)
  })
})

describe('incremental validation per transaction', () => {
  it('refuses invalid content in a block the transaction makes', () => {
    const { controller, editor } = mount()
    editor.view.dispatch(editor.state.tr.insertText('A', 1))
    editor.view.dispatch(editor.state.tr.setNodeMarkup(0, undefined, { ...editor.state.doc.firstChild!.attrs, textAlign: 'unsafe-alignment' }))
    expect(controller.state).toMatchObject({ lifecycle: 'failed', editable: false, error: { kind: 'content' } })
  })

  it('skips the blocks an earlier transaction accepted, and reading the value checks everything', () => {
    // Attrs of its own (default attrs are one object shared by every paragraph): only this block changes in place.
    const { controller, editor, capability } = mount(undefined, { format: 'atlas-rich', version: 1,
      document: { type: 'doc', content: [{ type: 'paragraph', attrs: { textAlign: 'center' }, content: [{ type: 'text', text: 'First' }] }, { type: 'paragraph' }] } })
    editor.view.dispatch(editor.state.tr.insertText('A', 1))
    Object.assign(editor.state.doc.firstChild!.attrs, { textAlign: 'unsafe-alignment' })
    editor.view.dispatch(editor.state.tr.insertText('B', editor.state.doc.content.size - 1))
    expect(controller.state.lifecycle).toBe('ready')
    expect(() => capability.getSnapshot()).toThrow()
    expect(controller.state).toMatchObject({ lifecycle: 'failed', error: { kind: 'content' } })
  })
})

describe('general content and projection failure state', () => {
  it('fails visibly when live mutable attributes become invalid, then refuses further controller writes', () => {
    const { controller, editor, capability } = mount()
    Object.assign(editor.state.doc.firstChild!.attrs, { textAlign: 'unsafe-alignment' })
    editor.view.dispatch(editor.state.tr.setMeta('validate', true))
    expect(controller.state).toMatchObject({ lifecycle: 'failed', editable: false, documentSave: 'degraded', projectionSave: 'degraded',
      error: { kind: 'content', recoverable: false } })
    expect(() => capability.getSnapshot()).toThrow()
    expect(() => controller.getValue()).toThrow()
    expect(() => controller.setValue(rich('Replacement'))).toThrow()
    controller.setEditable(true)
    expect(controller.state.editable).toBe(false)
    expect(controller.dispatch({ type: 'toggle-bold' })).toBe(false)
  })

  it('reports Markdown projection failure while preserving a valid rich value and allowing retry', () => {
    let fail = true
    const plugin: MarkdownPlugin = { id: 'test.projection-failure', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
      create: () => ({ markdownLossy: () => true, extensions: [Node.create({ name: 'projectionProbe', group: 'block', atom: true,
        renderHTML: () => ['div'], renderText: () => 'Valid rich content',
        addStorage: () => ({ markdown: { serialize: (state: { write(value: string): void }) => {
          if (fail) throw new Error('Markdown serializer unavailable')
          state.write('Recovered projection')
        } } }),
      })] }) }
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, plugins: [{ id: plugin.id, version: 1 }],
      document: { type: 'doc', content: [{ type: 'projectionProbe' }] } }
    const { controller, capability } = mount([plugin], value)
    expect(() => controller.getProjection()).toThrow('Markdown serializer unavailable')
    expect(controller.state).toMatchObject({ lifecycle: 'ready', editable: true, documentSave: 'local', projectionSave: 'degraded', error: { kind: 'projection' } })
    expect(capability.getSnapshot().value.document).toEqual(value.document)
    fail = false
    expect(controller.getProjection().markdown).toBe('Recovered projection')
    expect(controller.state).toMatchObject({ error: null, projectionSave: 'local' })
  })
})
