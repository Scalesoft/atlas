import { autoUpdate, computePosition, offset, shift, type Middleware, type VirtualElement } from '@floating-ui/dom'
import type { EditorView } from '@tiptap/pm/view'
import { copyMarkdownOverlayTheme, createIconElement, registerMarkdownOverlayForElement, type MarkdownIconName } from '../core'
import { assistMotion, assistTokens, finished, markerIn, markerOut, ripple, type AssistMotion } from './motion'

const SVG = 'http://www.w3.org/2000/svg'

/** Copies the editor theme and the assist tokens onto a body-level overlay. */
export function copyAssistTheme(source: HTMLElement, overlay: HTMLElement): void {
  copyMarkdownOverlayTheme(source, overlay)
  const style = source.ownerDocument.defaultView?.getComputedStyle(source)
  for (const token of assistTokens) {
    const value = style?.getPropertyValue(token).trim()
    if (value) overlay.style.setProperty(token, value)
    else overlay.style.removeProperty(token)
  }
}

/**
 * The body-level layer of one editor: markers and the status note when no status panel is mounted.
 * It is outside `contenteditable`, so nothing here is document content, a transaction or a Yjs update.
 */
export class AssistLayer {
  readonly element: HTMLElement
  private readonly release: () => void
  constructor(readonly view: EditorView) {
    const doc = view.dom.ownerDocument
    this.element = doc.createElement('div')
    this.element.className = 'md-assist-layer'
    copyAssistTheme(view.dom, this.element)
    doc.body.append(this.element)
    this.release = registerMarkdownOverlayForElement(view.dom, this.element, { consumesEscape: false })
  }
  refreshTheme(): void { copyAssistTheme(this.view.dom, this.element) }
  destroy(): void { this.release(); this.element.remove() }
}

export interface MarkerOptions {
  readonly view: EditorView
  readonly layer: AssistLayer
  readonly icon: MarkdownIconName
  readonly label: string
  readonly tone: 'default' | 'warning'
  /** Current DOM of the block, or null while it is not rendered. */
  readonly block: () => Element | null
  /** Current position directly before the block (`data-md-assist-pos`). */
  readonly pos: () => number
  readonly reducedMotion?: () => boolean
  readonly onShown?: () => void
  readonly onPress: (wasOpen: boolean) => void
  readonly isOpen: () => boolean
}

/** Default gap between the marker and its block (`--md-assist-marker-offset`), clear of the block handle. */
const MARKER_OFFSET = 32
/** Distance the marker keeps from the editor's own edge. */
const EDGE = 4

/** `--md-assist-marker-offset` as the editor resolves it (a length in px), else the default. */
function markerOffset(source: Element): number {
  const value = Number.parseFloat(source.ownerDocument.defaultView?.getComputedStyle(source).getPropertyValue('--md-assist-marker-offset') ?? '')
  return Number.isFinite(value) ? value : MARKER_OFFSET
}

/**
 * Keeps the marker within the editor's own box (`.md-editor`, else the element the view is attached to), never over a host sidebar.
 * A left gutter too narrow for it beside the block's grip (a page's 40 px) puts it right of the block instead: the grip stays reachable.
 */
function insideEditor(view: EditorView): Middleware {
  return { name: 'mdAssistInsideEditor', fn: ({ x, rects }) => {
    const box = (view.dom.closest('.md-editor') ?? view.dom.parentElement ?? view.dom).getBoundingClientRect()
    if (!box.width) return {}
    const right = box.right - EDGE - rects.floating.width
    return { x: x < box.left + EDGE ? Math.min(rects.reference.x + rects.reference.width + EDGE, right) : Math.min(x, right) }
  } }
}

/**
 * One marker button in the left gutter, `--md-assist-marker-offset` (32 px) left of the block (outside the block
 * handle), right of the block where that gutter has no room for it, and never outside the editor. `data-md-assist-pos` names its textblock (see `assistMarkersFor`).
 */
