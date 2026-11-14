// @vitest-environment jsdom
import type { Editor } from '@tiptap/core'
import { afterEach, beforeAll, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, openMarkdownContextPopover, type LocalMarkdownEditorController, type MarkdownPlugin } from '../../core'
import { createYouTrackPlugin } from './plugin'
import type { YouTrackClient } from './types'

// The picker's lazy runtime: imported cold inside the 1 s wait for its panel, it missed it in 3 of 3 editor suites run beside the web suite.
beforeAll(async () => { await import('./pickerRuntime') })

const controllers: LocalMarkdownEditorController<'rich'>[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
})

async function mount(search: YouTrackClient['search'] = async () => []) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture-picker', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const controller = createMarkdownEditorController({
    profile: 'rich', source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
      document: { type: 'doc', content: [{ type: 'paragraph' }] } } },
    plugins: [createYouTrackPlugin({ client: { search }, pickerHelpText: '<b>Saved snapshot</b>' }), capture],
  })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  editor.view.focus()
  await vi.waitFor(() => expect(controller.query(controller.actions.slash[0].command).enabled).toBe(true))
  controller.dispatch(controller.actions.slash[0].command)
  await vi.waitFor(() => expect(document.querySelector('dialog,[role="dialog"]')).not.toBeNull())
  return { controller, editor, panel: document.querySelector<HTMLElement>('dialog,[role="dialog"]')! }
}

it('uses the shared nonmodal panel, escapes host help, and cancels pending work when another panel replaces it', async () => {
  let requestSignal!: AbortSignal
  let respond!: (issues: readonly []) => void
  const { controller, editor, panel } = await mount((_query, { signal }) => {
    requestSignal = signal
    return new Promise(resolve => { respond = resolve })
  })
  expect(panel.tagName).toBe('DIV')
  expect(panel.getAttribute('aria-modal')).toBeNull()
  expect(panel.textContent).toContain('<b>Saved snapshot</b>')
  expect(panel.querySelector('b,h2')).toBeNull()
  const input = panel.querySelector('input')!
  input.value = 'pending'
  input.dispatchEvent(new Event('input'))
  await vi.waitFor(() => expect(requestSignal).toBeDefined())
  const before = controller.getValue()
  const trigger = document.createElement('button'); document.body.append(trigger)
  openMarkdownContextPopover({ editor, anchor: trigger, title: 'Other plugin', render(body) { body.append(document.createElement('input')) } })
  expect(panel.isConnected).toBe(false)
  expect(requestSignal.aborted).toBe(true)
  respond([])
  await Promise.resolve()
  expect(controller.getValue()).toEqual(before)
  expect(document.querySelectorAll('dialog,[role="dialog"]')).toHaveLength(1)
})

it('returns focus on Escape, leaves outside focus alone, and cancels when permissions become readonly', async () => {
  const { controller, editor, panel } = await mount()
  panel.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(panel.isConnected).toBe(false)
  expect(document.activeElement).toBe(editor.view.dom)
  await Promise.resolve()
  await vi.waitFor(() => expect(controller.query(controller.actions.slash[0].command).enabled).toBe(true))
  controller.dispatch(controller.actions.slash[0].command)
  await vi.waitFor(() => expect(document.querySelector('dialog,[role="dialog"]')).not.toBeNull())
  const outside = document.createElement('button'); document.body.append(outside); outside.focus()
  outside.dispatchEvent(new Event('pointerdown', { bubbles: true }))
  expect(document.querySelector('dialog,[role="dialog"]')).toBeNull()
  expect(document.activeElement).toBe(outside)
  await Promise.resolve()
  await vi.waitFor(() => expect(controller.query(controller.actions.slash[0].command).enabled).toBe(true))
  controller.dispatch(controller.actions.slash[0].command)
  await vi.waitFor(() => expect(document.querySelector('dialog,[role="dialog"]')).not.toBeNull())
  expect(document.querySelector('dialog,[role="dialog"]')).not.toBeNull()
  editor.view.dom.dispatchEvent(new Event('pointerdown', { bubbles: true }))
  expect(document.querySelector('dialog,[role="dialog"]')).toBeNull()
  await Promise.resolve()
  await vi.waitFor(() => expect(controller.query(controller.actions.slash[0].command).enabled).toBe(true))
  controller.dispatch(controller.actions.slash[0].command)
  await vi.waitFor(() => expect(document.querySelector('dialog,[role="dialog"]')).not.toBeNull())
  controller.setEditable(false)
  await vi.waitFor(() => expect(document.querySelector('dialog,[role="dialog"]')).toBeNull())
})
