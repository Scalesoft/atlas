import type { Editor, JSONContent } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { MAX_MARKDOWN_TEXT_LENGTH } from '../../core/documentConstraints'
import { closeMarkdownHistory } from '../../engine/historyBoundary'
import { importMarkdownFile } from '../../engine/markdownInput'
import { markdownStrings } from '../../ui/strings'
import { imagesStrings } from '../../ui/strings/images'
import { isUploadedImage } from './uploadService'
import { checkFileBudget, imageSource, resourcePolicyFor } from './resourcePolicy'
import { runResourceTask } from './resourceQueue'
import { checkImageFileBudget } from './imageFileBudget'
import { createFileDropPreview, type FileDropPreviewKind } from './fileDropPreview'
import { animateMarkdownMotion, markdownMotion } from '../../core/motion'

export const markdownFileDropErrorEvent = 'md-file-drop-error'
export const markdownFileDropStateEvent = 'md-file-drop-state'
export interface MarkdownFileDropHandler {
  readonly id: string
  accept(file: File): boolean
  resolve(file: File, context: { signal: AbortSignal }): Promise<JSONContent | JSONContent[]>
}
const handlers = new WeakMap<Editor, Set<MarkdownFileDropHandler>>()
const scopes = new WeakMap<Editor, { insert(position: number, work: (signal: AbortSignal) => Promise<JSONContent[]>, files: { file: File; kind: FileDropPreviewKind }[]): void; cancel(): void }>()
const handledEvents = new WeakSet<Event>()

/** Optional modules register file decoding/upload without adding dependencies to core. */
export function registerMarkdownFileDropHandler(editor: Editor, handler: MarkdownFileDropHandler): () => void {
  let entries = handlers.get(editor)
  if (!entries) { entries = new Set(); handlers.set(editor, entries) }
  if ([...entries].some(entry => entry.id === handler.id)) throw new Error(`Duplicate file handler: ${handler.id}`)
  entries.add(handler)
  return () => { entries.delete(handler); if (!entries.size) handlers.delete(editor) }
}

function readText(file: File, signal: AbortSignal, doc: Document): Promise<string> {
  if (file.size > MAX_MARKDOWN_TEXT_LENGTH) return Promise.reject(new Error(imagesStrings.textFileTooLarge))
  return new Promise((resolve, reject) => {
    const reader = new (doc.defaultView?.FileReader ?? FileReader)()
    const abort = () => { reader.abort(); reject(new DOMException(imagesStrings.importCancelled, 'AbortError')) }
    const finish = () => signal.removeEventListener('abort', abort)
    reader.onload = () => { finish(); resolve(String(reader.result ?? '').replace(/^\uFEFF/, '')) }
    reader.onerror = () => { finish(); reject(reader.error ?? new Error(imagesStrings.fileReadFailed)) }
    reader.onabort = () => { finish(); reject(new DOMException(imagesStrings.importCancelled, 'AbortError')) }
    if (signal.aborted) { abort(); return }
    signal.addEventListener('abort', abort, { once: true }); reader.readAsText(file)
  })
}

