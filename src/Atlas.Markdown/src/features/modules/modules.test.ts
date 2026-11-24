// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, createMarkdownModuleLoader, defineMarkdownBlockModule, openMarkdownModuleForm, type MarkdownEditorController, type MarkdownRichValue, type MarkdownPlugin, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import type { Editor } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { downloadMarkdownFile } from './forms'

// jsdom has no layout: ProseMirror's own press handling asks where a press landed.
document.elementFromPoint ??= () => null
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks(); vi.useRealTimers() })
const empty = (): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } })
type Probe = { text: string }
function probe(runtime: MarkdownModuleRuntime<Probe>, watchDocument = false) {
  return defineMarkdownBlockModule({ id: 'probe', name: 'probeBlock', label: 'Probe', defaults: { text: 'Saved' }, watchDocument,
    normalize(value) { if (!value || typeof (value as Probe).text !== 'string') throw new TypeError('invalid'); return value as Probe },
    text: data => data.text, load: async () => runtime,
  })
}
function attach(block: ReturnType<typeof probe>, owner = document) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [block.createPlugin(), capture], source: { kind: 'local', value: empty() } })
  controllers.push(controller)
  const host = owner.createElement('div'); owner.body.append(host)
  const detach = controller.attachView(host, { ariaLabel: 'Modules' })
  controller.dispatch(block.insert({ text: 'Saved' }))
  return { controller, host, editor, detach }
}

