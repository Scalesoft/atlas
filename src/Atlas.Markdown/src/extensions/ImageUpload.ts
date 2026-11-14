import { Extension, type Editor } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import type { EditorView } from '@tiptap/pm/view'
import { cancelImageUploads, createImageUploadTargetsPlugin, prepareImageUpload } from '../features/images/uploadOperations'
import { createHttpImageUpload, type ImageUploadService } from '../features/images/uploadService'
import { cancelMarkdownFileDrops, createMarkdownFileDropTargetsPlugin, handleMarkdownFileDrop } from '../features/images/fileDrop'
import { checkFileBudget, resourcePolicyFor } from '../features/images/resourcePolicy'
import { imageUploadErrorEvent } from '../features/images/uploadService'
import { checkImageFileBudget } from '../features/images/imageFileBudget'
import { runResourceTask } from '../features/images/resourceQueue'
import { blocksStrings } from '../ui/strings/blocks'
import { imagesStrings } from '../ui/strings/images'

export const defaultImageUploadAccept: readonly string[] = Object.freeze(['image/png', 'image/jpeg', 'image/gif', 'image/webp'])

export interface ImageUploadOptions {
  uploadUrl: string | null | undefined
  /** Optional host service; takes precedence over the legacy upload URL. */
  upload?: ImageUploadService | null
  accept: string[]
}

interface ImageUploadStorage {
  upload: ImageUploadService | undefined
  accept: readonly string[]
}

declare module '@tiptap/core' {
  interface Storage { imageUpload: ImageUploadStorage }
}

/** Internal view configuration bridge; content schema and controller identity stay unchanged. */
export function setImageUploadService(editor: Editor, upload: ImageUploadService | undefined): void {
  const storage = editor.storage.imageUpload
  if (!storage || storage.upload === upload) return
  storage.upload = upload
  cancelImageUploads(editor)
  cancelMarkdownFileDrops(editor)
}

export const ImageUpload = Extension.create<ImageUploadOptions, ImageUploadStorage>({
  name: 'imageUpload',

  addOptions() {
    return {
      uploadUrl: null,
      upload: null,
      accept: [...defaultImageUploadAccept],
    }
  },

  addStorage() {
    return { upload: this.options.upload ?? (this.options.uploadUrl ? createHttpImageUpload(this.options.uploadUrl) : undefined), accept: this.options.accept }
  },

  addProseMirrorPlugins() {
    const options = this.options
    const editor = this.editor

    const storage = this.storage
    const uploadAndInsert = (file: File, pos: number, upload: ImageUploadService) => {
      void prepareImageUpload(editor, pos, upload)?.upload(file)
    }

    const collectImages = (files: FileList | DataTransferItemList | undefined | null): File[] => {
      if (!files) return []
      const out: File[] = []
      for (let i = 0; i < files.length; i++) {
        const item = files[i] as File | DataTransferItem
        if ('kind' in item) {
          if (item.kind !== 'file') continue
          const f = item.getAsFile()
          if (f && options.accept.includes(f.type)) out.push(f)
        } else if (options.accept.includes(item.type)) {
          out.push(item)
        }
      }
      return out
    }

    return [
      createImageUploadTargetsPlugin(editor),
      createMarkdownFileDropTargetsPlugin(editor),
      new Plugin({
        key: new PluginKey('imageUpload'),
        props: {
          handlePaste: (view: EditorView, event: ClipboardEvent) => {
            const upload = storage.upload
            if (!upload || !editor.isEditable) return false
            const files = collectImages(event.clipboardData?.items)
            if (files.length === 0) return false
            try { checkFileBudget(files, resourcePolicyFor(editor)) }
            catch (error) {
              event.preventDefault()
              view.dom.dispatchEvent(new CustomEvent(imageUploadErrorEvent, { bubbles: true,
                detail: { message: error instanceof Error ? error.message : blocksStrings.pasteLimitExceeded, error },
              }))
              return true
            }
            // Wave 8 — only swallow the paste if the clipboard has files
            // ONLY. If it also carries text/html (typical Excel / Word
            // copy with embedded images), let ProseMirror handle the HTML
            // path AND fire image uploads alongside. The user gets both
            // the rich content and the uploaded images.
            const hasHtml = !!event.clipboardData?.types.includes('text/html')
            if (!hasHtml) event.preventDefault()
            const pos = view.state.selection.from
            const policy = resourcePolicyFor(editor)
            if (policy.maxImagePixels === undefined) {
              for (const f of files) uploadAndInsert(f, pos, upload)
            } else {
              const plans = files.map(file => ({ file, operation: prepareImageUpload(editor, pos, upload) }))
              void Promise.all(plans.map(({ file, operation }) => operation
                ? runResourceTask(editor, operation.signal, () => checkImageFileBudget(file, policy, operation.signal))
                : Promise.reject(new DOMException(imagesStrings.uploadCancelled, 'AbortError'))))
                .then(() => { for (const { file, operation } of plans) void operation?.upload(file) })
                .catch(error => {
                  const canceled = editor.isDestroyed || plans.some(({ operation }) => operation?.signal.aborted)
                  plans.forEach(({ operation }) => operation?.cancel())
                  if (!canceled) view.dom.dispatchEvent(new CustomEvent(imageUploadErrorEvent, { bubbles: true,
                    detail: { message: error instanceof Error ? error.message : blocksStrings.imageLimitExceeded, error },
                  }))
                })
            }
            return !hasHtml
          },
          handleDrop: (view: EditorView, event: DragEvent, _slice, moved: boolean) => {
            if (moved) return false
            return handleMarkdownFileDrop(editor, event)
          },
        },
      }),
    ]
  },
})
