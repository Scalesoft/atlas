import { createIconElement, openMarkdownContextPopover, retainMarkdownDraft, takeMarkdownDraft, type MarkdownModuleContext, type MarkdownModuleRuntime } from '../../core'
import { attachmentsStrings } from './runtimeStrings'
import { attachmentSize, normalizeAttachment, type AttachmentData, type AttachmentsPluginOptions } from './data'
import styles from './style.css?inline'

type Context = MarkdownModuleContext<AttachmentData>
function openUpload(context: Context, options: AttachmentsPluginOptions, anchor: HTMLElement): () => void {
  if (!context.editor || !options.upload || !context.update || !context.editor.isEditable || context.signal.aborted) return () => {}
  return openMarkdownContextPopover({ editor: context.editor, anchor, title: context.data.url ? attachmentsStrings.replaceFileTitle : attachmentsStrings.uploadFileTitle, signal: context.signal, className: 'md-attachment-upload',
    render(panel, close) {
      const doc = panel.ownerDocument
      const label = doc.createElement('label'); label.className = 'md-attachment-file-picker'; label.textContent = attachmentsStrings.choosePlaceholder
      const input = doc.createElement('input'); input.type = 'file'; label.append(input); panel.append(label)
      const hint = doc.createElement('p'); hint.className = 'md-attachment-upload-hint'; hint.textContent = context.data.url ? attachmentsStrings.replaceHint : attachmentsStrings.newUploadHint; panel.append(hint)
      const status = doc.createElement('p'); status.setAttribute('role', 'status'); panel.append(status)
      const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.textContent = attachmentsStrings.cancelUpload; cancel.hidden = true; panel.append(cancel)
      let request: AbortController | undefined
      const abort = () => request?.abort()
      const editable = () => !!context.update && !!context.editor?.isEditable && !context.signal.aborted
      cancel.onclick = () => { abort(); input.disabled = false; cancel.hidden = true; status.textContent = attachmentsStrings.uploadCancelled; input.value = '' }
      input.onchange = () => {
        const file = input.files?.[0]; if (!file || !editable()) return
        abort(); const current = new AbortController(); request = current
        input.disabled = true; cancel.hidden = false; status.textContent = attachmentsStrings.uploading(file.name)
        void Promise.resolve().then(() => options.upload!.upload(file, { signal: current.signal })).then(result => {
          if (current.signal.aborted || !editable()) return
          const uploaded = normalizeAttachment({ size: file.size, mimeType: file.type || undefined, ...result, version: 1 })
          if (!uploaded.url) throw new TypeError(attachmentsStrings.uploadMissingUrl)
          context.update!({ ...uploaded, description: context.data.description }); close()
        }).catch(() => { if (!current.signal.aborted) status.textContent = attachmentsStrings.uploadFailedGeneric }).finally(() => {
          if (!current.signal.aborted) { input.disabled = false; cancel.hidden = true; input.value = '' }
        })
      }
      return abort
    },
  })
}
/** Turns `el` into an in-place editor: click or Enter swaps it for a field; Enter/blur saves, Esc cancels (Ctrl+Enter saves a multiline field). */
function editInPlace(context: Context, el: HTMLElement, label: string, current: () => string, save: (value: string) => void, multiline = false) {
  const doc = el.ownerDocument
  el.tabIndex = 0; el.setAttribute('role', 'button'); el.setAttribute('aria-label', label); el.dataset.editable = ''
  const start = (fromBlock = false) => {
    if (el.dataset.editing !== undefined) return
    el.dataset.editing = ''
    const field = doc.createElement(multiline ? 'textarea' : 'input') as HTMLInputElement; field.className = 'md-attachment-field'; field.value = takeMarkdownDraft<string>(context.editor!, `attachment:${label}`, context.data) ?? current(); field.setAttribute('aria-label', label)
    if (multiline) (field as unknown as HTMLTextAreaElement).rows = 3
    let done = false
    // From the keyboard (Enter, Esc) the keyboard stays on the field; a blur leaves it where the person went.
    const finish = (commit: boolean, keyboard = false) => {
      if (done) return; done = true
      delete el.dataset.editing; field.remove()
      if (commit && field.value !== current()) save(field.value)
      // Opened from the selected block (Enter), the keyboard goes back to the editor, as a diagram's or a formula's does; opened on the name itself, back to the name.
      if (keyboard && el.isConnected) {
        if (fromBlock) context.editor?.view.focus(); else el.focus({ preventScroll: true })
      }
    }
    // The right went with the field open: the render is replaced, so the text is kept for the next edit and announced.
    context.signal.addEventListener('abort', () => { if (!done && field.value !== current()) retainMarkdownDraft(context.editor!, `attachment:${label}`, label, context.data, field.value) }, { once: true })
    field.addEventListener('keydown', event => {
      event.stopPropagation()
      if (event.key === 'Escape') { event.preventDefault(); finish(false, true) }
      else if (event.key === 'Enter' && (!multiline || event.ctrlKey || event.metaKey)) { event.preventDefault(); finish(true, true) }
    })
    field.addEventListener('blur', () => finish(true))
    field.addEventListener('mousedown', event => event.stopPropagation())
    el.after(field); field.focus(); field.select()
  }
  // preventDefault marks the click as handled here, so the block does not also open its own editor.
  el.addEventListener('click', event => { event.preventDefault(); start() })
  el.addEventListener('keydown', event => { if (event.key === 'Enter' && event.target === el) { event.preventDefault(); event.stopPropagation(); start() } })
  el.addEventListener('md-attachment-edit', () => start(true))
}
async function uploadAttachment(file: File, options: AttachmentsPluginOptions, signal: AbortSignal): Promise<AttachmentData> {
  const uploaded = normalizeAttachment({ size: file.size, mimeType: file.type || undefined, ...await options.upload!.upload(file, { signal }), version: 1 })
  if (!uploaded.url) throw new TypeError(attachmentsStrings.uploadMissingUrl)
  return uploaded
}
export function createAttachmentsRuntime(options: AttachmentsPluginOptions): MarkdownModuleRuntime<AttachmentData> {
  return {
    // A click on the card only selects it; its name and description edit where they are clicked, Enter edits the name.
    editOnClick: false,
    edit(context) {
      // A new, empty card (/Příloha) goes straight to the file picker; one with a file edits its name. Cancelling leaves the empty card.
      const pick = context.data.url ? null : context.container.querySelector<HTMLButtonElement>('.md-attachment-pick[data-variant="primary"]')
      if (pick) { pick.click(); return }
      const name = context.container.querySelector<HTMLElement>('.md-attachment-name[data-editable]')
      if (!name) return
      name.focus({ preventScroll: true }); name.dispatchEvent(new Event('md-attachment-edit'))
    },
    render(initial) {
      let context = initial
      const { container, signal } = context; const doc = container.ownerDocument
      const root = doc.createElement('div'); root.className = 'md-attachment'
      const style = doc.createElement('style'); style.textContent = styles; container.append(style, root)
      const icon = doc.createElement('span'); icon.className = 'md-attachment-icon'; icon.setAttribute('aria-hidden', 'true')
      const body = doc.createElement('div'); body.className = 'md-attachment-body'; root.append(icon, body)
      const editable = !!context.update && !!context.editor?.isEditable && !signal.aborted
      const canUpload = !!options.upload && editable
      const error = doc.createElement('small'); error.className = 'md-attachment-error'; error.setAttribute('role', 'alert'); error.hidden = true
      const fail = (message: string) => { error.textContent = message; error.hidden = false }
      // Invalid input (empty name, bad URL) keeps the stored value on the card and says why.
      const save = (patch: Partial<AttachmentData>) => { try { const next = normalizeAttachment({ ...context.data, ...patch, version: 1 }); error.hidden = true; context.update!(next) } catch (cause) { fail((cause as Error).message) } }
      const title = doc.createElement('strong'); title.className = 'md-attachment-name'
      const metadata = doc.createElement('small'); metadata.className = 'md-attachment-meta'
      const description = doc.createElement('span'); description.className = 'md-attachment-description'
      const empty = doc.createElement('small'); empty.className = 'md-attachment-empty'; empty.textContent = attachmentsStrings.noFileAttached
      body.append(title, metadata, description, empty)
      if (editable) {
        description.dataset.placeholder = attachmentsStrings.addDescription
        editInPlace(context, description, attachmentsStrings.fieldDescription, () => context.data.description ?? '', value => save({ description: value || undefined }), true)
        editInPlace(context, title, attachmentsStrings.fieldFileName, () => context.data.name, value => save({ name: value }))
      }
      const link = doc.createElement('a'); link.className = 'md-attachment-download'
      const text = doc.createElement('span'); text.textContent = attachmentsStrings.download
      link.append(createIconElement(doc, 'download', 16), text)
      // Firefox treats a mouse down on a link in this non-editable island as caret placement: the
      // selection jumps to the document start and scrolls there, so the click lands elsewhere and
      // nothing downloads. The download needs neither focus nor a selection change.
      link.addEventListener('mousedown', event => event.preventDefault())
      const cleanups: (() => void)[] = []
      const picker = doc.createElement('input'); picker.type = 'file'
      const pick = doc.createElement('button'); pick.type = 'button'; pick.className = 'md-attachment-pick'
      const hint = doc.createElement('small'); hint.className = 'md-attachment-drop-hint'; hint.textContent = attachmentsStrings.dropHint
      const status = doc.createElement('small'); status.className = 'md-attachment-status'; status.setAttribute('role', 'status')
      const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.className = 'md-attachment-pick'; cancel.dataset.variant = 'secondary'; cancel.textContent = attachmentsStrings.cancelUpload; cancel.hidden = true
      // The upload outlives this render (an editability toggle re-renders): it runs on the node's lifetime, writes through
      // the node and keeps the description the node has when it completes.
      const node = context.nodeContext?.()
      const latest = () => { const position = context.getPos?.(), at = position === undefined ? undefined : context.editor?.state.doc.nodeAt(position); return (at?.type.name === 'attachment' ? at.attrs.data as AttachmentData : undefined) ?? context.data }
      let busy: AbortController | undefined, stopWatching: (() => void) | undefined
      // Settles at once on abort, also when the upload service ignores the signal.
      const settle = (current: AbortController) => {
        if (busy !== current) return
        busy = undefined; stopWatching?.(); stopWatching = undefined; delete root.dataset.busy; root.removeAttribute('aria-busy'); pick.disabled = false; cancel.hidden = true; picker.value = ''
        status.textContent = current.signal.aborted ? attachmentsStrings.uploadCancelled : ''
      }
      const send = (file: File | undefined) => {
        if (!file || !canUpload || busy) return
        const current = busy = new AbortController(); (node?.signal ?? signal).addEventListener('abort', () => current.abort(), { once: true })
        current.signal.addEventListener('abort', () => settle(current), { once: true })
        // Writing stops (a lock, a revoked right, offline): the upload is called off, so no file is left behind that nothing links to.
        stopWatching = node?.onEditableChange(writable => { if (!writable) current.abort() })
        error.hidden = true; root.dataset.busy = ''; root.setAttribute('aria-busy', 'true'); pick.disabled = true; cancel.hidden = false; status.textContent = attachmentsStrings.uploading(file.name)
        void uploadAttachment(file, options, current.signal).then(uploaded => { if (!current.signal.aborted && context.editor?.isEditable) {
          (node?.update ?? context.update)!({ ...uploaded, description: latest().description })
          // The button that held the focus is disabled meanwhile: the caret returns to the editor instead of the page body.
          if (!doc.activeElement || doc.activeElement === doc.body || root.contains(doc.activeElement)) context.editor?.view.focus()
        } })
          .catch(() => { if (!current.signal.aborted) fail(attachmentsStrings.uploadFailedGeneric) })
          .finally(() => settle(current))
      }
      if (canUpload) {
        picker.addEventListener('change', () => send(picker.files?.[0]))
        pick.addEventListener('click', event => { event.preventDefault(); picker.click() })
        cancel.addEventListener('click', event => { event.preventDefault(); busy?.abort() })
        body.append(pick, hint, status, cancel)
        root.addEventListener('dragover', event => { if (event.dataTransfer?.types.includes('Files')) { event.preventDefault(); event.stopPropagation(); root.dataset.dropping = '' } })
        root.addEventListener('dragleave', () => delete root.dataset.dropping)
        // A file dropped while another uploads is consumed here (send ignores it), never inserted as a block of its own.
        root.addEventListener('drop', event => { const file = event.dataTransfer?.files?.[0]; delete root.dataset.dropping; if (!file) return; event.preventDefault(); event.stopPropagation(); send(file) })
      }
      // Only an editable card says something went wrong: a reader (or the static renderer) has no alert region.
      if (editable) body.append(error)
      const url = doc.createElement('span'); url.className = 'md-attachment-url'
      // The same rows for a reader and a writer (J7.1): the address is read by everyone, edited only by a writer.
      const more = doc.createElement('details'); more.className = 'md-attachment-more'
      const summary = doc.createElement('summary'); summary.textContent = attachmentsStrings.more
      if (editable) editInPlace(context, url, attachmentsStrings.fieldFileUrl, () => context.data.url, value => save({ url: value.trim() }))
      more.append(summary, url); body.append(more)
      const replace = canUpload && context.toolbar
        ? context.toolbar.addButton({ label: attachmentsStrings.uploadFileTitle, icon: 'replace', onClick: anchor => cleanups.push(openUpload(context, options, anchor)) }) : undefined
      // Refreshed in place: an open field, the focus and an upload in progress survive a rename, an undo or a collaborator's edit.
      const refresh = () => {
        const { data } = context
        const extension = data.name.match(/\.([a-z0-9]{1,5})$/i)?.[1]?.toLocaleUpperCase()
        const picture = data.mimeType?.startsWith('image/') || /^(PNG|JPG|JPEG|WEBP|GIF|SVG)$/.test(extension ?? '')
        const spreadsheet = /^(XLS|XLSX|CSV|TSV|ODS)$/.test(extension ?? '')
        icon.replaceChildren(createIconElement(doc, picture ? 'image' : spreadsheet ? 'table' : 'paperclip', 28))
        // The file type is the extension itself; an English "FILE" placeholder said nothing.
        if (extension) { const badge = doc.createElement('span'); badge.className = 'md-attachment-extension'; badge.textContent = extension; icon.append(badge) }
        title.textContent = data.name
        metadata.textContent = [extension, data.size === undefined ? '' : attachmentSize(data.size)].filter(Boolean).join(' · ')
        // A writer's empty description is a placeholder line: a reader's holds its place, so the card keeps its size whatever the right.
        const reserved = !data.description && !editable
        description.textContent = data.description ?? (reserved ? '\u00a0' : ''); description.toggleAttribute('data-reserved', reserved)
        root.toggleAttribute('data-empty', !data.url); empty.hidden = !!data.url; hint.hidden = !!data.url
        if (data.url) { link.href = data.url; link.download = data.name; link.setAttribute('aria-label', attachmentsStrings.downloadAriaLabel(data.name)); root.append(link); if (canUpload) root.append(pick) }
        else { link.remove(); if (canUpload) hint.before(pick) }
        pick.dataset.variant = data.url ? 'secondary' : 'primary'; pick.textContent = data.url ? attachmentsStrings.replaceShort : attachmentsStrings.uploadFileTitle
        url.textContent = data.url || (editable ? attachmentsStrings.addUrl : '—')
        if (replace) { replace.title = data.url ? attachmentsStrings.replaceFileTitle : attachmentsStrings.uploadFileTitle; replace.setAttribute('aria-label', replace.title) }
      }
      refresh()
      return {
        update(next) { context = next; refresh() },
        destroy() { cleanups.splice(0).reverse().forEach(cleanup => cleanup()) },
      }
    },
  }
}
