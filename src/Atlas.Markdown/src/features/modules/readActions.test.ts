// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { addMarkdownReadActions, copyMarkdownText, zoomMarkdownBlock } from './readActions'

afterEach(() => { document.body.replaceChildren(); vi.restoreAllMocks() })

/** What everyone may do with a block (contract 3.6, J1.3): the same buttons for a reader and a writer, nothing that selects or edits. */
describe('read actions of a block', () => {
  it('puts a labelled button per action that runs on a click and never takes the selection', () => {
    const host = document.body.appendChild(document.createElement('div'))
    const run = vi.fn()
    addMarkdownReadActions(host, new AbortController().signal, [{ label: 'Zvětšit', icon: 'maximize-2', run }])
    const button = host.querySelector<HTMLButtonElement>('button.md-read-action')!
    expect(button.getAttribute('aria-label')).toBe('Zvětšit')
    const press = new MouseEvent('mousedown', { bubbles: true, cancelable: true }); button.dispatchEvent(press)
    expect(press.defaultPrevented).toBe(true)
    button.click()
    expect(run).toHaveBeenCalledOnce()
  })

  it('removes its buttons with the block and runs nothing afterwards', () => {
    const host = document.body.appendChild(document.createElement('div')), abort = new AbortController(), run = vi.fn()
    addMarkdownReadActions(host, abort.signal, [{ label: 'Kopírovat', icon: 'copy', run }])
    const button = host.querySelector<HTMLButtonElement>('button')!
    abort.abort()
    expect(host.querySelector('.md-read-actions')).toBeNull()
    button.click()
    expect(run).not.toHaveBeenCalled()
  })

  it('copies text and says so on the button, or says it failed', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
    const button = document.body.appendChild(document.createElement('button')); button.setAttribute('aria-label', 'Kopírovat zdroj')
    copyMarkdownText(button, 'A --> B')
    await vi.waitFor(() => expect(button.dataset.state).toBe('copied'))
    expect(writeText).toHaveBeenCalledWith('A --> B')
    expect(button.getAttribute('aria-label')).not.toBe('Kopírovat zdroj')
    writeText.mockRejectedValue(new Error('denied'))
    document.execCommand = vi.fn().mockReturnValue(false)
    const other = document.body.appendChild(document.createElement('button')); other.setAttribute('aria-label', 'Kopírovat')
    copyMarkdownText(other, 'x')
    await vi.waitFor(() => expect(other.dataset.state).toBe('failed'))
  })

  it('enlarges a block in a dialog that Escape closes, and gives the focus back to the opener', () => {
    const opener = document.body.appendChild(document.createElement('button'))
    const release = vi.fn()
    const dialog = () => document.querySelector<HTMLDialogElement>('dialog.md-block-zoom')
    zoomMarkdownBlock({ opener, label: 'Graf', render(body) { body.textContent = 'velký'; return release } })
    expect(dialog()?.textContent).toContain('velký')
    dialog()!.dispatchEvent(new Event('cancel', { cancelable: true }))
    expect(dialog()).toBeNull()
    expect(release).toHaveBeenCalledOnce()
    expect(document.activeElement).toBe(opener)
  })
})
