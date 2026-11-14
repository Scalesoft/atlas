import type { Editor } from '@tiptap/core'
import { isMarkdownComposition } from './composition'

type Popover = { anchor: Element; key: string; close: () => void
  /** Left by a second press on its opener (P1) as by a click outside: a pending „Použít“ form applies (D3). True once closed. */
  leave?: () => boolean }
/** An editor, or the content root of a statically rendered document (its outline has a popover too). */
type PopoverOwner = Editor | object
const active = new WeakMap<PopoverOwner, Popover>()
const listeners = new WeakMap<PopoverOwner, Set<() => void>>()

export function activeMarkdownPopover(editor: PopoverOwner): Popover | undefined { return active.get(editor) }

/** A second press on the open popover's opener closes it as leaving it does; false while a refused form keeps it open with its error. */
export function leaveMarkdownPopover(popover: Popover): boolean {
  if (popover.leave) return popover.leave()
  popover.close(); return true
}

/** DOM components and Vue toolbars share one open popover per editor. */
export function registerMarkdownPopover(editor: PopoverOwner, popover: Popover): () => void {
  active.get(editor)?.close()
  active.set(editor, popover)
  notifyMarkdownPopoverChange(editor)
  return () => { if (active.get(editor) === popover) { active.delete(editor); notifyMarkdownPopoverChange(editor) } }
}

/** Notified whenever the editor's active popover changes (opens, closes or is replaced) — e.g. so
 *  the table cell "orb" trigger can hide itself while a different popover (a row/column menu)
 *  covers the same cell, instead of visibly poking through it. */
export function onMarkdownPopoverChange(editor: PopoverOwner, listener: () => void): () => void {
  let set = listeners.get(editor)
  if (!set) { set = new Set(); listeners.set(editor, set) }
  set.add(listener)
  return () => { set!.delete(listener) }
}

function notifyMarkdownPopoverChange(editor: PopoverOwner): void {
  listeners.get(editor)?.forEach(listener => listener())
}

/** Shared roving focus for linear menus; visual grids may handle keys first. */
export function bindMarkdownMenu(element: HTMLElement): { focus: () => void; destroy: () => void } {
  const doc = element.ownerDocument
  const items = () => [...element.querySelectorAll<HTMLElement>('[role=menuitem],[role=menuitemradio],[role=menuitemcheckbox]')]
    .filter(item => !item.matches(':disabled,[aria-disabled=true],[hidden]'))
  const sync = () => items().forEach(item => { item.tabIndex = item === doc.activeElement ? 0 : -1 })
  const keyboard = (event: KeyboardEvent) => {
    if (event.defaultPrevented || isMarkdownComposition(event)) return
    const options = items(), index = options.indexOf(doc.activeElement as HTMLElement)
    if (!options.length) return
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1
      : event.key === 'ArrowDown' ? (index + 1) % options.length
        : event.key === 'ArrowUp' ? (index - 1 + options.length) % options.length : undefined
    if (next !== undefined) { event.preventDefault(); options[next]?.focus({ preventScroll: true }) }
  }
  element.addEventListener('focusin', sync)
  element.addEventListener('keydown', keyboard)
  return {
    focus() {
      const options = items()
      const first = options.find(item => item === doc.activeElement) ?? options.find(item => item.getAttribute('aria-checked') === 'true') ?? options[0]
      first?.focus({ preventScroll: true }); sync()
    },
    destroy() { element.removeEventListener('focusin', sync); element.removeEventListener('keydown', keyboard) },
  }
}
