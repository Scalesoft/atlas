// @vitest-environment jsdom

import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type LocalMarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { createYouTrackPlugin, insertYouTrackIssue } from './plugin'
import type { YouTrackPickerContext } from './picker'
import type { YouTrackIssue } from './types'

const issue: YouTrackIssue = { id: '2-42', key: 'YT-42', title: 'Fix [issue]', url: 'https://tracker.example/issue/YT-42' }
const controllers: LocalMarkdownEditorController<'rich'>[] = []
const initial: MarkdownRichValue = {
  format: 'atlas-rich', version: 1,
  document: { type: 'doc', content: [
    { type: 'paragraph', content: [{ type: 'text', text: 'before' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'target' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'after' }] },
  ] },
}

function mount(plugin = createYouTrackPlugin({ client: { search: async () => [] } }), value = initial) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture-test', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins: [plugin, capture] })
  controllers.push(controller)
  const element = document.createElement('div')
  document.body.append(element)
  const detach = controller.attachView(element, { ariaLabel: 'Editor' })
  return { controller, editor, detach }
}

function deferredPicker() {
  let resolve!: (issue: YouTrackIssue | null) => void
  const promise = new Promise<YouTrackIssue | null>(yes => { resolve = yes })
  return { picker: vi.fn((_context: YouTrackPickerContext) => promise), resolve }
}

function issueNodes(editor: Editor) {
  const found: unknown[] = []
  editor.state.doc.descendants(node => { if (node.type.name === 'youtrackIssue') found.push(node.attrs) })
  return found
}

afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

