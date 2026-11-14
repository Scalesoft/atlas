// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Extension, type Editor, type JSONContent } from '@tiptap/core'
import { Fragment, Slice } from '@tiptap/pm/model'
import Collaboration from '@tiptap/extension-collaboration'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { createLegacyEditorController } from '../../core/controller'
import { editorForController } from '../../core/internal'
import { collectOutlineHeadings } from '../../ui/outline'
import { renderMarkdownValue } from '../../engine/staticRenderer'
import { createHeadingAnchorsPlugin, headingAnchorUrl, migrateHeadingAnchors, navigateToHeading } from './index'

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const value = (id?: string): MarkdownRichValue => ({ format: 'atlas-rich', version: 1,
  ...(id ? { plugins: [{ id: 'heading-anchors', version: 1 }] } : {}),
  document: { type: 'doc', content: [{ type: 'heading', attrs: { level: 1, ...(id ? { headingId: id } : {}) }, content: [{ type: 'text', text: 'Úvod' }] }] },
})
function mount(initial = value('existing'), editable = true, collaboration = false, ready = true, baseUrl?: string, getHeadingUrl?: (id: string) => string) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture-anchors', apiVersion: 1, create: () => ({ extensions: collaboration ? [Extension.create({ name: 'collaboration' })] : [], onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', editable, plugins: [createHeadingAnchorsPlugin({ collaborationReady: () => ready, baseUrl, getHeadingUrl }), capture], source: { kind: 'local', value: initial } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Headings' })
  return { editor, controller, host }
}
describe('persistent heading anchors', () => {
  it('copies a validated host route without replacing its page hash and rejects unsafe custom URLs', () => {
    let unsafe = false
    const { editor, controller } = mount(value('existing'), true, false, true, undefined, id => unsafe ? 'javascript:alert(1)' : `https://wiki.example/#/pages/page-id?heading=${id}`)
    editor.view.focus(); editor.commands.setTextSelection(2)
    const before = controller.getValue()
    const button = document.querySelector<HTMLButtonElement>('[aria-label="Kopírovat odkaz na nadpis"]')!
    expect(button).not.toBeNull(); button.click()
    expect(document.querySelector<HTMLInputElement>('label input[readonly]')!.value).toBe('https://wiki.example/#/pages/page-id?heading=existing')
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    unsafe = true; button.click()
    expect(document.querySelector('label input[readonly]')).toBeNull()
    expect(controller.getValue()).toEqual(before)
  })
  it('migrates purely and idempotently, repairs duplicates, and explicitly regenerates document copies', () => {
    const original = value('keep')
    const originalNodes = (original.document as JSONContent).content!
    originalNodes.push({ type: 'heading', attrs: { level: 2, headingId: 'keep' } }, { type: 'heading', attrs: { level: 2 } })
    const migrated = migrateHeadingAnchors(original)
    const ids = (migrated.document as JSONContent).content!.map(node => node.attrs!.headingId)
    expect(ids[0]).toBe('keep'); expect(new Set(ids).size).toBe(3)
    expect(originalNodes[2]!.attrs).not.toHaveProperty('headingId')
    expect(migrateHeadingAnchors(migrated)).toEqual(migrated)
    const copied = migrateHeadingAnchors(migrated, { copy: true })
    expect((copied.document as JSONContent).content!.every(node => !ids.includes(node.attrs!.headingId))).toBe(true)
  })
  it('keeps a dotted id (schema 2) and clears the former ids of a copy', () => {
    const original = value('portal.vpn')
    ;(original.document as JSONContent).content![0]!.attrs!.aliases = 'vpn,3f1c'
    const migrated = migrateHeadingAnchors(original)
    expect(migrated.plugins).toEqual([{ id: 'heading-anchors', version: 2 }])
    expect((migrated.document as JSONContent).content![0]!.attrs).toMatchObject({ headingId: 'portal.vpn', aliases: 'vpn,3f1c' })
    expect((migrateHeadingAnchors(migrated, { copy: true }).document as JSONContent).content![0]!.attrs!.aliases).toBe('')
  })
  it('preserves identity across rename, undo and rich reopen, and declares Markdown loss', () => {
    const { editor, controller } = mount()
    editor.commands.insertContentAt({ from: 1, to: 5 }, 'Nový název')
    expect(editor.state.doc.firstChild!.attrs.headingId).toBe('existing')
    expect(controller.getProjection()).toMatchObject({ markdown: '# Nový název', markdownLossy: true })
    const saved = controller.getValue(); controller.setValue(saved)
    expect(editor.state.doc.firstChild!.attrs.headingId).toBe('existing')
    controller.dispatch({ type: 'undo' })
    expect(editor.state.doc.firstChild!.attrs.headingId).toBe('existing')
  })
  it('uses upstream uniqueness for split headings without changing the first identity', () => {
    const { editor } = mount()
    editor.commands.setTextSelection(3); editor.commands.splitBlock()
    const nodes = editor.getJSON().content!.filter(node => node.type === 'heading')
    expect(nodes).toHaveLength(2)
    expect(nodes[0]!.attrs!.headingId).toBe('existing')
    expect(nodes[1]!.attrs!.headingId).toBeTruthy()
    expect(nodes[1]!.attrs!.headingId).not.toBe('existing')
  })
  it('keeps original text identity when split at the start, and first identity when joining', () => {
    const { editor } = mount()
    editor.commands.setTextSelection(1); editor.commands.splitBlock()
    const headings = editor.getJSON().content!.filter(node => node.type === 'heading')
    expect(headings).toHaveLength(1)
    expect(headings[0]!.attrs!.headingId).toBe('existing')
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
    editor.commands.setContent({ type: 'doc', content: [
      { type: 'heading', attrs: { level: 1, headingId: 'first' }, content: [{ type: 'text', text: 'První' }] },
      { type: 'heading', attrs: { level: 1, headingId: 'second' }, content: [{ type: 'text', text: 'Druhý' }] },
    ] })
    editor.commands.setTextSelection(8)
    editor.commands.joinBackward()
    expect(editor.state.doc.firstChild!.attrs.headingId).toBe('first')
    expect(editor.state.doc.firstChild!.textContent).toBe('PrvníDruhý')
  })
  it('removes copied IDs via upstream paste handling and assigns a new identity', () => {
    const { editor } = mount()
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    editor.view.someProp('handleDOMEvents', handlers => handlers.paste?.(editor.view, new Event('paste') as ClipboardEvent))
    let slice = new Slice(Fragment.from(editor.state.doc.firstChild!), 0, 0)
    editor.view.someProp('transformPasted', transform => { slice = transform(slice, editor.view, false); return false })
    expect(slice.content.firstChild!.attrs.headingId).toBeNull()
    editor.view.dispatch(editor.state.tr.insert(editor.state.doc.content.size, slice.content))
    const ids = editor.getJSON().content!.filter(node => node.type === 'heading').map(node => node.attrs!.headingId)
    expect(ids[0]).toBe('existing'); expect(ids[1]).toBeTruthy(); expect(ids[1]).not.toBe('existing')
  })
  it('preserves IDs for same-editor moves inside an iframe and regenerates copies', () => {
    const frame = document.createElement('iframe'); document.body.append(frame)
    const host = frame.contentDocument!.createElement('div'); frame.contentDocument!.body.append(host)
    let editor!: Editor
    const capture: MarkdownPlugin = { id: 'iframe-capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createHeadingAnchorsPlugin(), capture], source: { kind: 'local', value: value('move-id') } })
    controllers.push(controller); controller.attachView(host, { ariaLabel: 'Iframe headings' })
    const plugin = editor.state.plugins.find(item => item.props.transformPasted && item.props.handleDOMEvents?.paste && item.props.handleDOMEvents?.drop)!
    const drag = frame.contentDocument!.createEvent('Event'); drag.initEvent('dragstart', true, true)
    editor.view.dom.dispatchEvent(drag)
    plugin.props.handleDOMEvents!.drop!.call(plugin, editor.view, new Event('drop') as DragEvent)
    const slice = new Slice(Fragment.from(editor.state.doc.firstChild!), 0, 0)
    const moved = plugin.props.transformPasted!.call(plugin, slice, editor.view, false)
    expect(moved.content.firstChild!.attrs.headingId).toBe('move-id')
    const copy = new Event('drop')
    Object.defineProperty(copy, 'dataTransfer', { value: { effectAllowed: 'copy' } })
    plugin.props.handleDOMEvents!.drop!.call(plugin, editor.view, copy as DragEvent)
    expect(plugin.props.transformPasted!.call(plugin, slice, editor.view, false).content.firstChild!.attrs.headingId).toBeNull()
  })
  it('never assigns missing IDs in readonly, static rendering or a remote transaction', async () => {
    const { editor, controller } = mount(value(), false)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(editor.state.doc.firstChild!.attrs.headingId).toBeNull()
    const container = document.createElement('div'); const cleanup = renderMarkdownValue(value(), 'rich', [createHeadingAnchorsPlugin()], container)
    expect(container.querySelector('h1')!.hasAttribute('data-heading-id')).toBe(false); cleanup()
    controller.setEditable(true)
    editor.view.dispatch(editor.state.tr.insertText('!', 1).setMeta('y-sync$', { isChangeOrigin: true }))
    expect(editor.state.doc.firstChild!.attrs.headingId).toBeNull()
  })
  it('requires the explicit migration/synchronization gate in collaborative editors', () => {
    const { editor } = mount(value(), true, true, false)
    editor.commands.insertContent('X')
    expect(editor.state.doc.firstChild!.attrs.headingId).toBeNull()
  })
  it('scopes DOM IDs per view while navigation resolves stable identity', () => {
    const first = mount(), second = mount()
    const a = first.host.querySelector('h1')!, b = second.host.querySelector('h1')!
    expect(a.id).toBeTruthy(); expect(a.id).not.toBe(b.id)
    a.scrollIntoView = vi.fn(); b.scrollIntoView = vi.fn()
    expect(navigateToHeading(second.host, 'existing')).toBe(true)
    expect(b.getAttribute('tabindex')).toBe('-1')
    expect(navigateToHeading(second.host, 'existing')).toBe(true)
    const outside = document.createElement('button'); document.body.append(outside); outside.focus()
    expect(b.hasAttribute('tabindex')).toBe(false)
    expect(b.scrollIntoView).toHaveBeenCalledTimes(2); expect(a.scrollIntoView).not.toHaveBeenCalled()
    expect(headingAnchorUrl('https://wiki.example/page?view=read#old', 'existing')).toBe('https://wiki.example/page?view=read#heading=existing')
    expect(navigateToHeading(first.host, 'missing')).toBe(false)
    expect(collectOutlineHeadings(first.editor)[0]?.headingId).toBe('existing')
  })
  it('does not race ID migration during real Yjs synchronization and retains remote identities', () => {
    const docs = [new Y.Doc(), new Y.Doc()]
    const engines = docs.map(doc => {
      const plugin = createHeadingAnchorsPlugin({ collaborationReady: () => true }).create({ profile: 'rich', signal: new AbortController().signal })
      const controller = createLegacyEditorController({ profile: 'rich', initialMarkdown: '', collaborative: true, extraExtensions: [Collaboration.configure({ document: doc }), ...plugin.extensions!] }).controller
      controllers.push(controller)
      const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Collaborative headings' })
      return editorForController(controller)!
    })
    try {
      engines[0]!.commands.setContent({ type: 'doc', content: [{ type: 'heading', attrs: { level: 1, headingId: 'host-id' }, content: [{ type: 'text', text: 'Sdílený' }] }] })
      Y.applyUpdate(docs[1]!, Y.encodeStateAsUpdate(docs[0]!))
      expect(engines[1]!.state.doc.firstChild!.attrs.headingId).toBe('host-id')
      engines[0]!.commands.insertContentAt(1, 'Nový ')
      Y.applyUpdate(docs[1]!, Y.encodeStateAsUpdate(docs[0]!))
      expect(engines[1]!.state.doc.firstChild!.attrs.headingId).toBe('host-id')
      expect(engines[1]!.state.doc.firstChild!.textContent).toBe('Nový Sdílený')
      yUndoPluginKey.getState(engines[0]!.state)!.undoManager.clear()
      engines[0]!.commands.setTextSelection(3); engines[0]!.commands.splitBlock()
      const newId = engines[0]!.state.doc.child(1).attrs.headingId
      expect(newId).toBeTruthy(); expect(newId).not.toBe('host-id')
      Y.applyUpdate(docs[1]!, Y.encodeStateAsUpdate(docs[0]!))
      expect(engines[1]!.state.doc.child(1).attrs.headingId).toBe(newId)
      expect(engines[0]!.commands.undo()).toBe(true)
      Y.applyUpdate(docs[1]!, Y.encodeStateAsUpdate(docs[0]!))
      expect(engines[1]!.state.doc.firstChild!.attrs.headingId).toBe('host-id')
      expect(engines[1]!.state.doc.firstChild!.textContent).toBe('Nový Sdílený')
      // A legacy remote heading is left for the single host migration, never fixed by the receiver.
      docs[0]!.transact(() => {
        const heading = docs[0]!.getXmlFragment('default').get(0) as Y.XmlElement
        heading.removeAttribute('headingId')
      })
      Y.applyUpdate(docs[1]!, Y.encodeStateAsUpdate(docs[0]!))
      expect(engines[1]!.state.doc.firstChild!.attrs.headingId).toBeNull()
    } finally { controllers.splice(-2).forEach(controller => controller.destroy()); docs.forEach(doc => doc.destroy()) }
  })
  it('rejects old clients lacking the versioned attribute schema', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: value('existing') } })).toThrow(/missing plugin/i)
  })
  it('offers a labeled copy field in the shared selected-heading toolbar without changing the document', () => {
    const { editor, controller } = mount(value('existing'), true, false, true, 'https://wiki.example/page')
    editor.view.focus(); editor.commands.setTextSelection(2)
    const before = controller.getValue()
    const button = document.querySelector<HTMLButtonElement>('[aria-label="Kopírovat odkaz na nadpis"]')!
    expect(button).not.toBeNull(); button.click()
    const input = document.querySelector<HTMLInputElement>('label input[readonly]')!
    expect(input.value).toBe('https://wiki.example/page#heading=existing')
    expect(input.closest('label')?.textContent).toBe('Odkaz na nadpis')
    expect(controller.getValue()).toEqual(before)
  })
  it('stands after a heading\'s chips, never over them (a requirement\'s state chip stays clickable)', async () => {
    const { editor } = mount(value('existing'), true, false, true, 'https://wiki.example/page')
    const heading = editor.view.dom.querySelector('h1, h2, h3, h4, h5, h6')!
    const chips = Object.assign(document.createElement('span'), { className: 'rq-chips' })
    // Wrapped over two lines: the first reaches 500 (the line's end, a host's state there), the last ends at 300.
    chips.getClientRects = () => Object.assign([new DOMRect(200, 10, 300, 24), new DOMRect(0, 34, 300, 24)], { item: () => null }) as unknown as DOMRectList
    // ProseMirror would drop a node appended by hand; the host's chips are a widget it keeps.
    const find = heading.querySelector.bind(heading)
    heading.querySelector = ((selector: string) => selector === ':scope > .rq-chips' ? chips : find(selector)) as typeof heading.querySelector
    editor.view.focus(); editor.commands.setTextSelection(2)
    const toolbar = document.querySelector<HTMLElement>('[aria-label="Kopírovat odkaz na nadpis"]')!.closest<HTMLElement>('.md-context-toolbar')!
    window.dispatchEvent(new Event('resize'))
    // The chips end at 300; the toolbar starts right of that (floating-ui's 6 px offset).
    await vi.waitFor(() => expect(parseFloat(toolbar.style.left)).toBeGreaterThanOrEqual(300))
    expect(parseFloat(toolbar.style.left)).toBeLessThan(500)
  })
  it('morphs the copy action into an accessible success state after clipboard confirmation', async () => {
    const original = Object.getOwnPropertyDescriptor(window.navigator, 'clipboard')
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(window.navigator, 'clipboard', { configurable: true, value: { writeText } })
    try {
      const { editor } = mount(value('existing'), true, false, true, 'https://wiki.example/page')
      editor.view.focus(); editor.commands.setTextSelection(2)
      const button = document.querySelector<HTMLButtonElement>('[aria-label="Kopírovat odkaz na nadpis"]')!
      button.click()
      await vi.waitFor(() => expect(button.dataset.feedback).toBe('success'))
      expect(writeText).toHaveBeenCalledWith('https://wiki.example/page#heading=existing')
      expect(button.getAttribute('aria-label')).toBe('Odkaz zkopírován')
      expect(button.querySelector('svg')?.getAttribute('data-icon')).toBe('check')
    } finally {
      if (original) Object.defineProperty(window.navigator, 'clipboard', original)
      else delete (window.navigator as unknown as { clipboard?: Clipboard }).clipboard
    }
  })
})
