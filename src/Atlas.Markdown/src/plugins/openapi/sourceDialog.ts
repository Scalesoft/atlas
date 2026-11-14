import { copyMarkdownOverlayTheme, createIconElement, markdownStrings, registerMarkdownOverlay, retainMarkdownDraft, takeMarkdownDraft, trackMarkdownComposition, type MarkdownIconName, type MarkdownModuleContext } from '../../core'
import { openapiStrings } from './runtimeStrings'
import { MAX_OPENAPI_SOURCE_BYTES, normalizeOpenApi, type OpenApiData } from './data'
import type { OpenApiPluginOptions } from './index'
import { mountOpenApiSourceEditor } from './sourceEditorLoader'
import styles from './style.css?inline'

type SourceEditor = Awaited<ReturnType<typeof mountOpenApiSourceEditor>>

export function openSourceEditor(context: MarkdownModuleContext<OpenApiData>, options: OpenApiPluginOptions = {}): () => void {
  const editor = context.editor
  if (!editor || !context.update || !editor.isEditable || context.signal.aborted) return () => {}
  const doc = context.container.ownerDocument
  const previous = doc.activeElement as HTMLElement | null
  const dialog = doc.createElement('dialog')
  dialog.className = 'md-openapi-source-dialog'
  dialog.setAttribute('aria-label', openapiStrings.editTitle)
  const style = doc.createElement('style'); style.textContent = styles
  const form = doc.createElement('form')
  const makeButton = (label: string, icon?: MarkdownIconName) => {
    const item = doc.createElement('button'); item.type = 'button'; item.className = 'md-btn'
    if (icon) item.append(createIconElement(doc, icon))
    item.append(doc.createTextNode(label))
    return item
  }
  const header = doc.createElement('header'); header.className = 'md-openapi-source-header'
  const heading = doc.createElement('h2'); heading.textContent = openapiStrings.editTitle
  // A draft kept when writing stopped under the last dialog of this definition.
  const kept = takeMarkdownDraft<OpenApiData>(editor, 'openapi', context.data)
  const headerActions = doc.createElement('div')
  const maximize = makeButton(openapiStrings.maximizeLabel, 'maximize-2')
  const dismiss = makeButton(openapiStrings.closeEditorLabel, 'x')
  for (const item of [maximize, dismiss]) { item.setAttribute('aria-label', item.textContent!); item.title = item.textContent!; item.lastChild?.remove() }
  headerActions.append(maximize, dismiss); header.append(heading, headerActions)
  const metadata = doc.createElement('div'); metadata.className = 'md-openapi-source-metadata'
  const nameLabel = doc.createElement('label'); nameLabel.textContent = openapiStrings.filenameLabel
  const filename = doc.createElement('input'); filename.value = kept?.filename ?? context.data.filename; filename.maxLength = 255; nameLabel.append(filename)
  const fileLabel = doc.createElement('label'); fileLabel.textContent = openapiStrings.uploadLabel
  const file = doc.createElement('input'); file.type = 'file'; file.accept = '.yaml,.yml,.json,application/json,application/yaml,text/yaml'
  // The native field says „Choose File“ in the browser's language: hidden (still focusable for a screen reader), a Czech button opens it.
  file.className = 'md-openapi-file-input'; file.tabIndex = -1
  const chooser = doc.createElement('span'); chooser.className = 'md-openapi-file-chooser'
  const pickFile = doc.createElement('button'); pickFile.type = 'button'; pickFile.className = 'md-btn'; pickFile.textContent = openapiStrings.chooseFileLabel; pickFile.onclick = () => file.click()
  const chosen = doc.createElement('span'); chosen.className = 'md-openapi-file-name'; chosen.textContent = openapiStrings.noFileChosen
  chooser.append(pickFile, chosen); fileLabel.append(file, chooser)
  metadata.append(nameLabel, fileLabel)
  const modes = doc.createElement('div'); modes.className = 'md-openapi-source-modes'; modes.setAttribute('role', 'group'); modes.setAttribute('aria-label', openapiStrings.modeGroupLabel)
  let mode = kept?.mode ?? context.data.mode ?? 'interactive'
  const choices = [
    ['download', openapiStrings.downloadOnlyLabel, 'download'],
    ['reference', openapiStrings.docsOnlyLabel, 'book-open'],
    ['interactive', openapiStrings.tryItLabel, 'code'],
  ] as const
  const modeButtons = choices.map(([value, label, icon]) => {
    const item = makeButton(label, icon)
    item.setAttribute('aria-pressed', String(mode === value))
    item.onclick = () => { mode = value; modeButtons.forEach((entry, index) => entry.setAttribute('aria-pressed', String(choices[index]![0] === mode))) }
    modes.append(item)
    return item
  })
  modes.addEventListener('keydown', event => {
    const index = modeButtons.indexOf(event.target as HTMLButtonElement)
    if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
    event.preventDefault()
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? modeButtons.length - 1 : (index + (event.key === 'ArrowRight' ? 1 : -1) + modeButtons.length) % modeButtons.length
    modeButtons[next]!.focus(); modeButtons[next]!.click()
  })
  const sourceLabel = doc.createElement('div'); sourceLabel.className = 'md-openapi-source-label'; sourceLabel.textContent = openapiStrings.sourceLabel
  const host = doc.createElement('div'); host.className = 'md-openapi-source-host'
  const error = doc.createElement('p'); error.className = 'md-openapi-source-error'; error.setAttribute('role', 'alert')
  const actions = doc.createElement('footer'); actions.className = 'md-openapi-source-actions'
  const hint = doc.createElement('span'); hint.textContent = openapiStrings.draftHint
  const cancel = makeButton(markdownStrings.cancel), apply = makeButton(markdownStrings.apply); apply.type = 'submit'; apply.className = 'md-btn-primary'; apply.disabled = true
  actions.append(hint, cancel, apply)
  form.append(header, metadata, modes, sourceLabel, host, error, actions); dialog.append(style, form)
  copyMarkdownOverlayTheme(context.rootContainer ?? context.container, dialog)
  doc.body.append(dialog)
  const unregister = registerMarkdownOverlay(editor, dialog, { modal: true })
  const composition = trackMarkdownComposition(form)
  let active = true
  let sourceEditor: SourceEditor | undefined
  let draft = kept?.source ?? context.data.source
  let importGeneration = 0
  let imported = false
  const observer = new (doc.defaultView!.MutationObserver)(() => { if (!editor.isEditable) close() })
  const close = () => {
    if (!active) return
    active = false
    // Writing stopped with the dialog open: what was typed is kept for the next edit, announced, and nothing is written.
    const unsaved = { ...context.data, filename: filename.value, source: sourceEditor?.getValue() ?? draft, mode }
    if (JSON.stringify(unsaved) !== JSON.stringify({ ...context.data, mode: context.data.mode ?? 'interactive' })) retainMarkdownDraft(editor, 'openapi', openapiStrings.editTitle, context.data, unsaved)
    observer.disconnect(); composition.destroy(); sourceEditor?.destroy(); unregister()
    context.signal.removeEventListener('abort', close); editor.off('destroy', close)
    dialog.close(); dialog.remove()
    // Back in the editor through the view, which writes its selection to the page first: a plain focus() lets the browser's own caret
    // replace the selected card (P7).
    if (previous === editor.view.dom) { if (!editor.isDestroyed) editor.view.focus() }
    else if (previous?.isConnected) previous.focus({ preventScroll: true })
  }
  observer.observe(editor.view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
  editor.on('destroy', close)
  context.signal.addEventListener('abort', close, { once: true })
  cancel.onclick = dismiss.onclick = close
  dialog.addEventListener('cancel', event => { event.preventDefault(); if (!composition.active()) close() })
  maximize.onclick = () => {
    const expanded = dialog.classList.toggle('is-maximized')
    const label = expanded ? openapiStrings.restoreLabel : openapiStrings.maximizeLabel
    maximize.setAttribute('aria-label', label); maximize.title = label
    maximize.replaceChildren(createIconElement(doc, expanded ? 'minimize-2' : 'maximize-2'))
    sourceEditor?.layout()
  }
  filename.addEventListener('input', () => { importGeneration++; error.textContent = ''; sourceEditor?.setFilename(filename.value) })
  file.onchange = async () => {
    const generation = ++importGeneration
    imported = false
    const selected = file.files?.[0]
    chosen.textContent = selected?.name ?? openapiStrings.noFileChosen
    if (!selected || !active) return
    if (selected.size > MAX_OPENAPI_SOURCE_BYTES) { error.textContent = openapiStrings.sourceTooLargeError; return }
    try {
      const text = await selected.text()
      if (!active || generation !== importGeneration || context.signal.aborted || !editor.isEditable) return
      draft = text; filename.value = selected.name
      sourceEditor?.setValue(text, selected.name)
      // A selected file remains editable even when it needs repairs before saving.
      imported = true
      normalizeOpenApi({ version: 1, filename: filename.value, source: draft })
      error.textContent = ''
    } catch (cause) { if (active && generation === importGeneration) error.textContent = (cause as Error).message }
  }
  form.onsubmit = event => {
    event.preventDefault()
    if (composition.active() || !sourceEditor) return
    if (!active || context.signal.aborted || editor.isDestroyed || !editor.isEditable) { close(); return }
    try {
      context.update!(normalizeOpenApi({ version: 1, filename: filename.value, source: sourceEditor.getValue(), mode,
        ...(imported && options.uploaderName?.trim() ? { uploadedBy: options.uploaderName.trim() } : context.data.uploadedBy ? { uploadedBy: context.data.uploadedBy } : {}),
      }))
      close()
    } catch (cause) { error.textContent = (cause as Error).message; sourceEditor.focus() }
  }
  try { dialog.showModal() } catch { close(); return close }
  filename.focus({ preventScroll: true })
  // Shows a static skeleton of the source at once; Monaco replaces it when its chunk arrives.
  void mountOpenApiSourceEditor(host, {
    source: draft, filename: filename.value,
    onChange(value) { if (active) { draft = value; importGeneration++; error.textContent = '' } },
  }).then(mounted => {
    if (!active) { mounted.destroy(); return }
    sourceEditor = mounted
    // File reads can finish while Monaco itself is loading.
    mounted.setValue(draft, filename.value)
    apply.disabled = false
  }).catch(cause => {
    if (!active) return
    const status = doc.createElement('p'); status.setAttribute('role', 'status'); status.textContent = openapiStrings.editorLoadFailed; host.append(status)
    error.textContent = (cause as Error).message
  })
  return close
}
