import type { Editor } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { editorForController } from '../../core/internal'
import type { MarkdownPluginController } from '../../core/plugins/types'
import type { MarkdownEditorController } from '../../core/types'
import { isMarkdownComposition } from '../modules/composition'

/** The top-level block the keyboard path of the block grip (Alt+F10, then Tab) currently addresses. */
export interface MarkdownBlockKeyboardTarget {
  readonly pos: number
  readonly node: ProseMirrorNode
}
/**
 * Lets a plugin put its own controls for a block into the grip's keyboard path. Tab on the block grip, which
 * Alt+F10 reaches without hover, moves to the hooks' controls in registration order, then continues to the selected
 * blocks toolbar or the browser's next stop. Shift+Tab walks back to the grip, Escape returns focus to the editor.
 * Controls need not be tabbable (`tabindex="-1"` keeps them out of the ordinary Tab order) and must name themselves.
 */
export interface MarkdownBlockKeyboardHook {
  /** Unique per editor. */
  readonly id: string
  /** The elements for this block, or none. Called each time the path is entered, so it reflects the current state. */
  controls(block: MarkdownBlockKeyboardTarget): readonly HTMLElement[] | null | undefined
}

const hooks = new WeakMap<Editor, Map<string, MarkdownBlockKeyboardHook>>()

/** Join the block grip's keyboard path for one editor. Returns the cleanup; a duplicate id throws. */
export function registerMarkdownBlockKeyboardHook(owner: MarkdownPluginController | Editor, hook: MarkdownBlockKeyboardHook): () => void {
  const editor = 'view' in owner ? owner : editorForController(owner as MarkdownEditorController)
  if (!editor || editor.isDestroyed) throw new Error('Attach the Markdown editor view before registering a block keyboard hook.')
  if (!hook.id?.trim() || typeof hook.controls !== 'function') throw new TypeError('A block keyboard hook requires an id and a controls function.')
  let entries = hooks.get(editor)
  if (!entries) { entries = new Map(); hooks.set(editor, entries) }
  if (entries.has(hook.id)) throw new Error(`Duplicate block keyboard hook: ${hook.id}`)
  entries.set(hook.id, hook)
  return () => {
    if (entries.get(hook.id) !== hook) return
    entries.delete(hook.id)
    if (!entries.size && hooks.get(editor) === entries) hooks.delete(editor)
  }
}

/** The usable controls of every hook for `block`: connected, enabled, in this editor's document. A failing hook is skipped. */
export function markdownBlockKeyboardControls(editor: Editor, block: MarkdownBlockKeyboardTarget): HTMLElement[] {
  const found: HTMLElement[] = []
  for (const hook of hooks.get(editor)?.values() ?? []) {
    try {
      for (const element of hook.controls(block) ?? []) {
        if (element.isConnected && element.ownerDocument === editor.view.dom.ownerDocument && !(element as HTMLButtonElement).disabled && !found.includes(element)) found.push(element)
      }
    } catch { /* A plugin's failure must not take the grip's keyboard path with it. */ }
  }
  return found
}

/**
 * Tab pressed on the grip of `block`. Moves focus to the first hook control and returns true, or returns false with
 * nothing changed when no hook offers one. `after` runs when Tab leaves the last control and returns whether it
 * moved focus itself (otherwise the browser's default Tab applies). `at` starts at that control instead of the first (false if it is none).
 */
export function enterMarkdownBlockKeyboardControls(editor: Editor, block: MarkdownBlockKeyboardTarget, path: { grip: HTMLElement; after?: () => boolean }, at?: HTMLElement): boolean {
  const controls = markdownBlockKeyboardControls(editor, block)
  const start = at ? controls.indexOf(at) : 0
  if (start < 0 || !controls.length) return false
  const end = () => { for (const control of controls) { control.removeEventListener('keydown', onKeydown); control.removeEventListener('focusout', onFocusOut) } }
  const move = (control: HTMLElement | undefined) => { if (control?.isConnected) control.focus(); else editor.view.focus() }
  const onKeydown = (event: KeyboardEvent) => {
    if (event.defaultPrevented || isMarkdownComposition(event, editor)) return
    const index = controls.indexOf(event.currentTarget as HTMLElement)
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); end(); editor.view.focus(); return }
    if (event.key !== 'Tab' || event.altKey || event.ctrlKey || event.metaKey) return
    if (event.shiftKey) { event.preventDefault(); end(); if (index > 0) { enterAt(index - 1) } else move(path.grip); return }
    if (index < controls.length - 1) { event.preventDefault(); enterAt(index + 1); return }
    end()
    if (path.after?.()) event.preventDefault()
  }
  const onFocusOut = (event: FocusEvent) => {
    const next = event.relatedTarget as globalThis.Node | null
    if (next && controls.some(control => control === next || control.contains(next))) return
    end()
    // Focus to nowhere: a redraw removed the control (Space on a requirement's check redraws its heading's widgets). The redrawn control
    // that takes the focus next goes on with the path, or Esc would reach the editor's own handling and Tab the browser's.
    if (!next) editor.view.dom.ownerDocument.addEventListener('focusin', focus => {
      const node = editor.state.doc.nodeAt(block.pos)
      if (node) enterMarkdownBlockKeyboardControls(editor, { pos: block.pos, node }, path, focus.target as HTMLElement)
    }, { once: true })
  }
  const enterAt = (index: number) => {
    end()
    for (const control of controls) { control.addEventListener('keydown', onKeydown); control.addEventListener('focusout', onFocusOut) }
    move(controls[index])
  }
  enterAt(start)
  return true
}
