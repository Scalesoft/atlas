// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import type { Editor } from '@tiptap/core'
import { MarkdownEditor, type MarkdownOutlineMode } from '../vue'
import type { MarkdownPlugin } from '../core'
import { outlineWave, collectOutlineHeadings, createOutlineHighlight, editorOutlineSource } from './outline'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { createLiveOutlinePreview, previewSources } from '../features/outline/livePreview'
import Collaboration from '@tiptap/extension-collaboration'
import * as Y from 'yjs'
import { createLegacyEditorController } from '../core/controller'
import { editorForController } from '../core/internal'
import DocumentOutline from './DocumentOutline.vue'
import PoolEditor from '../PoolEditor.vue'

let app: App | undefined
let host: HTMLDivElement
beforeEach(() => {
  host = document.createElement('div'); document.body.append(host)
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} })
  window.matchMedia = vi.fn().mockReturnValue({ matches: false })
  HTMLElement.prototype.scrollIntoView = vi.fn()
})
afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren(); vi.unstubAllGlobals() })
// The outline reads the headings once the document is quiet (OUTLINE_IDLE_MS, 100 ms).
const flush = async () => { await nextTick(); await new Promise(resolve => setTimeout(resolve, 150)); await nextTick() }
async function mount(mode: MarkdownOutlineMode = 'floating') {
  const options = ref<MarkdownOutlineMode>(mode)
  const compact = ref(false)
  let engine!: Editor
  const plugin: MarkdownPlugin = { id: 'outline.probe', apiVersion: 1, create: () => ({ onAttach: ({ editor }) => { engine = editor } }) }
  app = createApp({ render: () => h(MarkdownEditor, { modelValue: '# One\n\nLive text\n\n## Two\n\nSecond\n\n### Three\n\nEnd', plugins: [plugin], outline: options.value, variant: compact.value ? 'compact' : 'document', ariaLabel: 'Outline test' }) })
  app.mount(host)
  await vi.waitFor(() => expect(host.querySelector('.md-outline-trigger')).not.toBeNull())
  await flush()
  return { options, compact, engine }
}
const button = (selector: string) => host.querySelector<HTMLButtonElement>(selector)!

