import type { Editor } from '@tiptap/core'

/** A component's own controls (a chip, a button) keep the keyboard after a change: Ctrl+Z / Ctrl+Y there undo the document as in its text. */
export function createUndoFromControls(editor: Editor | undefined) {
  // Typed as a plain listener: node views attach it to their DOM node whatever its declared type.
  return (raw: Event) => {
    const event = raw as KeyboardEvent, key = event.key.toLowerCase()
    if (event.defaultPrevented || !(event.ctrlKey || event.metaKey) || event.altKey || (key !== 'z' && key !== 'y')) return
    const target = event.target as HTMLElement
    if (target.closest('input, textarea') || target.isContentEditable || !editor?.isEditable) return
    event.preventDefault(); event.stopPropagation()
    if (event.shiftKey || key === 'y') editor.commands.redo(); else editor.commands.undo()
    // The component re-renders on the change: when its control is gone, the keyboard goes on in the editor (Ctrl+Y redoes there).
    setTimeout(() => { if (!target.isConnected && !editor.isDestroyed) editor.view.focus() })
  }
}
