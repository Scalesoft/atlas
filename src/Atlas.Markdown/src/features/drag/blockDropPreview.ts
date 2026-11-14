import type { EditorView } from '@tiptap/pm/view'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import { registerMarkdownOverlayForElement } from '../../core/overlay'
import { blockIndexAt, measureBlocks, type BlockDropTarget, type BlockRect } from './blockDrop'
import { dragMotion } from './motion'

/** Half the gap the drop target opens: the blocks above move up by it, the blocks below down. */
const GAP = 20

/** Visual-only movement: never mutate ProseMirror's managed DOM or document. */
export function createBlockDropPreview(view: EditorView) {
  const win = view.dom.ownerDocument.defaultView!
  const animations = new Map<HTMLElement, { animation: Animation; offset: number }>()
  let guide: HTMLDivElement | null = null
  let position: number | null = null
  let guideWidth = -1, band = ''
  let releaseGuide: (() => void) | undefined
  let measured: readonly BlockRect[] = []
  let look: { accent: string; motion: ReturnType<typeof dragMotion> } | undefined
  const landing = new Set<Animation>()
  // Only blocks on screen travel; the others take their place at once (they cannot be seen moving).
  function onScreen(block: BlockRect | undefined, editorTop: number) {
    return !!block && editorTop + block.bottom + GAP > 0 && editorTop + block.top - GAP < win.innerHeight
  }
  /** FLIP from where each block is drawn now: every read before any write, so one style flush per change. */
  function animate(changes: ReadonlyArray<{ element: HTMLElement; offset: number; duration: number }>, easing: string) {
    const current = changes.map(({ element }) => animations.has(element) ? win.getComputedStyle(element).transform : 'none')
    changes.forEach(({ element, offset, duration }, index) => {
      animations.get(element)?.animation.cancel()
      animations.delete(element)
      if (typeof element.animate !== 'function' || !element.isConnected) return
      const from = !current[index] || current[index] === 'none' ? 'translateY(0px)' : current[index]
      const animation = element.animate([{ transform: from }, { transform: `translateY(${offset}px)` }],
        { duration, easing, fill: offset === 0 ? 'none' : 'forwards' })
      animations.set(element, { animation, offset })
      if (offset === 0) animation.onfinish = () => { if (animations.get(element)?.animation === animation) animations.delete(element) }
    })
  }
  function clear(immediate = false) {
    const moving = [...animations].filter(([, { offset }]) => immediate || offset !== 0)
    const motion = moving.length && !immediate ? dragMotion(view.dom) : undefined
    if (!motion || motion.reduced) {
      for (const [element, { animation }] of moving) { animation.cancel(); animations.delete(element) }
    } else {
      const editorTop = view.dom.getBoundingClientRect().top
      const byElement = new Map(measured.map(block => [block.element, block]))
      const returning: Array<{ element: HTMLElement; offset: number; duration: number }> = []
      for (const [element, { animation }] of moving) {
        if (onScreen(byElement.get(element), editorTop)) returning.push({ element, offset: 0, duration: motion.returnDuration })
        else { animation.cancel(); animations.delete(element) }
      }
      animate(returning, motion.easing)
    }
    guide?.remove()
    releaseGuide?.(); releaseGuide = undefined
    guide = null
    position = null
    guideWidth = -1; band = ''; measured = []
  }
  function update(target: BlockDropTarget | null, blocks: readonly BlockRect[] = measureBlocks(view)) {
    if (!target) { clear(); return }
    const fresh = blocks !== measured
    measured = blocks
    // Elements ProseMirror redrew keep no gap of their own; their replacements take it below without travelling.
    if (fresh) for (const [element, { animation }] of animations) if (!element.isConnected) { animation.cancel(); animations.delete(element) }
    // Reads first. Only the blocks within a screen of the viewport move: a long page costs what one screen costs,
    // and scrolling sets the next blocks in place before they come into view.
    const editorTop = view.dom.getBoundingClientRect().top
    const first = Math.max(0, blockIndexAt(blocks, -win.innerHeight - editorTop) - 1)
    const last = Math.min(blocks.length, blockIndexAt(blocks, 2 * win.innerHeight - editorTop) + 1)
    if (!guide) {
      // Read for `settle` here, while the page is laid out: after the drop it would force a style pass over the redrawn page.
      look = { accent: win.getComputedStyle(view.dom).getPropertyValue('--accent').trim(), motion: dragMotion(view.dom) }
      guide = view.dom.ownerDocument.createElement('div')
      guide.className = 'md-block-drop-guide'
      guide.setAttribute('aria-hidden', 'true')
      const label = view.dom.ownerDocument.createElement('span')
      guide.append(label)
      copyMarkdownOverlayTheme(view.dom, guide)
      view.dom.ownerDocument.body.append(guide)
      releaseGuide = registerMarkdownOverlayForElement(view.dom, guide, { consumesEscape: false })
    }
    const label = guide.firstElementChild!
    if (label.textContent !== target.label) label.textContent = target.label
    // `translate` only: following the pointer never invalidates layout.
    if (guideWidth !== target.width) { guideWidth = target.width; guide.style.width = `${target.width}px` }
    guide.style.translate = `${target.left}px ${target.y - GAP}px`
    const moved = position !== target.position
    if (!moved && !fresh && band === `${first}:${last}`) return
    if (moved) guide.dataset.position = String(target.position)
    position = target.position
    band = `${first}:${last}`
    const motion = dragMotion(view.dom)
    const changes: Array<{ element: HTMLElement; offset: number; duration: number }> = []
    for (const block of blocks.slice(first, last)) {
      const offset = block.from < target.position ? -GAP : GAP
      if (animations.get(block.element)?.offset === offset) continue
      // Only a new drop position travels; blocks scrolled or redrawn into place take their offset at once.
      changes.push({ element: block.element, offset, duration: moved && !motion.reduced && onScreen(block, editorTop) ? motion.duration : 0 })
    }
    animate(changes, motion.easing)
  }
  function settle(from: number, to: number) {
    const { accent, motion } = look ?? { accent: '', motion: dragMotion(view.dom) }
    if (motion.reduced) return
    const tint = accent || '#58a9b9'
    view.state.doc.nodesBetween(from, to, (node, position) => {
      const element = view.nodeDOM(position)
      if (element instanceof win.HTMLElement && typeof element.animate === 'function') {
        const animation = element.animate([
          { backgroundColor: `color-mix(in srgb, ${tint} 10%, transparent)`, transform: 'translateY(3px)' },
          { backgroundColor: 'transparent', transform: 'translateY(0px)' },
        ], { duration: motion.duration, easing: motion.easing })
        landing.add(animation); animation.onfinish = () => landing.delete(animation)
      }
      return false
    })
  }
  return { update, clear, settle, destroy() { clear(true); landing.forEach(animation => animation.cancel()); landing.clear() } }
}
