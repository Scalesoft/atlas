import type { Editor } from '@tiptap/core'
import { prepareImageUpload } from './uploadOperations'
import type { ImageUploadService } from './uploadService'

/** The target is reserved while the native picker is open, before it yields a file. */
export function pickAndUploadImage(editor: Editor, upload: ImageUploadService, accept: readonly string[]) {
  const operation = prepareImageUpload(editor, editor.state.selection.from, upload)
  if (!operation) return
  const input = editor.view.dom.ownerDocument.createElement('input')
  input.type = 'file'
  input.accept = accept.join(',')
  const cleanup = () => {
    input.onchange = null
    input.oncancel = null
    operation.signal.removeEventListener('abort', cleanup)
  }
  operation.signal.addEventListener('abort', cleanup, { once: true })
  input.oncancel = () => { cleanup(); operation.cancel() }
  input.onchange = () => {
    const file = input.files?.[0]
    cleanup()
    if (!file || !accept.includes(file.type)) { operation.cancel(); return }
    void operation.upload(file)
  }
  try {
    input.click()
  } catch (error) {
    operation.cancel()
    throw error
  }
}
