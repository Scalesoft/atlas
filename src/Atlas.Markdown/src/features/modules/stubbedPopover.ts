import { createMarkdownModuleLoader, whenMarkdownIdle } from './lazy'
import { openMarkdownContextPopover } from './contextToolbar'
import { markdownStrings } from '../../ui/strings'
import { modulesStrings } from '../../ui/strings/modules'

/*
 * Stub, load, swap (plan 3.6-G, editors and menus): a component's editor or the insert menu opens at once as its popover frame with
 * a skeleton of the final size, entering the way every popover does, while the editor's own chunk and data load behind that motion.
 * Loaded already, it opens as itself with no stub. The skeleton's rows fade in only after `--atlas-motion-defer`, so a quick load
 * shows no flicker; the swap waits for the entry animation, keeps the size (no jump) and hands over the keyboard: focus sits on the
 * stub from the first frame and what was typed meanwhile goes into the editor's first text field. A chunk that fails to load ends the
 * loading: the stub says so and offers „Zkusit znovu“ (the same message and button as a block's failed render).
 */

type PopoverOptions = Parameters<typeof openMarkdownContextPopover>[0]
type RenderBody = (container: HTMLElement, close: () => void) => void | (() => void)

/** The longest the swap waits for the entry animation (a popover enters in 160–200 ms). */
const SETTLE_MS = 200

export interface MarkdownStubbedPopover<M> {
  /** Opens the popover (a stub until `load` is done); the same return as `openMarkdownContextPopover`. */
  open(options: Omit<PopoverOptions, 'render'> & { rows?: number; /** Height of a row in px: the editor's own field row, so the swap does not resize. */ rowHeight?: number
    /** Called when the popover closed before the editor arrived (so the caller can settle what waits for it). */
    onCancelled?: () => void
    /** Called once the popover has closed, however it closed, also while it was still the stub. */
    closed?: () => void
    render: (module: M, container: HTMLElement, close: () => void) => ReturnType<RenderBody> }): (() => void) & { /** Settles when the editor stands (or the popover did not open or was closed first). */ readonly ready: Promise<void> }
  /** Starts the download (hover or focus on the component, opening a menu); costs nothing twice. */
  prefetch(): void
  /** Prefetches when the browser idles, within the page's own idle time (never while typing). */
  prefetchWhenIdle(): void
  /** The module once loaded. */
  readonly loaded: M | undefined
}

/** `load` is the editor's own `import()`: one chunk per editor, shared by every opening. */
export function createMarkdownStubbedPopover<M>(load: () => Promise<M>): MarkdownStubbedPopover<M> {
  const once = createMarkdownModuleLoader(load)
  let module: M | undefined
  const fetch = () => once().then(loaded => (module = loaded), () => undefined)
  return {
    get loaded() { return module },
    prefetch() { if (!module) void fetch() },
    prefetchWhenIdle() { if (!module) whenMarkdownIdle(() => { void fetch() }) },
    open({ rows = 3, rowHeight, onCancelled, render, closed, ...options }) {
      let settle!: () => void, entered = false
      const ready = new Promise<void>(resolve => { settle = resolve })
      const close = openMarkdownContextPopover({
        ...options,
        render(container, close) {
          entered = true
          const cleanup = module ? (() => { const done = render(module, container, close); settle(); return done })()
            : stub(container, rows, rowHeight, fetch, loaded => render(loaded, container, close), close, () => { settle(); onCancelled?.() }, settle)
          return () => { if (typeof cleanup === 'function') cleanup(); closed?.() }
        },
      })
      if (!entered) settle()
      return Object.assign(close, { ready })
    },
  }
}

function stub(container: HTMLElement, rows: number, rowHeight: number | undefined, load: () => Promise<unknown>, swap: (loaded: never) => ReturnType<RenderBody>, close: () => void, onCancelled: () => void, swapped: () => void): () => void {
  const doc = container.ownerDocument
  const skeleton = doc.createElement('div')
  skeleton.className = 'md-stub'; skeleton.tabIndex = -1; skeleton.setAttribute('aria-busy', 'true')
  if (rowHeight) skeleton.style.setProperty('--md-stub-row', `${rowHeight}px`)
  const skeletonRows = () => Array.from({ length: rows }, () => doc.createElement('i'))
  skeleton.append(...skeletonRows())
  container.append(skeleton)
  skeleton.focus({ preventScroll: true })
  // The first frame is keyboard-ready: characters typed before the editor exists are kept for its first field, Escape closes.
  let typed = ''
  skeleton.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.stopPropagation(); close(); return }
    if (event.target !== skeleton) return // the retry button keeps its own Enter and Space
    if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) { typed += event.key; event.preventDefault() }
    else if (event.key === 'Backspace') { typed = typed.slice(0, -1); event.preventDefault() }
  })
  let closed = false, didSwap = false, cleanup: (() => void) | void
  const fail = () => {
    skeleton.setAttribute('aria-busy', 'false')
    const message = doc.createElement('p'); message.setAttribute('role', 'alert'); message.textContent = modulesStrings.renderFailed
    const retry = doc.createElement('button'); retry.type = 'button'; retry.dataset.moduleRetry = ''; retry.textContent = markdownStrings.retry
    retry.onclick = () => { skeleton.replaceChildren(...skeletonRows()); skeleton.setAttribute('aria-busy', 'true'); skeleton.focus({ preventScroll: true }); attempt() }
    skeleton.replaceChildren(message, retry)
    if (doc.activeElement === skeleton) retry.focus({ preventScroll: true })
    swapped() // whoever awaits `ready` is not kept waiting
  }
  const attempt = () => void load().then(async loaded => {
    if (closed) return
    if (loaded === undefined) return fail()
    const frame = container.closest('.md-context-popover') ?? container
    const entering = frame.getAnimations?.() ?? []
    await Promise.race([Promise.allSettled(entering.map(animation => animation.finished)), new Promise(resolve => setTimeout(resolve, SETTLE_MS))])
    if (closed) return
    // The stub's size holds while the editor draws, so nothing jumps; it is released once the editor stands.
    const height = skeleton.offsetHeight, had = doc.activeElement === skeleton
    container.style.minHeight = `${height}px`
    skeleton.remove()
    didSwap = true; cleanup = swap(loaded as never); swapped()
    const field = container.querySelector<HTMLInputElement | HTMLTextAreaElement>('input:not([type=hidden]):not([type=checkbox]):not([type=radio]), textarea')
    // A component that placed the focus itself (a menu's checked row) keeps it, as when it opens loaded; typed text still goes to its field.
    if (had && (typed && field || !container.contains(doc.activeElement))) (field ?? container.querySelector<HTMLElement>('button, [tabindex]'))?.focus({ preventScroll: true })
    if (typed && field && !field.readOnly) { field.value += typed; field.dispatchEvent(new Event('input', { bubbles: true })) }
    requestAnimationFrame(() => { container.style.minHeight = '' })
  })
  attempt()
  return () => { closed = true; if (typeof cleanup === 'function') cleanup(); else if (!didSwap) onCancelled() }
}
