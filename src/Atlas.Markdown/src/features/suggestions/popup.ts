import { computePosition, autoUpdate, flip, shift, offset, size, type Placement, type VirtualElement } from '@floating-ui/dom'
import type { Editor } from '@tiptap/core'
import { registerMarkdownOverlayForElement } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { registerMarkdownPopover } from '../modules/popoverInteraction'

/**
 * Minimal Floating UI–backed popup for Tiptap suggestion renderers
 * (slash commands, contact mention).
 *
 * Lifecycle mirrors what Tiptap's `render()` factory needs: create the popup
 * around a moving caret rect, update it as the user types, and tear it down
 * cleanly when the suggestion ends.
 */
export interface SuggestionPopup {
  /** Replace the caret reference. A getter keeps following layout changes between updates. */
  setReferenceRect(rect: DOMRect | (() => DOMRect)): void
  destroy(): void
}

export interface SuggestionPopupOptions {
  /** Supplying an editor joins its exclusive panel ownership registry. */
  editor?: Editor
  /** Close the owning Suggestion session, not just the popup DOM. */
  onDismiss?: () => void
  /** Live caret rect — re-evaluated on every Floating UI tick. */
  getReferenceRect(): DOMRect
  /** Pre-mounted DOM with the suggestion list. The popup wraps it. */
  content: HTMLElement
  /** The editor or anchor whose scrolling ancestors should be observed. */
  contextElement?: Element
  /** Floating UI placement, default `bottom-start` (Tiptap caret popups). */
  placement?: Placement
}

export function createSuggestionPopup(opts: SuggestionPopupOptions): SuggestionPopup {
  const placement = opts.placement ?? 'bottom-start'
  let getReferenceRect = opts.getReferenceRect
  let disposed = false
  let updateVersion = 0

  const reference: VirtualElement = {
    getBoundingClientRect: () => getReferenceRect(),
    contextElement: opts.contextElement,
  }

  const ownerDocument = opts.contextElement?.ownerDocument ?? opts.content.ownerDocument
  const wrap = ownerDocument.createElement('div')
  wrap.className = 'md-suggestion-popup'
  // An anchor inside an open native dialog (a list row in Atlas's Uspořádat): the popup joins its top layer, fixed, so the dialog's
  // inert outside and its own scrolling neither hide nor clip it.
  const layer = opts.contextElement?.closest('dialog[open]')
  const strategy = layer ? 'fixed' : 'absolute'
  wrap.style.position = strategy
  wrap.style.top = '0'
  wrap.style.left = '0'
  wrap.style.zIndex = '10000'
  // The first Floating UI calculation is asynchronous. Keep the portal out of
  // hit testing, focus navigation and paint until it has a real caret position.
  wrap.style.visibility = 'hidden'
  wrap.style.pointerEvents = 'none'
  wrap.inert = true
  wrap.appendChild(opts.content)
  const releaseOverlay = registerMarkdownOverlayForElement(opts.contextElement, wrap)
  ;(layer ?? ownerDocument.body).appendChild(wrap)
  const source = opts.contextElement as HTMLElement | undefined
  let themed = !source
  const releasePopover = opts.editor ? registerMarkdownPopover(opts.editor, {
    anchor: opts.contextElement ?? opts.editor.view.dom, key: 'suggestion', close: () => { opts.onDismiss?.(); destroy() },
  }) : () => undefined
  const outside = (event: Event) => {
    if (!wrap.contains(event.target as Node)) { opts.onDismiss?.(); destroy() }
  }
  ownerDocument.addEventListener('pointerdown', outside)

  const view = ownerDocument.defaultView ?? globalThis.window
  // Every layout read (the theme's computed style, the caret, Floating UI's geometry) waits for the next frame and runs once there: the
  // keystroke that opens or filters the menu reads no layout (J12.5), and the rows of a query arrive before the menu is measured.
  let positionFrame = 0
  function update() {
    if (!disposed && !positionFrame) positionFrame = view.requestAnimationFrame(() => { positionFrame = 0; position() })
  }
  function position() {
    if (disposed) return
    if (!themed) { themed = true; copyMarkdownOverlayTheme(source!, wrap) }
    stopAutoUpdate ??= autoUpdate(reference, wrap, update)
    const version = ++updateVersion
    void computePosition(reference, wrap, {
      placement, strategy,
      middleware: [offset(8), flip(), shift({ padding: 8 }), size({ padding: 8, apply({ availableHeight }) {
        if (!disposed) opts.content.style.maxHeight = `${Math.max(0, Math.min(320, availableHeight))}px`
      } })],
    }).then(({ x, y }) => {
      if (disposed || version !== updateVersion) return
      wrap.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`
      if (wrap.inert) {
        wrap.inert = false
        wrap.style.pointerEvents = ''
        wrap.style.visibility = 'visible'
        wrap.dataset.positioned = 'true'
      }
    }).catch(() => { if (!disposed) { opts.onDismiss?.(); destroy() } })
  }

  let stopAutoUpdate: (() => void) | undefined
  update()
  // Floating UI's animation-frame mode watches contextElement geometry. During
  // inner scrolling that element can stay fixed while the virtual caret moves,
  // so compare the virtual rect directly in every browser.
  let previousRect: ReturnType<VirtualElement['getBoundingClientRect']> | undefined
  let frame = 0
  const followCaret = () => {
    if (disposed) return
    const nextRect = reference.getBoundingClientRect()
    if (previousRect && (nextRect.x !== previousRect.x || nextRect.y !== previousRect.y
      || nextRect.width !== previousRect.width || nextRect.height !== previousRect.height)) update()
    previousRect = nextRect
    frame = view.requestAnimationFrame(followCaret)
  }
  frame = view.requestAnimationFrame(followCaret)

  function destroy() {
    if (disposed) return
    disposed = true
    view.cancelAnimationFrame(frame); view.cancelAnimationFrame(positionFrame)
    stopAutoUpdate?.()
    releaseOverlay()
    releasePopover()
    ownerDocument.removeEventListener('pointerdown', outside)
    wrap.remove()
  }

  return {
    setReferenceRect(next) {
      getReferenceRect = typeof next === 'function' ? next : () => next
      update()
    },
    destroy,
  }
}
