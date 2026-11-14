import { copyMarkdownOverlayTheme, createIconElement, downloadMarkdownFile, markdownStrings, whenMarkdownIdle, registerMarkdownOverlay, type MarkdownModuleContext, type MarkdownModuleNodeContext, type MarkdownModuleRuntime } from '../../core'
import { openapiStrings } from './runtimeStrings'
import { normalizeOpenApi, summarizeOpenApi } from './data'
import { lightOpenApiSummary, type OpenApiData, type OpenApiMode } from './shape'
import { isSpecFileName, specFromFile } from './cardEdit'
import type { OpenApiPluginOptions } from './index'
import styles from './style.css?inline'
import { openSourceEditor } from './sourceDialog'
import { mountOpenApiReference } from './reference'
import { prefetchOpenApiSourceEditor } from './sourceEditorLoader'
export { openSourceEditor } from './sourceDialog'

type Context = MarkdownModuleContext<OpenApiData>
const button = (doc: Document, label: string) => { const item = doc.createElement('button'); item.type = 'button'; item.textContent = label; return item }
// A host workspace outlives this render: hand it the node-bound context, whose signal survives
// editability changes and aborts only when the block itself goes (docs/openapi-detail-workspace.md).
// A hand-built context (tests, third-party mounts) without `nodeContext` falls back to its own render lifetime.
const detailContext = (context: Context): MarkdownModuleNodeContext<OpenApiData> => context.nodeContext?.() ?? {
  anchor: context.container, rootContainer: context.rootContainer, data: context.data, document: context.document, signal: context.signal,
  editor: context.editor, getPos: context.getPos, update: context.update, closeHistory: context.closeHistory, onEditableChange: () => () => {},
}

