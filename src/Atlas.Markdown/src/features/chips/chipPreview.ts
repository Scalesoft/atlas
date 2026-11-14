import type { Editor } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
/**
 * The hover and focus preview of a value chip (contract J3.4): a small card under the chip with its name and a few lines, the same for
 * a reader and a writer. It shows after a short delay for a fine pointer and for keyboard focus, never while the chip's own card is open,
 * and never moves the text around it (a fixed overlay, positioned once when it opens). It enters and leaves like every popover
 * (md-popover-enter / md-popover-leave, the motion tokens and their reduced-motion values in motion.css) and goes only when the exit is over. Returns the detach.
 */
export interface MarkdownChipPreviewContent { readonly title: string; readonly lines?: readonly string[] }

const DELAY = 350
/** A card whose exit never reports (a background tab) goes anyway. */
const LEAVE_MAX_MS = 400

/** Lets a hidden card play its exit (`[data-leaving]`, chip.css) and removes it afterwards; without an animation it goes at once. */
function leave(card: HTMLElement, gone: () => void) {
  card.dataset.leaving = 'true'; card.setAttribute('aria-hidden', 'true')
  const done = () => { card.remove(); gone() }
  const animations = card.getAnimations?.() ?? []
  if (!animations.length) return done()
  void Promise.allSettled(animations.map(animation => animation.finished)).then(done)
  setTimeout(done, LEAVE_MAX_MS)
}

/** `selection`: the chip's editor and position, so a chip the keyboard selected (the focus stays in the editor) previews too. */
export function bindMarkdownChipPreview(chip: HTMLElement, content: () => MarkdownChipPreviewContent | undefined, selection?: { editor: Editor; getPos: () => number | undefined }): () => void {
  const doc = chip.ownerDocument
  let timer: ReturnType<typeof setTimeout> | undefined, card: HTMLElement | undefined, title: string | null = null
  const leaving = new Set<HTMLElement>()
  const dismiss = (animated: boolean) => {
    if (timer) { clearTimeout(timer); timer = undefined }
    if (!card) return
    if (animated) { const gone = card; leaving.add(gone); leave(gone, () => leaving.delete(gone)) } else card.remove()
    card = undefined
    chip.removeAttribute('aria-describedby')
    if (title !== null) { chip.setAttribute('title', title); title = null }
  }
  const hide = () => dismiss(true)
  const show = () => {
    timer = undefined
    const what = content()
    if (!what || card || chip.getAttribute('aria-expanded') === 'true' || !chip.isConnected) return
    card = doc.createElement('div'); card.className = 'md-chip-preview'; card.setAttribute('role', 'tooltip'); card.id = `md-chip-preview-${Math.random().toString(36).slice(2, 8)}`
    const heading = doc.createElement('strong'); heading.textContent = what.title; card.append(heading)
    for (const line of what.lines ?? []) { const row = doc.createElement('span'); row.textContent = line; card.append(row) }
    const rect = chip.getBoundingClientRect()
    Object.assign(card.style, { position: 'fixed', zIndex: '60', insetInlineStart: `${Math.max(8, Math.min(rect.left, doc.documentElement.clientWidth - 280))}px`, insetBlockStart: `${rect.bottom + 6}px`,
      display: 'grid', gap: '2px', maxWidth: '272px', padding: '8px 10px', borderRadius: '10px', pointerEvents: 'none', fontFamily: doc.defaultView?.getComputedStyle(chip).fontFamily ?? 'inherit', fontSize: '12.5px', lineHeight: '1.35',
      color: 'var(--text, #2f2d29)', background: 'var(--paper, #fff)', border: '1px solid var(--line, #d9d6cf)', boxShadow: 'var(--shadow-menu, 0 8px 24px -12px #00000047)' })
    doc.body.append(card)
    chip.setAttribute('aria-describedby', card.id)
    title = chip.getAttribute('title'); if (title !== null) chip.removeAttribute('title')
  }
  const arm = () => { if (!timer && !card) timer = setTimeout(show, DELAY) }
  const enter = (event: PointerEvent) => { if (event.pointerType === 'mouse' || event.pointerType === 'pen') arm() }
  const focus = () => { if (chip.matches(':focus-visible')) arm() }
  const keydown = (event: KeyboardEvent) => { if (event.key === 'Escape') hide() }
  // Escape anywhere dismisses a shown preview; a chip selected by the keyboard arms it, any other selection hides it.
  const escape = (event: KeyboardEvent) => { if (event.key === 'Escape' && card) hide() }
  const selected = () => {
    const position = selection!.getPos(), current = selection!.editor.state.selection
    if (position !== undefined && current instanceof NodeSelection && current.from === position && chip.ownerDocument.activeElement !== chip) arm(); else hide()
  }
  doc.addEventListener('keydown', escape, true); selection?.editor.on('selectionUpdate', selected)
  chip.addEventListener('pointerenter', enter); chip.addEventListener('pointerleave', hide); chip.addEventListener('pointerdown', hide)
  chip.addEventListener('focus', focus); chip.addEventListener('blur', hide); chip.addEventListener('keydown', keydown)
  return () => {
    dismiss(false); leaving.forEach(gone => gone.remove()); leaving.clear(); doc.removeEventListener('keydown', escape, true); selection?.editor.off('selectionUpdate', selected)
    chip.removeEventListener('pointerenter', enter); chip.removeEventListener('pointerleave', hide); chip.removeEventListener('pointerdown', hide)
    chip.removeEventListener('focus', focus); chip.removeEventListener('blur', hide); chip.removeEventListener('keydown', keydown)
  }
}
