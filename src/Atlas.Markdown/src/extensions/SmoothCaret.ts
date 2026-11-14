import { Extension } from '@tiptap/core'
import { Plugin, PluginKey, TextSelection, type EditorState } from '@tiptap/pm/state'
import type { ResolvedPos } from '@tiptap/pm/model'
import type { EditorView } from '@tiptap/pm/view'

/**
 * Whether the drawn caret stands: a collapsed text caret of an editable editor that has the focus of a focused window. A range, a
 * selected node or cell and the gap cursor have no caret (the gap cursor draws its own); an IME composition keeps it after the
 * composed text, as the native caret.
 */
export function smoothCaretShown(state: EditorState, view: { editable: boolean; focused: boolean }): boolean {
  return view.editable && view.focused && state.selection instanceof TextSelection && state.selection.empty
}

/**
 * The side of the character the caret leans on: the letter before it while typing (end of a wrapped line stays on that line), the
 * text after it at a block start, after a chip or a line break, and after a space (a soft wrap there puts it at the next line's start).
 */
export function smoothCaretSide($head: ResolvedPos): -1 | 1 {
  const before = $head.nodeBefore
  return before?.isText && !/\s$/.test(before.text!) ? -1 : 1
}

type CaretRect = { left: number; top: number; bottom: number }
/**
 * Where a soft wrap puts the caret (one position, two places): End leaves it at the end of the upper line and a click on the line the
 * pointer was on, as the browser draws it; anything else keeps the side the text gives.
 */
export type SmoothCaretHint = { kind: 'end' } | { kind: 'pointer'; y: number } | null
export function smoothCaretAtWrap(lean: CaretRect, other: CaretRect, hint: SmoothCaretHint): CaretRect {
  if (lean.top === other.top || !hint) return lean
  const upper = lean.top < other.top ? lean : other, lower = upper === lean ? other : lean
  return hint.kind === 'end' || hint.y < lower.top ? upper : lower
}

/** Half a blink, the system caret's rate (Windows' default 530 ms): a caret's rhythm, not UI motion, so no motion token. */
const CARET_BLINK_MS = 530

/**
 * A caret that glides to where it goes (Word, Office): a thin element beside the document follows the selection with a short
 * transform transition and blinks only when idle; the native caret is transparent under `.md-smooth-caret`. It moves in the frame
 * after the one that draws the typed text, so the key's own frame does no work for it: one `coordsAtPos` on a layout already done,
 * then transform and opacity only. It hides at once and does nothing while the editor has no focus. Opt-in: `MarkdownEditor`'s
 * `smoothCaret`.
 */
export const SmoothCaret = Extension.create({
  name: 'markdownSmoothCaret',
  addProseMirrorPlugins() {
    let schedule = () => {}, hint: SmoothCaretHint = null
    return [new Plugin({
      key: new PluginKey('markdownSmoothCaret'),
      props: {
        attributes: { class: 'md-smooth-caret' },
        handleKeyDown: (_view, event) => { hint = event.key === 'End' ? { kind: 'end' } : null; return false },
        handleDOMEvents: {
          focus: () => { schedule(); return false }, blur: () => { schedule(); return false },
          mousedown: (_view, event) => { hint = { kind: 'pointer', y: event.clientY }; return false },
        },
      },
      view(view: EditorView) {
        const doc = view.dom.ownerDocument, win = doc.defaultView!
        const caret = doc.createElement('div')
        caret.className = 'md-smooth-caret-mark'
        caret.setAttribute('aria-hidden', 'true')
        let frame = 0, blink = 0, shown = false, last = { x: 0, y: 0, h: 0 }
        // A timer, not a CSS animation: restarting an animation at every key kept the main thread drawing frames while typing.
        const lit = () => { win.clearInterval(blink); caret.classList.remove('is-off'); blink = win.setInterval(() => caret.classList.toggle('is-off'), CARET_BLINK_MS) }
        const hide = () => { if (shown) { shown = false; win.clearInterval(blink); caret.classList.remove('is-shown', 'is-off') } }
        const standing = () => smoothCaretShown(view.state, { editable: view.editable, focused: view.hasFocus() && doc.hasFocus() })
        const draw = () => {
          frame = 0
          if (!standing()) return hide()
          if (caret.parentElement !== view.dom.parentElement) view.dom.parentElement?.appendChild(caret)
          const { $head, head } = view.state.selection
          let at: CaretRect
          try {
            const side = smoothCaretSide($head)
            at = view.coordsAtPos(head, side)
            // A space before text after it may be a soft wrap: the other side tells which two places the position has.
            if (hint && side === 1 && $head.nodeBefore?.isText && $head.nodeAfter) at = smoothCaretAtWrap(at, view.coordsAtPos(head, -1), hint)
          } catch { return hide() }
          const host = caret.offsetParent as HTMLElement | null
          if (!host) return hide()
          // Relative to the caret's containing block, which scrolls and scales (a zoomed board) with the text.
          const box = host.getBoundingClientRect(), sx = box.width / host.offsetWidth || 1, sy = box.height / host.offsetHeight || 1
          const next = { x: (at.left - box.left) / sx - host.clientLeft + host.scrollLeft, y: (at.top - box.top) / sy - host.clientTop + host.scrollTop, h: (at.bottom - at.top) / sy }
          // A line change or a longer sideways move glides; appearing, a jump of more than a couple of lines, or a few characters on the same
          // line (typing) stands there at once, so the mark never trails the text.
          const lineH = Math.max(next.h, last.h), dy = Math.abs(next.y - last.y)
          const jump = !shown || dy > 2.5 * lineH || (dy < lineH / 2 && Math.abs(next.x - last.x) < 2 * lineH)
          if (shown && next.x === last.x && next.y === last.y && next.h === last.h) return
          last = next
          caret.classList.toggle('is-jump', jump)
          caret.classList.add('is-shown')
          caret.style.transform = `translate(${next.x}px, ${next.y}px) scaleY(${next.h})`
          shown = true
          lit()
        }
        // Hidden at once (a range, a blur); a move waits for the next frame, so the typed text paints first.
        schedule = () => {
          if (!standing()) return hide()
          if (!frame) frame = win.requestAnimationFrame(() => { frame = win.requestAnimationFrame(draw) })
        }
        // What moves the text under a standing caret: a resize or reflow of the document, a scrolled wide table, the window's focus.
        const resized = typeof ResizeObserver === 'function' ? new ResizeObserver(() => schedule()) : null
        resized?.observe(view.dom)
        view.dom.addEventListener('scroll', schedule, { capture: true, passive: true })
        win.addEventListener('focus', schedule)
        win.addEventListener('blur', schedule)
        schedule()
        return {
          update: () => schedule(),
          destroy: () => {
            if (frame) win.cancelAnimationFrame(frame)
            win.clearInterval(blink)
            resized?.disconnect()
            view.dom.removeEventListener('scroll', schedule, { capture: true })
            win.removeEventListener('focus', schedule)
            win.removeEventListener('blur', schedule)
            caret.remove()
            schedule = () => {}
          },
        }
      },
    })]
  },
})