export function createOpenApiRuntime(options: OpenApiPluginOptions = {}): MarkdownModuleRuntime<OpenApiData> {
  const runtime: MarkdownModuleRuntime<OpenApiData> = {
    // A click on the card only selects it; its mode, name and buttons act where they are, Enter or the toolbar opens the source.
    editOnClick: false,
    render(initial) {
      let context = initial
      const { container, signal } = context
      const doc = container.ownerDocument
      const style = doc.createElement('style'); style.textContent = styles; container.append(style)
      const card = doc.createElement('section'); card.className = 'md-openapi-card'
      const identity = doc.createElement('div'); identity.className = 'md-openapi-identity'
      const emblem = doc.createElement('div'); emblem.className = 'md-openapi-emblem'; emblem.setAttribute('aria-hidden', 'true')
      const apiTag = doc.createElement('span'); apiTag.textContent = openapiStrings.label; emblem.append(createIconElement(doc, 'square-code', 30), apiTag)
      const details = doc.createElement('div'); details.className = 'md-openapi-details'
      const eyebrow = doc.createElement('p'); eyebrow.className = 'md-openapi-eyebrow'; eyebrow.textContent = openapiStrings.eyebrow
      const title = doc.createElement('h3')
      const summary = doc.createElement('p'); summary.className = 'md-openapi-summary'
      const author = doc.createElement('p'); author.className = 'md-openapi-author'
      const fileRow = doc.createElement('div'); fileRow.className = 'md-openapi-file'
      const filename = doc.createElement('span'); filename.className = 'md-openapi-filename'
      const format = doc.createElement('span'); format.className = 'md-openapi-format'
      fileRow.append(createIconElement(doc, 'file-text'), filename, format)
      const actions = doc.createElement('div'); actions.className = 'md-openapi-actions'
      const open = button(doc, openapiStrings.tryItLabel); open.className = 'md-btn-primary'
      const download = button(doc, openapiStrings.downloadLabel); download.className = 'md-btn'; download.prepend(createIconElement(doc, 'download'))
      actions.append(open, download)
      // Edit-mode controls (only while the document is editable; a readonly toggle re-renders): spec actions, filename and mode edited where they are shown.
      const editable = !!context.editor?.isEditable && !!context.update
      const edit = button(doc, openapiStrings.editSpecLabel); edit.className = 'md-btn md-openapi-edit'; edit.prepend(createIconElement(doc, 'pencil'))
      const upload = button(doc, openapiStrings.uploadFileLabel); upload.className = 'md-btn md-openapi-upload'; upload.prepend(createIconElement(doc, 'upload'))
      const picker = doc.createElement('input'); picker.type = 'file'; picker.hidden = true; picker.accept = '.yaml,.yml,.json,application/json,application/yaml,text/yaml'
      const error = doc.createElement('p'); error.className = 'md-openapi-card-error'; error.setAttribute('role', 'alert')
      const modes = doc.createElement('div'); modes.className = 'md-openapi-modes'; modes.setAttribute('role', 'group'); modes.setAttribute('aria-label', openapiStrings.modeGroupLabel)
      const choices: readonly (readonly [OpenApiMode, string])[] = [['interactive', openapiStrings.tryItLabel], ['reference', openapiStrings.docsOnlyLabel], ['download', openapiStrings.downloadOnlyLabel]]
      const modeButtons = choices.map(([value, label]) => { const item = button(doc, label); item.dataset.mode = value; modes.append(item); return item })
      details.append(eyebrow, title, summary, author); identity.append(emblem, details)
      card.append(identity, fileRow)
      // The mode row holds its place for a reader (inert, not shown), so the card keeps its size whatever the right (J7.1).
      // Only in an editor: a static rendering (history, a shared page) has no writer to match and carries no mode choices at all.
      if (editable) actions.append(edit, upload, picker); else if (context.editor) { modes.setAttribute('inert', ''); modes.dataset.reserved = '' }
      if (editable || context.editor) card.append(modes)
      card.append(actions); if (editable) card.append(error)
      container.append(card)
      let closed = false
      let closeReference: (() => void) | undefined
      const refresh = () => {
        const light = lightOpenApiSummary(context.data)
        title.textContent = light.title
        summary.textContent = light.version ? `v${light.version}` : ''
        author.textContent = context.data.uploadedBy ? openapiStrings.authorLine(context.data.uploadedBy) : ''
        author.hidden = !context.data.uploadedBy
        // The full summary parses the whole spec: off the frame that draws the card (a large spec costs over 100 ms); an invalid spec says why here.
        const shown = context.data
        whenMarkdownIdle(() => {
          if (closed || signal.aborted || shown !== context.data) return
          try {
            const info = summarizeOpenApi(shown)
            title.textContent = info.title
            summary.textContent = openapiStrings.versionSummary(info.version, info.operations, info.schemas)
            author.textContent = info.author ? openapiStrings.authorLine(info.author) : ''; author.hidden = !info.author
            error.textContent = ''
          } catch (cause) { error.textContent = (cause as Error).message }
        })
        filename.textContent = context.data.filename
        format.textContent = /\.json$/i.test(context.data.filename) ? 'JSON' : 'YAML'
        const mode = context.data.mode ?? 'interactive'
        open.hidden = mode === 'download'
        const interactive = mode === 'interactive' && options.allowRequests !== false
        modeButtons.forEach(item => item.setAttribute('aria-pressed', String(item.dataset.mode === mode)))
        open.replaceChildren(createIconElement(doc, interactive ? 'code' : 'book-open'), doc.createTextNode(interactive ? openapiStrings.tryItLabel : openapiStrings.docsOnlyLabel))
      }
      // The selection toolbar and the edit button hold source editing: warm the source editor on intent, never on render.
      const warmSource = () => { if (context.editor?.isEditable && context.update) prefetchOpenApiSourceEditor() }
      // Every card edit is one undo step of its own.
      const commit = (data: OpenApiData) => { context.closeHistory?.(); context.update!(data); context.closeHistory?.() }
      const fail = (cause: unknown) => { error.textContent = (cause as Error).message }
      const live = () => !closed && !signal.aborted && !!context.editor?.isEditable
      const replaceFrom = async (file: File | undefined) => {
        if (!file || !live()) return
        if (!isSpecFileName(file.name)) { fail(new TypeError(openapiStrings.notSpecFileError)); return }
        try { const next = await specFromFile(file, context.data, options.uploaderName); if (live()) { error.textContent = ''; commit(next) } } catch (cause) { fail(cause) }
      }
      const rename = () => {
        if (!live() || fileRow.querySelector('input')) return
        const field = doc.createElement('input'); field.className = 'md-openapi-rename'; field.value = context.data.filename; field.maxLength = 255; field.setAttribute('aria-label', openapiStrings.filenameLabel)
        let done = false
        // From the keyboard (Enter, Esc) the keyboard returns to the name; a blur leaves it where the person went.
        const finish = (save: boolean, keyboard = false) => {
          if (done) return; done = true
          const value = field.value.trim()
          if (save && value !== context.data.filename) { try { const next = normalizeOpenApi({ ...context.data, filename: value }); error.textContent = ''; commit(next) } catch (cause) { fail(cause) } }
          if (field.isConnected) field.replaceWith(filename)
          if (keyboard) filename.focus({ preventScroll: true })
        }
        field.onkeydown = event => { event.stopPropagation(); if (event.key === 'Enter') { event.preventDefault(); finish(true, true) } else if (event.key === 'Escape') { event.preventDefault(); finish(false, true) } }
        field.onblur = () => finish(true)
        filename.replaceWith(field); field.focus(); field.select()
      }
      if (editable) {
        filename.setAttribute('role', 'button'); filename.tabIndex = 0; filename.title = openapiStrings.renameFileLabel; filename.dataset.editable = ''
        filename.onclick = event => { event.stopPropagation(); rename() }
        filename.onkeydown = event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); event.stopPropagation(); rename() } }
        for (const item of modeButtons) item.onclick = () => { const value = item.dataset.mode as OpenApiMode; if (live() && value !== (context.data.mode ?? 'interactive')) commit({ ...context.data, mode: value }) }
        modes.addEventListener('keydown', event => {
          const index = modeButtons.indexOf(event.target as HTMLButtonElement)
          if (index < 0 || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return
          event.preventDefault(); event.stopPropagation()
          const next = modeButtons[(index + (event.key === 'ArrowRight' ? 1 : -1) + modeButtons.length) % modeButtons.length]!; next.click(); next.focus()
        })
        edit.onclick = () => { if (live()) runtime.edit!(context) }
        for (const type of ['pointerenter', 'focus']) edit.addEventListener(type, warmSource, { signal })
        upload.onclick = () => picker.click()
        picker.onchange = () => { const file = picker.files?.[0]; picker.value = ''; void replaceFrom(file) }
        // A click the card handles itself must not also reach the block (its click opens the source editor): say so with preventDefault.
        card.addEventListener('click', event => { if ((event.target as Element).closest('button, .md-openapi-filename, input:not([type=file])')) event.preventDefault() }, { signal })
        const hasFiles = (event: DragEvent) => !!event.dataTransfer?.types.includes('Files')
        card.addEventListener('dragover', event => { if (hasFiles(event)) { event.preventDefault(); card.classList.add('is-dropping') } }, { signal })
        card.addEventListener('dragleave', event => { if (!card.contains(event.relatedTarget as Node | null)) card.classList.remove('is-dropping') }, { signal })
        card.addEventListener('drop', event => {
          card.classList.remove('is-dropping')
          if (!hasFiles(event)) return
          event.preventDefault(); event.stopPropagation(); void replaceFrom(event.dataTransfer!.files[0])
        }, { signal })
      }
      const dismissReference = () => { const close = closeReference; closeReference = undefined; close?.() }
      open.onclick = () => { if (!closed && !signal.aborted && context.data.mode !== 'download') { dismissReference(); if (!options.openDetail?.(detailContext(context), 'api')) closeReference = showReference(context, options, dismissReference) } }
      download.onclick = () => { if (!closed && !signal.aborted) downloadMarkdownFile(doc, context.data.filename, new Blob([context.data.source], { type: 'text/plain;charset=utf-8' })) }
      if (context.toolbar) context.toolbar.addButton({ label: openapiStrings.downloadAction, icon: 'download', onClick: () => download.click() })
      for (const type of ['pointerenter', 'focusin']) context.toolbar?.element.addEventListener(type, warmSource, { signal })
      refresh()
      return {
         update(next) { if (closed || signal.aborted) return; if (next.data.source !== context.data.source || next.data.mode !== context.data.mode) dismissReference(); context = next; refresh() },
        destroy() { if (closed) return; closed = true; dismissReference() },
      }
    },
    edit: context => { if (!options.openDetail?.(detailContext(context), 'yaml')) return openSourceEditor(context, options) },
  }
  return runtime
}

