import type { Editor } from '@tiptap/core'

/** Shared IME boundary for editor chrome. Composing keys belong to the input method. */
export function isMarkdownComposition(event: KeyboardEvent, editor?: Editor): boolean {
  return event.isComposing || event.keyCode === 229 || !!editor?.view.composing
}

/** Native form submit has no isComposing flag; retain composition state on its inputs. */
export function trackMarkdownComposition(element: HTMLElement) {
  let active = false
  const start = () => { active = true }
  const end = () => { active = false }
  element.addEventListener('compositionstart', start)
  element.addEventListener('compositionend', end)
  return {
    active: () => active,
    destroy() { element.removeEventListener('compositionstart', start); element.removeEventListener('compositionend', end) },
  }
}
