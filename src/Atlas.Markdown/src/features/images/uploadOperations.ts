import type { Editor } from '@tiptap/core'
import { NodeSelection, Plugin, PluginKey, Selection } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { checkFileBudget, imageSource, resourcePolicyFor } from './resourcePolicy'
import { runResourceTask } from './resourceQueue'
import { checkImageFileBudget, imageFileDimensions } from './imageFileBudget'
import { defaultImagePresentation } from './presentation'
import {
  imageUploadErrorEvent,
  isUploadedImage,
  type ImageUploadService,
} from './uploadService'
import { imagesStrings } from '../../ui/strings/images'
import { createFileDropPreview } from './fileDropPreview'
import { markdownChangeMapping } from '../../core/changeMapping'

type TargetId = symbol
type Targets = ReadonlyMap<TargetId, number>
type TargetChange = { add: { id: TargetId; position: number } } | { remove: TargetId } | { preview: TargetId }

export interface PendingImageUpload {
  readonly signal: AbortSignal
  /** Resolves false on cancellation or failure; failures emit md-image-upload-error. */
  upload(file: File): Promise<boolean>
  cancel(): void
}

interface UploadScope {
  prepare(position: number, upload: ImageUploadService): PendingImageUpload | undefined
  cancelAll(): void
}

const scopes = new WeakMap<Editor, UploadScope>()

/** Reserve the insertion point before opening a picker or starting a request. */
export function prepareImageUpload(editor: Editor, position: number, upload: ImageUploadService) {
  return scopes.get(editor)?.prepare(position, upload)
}

/** A service replacement invalidates both requests and native pickers using the previous context. */
export function cancelImageUploads(editor: Editor): void {
  scopes.get(editor)?.cancelAll()
}