function showReference(context: Context, options: OpenApiPluginOptions, onClose: () => void): () => void {
  const doc = context.container.ownerDocument
  const previous = doc.activeElement as HTMLElement | null
  const dialog = doc.createElement('dialog'); dialog.className = 'md-openapi-dialog'; dialog.setAttribute('aria-label', openapiStrings.dialogLabel)
  const header = doc.createElement('header'); header.className = 'md-openapi-dialog-header'
  const heading = doc.createElement('h2'); heading.textContent = summarizeOpenApi(context.data).title
  const closeButton = button(doc, markdownStrings.close); closeButton.setAttribute('aria-label', openapiStrings.closeDocumentationLabel)
  header.append(heading, closeButton)
  const host = doc.createElement('div'); host.className = 'md-openapi-reference'
  dialog.append(header, host)
  copyMarkdownOverlayTheme(context.rootContainer ?? context.container, dialog)
  doc.body.append(dialog)
  const unregister = context.editor ? registerMarkdownOverlay(context.editor, dialog, { modal: true }) : () => {}
  let ended = false
  let disposeScalar: (() => void) | undefined
  const close = () => {
    if (ended) return
    ended = true; context.signal.removeEventListener('abort', close); disposeScalar?.(); unregister(); dialog.close(); dialog.remove()
    // Back in the editor through the view, which keeps its selection (as the source dialog does).
    if (context.editor && previous === context.editor.view.dom) { if (!context.editor.isDestroyed) context.editor.view.focus() }
    else if (previous?.isConnected) previous.focus({ preventScroll: true })
    onClose()
  }
  closeButton.onclick = close
  dialog.addEventListener('cancel', event => { event.preventDefault(); close() })
  context.signal.addEventListener('abort', close, { once: true })
  try { dialog.showModal() } catch { close(); return close }
  // Owns its loading/error/retry state; the frame code and Scalar bundle load lazily inside.
  disposeScalar = mountOpenApiReference(host, context.data, { ...options, allowRequests: context.data.mode !== 'reference' && context.data.mode !== 'download' && options.allowRequests !== false })
  return close
}
