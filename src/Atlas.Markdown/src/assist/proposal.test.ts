// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { openMarkdownProposalPanel } from './index'

// jsdom has no layout: ProseMirror measures ranges when it scrolls the selection into view.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })

const original = 'Pro přístup do serverovny je nutné nejprve vyplnit žádost v systému Service Desk.'
const shorter = 'Pro přístup do serverovny vyplňte žádost v Service Desku.'
function mount(texts = [original, 'Při vstupu se zapište do knihy návštěv.']) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
    document: { type: 'doc', content: texts.map(text => ({ type: 'paragraph', content: [{ type: 'text', text }] })) } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Dokument' })
  editor.view.focus()
  // The first paragraph's inline content.
  const range = { from: 1, to: 1 + original.length }
  return { editor, controller, range }
}
const after: JSONContent = { type: 'paragraph', content: [{ type: 'text', text: shorter }] }
const panel = () => document.querySelector<HTMLElement>('.md-assist-proposal')
const button = (label: string) => [...panel()!.querySelectorAll('button')].find(item => item.textContent === label)!
const flush = async () => { for (let i = 0; i < 4; i++) await Promise.resolve() }

describe('openMarkdownProposalPanel', () => {
  it('shows before and after with a textual summary', async () => {
    const { editor, range } = mount()
    const result = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after })
    await flush()
    const root = panel()!
    expect(root.closest('[role=dialog]')?.getAttribute('aria-label')).toBe('Zkrátit')
    expect(root.textContent).toContain('Před')
    expect(root.textContent).toContain('Po')
    // Removed words are struck through, added words underlined: never by colour alone.
    expect([...root.querySelectorAll('del')].map(item => item.textContent!.trim()).join(' ')).toContain('je nutné nejprve')
    expect([...root.querySelectorAll('ins')].map(item => item.textContent!.trim()).join(' ')).toContain('vyplňte')
    expect(root.querySelector('.md-assist-summary')?.textContent).toMatch(/^Odebráno \d+ slov(a|o)?, přidáno \d+ slov(a|o)?$/)
    expect(root.querySelector('svg.md-assist-dash rect')).not.toBeNull()
    expect([...root.querySelectorAll('button')].map(item => item.textContent)).toEqual(['Použít', 'Zahodit'])
    button('Zahodit').click()
    await expect(result).resolves.toBe('cancelled')
  })

  it('applies once as one undo step, cancels on Escape, outside click and readonly', async () => {
    const { editor, controller, range } = mount()
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    editor.commands.insertContent(' Díky.')
    const typed = editor.getText()

    const applied = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after, summary: 'Odebráno 1 věta' })
    await flush()
    expect(panel()!.querySelector('.md-assist-summary')?.textContent).toBe('Odebráno 1 věta')
    button('Použít').click()
    await expect(applied).resolves.toBe('applied')
    expect(editor.state.doc.firstChild!.textContent).toBe(shorter)
    expect(panel()).toBeNull()
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).toBe(typed)
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).not.toContain('Díky.')
    controller.dispatch({ type: 'redo' })

    const escaped = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after })
    await flush()
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    await expect(escaped).resolves.toBe('cancelled')

    const outside = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after })
    await flush()
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    await expect(outside).resolves.toBe('cancelled')

    const readonly = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after })
    await flush()
    controller.setEditable(false)
    await expect(readonly).resolves.toBe('cancelled')
    expect(editor.getText()).toBe(typed)
    expect(panel()).toBeNull()
  })

  it('edits before applying: writes once and selects the result', async () => {
    const { editor, controller, range } = mount()
    const edited = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after, allowEdit: true })
    await flush()
    button('Upravit před použitím').click()
    await expect(edited).resolves.toBe('edited')
    expect(editor.state.doc.firstChild!.textContent).toBe(shorter)
    const { from, to } = editor.state.selection
    expect(editor.state.doc.textBetween(from, to)).toBe(shorter)
    controller.dispatch({ type: 'undo' })
    expect(editor.state.doc.firstChild!.textContent).toBe(original)
  })

  it('keeps a changed range from being overwritten and aborts with its signal', async () => {
    const { editor, range } = mount()
    const stale = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after })
    await flush()
    editor.view.dispatch(editor.state.tr.insertText('Nově: ', 1).setMeta('y-sync$', { isChangeOrigin: true }))
    await flush()
    expect(panel()!.querySelector('[role=status]')?.textContent).toBe('Návrh už neodpovídá textu.')
    expect(button('Použít').disabled).toBe(true)
    button('Zahodit').click()
    await expect(stale).resolves.toBe('cancelled')

    const controllerAbort = new AbortController()
    const aborted = openMarkdownProposalPanel({ editor, range: { from: 1, to: 6 }, title: 'Zkrátit', before: 'Nově:', after, signal: controllerAbort.signal })
    await flush()
    controllerAbort.abort()
    await expect(aborted).resolves.toBe('cancelled')
    expect(editor.state.doc.firstChild!.textContent).toContain('Nově: ')
  })

  it('renders a structural proposal and writes a doc of blocks', async () => {
    const { editor, controller } = mount(['a;b', 'c;d'])
    const table: JSONContent = { type: 'table', content: [
      { type: 'tableRow', content: [{ type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'a' }] }] }, { type: 'tableHeader', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'b' }] }] }] },
      { type: 'tableRow', content: [{ type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'c' }] }] }, { type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'd' }] }] }] },
    ] }
    const whole = { from: 0, to: editor.state.doc.content.size }
    const result = openMarkdownProposalPanel({ editor, range: whole, title: 'Převést na tabulku', before: 'a;b\nc;d', after: { type: 'doc', content: [table] } })
    await flush()
    expect(panel()!.querySelector('ins table')).not.toBeNull()
    button('Použít').click()
    await expect(result).resolves.toBe('applied')
    expect(editor.state.doc.firstChild!.type.name).toBe('table')
    controller.dispatch({ type: 'undo' })
    expect(editor.getText({ blockSeparator: '\n' })).toBe('a;b\nc;d')
  })
})
it('keeps one proposal open while loading, retries errors, and never writes before Use', async () => {
  const { editor, range } = mount()
  let fail!: (reason: Error) => void
  const load = vi.fn().mockImplementationOnce(() => new Promise((_resolve, reject) => { fail = reject })).mockResolvedValue({ after })
  const result = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, load, allowEdit: true })
  await flush()
  expect(panel()!.textContent).toContain('Připravuji návrh')
  expect(panel()!.querySelector('button')?.textContent).toBe('Zrušit')
  fail(new Error('provider'))
  await flush()
  expect(panel()!.textContent).toContain('Návrh se nepodařilo připravit')
  button('Zkusit znovu').click()
  await flush()
  expect(document.querySelectorAll('.md-assist-proposal')).toHaveLength(1)
  expect(editor.state.doc.firstChild!.textContent).toBe(original)
  button('Použít').click()
  await expect(result).resolves.toBe('applied')
  expect(editor.state.doc.firstChild!.textContent).toBe(shorter)
})

