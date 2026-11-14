// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Plugin, type Transaction } from '@tiptap/pm/state'
import { createMarkdownEditorController } from './controller'
import { editorForController } from './internal'

const controllers: ReturnType<typeof createMarkdownEditorController>[] = []
afterEach(() => {
  for (const controller of controllers.splice(0)) controller.destroy()
  document.body.replaceChildren()
})

function mount() {
  const controller = createMarkdownEditorController({
    profile: 'rich', source: { kind: 'local', value: {
      format: 'atlas-rich', version: 1,
      document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Original' }] }] },
    } },
  })
  controllers.push(controller)
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Transaction batches' })
  return { controller, editor: editorForController(controller)! }
}

describe('controller observes the final accepted transaction batch', () => {
  it('publishes an appended-only content change and invalidates a previously read projection', () => {
    const { controller, editor } = mount()
    expect(controller.getProjection().text).toBe('Original')
    editor.registerPlugin(new Plugin({
      appendTransaction: (transactions, _before, after) => transactions.some(tr => tr.getMeta('append-text'))
        ? after.tr.insertText('!', 1) : null,
    }))
    const revision = controller.state.contentRevision
    const seen: number[] = []
    controller.subscribe(state => { seen.push(state.contentRevision) })
    editor.view.dispatch(editor.state.tr.setMeta('append-text', true))
    expect(controller.state.contentRevision).toBe(revision + 1)
    expect(seen).toEqual([revision, revision + 1])
    expect(controller.getProjection().text).toBe('!Original')
    expect(controller.getValue().document).toMatchObject({ content: [{ content: [{ text: '!Original' }] }] })
  })

  it('counts a multi-plugin append chain once and executes each transition once', () => {
    const { controller, editor } = mount()
    const transitions = vi.fn()
    for (const [trigger, next, text] of [['start', 'second', 'A'], ['second', 'done', 'B']]) {
      editor.registerPlugin(new Plugin({
        appendTransaction: (transactions, _before, after) => {
          if (!transactions.some(tr => tr.getMeta(trigger!))) return null
          transitions(trigger)
          return after.tr.insertText(text!, 1).setMeta(next!, true)
        },
      }))
    }
    const revision = controller.state.contentRevision
    editor.view.dispatch(editor.state.tr.setMeta('start', true))
    expect(controller.state.contentRevision).toBe(revision + 1)
    expect(controller.getProjection().text).toBe('BAOriginal')
    expect(transitions.mock.calls).toEqual([['start'], ['second']])
  })

  it('does not publish a content revision when an appended repair restores the original content', () => {
    const { controller, editor } = mount()
    editor.registerPlugin(new Plugin({
      appendTransaction: (transactions, _before, after) => transactions.some(tr => tr.getMeta('cancel-text'))
        ? after.tr.delete(1, 2) : null,
    }))
    const revision = controller.state.contentRevision
    const value = controller.getValue()
    editor.view.dispatch(editor.state.tr.insertText('!', 1).setMeta('cancel-text', true))
    expect(controller.getValue()).toEqual(value)
    expect(controller.state.contentRevision).toBe(revision)
  })

  it('publishes appended stored marks without inventing a content revision', () => {
    const { controller, editor } = mount()
    editor.registerPlugin(new Plugin({
      appendTransaction: (transactions, _before, after) => transactions.some(tr => tr.getMeta('append-bold'))
        ? after.tr.addStoredMark(editor.schema.marks.bold!.create()) : null,
    }))
    const listener = vi.fn()
    controller.subscribe(listener)
    const revision = controller.state.contentRevision
    editor.view.dispatch(editor.state.tr.setMeta('append-bold', true))
    expect(controller.query({ type: 'toggle-bold' }).active).toBe(true)
    expect(controller.state.contentRevision).toBe(revision)
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it('keeps ordered revisions when a subscriber edits again during the appended change notification', () => {
    const { controller, editor } = mount()
    editor.registerPlugin(new Plugin({
      appendTransaction: (transactions, _before, after) => transactions.some(tr => tr.getMeta('append-text'))
        ? after.tr.insertText('A', 1) : null,
    }))
    const revision = controller.state.contentRevision
    let edited = false
    const seen: number[] = []
    controller.subscribe(state => {
      if (state.contentRevision === revision + 1 && !edited) {
        edited = true
        editor.view.dispatch(editor.state.tr.insertText('B', 1))
      }
    })
    controller.subscribe(state => { seen.push(state.contentRevision) })
    editor.view.dispatch(editor.state.tr.setMeta('append-text', true))
    expect(seen).toEqual([revision, revision + 1, revision + 2])
    expect(controller.getProjection().text).toBe('BAOriginal')
  })

  it('retains appended-only content if a subscriber destroys the controller immediately', () => {
    const { controller, editor } = mount()
    editor.registerPlugin(new Plugin({
      appendTransaction: (transactions, _before, after) => transactions.some(tr => tr.getMeta('append-text'))
        ? after.tr.insertText('A', 1) : null,
    }))
    const revision = controller.state.contentRevision
    controller.subscribe(state => { if (state.contentRevision > revision) controller.destroy() })
    expect(() => editor.view.dispatch(editor.state.tr.setMeta('append-text', true))).not.toThrow()
    expect(controller.state.lifecycle).toBe('destroyed')
    expect(controller.getProjection().text).toBe('AOriginal')
  })

  it('ignores rejected root transactions', () => {
    const { controller, editor } = mount()
    editor.registerPlugin(new Plugin({ filterTransaction: (tr: Transaction) => !tr.getMeta('reject') }))
    const revision = controller.state.contentRevision
    editor.view.dispatch(editor.state.tr.insertText('X', 1).setMeta('reject', true))
    expect(controller.state.contentRevision).toBe(revision)
    expect(controller.getProjection().text).toBe('Original')
  })
})
