import type { Editor } from '@tiptap/core'
import { autoUpdate, computePosition, flip, offset, shift, size } from '@floating-ui/dom'
import { registerMarkdownOverlayForElement } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { isMarkdownComposition, trackMarkdownComposition } from './composition'
import { registerMarkdownPopover } from './popoverInteraction'

export interface MarkdownReaderPopoverOptions {
  /** The control that opened the panel; the panel is placed at it and focus returns to it. */
  readonly anchor: HTMLElement
  /** The editor the content belongs to, if any. The panel then shares the editor's one active popover. */
  readonly editor?: Editor
  /** Root of the rendering (a static renderer's container, or the editor's content element). Default: the editor view. */
  readonly root?: HTMLElement
  /** Accessible name of the panel; there is no visible title or close button. */
  readonly title: string
  readonly signal?: AbortSignal
  readonly className?: string
  readonly render: (body: HTMLElement, close: () => void) => void | (() => void)
}

/**
 * An anchored panel for reader actions: it works without an editable editor, a NodeSelection or an editor at all
 * (static rendering), so a reader can act on content, for example open details or request access. Same surface,
 * theme, overlay ownership and dismissal (Escape, pointer outside, anchor removal, abort) as the editor's panels.
 */
export function openMarkdownReaderPopover(options: MarkdownReaderPopoverOptions): () => void {
  const { anchor, editor } = options
  if (options.signal?.aborted || editor?.isDestroyed) return () => {}
  const doc = anchor.ownerDocument
  const root = options.root ?? editor?.view.dom ?? anchor
  const popup = doc.createElement('div')
  popup.className = `md-context-popover${options.className ? ` ${options.className}` : ''}`
  popup.setAttribute('role', 'dialog'); popup.setAttribute('aria-label', options.title); popup.tabIndex = -1
  const body = doc.createElement('div'); body.className = 'md-context-popover-body'; popup.append(body)
  const composition = trackMarkdownComposition(popup)
  copyMarkdownOverlayTheme(anchor, popup); doc.body.append(popup)
  const unregister = registerMarkdownOverlayForElement(root, popup)
  anchor.setAttribute('aria-haspopup', 'dialog'); anchor.setAttribute('aria-expanded', 'true')
  let closed = false, revision = 0
  let stopPosition: (() => void) | undefined
  let cleanup: void | (() => void)
  let releaseActive: (() => void) | undefined
  const close = (focus = true) => {
    if (closed) return
    closed = true; revision++; stopPosition?.(); observer?.disconnect(); unregister(); popup.remove()
    doc.removeEventListener('pointerdown', outside, true); doc.removeEventListener('keydown', keyboard, true)
    options.signal?.removeEventListener('abort', abort); editor?.off('destroy', abort)
    releaseActive?.(); composition.destroy()
    anchor.setAttribute('aria-expanded', 'false')
    try { cleanup?.() } finally { if (focus && anchor.isConnected) anchor.focus({ preventScroll: true }) }
  }
  const abort = () => close(false)
  const outside = (event: Event) => { if (!popup.contains(event.target as Node) && !anchor.contains(event.target as Node)) close(false) }
  const keyboard = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || composition.active() || isMarkdownComposition(event)) return
    event.preventDefault(); event.stopPropagation(); close()
  }
  const observer = doc.defaultView?.MutationObserver ? new doc.defaultView.MutationObserver(() => { if (!anchor.isConnected) close(false) }) : undefined
  const updatePosition = () => {
    const current = ++revision
    void computePosition(anchor, popup, { placement: 'bottom-start', strategy: 'fixed', middleware: [offset(6), flip(), shift({ padding: 12 }),
      size({ padding: 12, apply({ availableHeight }) { if (!closed) popup.style.maxHeight = `${Math.max(80, availableHeight)}px` } })] })
      .then(({ x, y }) => { if (!closed && revision === current) { Object.assign(popup.style, { left: `${x}px`, top: `${y}px` }); popup.dataset.positioned = 'true' } })
      .catch(() => {})
  }
  doc.addEventListener('pointerdown', outside, true); doc.addEventListener('keydown', keyboard, true)
  options.signal?.addEventListener('abort', abort, { once: true }); editor?.on('destroy', abort)
  // One active panel per editor, or per static rendering.
  releaseActive = registerMarkdownPopover(editor ?? root, { anchor, key: options.title, close: abort })
  observer?.observe(doc.body, { childList: true, subtree: true })
  try {
    cleanup = options.render(body, () => close())
  } catch (error) { close(false); throw error }
  if (closed) return () => {}
  stopPosition = autoUpdate(anchor, popup, updatePosition, { elementResize: typeof doc.defaultView?.ResizeObserver === 'function', layoutShift: typeof doc.defaultView?.IntersectionObserver === 'function' })
  if (!popup.contains(doc.activeElement)) (body.querySelector<HTMLElement>('input,textarea,select,a[href],button') ?? popup).focus({ preventScroll: true })
  return () => close()
}
