// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController } from './index'
import { editorForController, registerCollaborationAdapter, type CollaborationUpdate } from './internal'

const controllers: MarkdownEditorController[] = []
afterEach(() => { for (const controller of controllers.splice(0)) controller.destroy() })

function session(editable = true) {
  let publish!: (update: CollaborationUpdate) => void
  const adapter = registerCollaborationAdapter(next => { publish = next; return { extensions: [], isRemoteTransaction: () => true, attachEditor() {}, detachEditor() {}, destroy() {} } })
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter }, editable })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Hydration' })
  return { controller, view: () => editorForController(controller)!.isEditable, publish }
}

describe('the view turns editable before the server document lands (perf review 2026-11-02)', () => {
  it('is editable from hydration on, across the synced-before-connected gap, while the state waits for the sync', () => {
    const { controller, view, publish } = session()
    publish({ hydrated: false, writable: false })
    expect(view()).toBe(false)
    publish({ hydrating: true })
    expect(view()).toBe(true)
    expect(controller.state.editable).toBe(false)
    // Synced first, writable once connected: the view does not flip back in between.
    publish({ hydrated: true, writable: false })
    expect(view()).toBe(true)
    expect(controller.state.editable).toBe(false)
    publish({ hydrated: true, writable: true })
    expect(view()).toBe(true)
    expect(controller.state.editable).toBe(true)
  })

  it('goes back to read-only when the session fails before it synced', () => {
    const { controller, view, publish } = session()
    publish({ hydrating: true })
    publish({ error: { kind: 'collaboration', message: 'refused', recoverable: false } })
    expect(view()).toBe(false)
    expect(controller.state.editable).toBe(false)
  })

  it('sets the view editable only when that changes: each update of the session restyled the whole document (3.6 perf)', () => {
    const { controller, publish } = session()
    const editor = editorForController(controller)!
    let calls = 0
    const setProps = editor.view.setProps.bind(editor.view)
    editor.view.setProps = props => { calls++; setProps(props) }
    publish({ hydrating: true })
    publish({ hydrated: true, writable: true })
    expect(calls).toBe(1)
    for (let i = 0; i < 5; i++) publish({ pendingChanges: i })
    expect(calls).toBe(1)
    expect(editor.view.dom.getAttribute('aria-readonly')).toBe('false')
    publish({ writable: false })
    expect(calls).toBe(2)
    expect(editor.view.dom.getAttribute('aria-readonly')).toBe('true')
  })

  it('leaves a read-only editor read-only', () => {
    const { view, publish } = session(false)
    publish({ hydrating: true })
    expect(view()).toBe(false)
  })
})

describe('a session hydrated at once (the page bundle) opens clean (3.6 review: every reload dirtied the page)', () => {
  it('does not count the sync plugin rendering the shared document into the new view as a change', () => {
    // Like y-prosemirror: the view's plugin renders the shared document as it mounts; the bundle hydrated the session already.
    const firstRender = Extension.create({ name: 'firstRender', addProseMirrorPlugins: () => [new Plugin({
      view: view => { view.dispatch(view.state.tr.insertText('shared', 1)); return {} },
    })] })
    const adapter = registerCollaborationAdapter(publish => {
      publish({ hydrated: true, writable: true, connection: 'connecting' })
      return { extensions: [firstRender], isRemoteTransaction: () => false, attachEditor() {}, detachEditor() {}, destroy() {} }
    })
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Bundle' })
    expect(controller.state.lifecycle).toBe('ready')
    expect(editorForController(controller)!.getText()).toBe('shared')
    expect(controller.state.contentRevision).toBe(0)
    // The user's own edit still counts.
    editorForController(controller)!.commands.insertContent('!')
    expect(controller.state.contentRevision).toBe(1)
  })
})