describe('document outline', () => {
  it('supports the legacy editor and removes its navigation without remounting the content', async () => {
    const mode = ref<MarkdownOutlineMode>('panel')
    app = createApp({ render: () => h(PoolEditor, { modelValue: '# Legacy\n\nBody', paper: true, readonly: true, outline: mode.value }) })
    app.mount(host)
    await vi.waitFor(() => expect(host.querySelector('.md-outline-entry')).not.toBeNull())
    const content = host.querySelector('.editor-body > .ProseMirror')
    expect(button('.md-outline-entry').textContent).toBe('Legacy')
    mode.value = 'hidden'; await flush()
    expect(host.querySelector('.md-outline-navigation')).toBeNull()
    expect(host.querySelector('.editor-body > .ProseMirror')).toBe(content)
  })
  it('preserves host decorations and independent highlight owners through remount and document shrink', () => {
    const controller = createLegacyEditorController({ profile: 'markdown-safe', initialMarkdown: '# First\n\nA sufficiently long paragraph before the late heading.\n\n## Last' }).controller
    let detach = controller.attachView(host, { ariaLabel: 'Highlights' })
    const editor = editorForController(controller)!
    const hostDecorations = (state: typeof editor.state) => DecorationSet.create(state.doc, [Decoration.node(0, state.doc.firstChild!.nodeSize, { 'data-host-highlight': 'true' })])
    editor.setOptions({ editorProps: { ...editor.options.editorProps, decorations: hostDecorations } })
    const first = createOutlineHighlight(editor), last = createOutlineHighlight(editor)
    try {
      const headings = collectOutlineHeadings(editor)
      first.set(headings[0]!.pos); last.set(headings[1]!.pos)
      expect(host.querySelectorAll('[data-md-outline-target]')).toHaveLength(2)
      expect(host.querySelector('[data-host-highlight]')).not.toBeNull()
      detach(); detach = controller.attachView(host, { ariaLabel: 'Highlights' })
      expect(host.querySelectorAll('[data-md-outline-target]')).toHaveLength(2)
      first.destroy()
      expect(host.querySelectorAll('[data-md-outline-target]')).toHaveLength(1)
      expect(() => editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph' }] })).not.toThrow()
      expect(host.querySelector('[data-md-outline-target]')).toBeNull()
      last.destroy()
      expect(editor.options.editorProps.decorations).toBe(hostDecorations)
    } finally { first.destroy(); last.destroy(); detach(); controller.destroy() }
  })
  it('reads the headings once typing pauses, with every change since mapped, not per keystroke (J12.6)', () => {
    vi.useFakeTimers()
    const controller = createLegacyEditorController({ profile: 'markdown-safe', initialMarkdown: 'Intro\n\n# Heading' }).controller
    const detach = controller.attachView(host, { ariaLabel: 'Idle outline' })
    const editor = editorForController(controller)!, changed = vi.fn(), redrawn = vi.fn()
    const stop = editorOutlineSource(editor).subscribe(changed, redrawn)
    try {
      const heading = collectOutlineHeadings(editor)[0]!.pos
      for (const char of 'abc') editor.commands.insertContentAt(1, char)
      vi.advanceTimersByTime(99)
      expect(changed).not.toHaveBeenCalled()
      vi.advanceTimersByTime(1)
      expect(changed).toHaveBeenCalledOnce()
      expect(changed.mock.calls[0]![0](heading)).toBe(heading + 3)
    } finally { stop(); detach(); controller.destroy(); vi.useRealTimers() }
  })
  it('has a continuous wave, hierarchy and gradual labels without altering pointer targets', () => {
    expect(outlineWave(1, null).width).toBeGreaterThan(outlineWave(3, null).width)
    expect(outlineWave(2, null).opacity).toBe(0)
    expect(outlineWave(2, 0).opacity).toBe(1)
    expect(outlineWave(2, .9).opacity).toBeLessThan(outlineWave(2, .1).opacity)
    expect(outlineWave(2, .4).height).toBeCloseTo(outlineWave(2, .4001).height, 2)
  })
  it('keeps dense marks distinct and makes room for the hovered level label', () => {
    for (const pitch of [2, 3.84, 8]) {
      expect(outlineWave(3, null, false, pitch).height).toBeLessThan(pitch)
      expect(outlineWave(3, 0, false, pitch).opacity).toBe(1)
      const middle = outlineWave(3, 0, false, pitch)
      const next = outlineWave(3, 1, false, pitch)
      expect(pitch + next.y - next.height / 2).toBeGreaterThan(middle.height / 2)
      expect(outlineWave(3, -1, false, pitch).y).toBe(-next.y)
    }
  })
  it('changes modes without replacing the engine, document, selection or history', async () => {
    const { engine, options, compact } = await mount()
    const dom = engine.view.dom
    engine.commands.setTextSelection(3)
    const value = engine.getJSON(); const selection = engine.state.selection.toJSON()
    options.value = 'panel'; await flush()
    expect(host.querySelector('.md-outline-panel.is-docked')).not.toBeNull()
    expect(button('.md-outline-rail').style.display).toBe('none')
    button('[aria-label="Sbalit obsah do ikonky"]').click(); await flush()
    expect(button('.md-outline-trigger').getAttribute('aria-expanded')).toBe('false')
    options.value = 'hidden'; await flush()
    expect(host.querySelector('.md-outline-navigation')).toBeNull()
    expect(engine.view.dom).toBe(dom); expect(engine.getJSON()).toEqual(value)
    expect(engine.state.selection.toJSON()).toEqual(selection)
    options.value = 'floating'; await flush()
    compact.value = true; await flush()
    expect(host.querySelector('.md-outline-navigation')).toBeNull()
    expect(engine.view.dom).toBe(dom)
  })
  it('pins and unpins, dismisses with Escape and navigates without document writes, the writer\'s caret into the heading', async () => {
    const { engine } = await mount()
    const initial = engine.getJSON()
    engine.commands.setTextSelection(engine.state.doc.content.size - 1)
    button('.md-outline-trigger').click(); await flush()
    button('.md-outline-pin').click(); await flush()
    expect(host.querySelector('.md-outline-layout.is-docked')).not.toBeNull()
    host.querySelectorAll<HTMLButtonElement>('.md-outline-entry')[1]!.click(); await flush()
    expect(button('.md-outline-trigger').getAttribute('aria-expanded')).toBe('true')
    expect(engine.getJSON()).toEqual(initial)
    expect(engine.state.selection.$head.parent.textContent).toBe('Two')
    // A reader's entry only scrolls: the selection stays.
    engine.setEditable(false)
    const selection = engine.state.selection.toJSON()
    button('.md-outline-entry').click(); await flush()
    expect(engine.state.selection.toJSON()).toEqual(selection)
    engine.setEditable(true)
    button('.md-outline-pin').click(); await flush()
    expect(host.querySelector('.md-outline-layout.is-docked')).toBeNull()
    button('.md-outline-entry').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flush()
    expect(document.activeElement).toBe(button('.md-outline-trigger'))
    expect(host.querySelector('[data-md-outline-target]')).toBeNull()
  })
  it('keeps pin and collapse state when the host re-renders an equal outline literal, but applies real changes', async () => {
    const tick = ref(0)
    const mode = ref<MarkdownOutlineMode>('floating')
    const pinned = ref(false)
    // An inline literal: every parent render passes a new object with the same values.
    app = createApp({ render: () => h(MarkdownEditor, { modelValue: '# One\n\nText\n\n## Two', outline: { mode: mode.value, pinned: pinned.value }, ariaLabel: `Outline test ${tick.value}` }) })
    app.mount(host)
    await vi.waitFor(() => expect(host.querySelector('.md-outline-trigger')).not.toBeNull())
    await flush()

    button('.md-outline-trigger').click(); await flush()
    button('.md-outline-pin').click(); await flush()
    expect(host.querySelector('.md-outline-layout.is-docked')).not.toBeNull()
    tick.value++; await flush()
    expect(host.querySelector('.md-outline-layout.is-docked')).not.toBeNull()
    expect(button('.md-outline-pin').getAttribute('aria-pressed')).toBe('true')

    mode.value = 'panel'; await flush()
    expect(host.querySelector('.md-outline-panel.is-docked')).not.toBeNull()
    button('[aria-label="Sbalit obsah do ikonky"]').click(); await flush()
    expect(button('.md-outline-trigger').getAttribute('aria-expanded')).toBe('false')
    tick.value++; await flush()
    expect(button('.md-outline-trigger').getAttribute('aria-expanded')).toBe('false')

    pinned.value = true; await flush()
    expect(button('.md-outline-trigger').getAttribute('aria-expanded')).toBe('true')
  })
  it('updates headings and the passive preview after edits and undo, including readonly', async () => {
    const { engine } = await mount('panel')
    engine.setEditable(false)
    expect(collectOutlineHeadings(engine).map(entry => entry.title)).toEqual(['One', 'Two', 'Three'])
    expect(host.querySelector('.md-outline-preview')?.hasAttribute('inert')).toBe(true)
    expect(host.querySelector('.md-outline-preview')?.getAttribute('aria-hidden')).toBe('true')
    engine.commands.setContent({ type: 'doc', content: [
      { type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Remote heading' }] },
      { type: 'paragraph', content: [{ type: 'text', text: 'Remote body' }] },
      { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Next' }] },
    ] })
    await flush()
    expect(button('.md-outline-entry').textContent).toBe('Remote heading')
    expect(host.querySelector('.md-outline-preview-content')?.textContent).toContain('Remote body')
    engine.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'No headings' }] }] }); await flush()
    expect(host.querySelector('.md-outline-empty')?.textContent).toContain('Přidejte')
    expect(host.querySelector('.md-outline-preview')).toBeNull()
  })
  it('preserves undo and maps the preview after inserting content before its heading', async () => {
    const { engine } = await mount('panel')
    const original = engine.getJSON()
    engine.commands.insertContentAt(1, 'First ')
    await flush()
    button('.md-outline-entry').dispatchEvent(new Event('pointerenter'))
    await flush()
    expect(host.querySelector('.md-outline-preview-content')?.textContent).toContain('First One')
    engine.commands.undo(); await flush()
    expect(engine.getJSON()).toEqual(original)
    const before = engine.state.doc.nodeSize
    engine.commands.insertContentAt(0, { type: 'paragraph', content: [{ type: 'text', text: 'Preface' }] })
    await flush()
    expect(engine.state.doc.nodeSize).toBeGreaterThan(before)
    expect(button('.md-outline-entry').textContent).toBe('One')
    button('.md-outline-entry').dispatchEvent(new Event('pointerenter')); await flush()
    expect(host.querySelector('.md-outline-preview-content')?.textContent).toContain('Live text')
  })
  it('mirrors real Yjs changes from another editor without creating a second collaboration participant', async () => {
    const doc = new Y.Doc()
    const source = document.createElement('div'), peer = document.createElement('div')
    document.body.append(source, peer)
    const first = createLegacyEditorController({ profile: 'markdown-safe', initialMarkdown: '', collaborative: true, extraExtensions: [Collaboration.configure({ document: doc })] }).controller
    const second = createLegacyEditorController({ profile: 'markdown-safe', initialMarkdown: '', collaborative: true, extraExtensions: [Collaboration.configure({ document: doc })] }).controller
    const detachFirst = first.attachView(source, { ariaLabel: 'First' }), detachSecond = second.attachView(peer, { ariaLabel: 'Second' })
    try {
      const local = editorForController(first)!, remote = editorForController(second)!
      local.commands.setContent({ type: 'doc', content: [{ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Shared heading' }] }, { type: 'paragraph', content: [{ type: 'text', text: 'Body' }] }] })
      const client = doc.clientID
      app = createApp({ render: () => h(DocumentOutline, { editor: local, options: { mode: 'panel' } }) }); app.mount(host)
      await flush()
      expect(host.querySelector('.md-outline-preview-content')?.textContent).toContain('Shared heading')
      remote.commands.insertContentAt(1, 'Remote ')
      await flush()
      expect(host.querySelector('.md-outline-preview-content')?.textContent).toContain('Remote Shared heading')
      expect(local.getJSON()).toEqual(remote.getJSON()); expect(doc.clientID).toBe(client)
      const snapshot = Y.encodeStateVector(doc)
      button('.md-outline-entry').dispatchEvent(new Event('pointerenter')); await flush()
      expect(Y.encodeStateVector(doc)).toEqual(snapshot)
    } finally {
      app?.unmount(); app = undefined
      detachFirst(); detachSecond(); first.destroy(); second.destroy(); doc.destroy()
    }
  })
})

describe('passive live mirror', () => {
  it('copies canvas output without creating a renderer and releases its refresh timer', async () => {
    host.innerHTML = '<div class="source"><h2>Chart</h2><canvas width="100" height="50"></canvas></div><div class="preview"></div>'
    const drawImage = vi.fn(), clearRect = vi.fn()
    const context = vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage, clearRect } as unknown as CanvasRenderingContext2D)
    const source = host.querySelector<HTMLElement>('.source')!, target = host.querySelector<HTMLElement>('.preview')!
    const mirror = createLiveOutlinePreview(source, target, () => source.querySelector('h2'))
    try {
      await flush()
      expect(drawImage).toHaveBeenCalled()
      const first = target.firstChild
      mirror.refresh(); await flush()
      expect(target.firstChild).toBe(first)
      mirror.destroy(); const count = drawImage.mock.calls.length
      await new Promise(resolve => setTimeout(resolve, 200))
      expect(drawImage).toHaveBeenCalledTimes(count)
    } finally { mirror.destroy(); context.mockRestore() }
  })
  it('mirrors text, selection/caret markup and components but strips editing controls and duplicate identities', async () => {
    host.innerHTML = '<div class="source"><h2 id="heading">One</h2><p>Text <span class="collaboration-carets__caret">Jana</span></p><table><tbody><tr><td>Wide value</td></tr></tbody></table><div data-md-module="chart"><svg><defs><clipPath id="clip"></clipPath></defs><g clip-path="url(#clip)"></g></svg><button>Settings</button></div><h2>Next</h2><p>Excluded</p></div><div class="preview"></div>'
    const source = host.querySelector<HTMLElement>('.source')!, target = host.querySelector<HTMLElement>('.preview')!, heading = source.querySelector<HTMLElement>('h2')!
    expect(previewSources(source, heading)).toHaveLength(4)
    const mirror = createLiveOutlinePreview(source, target, () => heading)
    await flush()
    expect(target.textContent).toContain('Jana'); expect(target.querySelector('table')).not.toBeNull()
    expect(target.style.width).toBe('100%')
    expect(target.querySelector('table')!.style.width).toBe('240px')
    expect(target.querySelector<HTMLElement>('[data-md-module]')!.style.width).toBe('240px')
    expect(target.textContent).not.toContain('Excluded'); expect(target.querySelector('button')).toBeNull()
    expect(target.querySelector('h2')?.id).not.toBe('heading')
    expect(target.querySelector('g')?.getAttribute('clip-path')).toContain(target.querySelector('clipPath')!.id)
    source.querySelector('p')!.firstChild!.textContent = 'Changed remotely '
    await flush(); expect(target.textContent).toContain('Changed remotely')
    mirror.destroy(); source.querySelector('p')!.textContent = 'After disposal'; await flush()
    expect(target.childNodes).toHaveLength(0)
  })
  it('bounds cloning and stops at the next heading', async () => {
    host.innerHTML = '<h2>One</h2>' + '<p>text</p>'.repeat(50)
    expect(previewSources(host, host.querySelector('h2')!)).toHaveLength(8)
  })
})