/** Pure transaction mapping also sees appended and collaborative transactions. */
export function createImageUploadTargetsPlugin(editor: Editor): Plugin<Targets> {
  // Anchors belong to this mounted plugin, never to a subsequently rebuilt view.
  const targetKey = new PluginKey<Targets>('imageUploadTargets')
  // The pasted image's preview (its own blob, a sized card with progress) stands in the document while the request runs; view state only.
  const previews = new Map<TargetId, ReturnType<typeof createFileDropPreview>>()
  return new Plugin<Targets>({
    key: targetKey,
    props: {
      decorations: state => DecorationSet.create(state.doc, [...targetKey.getState(state) ?? []].flatMap(([id, position]) => {
        const preview = previews.get(id)
        return preview ? [Decoration.widget(position, preview.dom, { side: 1, ignoreSelection: true })] : []
      })),
    },
    state: {
      init: () => new Map(),
      apply(transaction, previous) {
        const change = transaction.getMeta(targetKey) as TargetChange | undefined
        if (!transaction.docChanged && !change) return previous
        // A collaborator's change or a server's echo moves a target only by what really changed (UX PG4-6: an echo right after the
        // slash command cancelled the image picker, ~1 run in 2).
        const targets = new Map<TargetId, number>(), mapping = markdownChangeMapping(transaction)
        for (const [id, position] of previous) {
          const mapped = mapping.mapResult(position, 1)
          if (!mapped.deleted) targets.set(id, mapped.pos)
        }
        if (change && 'add' in change) targets.set(change.add.id, change.add.position)
        if (change && 'remove' in change) targets.delete(change.remove)
        return targets
      },
    },
    view(view) {
      let disposed = false
      const pending = new Map<TargetId, PendingImageUpload>()

      const removeTarget = (id: TargetId) => {
        pending.delete(id)
        previews.get(id)?.destroy(); previews.delete(id)
        if (!disposed && !editor.isDestroyed && targetKey.getState(view.state)?.has(id)) {
          view.dispatch(view.state.tr.setMeta(targetKey, { remove: id } satisfies TargetChange).setMeta('addToHistory', false))
        }
      }

      const scope: UploadScope = {
        cancelAll() {
          // Abort handlers may start new operations; only cancel those that existed at replacement time.
          for (const operation of [...pending.values()]) operation.cancel()
        },
        prepare(position, upload) {
          if (disposed || editor.isDestroyed || !editor.isEditable || !view.state.schema.nodes.image) return undefined
          if (!Number.isInteger(position) || position < 0 || position > view.state.doc.content.size) return undefined
          const id = Symbol('imageUpload')
          const controller = new AbortController()
          let started = false
          const operation: PendingImageUpload = {
            signal: controller.signal,
            cancel() {
              // Remove first: abort listeners may themselves clean up the picker.
              removeTarget(id)
              controller.abort()
            },
            async upload(file) {
              if (started || controller.signal.aborted || !pending.has(id)) return false
              started = true
              previews.set(id, createFileDropPreview(view.dom.ownerDocument, [{ file, kind: 'image' }]))
              view.dispatch(view.state.tr.setMeta(targetKey, { preview: id } satisfies TargetChange).setMeta('addToHistory', false))
              try {
                checkFileBudget([file], resourcePolicyFor(editor))
                const image = await runResourceTask(editor, controller.signal, async () => {
                  if (resourcePolicyFor(editor).maxImagePixels !== undefined) await checkImageFileBudget(file, resourcePolicyFor(editor), controller.signal)
                  if (controller.signal.aborted) throw new DOMException(imagesStrings.uploadCancelled, 'AbortError')
                  return upload(file, { signal: controller.signal })
                })
                // An image enters at its own size, up to the column (not stretched to it): the header says how wide that is.
                const natural = await imageFileDimensions(file).catch(() => null)
                if (disposed || editor.isDestroyed || controller.signal.aborted) return false
                const column = view.dom.clientWidth
                const currentPosition = targetKey.getState(view.state)?.get(id)
                if (currentPosition === undefined) return false
                if (!editor.isEditable) { operation.cancel(); return false }
                if (!isUploadedImage(image)) throw new Error(imagesStrings.uploadInvalidUrl)
                removeTarget(id)
                // Completing a background request must not move the user's focus or selection.
                const before = editor.state.selection
                const inserted = editor.commands.insertContentAt(currentPosition, {
                  type: 'image',
                  attrs: { src: imageSource(image.url, resourcePolicyFor(editor)), alt: image.alt ?? file.name,
                    ...natural && column > 0 ? { presentation: { ...defaultImagePresentation, naturalWidth: natural.width, naturalHeight: natural.height,
                      width: Math.max(10, Math.min(100, Math.round(natural.width / column * 100))) } } : {} },
                }, { updateSelection: false })
                // A caret in the empty line the image replaced maps onto the image, and the next letter typed would replace it (UX
                // PG4-6): the caret goes on after the image instead, where the writer continues.
                const after = editor.state.selection
                if (inserted && !(before instanceof NodeSelection) && after instanceof NodeSelection && after.node.type.name === 'image'
                  && after.from <= currentPosition && currentPosition <= after.to) {
                  const next = Selection.findFrom(editor.state.doc.resolve(after.to), 1, true)
                  if (next) view.dispatch(editor.state.tr.setSelection(next).setMeta('addToHistory', false))
                }
                return inserted
              } catch (error) {
                if (!disposed && !editor.isDestroyed && !controller.signal.aborted) {
                  removeTarget(id)
                  view.dom.dispatchEvent(new CustomEvent(imageUploadErrorEvent, {
                    bubbles: true,
                    detail: { message: error instanceof RangeError ? error.message : imagesStrings.uploadFailed, error },
                  }))
                }
                return false
              } finally {
                removeTarget(id)
              }
            },
          }
          pending.set(id, operation)
          view.dispatch(view.state.tr.setMeta(targetKey, { add: { id, position } } satisfies TargetChange).setMeta('addToHistory', false))
          return operation
        },
      }
      scopes.set(editor, scope)

      return {
        update(nextView) {
          const targets = targetKey.getState(nextView.state)
          for (const [id, operation] of pending) {
            if (!targets?.has(id) || !editor.isEditable) operation.cancel()
          }
        },
        destroy() {
          disposed = true
          scope.cancelAll()
          if (scopes.get(editor) === scope) scopes.delete(editor)
        },
      }
    },
  })
}