it('AI4-6 keeps the keyboard in the panel: Zrušit while it loads, Použít once the proposal is there, Zrušit again on a retry', async () => {
  const { editor, range } = mount()
  let ready!: (value: { after: JSONContent }) => void
  const load = vi.fn().mockImplementationOnce(() => new Promise(resolve => { ready = resolve })).mockImplementationOnce(() => new Promise(() => {}))
  void openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, load, allowEdit: true })
  await flush()
  button('Zrušit').focus()
  ready({ after })
  await flush()
  expect(document.activeElement?.textContent).toBe('Použít')
  const retry = button('Zkusit znovu')
  retry.focus()
  retry.click()
  await flush()
  expect(document.activeElement?.textContent).toBe('Zrušit')
})

it('leaves the focus where the author went while the proposal loads', async () => {
  const { editor, range } = mount()
  let ready!: (value: { after: JSONContent }) => void
  void openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, load: () => new Promise(resolve => { ready = resolve }) })
  await flush()
  editor.view.focus()
  ready({ after })
  await flush()
  expect(editor.view.hasFocus()).toBe(true)
})

it('aborts a pending proposal on readonly and discards late responses', async () => {
  const { editor, controller, range } = mount()
  let signal!: AbortSignal, resolve!: (value: { after: JSONContent }) => void
  const result = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, load: current => { signal = current; return new Promise(done => { resolve = done }) } })
  await flush()
  controller.setEditable(false)
  await expect(result).resolves.toBe('cancelled')
  expect(signal.aborted).toBe(true)
  resolve({ after }); await flush()
  expect(panel()).toBeNull()
  expect(editor.state.doc.firstChild!.textContent).toBe(original)
})
it('retries a ready or stale proposal against the newly mapped selection', async () => {
  const { editor, range } = mount()
  const load = vi.fn(async (_signal: AbortSignal, current: { from: number; to: number }) => ({ after, before: editor.state.doc.textBetween(current.from, current.to) }))
  const result = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, load })
  await flush()
  expect(button('Zkusit znovu')).toBeDefined()
  editor.view.dispatch(editor.state.tr.insertText('Nově: ', 1))
  await flush()
  expect(button('Použít').disabled).toBe(true)
  button('Zkusit znovu').click(); await flush()
  expect(load).toHaveBeenCalledTimes(2)
  expect(panel()!.querySelector('.md-assist-before')!.textContent).toContain('Nově:')
  expect(button('Použít').disabled).toBe(false)
  button('Použít').click(); await expect(result).resolves.toBe('applied')
})
it('optional feedback reports one local vote without changing the document or closing the proposal', async () => {
  const { editor, range } = mount()
  const onFeedback = vi.fn()
  const result = openMarkdownProposalPanel({ editor, range, title: 'Zkrátit', before: original, after, onFeedback })
  await flush()
  const up = panel()!.querySelector<HTMLButtonElement>('[aria-label="Užitečný návrh"]')!
  up.click(); up.click()
  expect(onFeedback).toHaveBeenCalledTimes(1)
  expect(onFeedback).toHaveBeenCalledWith('up')
  expect(up.getAttribute('aria-pressed')).toBe('true')
  expect(editor.state.doc.firstChild!.textContent).toBe(original)
  button('Zahodit').click(); await expect(result).resolves.toBe('cancelled')
})
