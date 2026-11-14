// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, MARKDOWN_FOLLOW_LINK_EVENT, MARKDOWN_OPEN_LINK_EVENT } from '../core/index'
import { editorForController } from '../core/internal'

const controllers: ReturnType<typeof createMarkdownEditorController>[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

function mount(value: string) {
  const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Links' })
  const editor = editorForController(controller)!
  // The caret after `text` (its first occurrence in the document).
  const after = (text: string) => {
    let at = -1
    editor.state.doc.descendants((node, pos) => { if (at < 0 && node.isText && node.text!.includes(text)) at = pos + node.text!.indexOf(text) + text.length })
    editor.commands.setTextSelection(at)
  }
  const type = (text: string) => editor.view.dispatch(editor.state.tr.insertText(text))
  const links = () => [...host.querySelectorAll('a')].map(link => `${link.textContent}→${link.getAttribute('href')}`)
  return { controller, host, editor, after, type, links }
}
const toolbar = () => [...document.querySelectorAll<HTMLElement>('.md-context-toolbar')].find(element => !element.hidden)
const settle = () => new Promise(resolve => setTimeout(resolve, 5))

describe('editing a link in the text', () => {
  it('keeps text typed at the end of a link out of it and text typed inside in it', () => {
    const { after, type, links } = mount('- https://example.test')
    after('https://example.test'); type(' note')
    expect(links()).toEqual(['https://example.test→https://example.test'])
    after('https://exam'); type('X')
    expect(links()).toEqual(['https://examXple.test→https://example.test'])
  })

  it('keeps the rest a link when characters of it are deleted', () => {
    const { editor, after, links } = mount('See [the guide](https://example.test) now')
    after('the gui')
    editor.view.dispatch(editor.state.tr.delete(editor.state.selection.from - 3, editor.state.selection.from))
    expect(links()).toEqual(['the de→https://example.test'])
  })

  it('unlinks only the link at the caret, and only the selected part of a link', () => {
    const { controller, editor, after, links, host } = mount('[one](https://a.test)[two](https://b.test) and [three four](https://c.test)')
    after('on')
    expect(controller.dispatch({ type: 'unset-link' })).toBe(true)
    expect(links()).toEqual(['two→https://b.test', 'three four→https://c.test'])
    after('three')
    editor.commands.setTextSelection({ from: editor.state.selection.from, to: editor.state.selection.from + 5 })
    expect(controller.dispatch({ type: 'unset-link' })).toBe(true)
    expect(links()).toEqual(['two→https://b.test', 'three→https://c.test'])
    expect(host.textContent).toBe('onetwo and three four')
  })

  it('shows the link bar with the address at a caret inside a link: open, edit, unlink', async () => {
    const { editor, after, links } = mount('See [the guide](https://example.test/guide) now')
    editor.view.focus()
    after('See'); await settle()
    expect(toolbar()).toBeUndefined()
    after('the gu'); await settle()
    const buttons = [...toolbar()!.querySelectorAll('button')]
    expect(buttons.map(button => button.getAttribute('aria-label'))).toEqual(['Otevřít odkaz', 'Upravit odkaz', 'Odebrat odkaz'])
    expect(buttons[0]!.textContent).toBe('https://example.test/guide')

    // A host that routes the link itself takes it; otherwise it opens in a new tab.
    const open = vi.spyOn(window, 'open').mockReturnValue(null)
    const routed = vi.fn((event: Event) => event.preventDefault())
    editor.view.dom.addEventListener(MARKDOWN_FOLLOW_LINK_EVENT, routed, { once: true })
    buttons[0]!.click()
    expect((routed.mock.calls[0]![0] as CustomEvent).detail).toEqual({ href: 'https://example.test/guide' })
    expect(open).not.toHaveBeenCalled()
    buttons[0]!.click()
    expect(open).toHaveBeenCalledWith('https://example.test/guide', '_blank', 'noopener,noreferrer')

    const field = vi.fn()
    editor.view.dom.addEventListener(MARKDOWN_OPEN_LINK_EVENT, field)
    buttons[1]!.click()
    expect(field).toHaveBeenCalledTimes(1)

    buttons[2]!.click(); await settle()
    expect(links()).toEqual([])
    expect(editor.getText()).toBe('See the guide now')
    expect(toolbar()).toBeUndefined()
  })

  it('offers the same three actions from the keyboard menu at the caret', async () => {
    const { editor, after } = mount('See [the guide](https://example.test/guide) now')
    editor.view.focus(); after('the gu'); await settle()
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', code: 'Space', ctrlKey: true, bubbles: true, cancelable: true }))
    await settle()
    const rows = [...document.querySelectorAll('[role=option]')].map(row => row.textContent)
    for (const label of ['Otevřít odkaz', 'Upravit odkaz', 'Odebrat odkaz']) expect(rows.some(row => row?.includes(label))).toBe(true)
  })
})
