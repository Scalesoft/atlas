import type { Editor } from '@tiptap/core'

const observers = new WeakMap<Editor, { callbacks: Set<() => void>; stop(): void }>()

/** One observer per editor; ordinary content/selection transactions do not redraw every node view. */
export function observeMarkdownEditability(editor: Editor, root: HTMLElement, changed: () => void): () => void {
  let shared = observers.get(editor)
  if (!shared) {
    const callbacks = new Set<() => void>()
    let editable = editor.isEditable
    const sync = () => {
      if (editor.isEditable === editable) return
      editable = editor.isEditable
      for (const callback of [...callbacks]) callback()
    }
    const Observer = root.ownerDocument.defaultView?.MutationObserver
    const observer = Observer ? new Observer(sync) : undefined
    observer?.observe(root, { attributes: true, attributeFilter: ['contenteditable'] })
    editor.on('transaction', sync); editor.on('update', sync)
    shared = { callbacks, stop() { observer?.disconnect(); editor.off('transaction', sync); editor.off('update', sync) } }
    observers.set(editor, shared)
  }
  shared.callbacks.add(changed)
  let stopped = false
  return () => {
    if (stopped) return
    stopped = true
    shared.callbacks.delete(changed)
    if (!shared.callbacks.size) { shared.stop(); observers.delete(editor) }
  }
}