function resolver(editor: Editor, file: File): { kind: FileDropPreviewKind; load?: MarkdownFileDropHandler['resolve'] } {
  const extension = file.name.toLowerCase().match(/\.([^.]+)$/)?.[1]
  const imageType = file.type || ({ png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', gif: 'image/gif', webp: 'image/webp' } as Record<string, string>)[extension ?? '']
  if (imageType?.startsWith('image/') && editor.storage.imageUpload?.accept.includes(imageType)) {
    const upload = editor.storage.imageUpload?.upload
    if (upload) return { kind: 'image', load: async (file, context) => {
      if (context.signal.aborted) throw new DOMException(imagesStrings.importCancelled, 'AbortError')
      const image = await upload(file, context)
      if (!isUploadedImage(image)) throw new Error(imagesStrings.uploadInvalidUrl)
      return { type: 'image', attrs: { src: imageSource(image.url, resourcePolicyFor(editor)), alt: image.alt ?? file.name } }
    } }
  }
  if (extension === 'md' || extension === 'markdown' || (!extension && file.type === 'text/markdown')) return { kind: 'text', load: async (file, { signal }) => {
    const text = await readText(file, signal, editor.view.dom.ownerDocument)
    if (!(editor.storage as unknown as { markdown?: unknown }).markdown) throw new Error(imagesStrings.markdownImportDisabled)
    // Same import boundary as Markdown paste; the engine's parser error is not user copy.
    try { return importMarkdownFile(editor, text, file.name) }
    catch (cause) { throw Object.assign(new Error(markdownStrings.markdownImportFailed), { cause }) }
  } }
  if (extension === 'txt' || (!extension && file.type === 'text/plain')) return { kind: 'text', load: async (file, { signal }) => (await readText(file, signal, editor.view.dom.ownerDocument)).split(/\r\n?|\n/).map(text => ({
    type: 'paragraph', ...(text ? { content: [{ type: 'text', text }] } : {}),
  })) }
  if ((extension === 'mmd' || extension === 'mermaid') && editor.schema.nodes.diagram) return { kind: 'diagram', load: async (file, { signal }) => ({
    type: 'diagram', attrs: { data: { version: 1, source: await readText(file, signal, editor.view.dom.ownerDocument), caption: file.name.replace(/\.[^.]+$/, '') } },
  }) }
  return { kind: 'attachment', load: [...handlers.get(editor) ?? []].find(handler => handler.accept(file))?.resolve }
}

export function canHandleMarkdownFiles(editor: Editor, files: readonly File[]): boolean {
  return !editor.isDestroyed && editor.isEditable && files.some(file => !!resolver(editor, file).load)
}

/** Shared by the native ProseMirror drop route and surrounding editor whitespace. */
export function handleMarkdownFileDrop(editor: Editor, event: DragEvent, position?: number): boolean {
  if (handledEvents.has(event)) return true
  const files = [...event.dataTransfer?.files ?? []]
  if (editor.isDestroyed || !editor.isEditable || !files.length || !scopes.has(editor)) return false
  handledEvents.add(event); event.preventDefault()
  const view = editor.view
  try { checkFileBudget(files, resourcePolicyFor(editor)) }
  catch (error) {
    view.dom.dispatchEvent(new CustomEvent(markdownFileDropErrorEvent, { bubbles: true,
      detail: { message: error instanceof Error ? error.message : imagesStrings.importLimitExceeded, error },
    }))
    return true
  }
  const point = position ?? view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ?? view.state.selection.from
  const plans = files.map(file => ({ file, ...resolver(editor, file) }))
  scopes.get(editor)!.insert(point, async signal => {
    const policy = resourcePolicyFor(editor)
    if (policy.maxImagePixels !== undefined) {
      // A policy rejection aborts the whole batch before any remote resolver/upload.
      await Promise.all(files.filter(file => file.type.startsWith('image/') || /\.(png|jpe?g|gif|webp)$/i.test(file.name))
        .map(file => runResourceTask(editor, signal, () => checkImageFileBudget(file, policy, signal))))
      if (signal.aborted) return []
    }
    // Shared queue bounds resolver/upload work; results retain FileList order.
    const results = await Promise.all(plans.map(({ file, load }) => runResourceTask(editor, signal, async () => {
      try {
        if (!load) throw new Error(imagesStrings.noAttachmentStorage)
        const result = await load(file, { signal })
        if (signal.aborted || editor.isDestroyed) return []
        const content = Array.isArray(result) ? result : [result]
        content.forEach(node => editor.schema.nodeFromJSON(node).check())
        return content
      } catch (error) {
        if (!signal.aborted && !editor.isDestroyed) view.dom.dispatchEvent(new CustomEvent(markdownFileDropErrorEvent, {
          bubbles: true, detail: { fileName: file.name, message: `${file.name}: ${error instanceof Error ? error.message : imagesStrings.fileInsertFailed}`, error },
        }))
        return []
      }
    })))
    return results.flat()
  }, plans)
  return true
}

export function cancelMarkdownFileDrops(editor: Editor): void { scopes.get(editor)?.cancel() }

type Targets = ReadonlyMap<symbol, number>
type Change = { id: symbol; position: number } | { remove: symbol }
/** Ephemeral bookmarks map through typing, deletion and collaborative changes. */
export function createMarkdownFileDropTargetsPlugin(editor: Editor): Plugin<Targets> {
  const key = new PluginKey<Targets>('markdownFileDropTargets')
  const previews = new Map<symbol, ReturnType<typeof createFileDropPreview>>()
  return new Plugin<Targets>({ key,
    state: { init: () => new Map(), apply(transaction, previous) {
      const change = transaction.getMeta(key) as Change | undefined
      if (!transaction.docChanged && !change) return previous
      const targets = new Map<symbol, number>()
      for (const [id, position] of previous) { const mapped = transaction.mapping.mapResult(position, 1); if (!mapped.deleted) targets.set(id, mapped.pos) }
      if (change && 'position' in change) targets.set(change.id, change.position)
      if (change && 'remove' in change) targets.delete(change.remove)
      return targets
    } },
    props: { decorations(state) {
      return DecorationSet.create(state.doc, [...key.getState(state) ?? []].flatMap(([id, position]) => {
        const preview = previews.get(id)
        return preview ? [Decoration.widget(position, preview.dom, { side: 1, ignoreSelection: true })] : []
      }))
    } },
    view(view) {
      let disposed = false
      const pending = new Map<symbol, AbortController>()
      const counts = new Map<symbol, number>()
      const notify = () => view.dom.dispatchEvent(new CustomEvent(markdownFileDropStateEvent, {
        bubbles: true, detail: { pending: [...counts.values()].reduce((sum, count) => sum + count, 0) },
      }))
      const remove = (id: symbol) => {
        const existed = pending.delete(id); counts.delete(id)
        previews.get(id)?.destroy(); previews.delete(id)
        if (existed) notify()
        if (!disposed && !editor.isDestroyed && key.getState(view.state)?.has(id)) view.dispatch(view.state.tr.setMeta(key, { remove: id }).setMeta('addToHistory', false))
      }
      const cancel = (id: symbol, controller: AbortController) => { remove(id); controller.abort() }
      const scope = {
        cancel() { for (const [id, controller] of [...pending]) cancel(id, controller) },
        insert(position: number, work: (signal: AbortSignal) => Promise<JSONContent[]>, files: { file: File; kind: FileDropPreviewKind }[]) {
          if (disposed || !editor.isEditable || !Number.isInteger(position) || position < 0 || position > view.state.doc.content.size) return
          const id = Symbol('fileDrop'), controller = new AbortController()
          previews.set(id, createFileDropPreview(view.dom.ownerDocument, files))
          pending.set(id, controller); counts.set(id, files.length); notify()
          view.dispatch(view.state.tr.setMeta(key, { id, position }).setMeta('addToHistory', false))
          void work(controller.signal).then(content => {
            if (disposed || editor.isDestroyed || controller.signal.aborted || !editor.isEditable) return
            const target = key.getState(view.state)?.get(id)
            if (target === undefined || !content.length) return
            remove(id)
            // Tiptap handles block splitting; close both sides of this discrete undo event.
            let insertedTo = target
            editor.chain().command(({ tr }) => { closeMarkdownHistory(tr); return true })
              .insertContentAt(target, content, { updateSelection: false })
              .command(({ tr }) => {
                insertedTo = tr.mapping.map(target, 1)
                return true
              }).run()
            if (!editor.isDestroyed) {
              view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
              // Inserted blocks rise in; under reduced motion only the fade remains (at most 160 ms, linear).
              const motion = markdownMotion(view.dom)
              view.state.doc.nodesBetween(target, insertedTo, (node, pos) => {
                if (!node.isBlock) return
                const dom = view.nodeDOM(pos)
                if (dom?.nodeType === 1) animateMarkdownMotion(dom as HTMLElement,
                  [{ opacity: .45, transform: `translateY(${motion.rise}px)` }, { opacity: 1, transform: 'translateY(0)' }],
                  { duration: motion.duration('base'), fill: 'none' }, motion)
                return false
              })
            }
          }).catch(error => {
            if (!disposed && !editor.isDestroyed && !controller.signal.aborted) view.dom.dispatchEvent(new CustomEvent(markdownFileDropErrorEvent, {
              bubbles: true, detail: { message: imagesStrings.filesInsertFailed, error },
            }))
          }).finally(() => remove(id))
        },
      }
      scopes.set(editor, scope)
      const refresh = () => { for (const [id, controller] of [...pending]) if (!editor.isEditable || !key.getState(view.state)?.has(id)) cancel(id, controller) }
      const observer = view.dom.ownerDocument.defaultView?.MutationObserver ? new view.dom.ownerDocument.defaultView.MutationObserver(refresh) : undefined
      observer?.observe(view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
      return { update: refresh, destroy() { disposed = true; observer?.disconnect(); scope.cancel(); if (scopes.get(editor) === scope) scopes.delete(editor) } }
    },
  })
}