describe('YouTrack public plugin contract', () => {
  it('exposes saved references without a URL resolver or guessed matcher', () => {
    const search = vi.fn(async () => [])
    const instance = createYouTrackPlugin({ client: { search }, matchUrl: () => true }).create({ profile: 'rich', signal: new AbortController().signal })
    const provider = instance.references?.[0]
    expect(provider).toBeDefined()
    expect(provider!.nodeType).toBe('youtrackIssue')
    expect(provider!.read(Object.freeze({ type: 'youtrackIssue', attrs: Object.freeze(issue) }))).toEqual({ url: issue.url, label: 'YT-42: Fix [issue]' })
    expect(provider!.read({ type: 'paragraph' })).toBeNull()
    expect(provider!.matchUrl).toBeUndefined()
    expect(provider!.resolveUrl).toBeUndefined()
    expect(search).not.toHaveBeenCalled()
  })

  it('recognizes only host-matched URLs without requests and explicitly resolves normalized issue content', async () => {
    const search = vi.fn(async () => [])
    const resolve = vi.fn(async (_url: string, _context: { readonly signal: AbortSignal }) => ({ ...issue, id: ' 2-42 ', title: ' Fix [issue] ', state: ' In progress ' }))
    const matchUrl = (url: string) => new URL(url).origin === 'https://tracker.example' && new URL(url).pathname.startsWith('/issue/')
    const instance = createYouTrackPlugin({ client: { search, resolve }, matchUrl }).create({ profile: 'rich', signal: new AbortController().signal })
    const provider = instance.references?.[0]
    expect(provider).toBeDefined()
    expect(provider!.matchUrl!(issue.url)).toBe(true)
    expect(provider!.matchUrl!('https://other.example/issue/YT-42')).toBe(false)
    expect(provider!.read({ type: 'youtrackIssue', attrs: { ...issue } })).toEqual({ url: issue.url, label: 'YT-42: Fix [issue]' })
    expect(resolve).not.toHaveBeenCalled()
    expect(search).not.toHaveBeenCalled()
    const signal = new AbortController().signal
    expect(await provider!.resolveUrl!(issue.url, { signal })).toEqual({ type: 'youtrackIssue', attrs: { ...issue, state: 'In progress' } })
    expect(resolve).toHaveBeenCalledWith(issue.url, { signal })
    const unconfigured = createYouTrackPlugin({ client: { search, resolve } }).create({ profile: 'rich', signal }).references![0]!
    expect(unconfigured.matchUrl).toBeUndefined()
  })

  it('preserves unavailable resolution and rejects unsafe resolved issue data', async () => {
    const resolve = vi.fn(async (url: string) => url === issue.url ? { ...issue, url: 'javascript:alert(1)' } : null)
    const provider = createYouTrackPlugin({ client: { search: async () => [], resolve }, matchUrl: () => true }).create({ profile: 'rich', signal: new AbortController().signal }).references?.[0]
    expect(provider).toBeDefined()
    const context = { signal: new AbortController().signal }
    expect(await provider!.resolveUrl!('https://tracker.example/issue/missing', context)).toBeNull()
    await expect(provider!.resolveUrl!(issue.url, context)).rejects.toThrow(/HTTP/)
  })

  it('shows the saved key, title and state as plain text through rich reload', () => {
    const { controller, editor } = mount()
    const withState = { ...issue, title: '<img src=x onerror=alert(1)> Fix [issue]', state: '<b>In progress</b>' }
    expect(controller.dispatch(insertYouTrackIssue(withState))).toBe(true)
    expect(issueNodes(editor)).toEqual([withState])
    const chip = editor.view.dom.querySelector<HTMLAnchorElement>('.md-youtrack-issue')!
    expect(chip.textContent).toBe('YT-42: <img src=x onerror=alert(1)> Fix [issue] · <b>In progress</b>')
    expect(chip.querySelector('img, b')).toBeNull()
    expect(chip.href).toBe(issue.url)
    const reopened = mount(undefined, controller.getValue())
    expect(issueNodes(reopened.editor)).toEqual([withState])
    expect(reopened.editor.view.dom.querySelector('.md-youtrack-issue')?.textContent).toBe(chip.textContent)
    expect(controller.getProjection().markdown).toContain('In progress')
  })
  it('requires rich storage because Markdown falls back to an ordinary link', () => {
    expect(() => createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: '' },
      plugins: [createYouTrackPlugin({ client: { search: async () => [] } })],
    })).toThrow(/does not support/)
  })

  it('inserts a validated issue using the public command and persists its rich representation', () => {
    const { controller, editor } = mount()
    editor.commands.setTextSelection(9)
    expect(controller.dispatch(insertYouTrackIssue(issue))).toBe(true)
    expect(issueNodes(editor)).toEqual([{ ...issue, state: null }])
    expect(editor.view.dom.querySelector('.md-youtrack-issue')?.textContent).toBe('YT-42: Fix [issue]')
    const projection = controller.getProjection()
    expect(projection.markdownLossy).toBe(true)
    expect(projection.markdown).toContain('[YT-42: Fix \\[issue\\]](https://tracker.example/issue/YT-42)')
    expect(projection.text).toContain('YT-42: Fix [issue] (https://tracker.example/issue/YT-42)')
    expect(projection.rich.plugins).toContainEqual({ id: 'youtrack', version: 1 })

    const reopened = mount(undefined, projection.rich)
    expect(issueNodes(reopened.editor)).toEqual([{ ...issue, state: null }])
    expect(reopened.controller.getProjection().markdown).toBe(projection.markdown)
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(issueNodes(editor)).toEqual([])
  })

  it('rejects invalid issue data and readonly insertion', () => {
    const { controller, editor } = mount()
    expect(controller.dispatch(insertYouTrackIssue({ ...issue, url: 'javascript:alert(1)' }))).toBe(false)
    expect(controller.dispatch(insertYouTrackIssue({ ...issue, url: 'https://user:secret@tracker.example/issue/1' }))).toBe(false)
    expect(controller.dispatch(insertYouTrackIssue({ ...issue, title: '' }))).toBe(false)
    controller.setEditable(false)
    expect(controller.query(insertYouTrackIssue(issue)).enabled).toBe(false)
    expect(controller.dispatch(insertYouTrackIssue(issue))).toBe(false)
    expect(issueNodes(editor)).toEqual([])
  })

  it('rejects unsafe rich data at the load boundary and requires the plugin to reopen it', () => {
    const { controller } = mount()
    controller.dispatch(insertYouTrackIssue(issue))
    const value = controller.getValue()
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value } })).toThrow()
    const unsafe = structuredClone(value)
    const document = unsafe.document as { content: Array<{ content: Array<{ attrs?: { url: string } }> }> }
    document.content[0].content[0].attrs!.url = 'data:text/html,unsafe'
    expect(() => createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: unsafe },
      plugins: [createYouTrackPlugin({ client: { search: async () => [] } })],
    })).toThrow()
  })

  it('keeps command queries free of UI and maps the picker target through intervening edits', async () => {
    const request = deferredPicker()
    const { controller, editor } = mount(createYouTrackPlugin({ client: { search: async () => [] }, picker: request.picker }))
    editor.commands.setTextSelection(9)
    const command = controller.actions.slash[0].command
    expect(controller.query(command).enabled).toBe(true)
    expect(request.picker).not.toHaveBeenCalled()
    expect(controller.dispatch(command)).toBe(true)
    await Promise.resolve()
    expect(request.picker).toHaveBeenCalledTimes(1)
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    request.resolve(issue)
    await vi.waitFor(() => expect(issueNodes(editor)).toEqual([{ ...issue, state: null }]))

    expect(editor.getJSON().content?.[1].content?.[0].type).toBe('youtrackIssue')
    expect((editor.getJSON() as any).content?.[1].content?.[1].text).toBe('target')
    expect(editor.state.selection.$from.parent.textContent).toBe('after')
  })

  it('opens the picker with the slash parameter as its initial query', async () => {
    const request = deferredPicker()
    const { controller, editor } = mount(createYouTrackPlugin({ client: { search: async () => [] }, picker: request.picker }))
    editor.commands.setTextSelection(9)
    const action = controller.actions.slash[0]
    expect(action.parameter?.label).toBeTruthy()
    expect(controller.dispatch(action.parameter!.command('YT-42')!)).toBe(true)
    await Promise.resolve()
    expect(request.picker.mock.calls[0][0].initialQuery).toBe('YT-42')
    request.resolve(null); await new Promise(resolve => setTimeout(resolve, 0))
    expect(controller.dispatch(action.command)).toBe(true)
    await Promise.resolve()
    expect(request.picker.mock.calls[1][0].initialQuery).toBeUndefined()
  })

  it('cancels a removed target and ignores a picker that resolves after cancellation', async () => {
    const request = deferredPicker()
    const { controller, editor } = mount(createYouTrackPlugin({ client: { search: async () => [] }, picker: request.picker }))
    editor.commands.setTextSelection(9)
    controller.dispatch(controller.actions.slash[0].command)
    await Promise.resolve()
    editor.view.dispatch(editor.state.tr.delete(8, 16))
    expect(request.picker.mock.calls[0][0].signal.aborted).toBe(true)
    request.resolve(issue)
    await Promise.resolve()
    expect(issueNodes(editor)).toEqual([])
  })

  it('isolates reusable plugin instances and cancels on detach, readonly and destruction', async () => {
    const requests = [deferredPicker(), deferredPicker()]
    let call = 0
    const picker = vi.fn((context: YouTrackPickerContext) => requests[call++].picker(context))
    const plugin = createYouTrackPlugin({ client: { search: async () => [] }, picker })
    const first = mount(plugin)
    const second = mount(plugin)
    first.controller.dispatch(first.controller.actions.slash[0].command)
    second.controller.dispatch(second.controller.actions.slash[0].command)
    await Promise.resolve()
    first.detach()
    expect(requests[0].picker.mock.calls[0][0].signal.aborted).toBe(true)
    expect(requests[1].picker.mock.calls[0][0].signal.aborted).toBe(false)
    second.controller.setEditable(false)
    expect(requests[1].picker.mock.calls[0][0].signal.aborted).toBe(true)
    first.controller.destroy()
    requests.forEach(request => request.resolve(issue))
    await Promise.resolve()
    expect(issueNodes(second.editor)).toEqual([])
  })
})