describe('lazy content modules', () => {
  it('updates an opted-in retained view without remounting and ignores stale writes after removal', async () => {
    const destroy = vi.fn(), update = vi.fn()
    let live!: MarkdownModuleContext<Probe>
    const render = vi.fn((context: MarkdownModuleContext<Probe>) => { live = context; return { update(next: MarkdownModuleContext<Probe>) { live = next; update(next) }, destroy } })
    const block = probe({ render }); const { editor, controller } = attach(block)
    await vi.waitFor(() => expect(render).toHaveBeenCalledOnce())
    live.update!({ text: 'Changed' })
    await vi.waitFor(() => expect(update).toHaveBeenCalledOnce())
    expect(live.data.text).toBe('Changed'); expect(render).toHaveBeenCalledOnce(); expect(destroy).not.toHaveBeenCalled()
    const stale = live.update!
    editor.commands.setNodeSelection(0); editor.commands.deleteSelection()
    expect(destroy).toHaveBeenCalledOnce(); stale({ text: 'Resurrected' })
    expect(controller.getProjection().markdown).not.toContain('Resurrected')
  })
  it('recovers a retained update failure with the latest saved data and fresh generation', async () => {
    const release = vi.fn(), update = vi.fn(() => { throw new Error('update failed') })
    const contexts: MarkdownModuleContext<Probe>[] = []
    const render = vi.fn((context: MarkdownModuleContext<Probe>) => {
      contexts.push(context); context.container.textContent = context.data.text
      return { update, destroy: release }
    })
    const block = probe({ render }); const { host, controller } = attach(block)
    await vi.waitFor(() => expect(render).toHaveBeenCalledOnce())
    contexts[0]!.update!({ text: 'Latest saved data' })
    await vi.waitFor(() => expect(render).toHaveBeenCalledTimes(2))
    expect(release).toHaveBeenCalledOnce(); expect(contexts[0]!.signal.aborted).toBe(true)
    expect(host.textContent).toContain('Latest saved data')
    expect(contexts[1]!.data.text).toBe('Latest saved data')
    contexts[0]!.update!({ text: 'Stale generation' })
    expect(controller.getProjection().markdown).toBe('Latest saved data')
    controller.destroy(); expect(release).toHaveBeenCalledTimes(2)
  })
  it('destroys a late retained view without replacing the current generation', async () => {
    const pending: { context: MarkdownModuleContext<Probe>; resolve: (view: { update: () => void; destroy: () => void }) => void }[] = []
    const render = vi.fn((context: MarkdownModuleContext<Probe>) => new Promise<{ update: () => void; destroy: () => void }>(resolve => { pending.push({ context, resolve }) }))
    const block = probe({ render }); const { host, editor, controller } = attach(block)
    await vi.waitFor(() => expect(pending).toHaveLength(1))
    editor.view.dispatch(editor.state.tr.setNodeMarkup(0, undefined, { data: { text: 'Replacement' } }))
    await vi.waitFor(() => expect(pending).toHaveLength(2))
    const oldDestroy = vi.fn(), newDestroy = vi.fn()
    pending[1]!.context.container.textContent = 'Current renderer'
    pending[1]!.resolve({ update() {}, destroy: newDestroy })
    pending[0]!.context.container.textContent = 'Late renderer'
    pending[0]!.resolve({ update() {}, destroy: oldDestroy })
    await vi.waitFor(() => expect(oldDestroy).toHaveBeenCalledOnce())
    expect(host.textContent).toContain('Current renderer'); expect(host.textContent).not.toContain('Late renderer')
    pending[0]!.context.update!({ text: 'Stale data' })
    expect(controller.getProjection().markdown).toBe('Replacement')
    controller.destroy(); expect(oldDestroy).toHaveBeenCalledOnce(); expect(newDestroy).toHaveBeenCalledOnce()
  })
  it('coalesces loading and retries a failed import', async () => {
    const importModule = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue({ ready: true })
    const load = createMarkdownModuleLoader(importModule)
    const first = load(); expect(load()).toBe(first)
    await expect(first).rejects.toThrow('offline')
    expect(await load()).toEqual({ ready: true }); expect(await load()).toEqual({ ready: true })
    expect(importModule).toHaveBeenCalledTimes(2)
  })
  it('does not load unused modules and disposes a late renderer exactly once', async () => {
    let resolve!: (cleanup: () => void) => void
    const cleanup = vi.fn()
    const render = vi.fn(() => new Promise<() => void>(yes => { resolve = yes }))
    const load = vi.fn(async () => ({ render }))
    const block = defineMarkdownBlockModule({ id: 'probe', name: 'probeBlock', label: 'Probe', defaults: { text: 'Default' },
      normalize(value) { if (!value || typeof (value as { text: unknown }).text !== 'string') throw new TypeError('invalid'); return value as { text: string } },
      text: data => data.text, load,
    })
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [block.createPlugin()], source: { kind: 'local', value: empty() } })
    controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Modules' })
    expect(load).not.toHaveBeenCalled()
    expect(controller.dispatch(block.insert({ text: 'Keep this content' }))).toBe(true)
    await vi.waitFor(() => expect(render).toHaveBeenCalledTimes(1))
    expect(controller.getProjection()).toMatchObject({ markdown: 'Keep this content', markdownLossy: true })
    expect(controller.getValue().plugins).toEqual([{ id: 'probe', version: 1 }])
    detach(); resolve(cleanup)
    await vi.waitFor(() => expect(cleanup).toHaveBeenCalledTimes(1))
  })
  it('keeps rich builtin blocks through reload; a callout is a GitHub alert, no Markdown loss', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: empty() } }); controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Builtins' })
    controller.dispatch({ type: 'insert-callout', variant: 'warning' })
    const value = controller.getValue()
    controller.setValue(empty()); controller.setValue(value)
    expect(host.querySelector('aside[data-variant=warning]')).not.toBeNull()
    expect(controller.getProjection().markdownLossy).toBe(false)
    expect(controller.getProjection().markdown).toContain('> [!WARNING]')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
  })
  it('isolates late renderer DOM and data writes from a replacement generation', async () => {
    const calls: { context: MarkdownModuleContext<Probe>; resolve(clean: () => void): void }[] = []
    const block = probe({ render: context => new Promise<() => void>(resolve => { calls.push({ context, resolve }) }) }, true)
    const { host, editor, controller, detach } = attach(block)
    await vi.waitFor(() => expect(calls).toHaveLength(1))
    editor.commands.insertContentAt(editor.state.doc.content.size, { type: 'paragraph', content: [{ type: 'text', text: 'Elsewhere' }] })
    await vi.waitFor(() => expect(calls).toHaveLength(2))
    const oldClean = vi.fn(); const currentClean = vi.fn()
    calls[1]!.context.container.textContent = 'Current renderer'; calls[1]!.resolve(currentClean)
    calls[0]!.context.container.textContent = 'Stale renderer'; calls[0]!.context.update!({ text: 'Stale data' }); calls[0]!.resolve(oldClean)
    await vi.waitFor(() => expect(oldClean).toHaveBeenCalledOnce())
    expect(host.textContent).toContain('Current renderer'); expect(host.textContent).not.toContain('Stale renderer')
    expect(controller.getProjection().markdown).toContain('Saved'); expect(controller.getProjection().markdown).not.toContain('Stale data')
    detach(); controller.destroy(); expect(oldClean).toHaveBeenCalledOnce(); expect(currentClean).toHaveBeenCalledOnce()
  })
  it('isolates consecutive module saves from each other and typing on both sides', async () => {
    let live!: MarkdownModuleContext<Probe>
    const block = probe({ render(context) { live = context } })
    const { editor } = attach(block)
    await vi.waitFor(() => expect(live).toBeDefined())
    const end = editor.state.doc.content.size
    editor.view.dispatch(editor.state.tr.insert(end, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Base'))).setMeta('addToHistory', false))
    vi.spyOn(Date, 'now').mockReturnValue(1000)
    editor.view.dispatch(editor.state.tr.insertText('Before ', end + 1))
    live.update!({ text: 'First' })
    await vi.waitFor(() => expect(live.data.text).toBe('First'))
    live.update!({ text: 'Second' })
    await vi.waitFor(() => expect(live.data.text).toBe('Second'))
    editor.view.dispatch(editor.state.tr.insertText('After ', end + 1))
    const value = () => editor.state.doc.firstChild!.attrs.data.text
    editor.commands.undo()
    expect(value()).toBe('Second'); expect(editor.state.doc.lastChild!.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(value()).toBe('First'); expect(editor.state.doc.lastChild!.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(value()).toBe('Saved'); expect(editor.state.doc.lastChild!.textContent).toBe('Before Base')
    editor.commands.undo()
    expect(editor.state.doc.lastChild!.textContent).toBe('Base')
  })
  it('keeps settings drafts during unrelated changes and closes them on readonly transitions', async () => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value() { this.setAttribute('open', '') } })
    const block = probe({
      render(context) { context.container.textContent = context.data.text },
      edit(context) { return openMarkdownModuleForm({ editor: context.editor!, anchor: context.anchor, signal: context.signal, title: 'Settings', fields: [{ key: 'text', label: 'Text' }], values: context.data, save: values => context.update!({ text: values.text! }) }) },
    }, true)
    const { host, editor, controller } = attach(block)
    await vi.waitFor(() => expect(document.querySelector('[aria-label="Upravit: Probe"]')).not.toBeNull())
    host.querySelector<HTMLElement>('.md-module')!.click()
    document.querySelector<HTMLButtonElement>('[aria-label="Upravit: Probe"]')!.click()
    const dialog = document.querySelector('[role=dialog]')!; const input = dialog.querySelector('input')!; input.value = 'Unsaved draft'
    editor.view.dispatch(editor.state.tr.insert(editor.state.doc.content.size, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Elsewhere'))))
    await vi.waitFor(() => expect(document.querySelector('[aria-label="Upravit: Probe"]')).not.toBeNull())
    expect(document.querySelector('[role=dialog]')).toBe(dialog); expect(input.value).toBe('Unsaved draft')
    controller.setEditable(false)
    await vi.waitFor(() => expect(document.querySelector('[role=dialog]')).toBeNull())
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('.md-context-toolbar')?.hidden).toBe(true))
    dialog.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(controller.getProjection().markdown).not.toContain('Unsaved draft')
    controller.setEditable(true)
    host.querySelector<HTMLElement>('.md-module')!.click()
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('.md-context-toolbar')?.hidden).toBe(false))
    document.querySelector<HTMLButtonElement>('[aria-label="Upravit: Probe"]')!.click()
    expect(document.querySelector('[role=dialog]')).not.toBeNull()
    controller.destroy(); expect(document.querySelector('[role=dialog]')).toBeNull()
  })
  it('applies a changed form that is left (D3) and keeps the block selected with the keyboard in the editor (P7)', async () => {
    const save = vi.fn((values: Record<string, string>, context: MarkdownModuleContext<Probe>) => { if (values.text === 'bad') throw new Error('Neplatné'); context.update!({ text: values.text! }) })
    const block = probe({
      render(context) { const text = document.createElement('p'); text.textContent = context.data.text; context.container.append(text) },
      edit(context) { return openMarkdownModuleForm({ editor: context.editor!, anchor: context.anchor, signal: context.signal, title: 'Settings', fields: [{ key: 'text', label: 'Text' }], values: context.data, save: values => save(values, context) }) },
    })
    const { host, editor } = attach(block)
    const outside = document.body.appendChild(document.createElement('button'))
    const form = () => document.querySelector<HTMLElement>('[role=dialog]')
    const field = () => form()!.querySelector('input')!
    const open = async () => {
      (await vi.waitFor(() => { const shown = host.querySelector<HTMLElement>('.md-module p'); expect(shown).not.toBeNull(); return shown! })).click()
      return field()
    }
    const type = (input: HTMLInputElement, value: string) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })) }
    const text = () => editor.state.doc.firstChild!.attrs.data.text
    // Unchanged, a click outside only closes it.
    await open(); outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
    expect(form()).toBeNull(); expect(save).not.toHaveBeenCalled()
    type(await open(), 'Clicked'); outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
    expect(form()).toBeNull(); expect(text()).toBe('Clicked')
    // The focus going elsewhere applies it too; an invalid value keeps the form open with its error.
    type(await open(), 'bad'); outside.focus()
    expect(form()?.querySelector('[role=alert]')?.textContent).toBe('Neplatné'); expect(text()).toBe('Clicked')
    type(field(), 'Left'); field().focus(); outside.focus()
    expect(form()).toBeNull(); expect(text()).toBe('Left'); expect(document.activeElement).toBe(outside)
    type(await open(), 'Applied'); form()!.querySelector('form')!.requestSubmit()
    expect(text()).toBe('Applied'); expect(editor.state.selection).toBeInstanceOf(NodeSelection); expect(document.activeElement).toBe(editor.view.dom)
  })
  it('a second press on the element applies a changed form once (P1, D3); a refused form keeps the panel and the press goes nowhere', async () => {
    const save = vi.fn((values: Record<string, string>, context: MarkdownModuleContext<Probe>) => { if (values.text === 'bad') throw new Error('Neplatné'); context.update!({ text: values.text! }) })
    const block = probe({
      render(context) { const text = document.createElement('p'); text.textContent = context.data.text; context.container.append(text) },
      edit(context) { return openMarkdownModuleForm({ editor: context.editor!, anchor: context.anchor, signal: context.signal, title: 'Settings', fields: [{ key: 'text', label: 'Text' }], values: context.data, save: values => save(values, context) }) },
    })
    const { host, editor } = attach(block)
    const form = () => document.querySelector<HTMLElement>('[role=dialog]')
    const field = () => form()!.querySelector('input')!
    const shown = () => vi.waitFor(() => { const element = host.querySelector<HTMLElement>('.md-module p'); expect(element).not.toBeNull(); return element! })
    const type = (value: string) => { field().focus(); field().value = value; field().dispatchEvent(new Event('input', { bubbles: true })) }
    const text = () => editor.state.doc.firstChild!.attrs.data.text
    // A press as Chrome makes it: the editor takes the focus before the click (unless the press was prevented), each event goes to
    // what is under the pointer then (a save may have redrawn the element).
    const press = (selector: string) => {
      const init = { bubbles: true, cancelable: true, button: 0 }, target = () => host.querySelector(selector)!
      target().dispatchEvent(new PointerEvent('pointerdown', init))
      const down = new MouseEvent('mousedown', init); target().dispatchEvent(down)
      if (!down.defaultPrevented) editor.view.focus()
      target().dispatchEvent(new MouseEvent('mouseup', init))
      const click = new MouseEvent('click', init); target().dispatchEvent(click)
      return { down, click }
    }
    const open = async () => { (await shown()).click() }
    await open(); type('Toggled')
    await shown(); press('.md-module p')
    expect(text()).toBe('Toggled'); expect(form()).toBeNull(); expect(save).toHaveBeenCalledTimes(1)

    await open(); type('bad')
    const { down, click } = press('.ProseMirror > p')
    expect([down.defaultPrevented, click.defaultPrevented]).toEqual([true, true])
    expect(form()?.querySelector('[role=alert]')?.textContent).toBe('Neplatné'); expect(document.activeElement).toBe(field())
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    // Fixed, the next press outside applies it and goes on to where it was aimed.
    type('Fixed')
    // (ProseMirror 1.43 handles a mouse press itself and prevents the browser's mousedown, so only the click tells it was not swallowed.)
    expect(press('.ProseMirror > p').click.defaultPrevented).toBe(false)
    expect(text()).toBe('Fixed'); expect(form()).toBeNull()

    // A draft given back (a question about it went back) is a change already: a click outside applies it.
    const draft = vi.fn()
    openMarkdownModuleForm({ editor, anchor: host.querySelector<HTMLElement>('.md-module')!, title: 'Draft', fields: [{ key: 'text', label: 'Text' }], values: { text: 'Typed' }, draft: true, save: draft })
    document.body.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
    expect(draft).toHaveBeenCalledWith({ text: 'Typed' }); expect(form()).toBeNull()
  })
  it('edits a block where it is shown: a click on its content or Enter/F2, never a click the runtime handled', async () => {
    const block = probe({
      render(context) {
        const own = document.createElement('span'); own.dataset.own = ''; own.textContent = 'own'; own.onclick = event => event.preventDefault()
        const text = document.createElement('p'); text.textContent = context.data.text
        context.container.append(text, own)
      },
      edit(context) { return openMarkdownModuleForm({ editor: context.editor!, anchor: context.anchor, signal: context.signal, title: 'Settings', fields: [{ key: 'text', label: 'Text' }], values: context.data, save: values => context.update!({ text: values.text! }) }) },
    })
    const { host, editor, controller } = attach(block)
    await vi.waitFor(() => expect(host.querySelector('.md-module p')).not.toBeNull())
    const section = host.querySelector<HTMLElement>('.md-module')!
    expect(section.classList).toContain('md-module-editable')
    host.querySelector<HTMLElement>('[data-own]')!.click()
    expect(document.querySelector('[role=dialog]')).toBeNull(); expect(editor.state.selection.from).toBe(0)
    host.querySelector<HTMLElement>('.md-module p')!.click()
    const dialog = document.querySelector<HTMLElement>('[role=dialog]')!
    expect(section.getAttribute('aria-expanded')).toBe('true')
    expect(document.activeElement).toBe(dialog.querySelector('input'))
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(document.querySelector<HTMLElement>('.md-context-toolbar')!.hidden).toBe(false)
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(document.querySelector('[role=dialog]')).toBeNull(); expect(document.activeElement).toBe(editor.view.dom)
    const before = editor.getJSON()
    for (const key of ['Enter', 'F2']) {
      const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }); editor.view.dom.dispatchEvent(event)
      expect(event.defaultPrevented).toBe(true); expect(document.querySelector('[role=dialog]')).not.toBeNull()
      document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    }
    expect(editor.getJSON()).toEqual(before)
    host.querySelector<HTMLElement>('.md-module p')!.click(); host.querySelector<HTMLElement>('.md-module p')!.click()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    const editable = host.querySelector('.md-module p')
    controller.setEditable(false)
    await vi.waitFor(() => { const text = host.querySelector('.md-module p'); expect(text).not.toBeNull(); expect(text).not.toBe(editable) })
    host.querySelector<HTMLElement>('.md-module p')!.click()
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(document.querySelector('[role=dialog]')).toBeNull()
  })
  it('preserves data and keeps editing available after a render failure, then retries', async () => {
    const render = vi.fn().mockRejectedValueOnce(new Error('renderer failed')).mockImplementation((context: MarkdownModuleContext<Probe>) => { context.container.textContent = 'Recovered' })
    const edit = vi.fn()
    const block = probe({ render, edit }); const { host, controller } = attach(block)
    await vi.waitFor(() => expect(host.querySelector('[data-module-retry]')).not.toBeNull())
    const value = controller.getValue()
    expect(host.textContent).toContain('Saved'); host.querySelector<HTMLElement>('.md-module')!.click(); expect(edit).toHaveBeenCalledOnce(); document.querySelector<HTMLButtonElement>('[aria-label="Upravit: Probe"]')!.click(); expect(edit).toHaveBeenCalledTimes(2)
    host.querySelector<HTMLButtonElement>('[data-module-retry]')!.click()
    await vi.waitFor(() => expect(host.textContent).toContain('Recovered'))
    expect(controller.getValue()).toEqual(value)
  })
  it('keeps a node context alive across readonly re-renders and reports editability separately', async () => {
    const contexts: MarkdownModuleContext<Probe>[] = []
    const render = vi.fn((context: MarkdownModuleContext<Probe>) => { contexts.push(context); context.container.textContent = context.data.text })
    const block = probe({ render }); const { host, editor, controller } = attach(block)
    await vi.waitFor(() => expect(render).toHaveBeenCalledOnce())
    const detail = contexts[0]!.nodeContext!()
    expect(detail.anchor).toBe(host.querySelector('.md-module'))
    const changes: boolean[] = []
    detail.onEditableChange(editable => changes.push(editable))
    controller.setEditable(false)
    await vi.waitFor(() => expect(contexts[0]!.signal.aborted).toBe(true))
    expect(detail.signal.aborted).toBe(false)
    detail.update!({ text: 'Refused while readonly' })
    expect(controller.getProjection().markdown).toBe('Saved')
    controller.setEditable(true)
    await vi.waitFor(() => expect(changes).toEqual([false, true]))
    // The render generation's update is stale now; the node context still writes to the same node.
    contexts[0]!.update!({ text: 'Stale generation' })
    expect(controller.getProjection().markdown).toBe('Saved')
    detail.update!({ text: 'From the workspace' })
    expect(controller.getProjection().markdown).toBe('From the workspace')
    expect(detail.signal.aborted).toBe(false)
    editor.commands.setNodeSelection(0); editor.commands.deleteSelection()
    expect(detail.signal.aborted).toBe(true)
    controller.setEditable(false)
    await Promise.resolve()
    expect(changes).toEqual([false, true])
    detail.update!({ text: 'Resurrected' })
    expect(controller.getProjection().markdown).not.toContain('Resurrected')
  })
  it('rejects non-JSON normalized data before mutation and cannot dispatch forged commands', () => {
    const block = probe({ render() {} }); const { controller } = attach(block)
    const before = controller.getValue()
    for (const extra of [Number.NaN, Number.POSITIVE_INFINITY, undefined, new Date(), { nested: BigInt(1) }]) {
      const data = { text: 'Invalid', extra }
      expect(() => block.node(data)).toThrow()
      expect(controller.dispatch(block.insert(data))).toBe(false)
    }
    expect(controller.dispatch({ type: 'plugin-command', id: 'probe.insert' } as unknown as ReturnType<typeof block.insert>)).toBe(false)
    expect(controller.getValue()).toEqual(before)
    const input = { text: 'Snapshot' }; const node = block.node(input); input.text = 'Mutated'
    expect(node.attrs?.data).toEqual({ text: 'Snapshot' })
  })
  it('enhances readonly fallback without an editor and disposes exactly once', async () => {
    const cleanup = vi.fn()
    const block = probe({ render(context) { expect(context.editor).toBeUndefined(); expect(context.update).toBeUndefined(); expect(context.rootContainer).toBe(root); context.container.textContent = 'Rendered'; return cleanup } })
    const root = document.createElement('div'); const section = document.createElement('section')
    section.dataset.mdModule = 'probeBlock'; section.dataset.value = JSON.stringify({ text: 'Saved' }); section.textContent = 'Saved'; root.append(section); document.body.append(root)
    const abort = new AbortController(); const plugin = block.createPlugin().create({ profile: 'rich', signal: abort.signal })
    const clean = plugin.onRender!({ container: root, document: { type: 'doc' }, ownerDocument: document, signal: abort.signal })
    await vi.waitFor(() => expect(root.textContent).toBe('Rendered'))
    abort.abort(); clean?.(); expect(cleanup).toHaveBeenCalledOnce()
  })
  it('uses the owner document for forms/downloads and releases failed dialog mounts', () => {
    const iframe = document.createElement('iframe'); document.body.append(iframe)
    const owner = iframe.contentDocument!; const block = probe({ render() {} }); const { editor } = attach(block, owner)
    const proto = owner.createElement('dialog').constructor.prototype
    Object.defineProperty(proto, 'showModal', { configurable: true, value() { this.setAttribute('open', '') } })
    const close = openMarkdownModuleForm({ editor, title: 'Owned', fields: [], values: {}, save() {} })
    expect(owner.querySelector('dialog')).not.toBeNull(); expect(document.querySelector('dialog')).toBeNull(); close()
    Object.defineProperty(proto, 'showModal', { configurable: true, value() { throw new Error('No modal') } })
    expect(() => openMarkdownModuleForm({ editor, title: 'Failure', fields: [], values: {}, save() {} })).toThrow('No modal')
    expect(owner.querySelector('dialog')).toBeNull(); expect(editor.view.dom.hasAttribute('data-md-overlay-owner')).toBe(false)
    vi.useFakeTimers()
    const ownerUrl = owner.defaultView!.URL; const create = vi.fn(() => 'blob:owner'); const revoke = vi.fn()
    Object.defineProperty(ownerUrl, 'createObjectURL', { configurable: true, value: create }); Object.defineProperty(ownerUrl, 'revokeObjectURL', { configurable: true, value: revoke })
    vi.spyOn(owner.createElement('a').constructor.prototype, 'click').mockImplementation(() => {})
    downloadMarkdownFile(owner, 'example.txt', new Blob(['example']))
    expect(create).toHaveBeenCalledOnce(); expect(owner.querySelector('a[download]')).toBeNull()
    vi.runAllTimers(); expect(revoke).toHaveBeenCalledWith('blob:owner')
  })
})
