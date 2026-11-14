// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownEditorController } from '../../core/controller'
import { editorForController, registerCollaborationAdapter } from '../../core/internal'
import type { MarkdownEditorController } from '../../core/types'
import { createCanonicalUpdateCollector, type CanonicalUpdateBatch, type CanonicalUpdateCollector } from './canonicalUpdates'

const cleanup: Array<() => void> = []
afterEach(() => { cleanup.splice(0).reverse().forEach(dispose => dispose()); document.body.replaceChildren(); vi.restoreAllMocks() })
function fixture() {
  const doc = new Y.Doc(), batches: CanonicalUpdateBatch[] = [], order: string[] = [], errors = vi.fn()
  const remote = Symbol('remote')
  const collector = createCanonicalUpdateCollector({ doc, isRemoteOrigin: origin => origin === remote,
    onBatch: batch => { order.push('batch'); batches.push(batch) }, onError: errors })
  cleanup.push(() => { collector.destroy(); doc.destroy() })
  return { doc, batches, order, errors, remote, collector }
}
function mount(doc: Y.Doc, collector: CanonicalUpdateCollector): MarkdownEditorController<'rich'> {
  const adapter = registerCollaborationAdapter(publish => ({
    extensions: [Collaboration.configure({ document: doc })], isRemoteTransaction: () => false,
    attachEditor: editor => { collector.attachEditor(editor); publish({ hydrated: true, writable: true }) },
    detachEditor: () => collector.detachEditor(), destroy: () => collector.destroy(),
  }))
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Canonical edit batches' })
  cleanup.push(() => controller.destroy())
  return controller
}
function decoded(bytes: Uint8Array) {
  const doc = new Y.Doc(); Y.applyUpdate(doc, bytes)
  const value = doc.getXmlFragment('default').toString()
  doc.destroy(); return value
}

