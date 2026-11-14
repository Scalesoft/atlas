import type { Editor } from '@tiptap/core'

interface PlaceholderStorage {
  markdownEditorView?: {
    configured: boolean
    value?: string
  }
}

function storageFor(editor: Editor): PlaceholderStorage {
  const storage = editor.storage as unknown as { placeholder?: PlaceholderStorage }
  storage.placeholder ??= {}
  return storage.placeholder
}

export function resolveEditorPlaceholder(editor: Editor, initial: string | undefined): string {
  const current = storageFor(editor).markdownEditorView
  return current?.configured ? (current.value ?? '') : (initial ?? '')
}

export function setEditorPlaceholder(editor: Editor, value: string | undefined, refresh = false): void {
  storageFor(editor).markdownEditorView = { configured: true, value }
  if (refresh && !editor.isDestroyed) {
    editor.view.dispatch(editor.state.tr
      .setMeta('addToHistory', false)
      .setMeta('markdownEditorViewConfiguration', 'placeholder'))
  }
}