export class AssistMarker {
  readonly element: HTMLButtonElement
  private readonly stop: () => void
  private readonly reference: VirtualElement
  private removed = false
  readonly tone: 'default' | 'warning'
  constructor(private readonly options: MarkerOptions) {
    const doc = options.view.dom.ownerDocument
    this.tone = options.tone; this.currentIcon = options.icon
    const button = this.element = doc.createElement('button')
    button.type = 'button'; button.tabIndex = -1
    button.className = 'md-assist-marker'
    button.dataset.tone = options.tone
    button.setAttribute('data-md-assist-marker', '')
    button.setAttribute('aria-label', options.label)
    button.setAttribute('aria-haspopup', 'listbox')
    button.setAttribute('aria-expanded', 'false')
    // The dashed ring is a geometric frame (like the drag selection frame), not an icon.
    const ring = doc.createElementNS(SVG, 'svg')
    ring.setAttribute('class', 'md-assist-ring'); ring.setAttribute('viewBox', '0 0 26 26'); ring.setAttribute('aria-hidden', 'true')
    const circle = doc.createElementNS(SVG, 'circle')
    circle.setAttribute('cx', '13'); circle.setAttribute('cy', '13'); circle.setAttribute('r', '12')
    ring.append(circle)
    const icon = doc.createElement('span'); icon.className = 'md-assist-marker-icon'
    icon.append(createIconElement(doc, options.icon, 13))
    button.append(ring, icon)
    let rippleElement: HTMLElement | undefined
    if (options.tone === 'warning') { rippleElement = doc.createElement('span'); rippleElement.className = 'md-assist-ripple'; button.append(rippleElement) }
    let wasOpen = false
    // Keep the caret in the document: the marker never takes focus from typing.
    button.addEventListener('pointerdown', () => { wasOpen = options.isOpen() })
    button.addEventListener('mousedown', event => event.preventDefault())
    button.addEventListener('click', () => { options.onPress(wasOpen); wasOpen = false })
    options.layer.element.append(button)
    const view = doc.defaultView
    const reference: VirtualElement = this.reference = {
      getBoundingClientRect: () => options.block()?.getBoundingClientRect() ?? new (view?.DOMRect ?? DOMRect)(),
      contextElement: options.view.dom,
    }
    this.stop = autoUpdate(reference, button, () => this.position(), {
      elementResize: typeof view?.ResizeObserver === 'function', layoutShift: typeof view?.IntersectionObserver === 'function',
    })
    const motion = this.motion()
    markerIn(ring, icon, motion)
    if (rippleElement) ripple(rippleElement, motion)
  }
  motion(): AssistMotion { return assistMotion(this.options.view.dom, this.options.reducedMotion) }
  /** Present, not leaving and rendered next to a block. */
  get visible(): boolean { return !this.removed && !this.element.hidden }
  setExpanded(expanded: boolean): void { this.element.setAttribute('aria-expanded', String(expanded)) }
  /** A dismissed lead suggestion hands the marker to the next one without a new entrance. */
  setContent(icon: MarkdownIconName, label: string): void {
    this.element.setAttribute('aria-label', label)
    if (icon === this.currentIcon) return
    this.currentIcon = icon
    this.element.querySelector('.md-assist-marker-icon')?.replaceChildren(createIconElement(this.element.ownerDocument, icon, 13))
  }
  private currentIcon: MarkdownIconName
  private shown = false
  private anchor: Element | null | undefined
  private anchorPos: number | undefined
  private frame = 0
  /**
   * After a transaction: measures again, in the next frame, only when the block moved in the document or was rendered anew.
   * A keystroke reads no layout (J12.5); a block that grows, scrolls or shifts is re-measured by `autoUpdate`.
   */
  refresh(): void {
    if (this.removed) return
    const block = this.options.block(), pos = this.options.pos()
    if (block === this.anchor && pos === this.anchorPos) return
    this.anchor = block; this.anchorPos = pos
    this.element.setAttribute('data-md-assist-pos', String(pos))
    const view = this.options.view.dom.ownerDocument.defaultView
    if (!this.frame && view) this.frame = view.requestAnimationFrame(() => { this.frame = 0; this.position() })
  }
  /** Re-measures (`autoUpdate`: scroll, resize, layout shift; `refresh`: the block moved). */
  position(): void {
    if (this.removed) return
    const { view } = this.options
    this.element.hidden = !this.options.block()
    if (!this.element.hidden && !this.shown) { this.shown = true; this.options.onShown?.() }
    this.element.setAttribute('data-md-assist-pos', String(this.options.pos()))
    void computePosition(this.reference, this.element, { strategy: 'fixed', placement: 'left-start',
      middleware: [offset({ mainAxis: markerOffset(view.dom), crossAxis: -1 }), shift({ padding: EDGE }), insideEditor(view)] })
      .then(({ x, y }) => { if (!this.removed) Object.assign(this.element.style, { left: `${Math.round(x)}px`, top: `${Math.round(y)}px` }) })
      .catch(() => undefined)
  }
  /** Scale .6 + fade (160 ms), then gone. */
  remove(animated = true): void {
    if (this.removed) return
    this.removed = true
    this.stop()
    if (this.frame) this.options.view.dom.ownerDocument.defaultView?.cancelAnimationFrame(this.frame)
    this.element.setAttribute('data-leaving', '')
    this.element.inert = true
    if (!animated) { this.element.remove(); return }
    void finished(markerOut(this.element, this.motion())).then(() => this.element.remove())
  }
}
