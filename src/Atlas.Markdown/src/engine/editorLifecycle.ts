import type { Editor } from '@tiptap/core'
import type { EditorView } from '@tiptap/pm/view'

/**
 * Tiptap stops its destroy sequence when an extension callback throws. Continue every
 * independent teardown step so a faulty extension cannot retain a view or manager.
 */
export function destroyEditor(editor: Editor | null): unknown[] {
  if (!editor) return []
  const errors: unknown[] = []
  let view: EditorView | null = null
  // mount() installs the view before Tiptap flips isInitialized in a timer.
  try {
    if (editor.options.element) {
      const candidate = editor.view
      void candidate.dom
      view = candidate
    }
  } catch { /* A headless editor exposes a throwing proxy instead of a real view. */ }

  try {
    editor.destroy()
    return errors
  } catch (error) { errors.push(error) }

  // Prevent another extension event from interrupting the fallback cleanup.
  try { editor.removeAllListeners() } catch (error) { errors.push(error) }
  try { editor.unmount() } catch (error) { errors.push(error) }
  if (view) {
    let needsFallback = true
    try { needsFallback = !view.isDestroyed } catch { /* Continue with best-effort fallback. */ }
    if (needsFallback) {
      try { view.destroy() } catch (error) { errors.push(error) }
      try { view.dom.remove() } catch (error) { errors.push(error) }
    }
  }
  try { editor.extensionManager.destroy() } catch (error) { errors.push(error) }
  return errors
}

export function initializationFailure(original: unknown, cleanupErrors: readonly unknown[]): unknown {
  if (!cleanupErrors.length) return original
  const message = original instanceof Error ? original.message : String(original)
  return new AggregateError([original, ...cleanupErrors], `Editor initialization failed: ${message}`, { cause: original })
}