describe('canonical Yjs update collection', () => {
  it('collects one raw outer Yjs transaction with owned snapshot/update bytes', () => {
    const { doc, collector, batches, errors } = fixture()
    doc.transact(() => { doc.getText('text').insert(0, 'A'); doc.transact(() => doc.getText('text').insert(1, 'B')) })
    expect(batches).toHaveLength(1)
    const reader = new Y.Doc(); Y.applyUpdate(reader, batches[0]!.update)
    expect(reader.getText('text').toString()).toBe('AB'); reader.destroy()
    batches[0]!.snapshot.fill(0); batches[0]!.update.fill(0)
    expect(doc.getText('text').toString()).toBe('AB')
    expect(errors).not.toHaveBeenCalled()
    collector.destroy(); doc.getText('text').insert(2, 'C')
    expect(batches).toHaveLength(1)
  })

  it('does not echo remote operations and includes them in later local snapshots', () => {
    const { doc, batches, remote } = fixture(), server = new Y.Doc()
    server.getText('text').insert(0, 'Remote')
    Y.applyUpdate(doc, Y.encodeStateAsUpdate(server), remote)
    expect(batches).toHaveLength(0)
    doc.getText('text').insert(doc.getText('text').length, ' Local')
    expect(batches).toHaveLength(1)
    const reader = new Y.Doc(); Y.applyUpdate(reader, batches[0]!.snapshot)
    expect(reader.getText('text').toString()).toBe('Remote Local')
    reader.destroy(); server.destroy()
  })

  it('seals appended changes once before controller content subscribers run', () => {
    const { doc, collector, batches, order, errors } = fixture()
    const controller = mount(doc, collector), editor = editorForController(controller)!
    const baseline = Y.encodeStateAsUpdate(doc)
    batches.length = 0; order.length = 0
    editor.registerPlugin(new Plugin({ appendTransaction: (transactions, _previous, current) =>
      transactions.some(transaction => transaction.getMeta('append')) ? current.tr.insertText('B', 1) : null }))
    let revision = controller.state.contentRevision
    controller.subscribe(state => { if (state.contentRevision !== revision) { revision = state.contentRevision; order.push('subscriber') } })
    editor.view.dispatch(editor.state.tr.insertText('A', 1).setMeta('append', true))
    expect(order).toEqual(['batch', 'subscriber'])
    expect(batches).toHaveLength(1)
    expect(decoded(batches[0]!.snapshot)).toContain('BA')
    const reader = new Y.Doc(); Y.applyUpdate(reader, baseline); Y.applyUpdate(reader, batches[0]!.update)
    expect(reader.getXmlFragment('default').toString()).toContain('BA')
    reader.destroy(); expect(errors).not.toHaveBeenCalled()
  })

  it('seals a subscriber-triggered reentrant edit as a second independent batch', () => {
    const { doc, collector, batches, order, errors } = fixture()
    const controller = mount(doc, collector), editor = editorForController(controller)!
    batches.length = 0; order.length = 0
    let revision = controller.state.contentRevision, edited = false
    controller.subscribe(state => {
      if (state.contentRevision === revision) return
      revision = state.contentRevision; order.push('subscriber')
      if (!edited) { edited = true; editor.view.dispatch(editor.state.tr.insertText('B', 1)) }
    })
    editor.view.dispatch(editor.state.tr.insertText('A', 1))
    expect(order).toEqual(['batch', 'subscriber', 'batch', 'subscriber'])
    expect(batches).toHaveLength(2)
    expect(decoded(batches[0]!.snapshot)).toContain('A')
    expect(decoded(batches[0]!.snapshot)).not.toContain('BA')
    expect(decoded(batches[1]!.snapshot)).toContain('BA')
    expect(errors).not.toHaveBeenCalled()
  })

  it('includes raw Yjs mutations from append hooks in the same final batch', () => {
    const { doc, collector, batches, errors } = fixture()
    const controller = mount(doc, collector), editor = editorForController(controller)!
    batches.length = 0
    editor.registerPlugin(new Plugin({ appendTransaction: transactions => {
      if (transactions.some(transaction => transaction.getMeta('raw-change'))) doc.getMap('metadata').set('version', 2)
      return null
    } }))
    editor.view.dispatch(editor.state.tr.insertText('Text', 1).setMeta('raw-change', true))
    expect(batches).toHaveLength(1)
    const reader = new Y.Doc(); Y.applyUpdate(reader, batches[0]!.snapshot)
    expect(reader.getMap('metadata').get('version')).toBe(2)
    expect(reader.getXmlFragment('default').toString()).toContain('Text')
    reader.destroy(); expect(errors).not.toHaveBeenCalled()
  })

  it('reports rejected dispatches that nevertheless mutate Yjs and emits no apparently accepted batch', () => {
    const { doc, collector, batches, errors } = fixture()
    const controller = mount(doc, collector), editor = editorForController(controller)!
    batches.length = 0
    editor.registerPlugin(new Plugin({ filterTransaction: transaction => {
      if (!transaction.getMeta('reject')) return true
      doc.getMap('metadata').set('unaccepted', true)
      return false
    } }))
    editor.view.dispatch(editor.state.tr.insertText('Rejected', 1).setMeta('reject', true))
    expect(batches).toHaveLength(0)
    expect(errors).toHaveBeenCalledOnce()
    expect(errors.mock.calls[0]![0].message).toContain('did not install an accepted state')
  })

  it('restores the original editor dispatch and keeps the explicit raw-Yjs fallback after detach', () => {
    const { doc, collector, batches, errors } = fixture()
    const controller = mount(doc, collector), editor = editorForController(controller)!
    const wrapped = editor.view.props.dispatchTransaction
    collector.detachEditor()
    expect(editor.view.props.dispatchTransaction).not.toBe(wrapped)
    batches.length = 0
    doc.getMap('metadata').set('detached', true)
    expect(batches).toHaveLength(1)
    collector.attachEditor(editor)
    editor.view.dispatch(editor.state.tr.insertText('Reattached', 1))
    expect(batches).toHaveLength(2)
    expect(errors).not.toHaveBeenCalled()
  })

  it('stops collection after a failing batch consumer without allowing its error to escape Yjs', () => {
    const doc = new Y.Doc(), errors = vi.fn(), onBatch = vi.fn(() => { throw new Error('Queue unavailable') })
    const collector = createCanonicalUpdateCollector({ doc, isRemoteOrigin: () => false, onBatch, onError: errors })
    expect(() => doc.getText('text').insert(0, 'A')).not.toThrow()
    doc.getText('text').insert(1, 'B')
    expect(onBatch).toHaveBeenCalledOnce(); expect(errors).toHaveBeenCalledOnce()
    expect(doc.getText('text').toString()).toBe('AB')
    collector.destroy(); doc.destroy()
  })
})
