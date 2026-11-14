import type { Editor } from '@tiptap/core'
import { registerMarkdownOverlay } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { createIconElement, type MarkdownIconName } from '../../ui/iconMarkup'
import { markdownStrings } from '../../ui/strings'
import { modulesStrings } from '../../ui/strings/modules'

export interface MarkdownReadAction {
  readonly label: string
  readonly icon: MarkdownIconName
  /** Runs on a click; a press never moves the selection, so a reader and a writer get the same. */
  run(button: HTMLButtonElement): void
}

/**
 * What everyone may do with a block (J1.3, contract 3.6): zoom, copy, open. The buttons are the same in a reader's and a writer's
 * page, so the geometry does not depend on the right; `host` must be positioned and has room at its end.
 */
export function addMarkdownReadActions(host: HTMLElement, signal: AbortSignal, actions: readonly MarkdownReadAction[]): HTMLElement {
  const doc = host.ownerDocument
  const bar = doc.createElement('div'); bar.className = 'md-read-actions'; bar.setAttribute('role', 'group'); bar.setAttribute('aria-label', modulesStrings.readActions)
  for (const action of actions) {
    const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-read-action'
    button.setAttribute('aria-label', action.label); button.title = action.label; button.append(createIconElement(doc, action.icon, 14))
    button.addEventListener('mousedown', event => event.preventDefault())
    button.addEventListener('click', event => { event.preventDefault(); if (!signal.aborted) action.run(button) })
    bar.append(button)
  }
  host.append(bar)
  signal.addEventListener('abort', () => bar.remove(), { once: true })
  return bar
}

/**
 * Writes text to the clipboard. The asynchronous API is missing on a page served over plain HTTP (an intranet) and refused without the
 * permission; the old selection copy still works from a press there.
 */
export async function writeMarkdownClipboard(doc: Document, text: string): Promise<boolean> {
  try {
    const clipboard = doc.defaultView?.navigator.clipboard
    if (clipboard) { await clipboard.writeText(text); return true }
  } catch { /* falls through to the selection copy */ }
  const field = doc.createElement('textarea'); field.value = text; field.setAttribute('readonly', ''); field.setAttribute('aria-hidden', 'true')
  field.style.cssText = 'position:fixed;top:0;left:0;opacity:0;pointer-events:none'
  const keep = doc.activeElement instanceof HTMLElement ? doc.activeElement : undefined
  doc.body.append(field); field.select()
  try { return doc.execCommand('copy') } catch { return false } finally { field.remove(); keep?.focus({ preventScroll: true }) }
}

/** Copies text for a read action's button and says how it went on the button itself (its label and a check mark for a moment). */
export function copyMarkdownText(button: HTMLButtonElement, text: string): void {
  const doc = button.ownerDocument
  const label = button.getAttribute('aria-label') ?? ''
  const icon = button.firstElementChild
  const say = (message: string, done: boolean) => {
    if (!button.isConnected) return
    button.setAttribute('aria-label', message); button.title = message
    if (done) button.replaceChildren(createIconElement(doc, 'check', 14))
    button.dataset.state = done ? 'copied' : 'failed'
    doc.defaultView?.setTimeout(() => {
      if (!button.isConnected) return
      button.setAttribute('aria-label', label); button.title = label; delete button.dataset.state
      if (done && icon) button.replaceChildren(icon)
    }, 1600)
  }
  void writeMarkdownClipboard(doc, text).then(done => say(done ? modulesStrings.copied : modulesStrings.copyFailed, done))
}

export interface MarkdownZoomOptions {
  /** The control that opened it; focus returns to it. */
  readonly opener: HTMLElement
  /** The editor the block belongs to, if any: the dialog then shares its overlay ownership (a modal dialog in fullscreen, focus isolation). */
  readonly editor?: Editor
  readonly label: string
  readonly signal?: AbortSignal
  /** Fills the dialog; may return what to release when it closes. */
  render(body: HTMLElement): void | (() => void)
}

/** A block enlarged in a dialog (the image's zoom): Esc, the button and a click outside close it. Returns the function that closes it. */
export function zoomMarkdownBlock(options: MarkdownZoomOptions): () => void {
  const { opener, editor } = options
  const doc = opener.ownerDocument
  const dialog = doc.createElement('dialog'); dialog.className = 'md-image-zoom md-block-zoom'; dialog.setAttribute('aria-label', options.label)
  const close = doc.createElement('button'); close.type = 'button'; close.className = 'md-image-zoom-close'; close.textContent = markdownStrings.close
  const body = doc.createElement('div'); body.className = 'md-block-zoom-body'
  dialog.append(close, body); copyMarkdownOverlayTheme(opener, dialog); doc.body.append(dialog)
  const unregister = editor && !editor.isDestroyed ? registerMarkdownOverlay(editor, dialog, { modal: true }) : () => {}
  const release = options.render(body)
  let done = false
  const finish = () => {
    if (done) return
    done = true; unregister(); options.signal?.removeEventListener('abort', finish)
    try { if (typeof release === 'function') release() } finally { dialog.remove(); if (opener.isConnected) opener.focus({ preventScroll: true }); if (editor && !editor.isDestroyed && opener.ownerDocument.activeElement !== opener) editor.view.focus() }
  }
  close.onclick = finish
  dialog.addEventListener('cancel', event => { event.preventDefault(); finish() })
  dialog.addEventListener('click', event => { if (event.target === dialog) finish() })
  options.signal?.addEventListener('abort', finish, { once: true })
  if (typeof dialog.showModal === 'function') dialog.showModal(); else dialog.setAttribute('open', '')
  close.focus({ preventScroll: true })
  return finish
}
