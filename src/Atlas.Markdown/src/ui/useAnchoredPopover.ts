import { watchEffect } from 'vue'
import { autoUpdate, computePosition, flip, offset, shift, type ReferenceElement } from '@floating-ui/dom'
import type { Editor } from '@tiptap/core'
import { bindMarkdownMenu, registerMarkdownPopover } from '../features/modules/popoverInteraction'
import { isMarkdownComposition, trackMarkdownComposition } from '../features/modules/composition'

/** Keep a mounted menu anchored through scrolling, resizing and toolbar motion. */
export function useAnchoredPopover(reference: () => ReferenceElement | null, popup: () => HTMLElement | null, onOpen?: () => void,
  owner?: { editor: Editor; close: () => void }, placement: 'bottom-start' | 'right-start' = 'bottom-start') {
  watchEffect(onCleanup => {
    const element = popup()
    const anchor = reference()
    if (!element || !anchor) return
    const trigger = 'contextElement' in anchor ? anchor.contextElement : anchor as Element
    const release = owner && trigger ? registerMarkdownPopover(owner.editor, { anchor: trigger, key: element.getAttribute('aria-label') ?? '', close: owner.close }) : undefined
    const navigation = element.getAttribute('role') === 'menu' ? bindMarkdownMenu(element) : undefined
    const composition = trackMarkdownComposition(element)
    const tab = (event: KeyboardEvent) => {
      if (composition.active() || isMarkdownComposition(event, owner?.editor)) return
      if (event.key === 'Tab' && navigation && owner) {
        owner.close()
        if (trigger instanceof element.ownerDocument.defaultView!.HTMLElement) trigger.focus({ preventScroll: true })
      }
    }
    element.addEventListener('keydown', tab)
    let active = true
    let positioned = false
    // Absolute positioning also works inside Safari's backdrop-filter containing blocks. No `visibility: hidden` until placed: the
    // position comes in a microtask, before any paint, and a hidden→visible step transitions the items' visibility (their shared
    // `transition`), so the first focus on an item or the link field found it hidden and stayed on the trigger or the text.
    element.style.position = 'absolute'
    const stop = autoUpdate(anchor, element, () => {
      void computePosition(anchor, element, {
        strategy: 'absolute', placement,
        middleware: [offset(6), flip({ boundary: [], padding: 8 }), shift({ boundary: [], padding: 8 })],
      }).then(({ x, y }) => {
        if (!active) return
        element.style.left = `${x}px`
        element.style.top = `${y}px`
        element.dataset.positioned = 'true'
        if (!positioned) { positioned = true; navigation?.focus(); onOpen?.() }
      })
    }, { animationFrame: true })
    onCleanup(() => {
      active = false
      delete element.dataset.positioned
      stop()
      release?.(); navigation?.destroy(); composition.destroy(); element.removeEventListener('keydown', tab)
    })
  }, { flush: 'post' })
}
