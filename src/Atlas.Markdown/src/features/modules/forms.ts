import { registerMarkdownOverlay } from '../../core/overlay'
import { copyMarkdownOverlayTheme } from '../../core/overlayTheme'
import type { Editor } from '@tiptap/core'
import { openMarkdownContextPopover, type MarkdownPopoverAnchor } from './contextToolbar'
import { trackMarkdownComposition } from './composition'
import { createIconElement } from '../../ui/iconMarkup'
import { markdownStrings } from '../../ui/strings'
import { modulesStrings } from '../../ui/strings/modules'

export interface MarkdownModuleField {
  key: string
  label: string
  type?: 'text' | 'textarea' | 'number' | 'select'
  options?: readonly string[]
  min?: number
  max?: number
  step?: number
  /** Override the default full-width text/textarea or half-width numeric field. */
  wide?: boolean
  /** Visible textarea rows; defaults to five. */
  rows?: number
  /** Optional fields with the same section title share one collapsed details group. */
  section?: string
}

/**
 * A „Použít“ form of a panel (D3, P5): a change marks it `data-md-apply`, so a click outside or the focus leaving submits it as „Použít“
 * does (openMarkdownContextPopover: an invalid form stays open with its error); Ctrl+Enter (⌘Enter) is „Použít“. Returns the mark, for a
 * change no input event tells (a tile or a choice clicked).
 */
export function trackMarkdownApplyForm(form: HTMLFormElement): () => void {
  const changed = () => { form.dataset.mdApply = '' }
  form.addEventListener('input', changed); form.addEventListener('change', changed)
  form.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey || event.isComposing) return
    event.preventDefault(); event.stopPropagation(); form.requestSubmit()
  })
  return changed
}

/** A real labelled form, with errors kept in the dialog and no document mutation until „Použít“ (or, anchored, until it is left). */
export function openMarkdownModuleForm(options: {
  editor: Editor; title: string; fields: readonly MarkdownModuleField[]; values: Record<string, unknown>
  save: (values: Record<string, string>) => void; signal?: AbortSignal
  anchor?: HTMLElement
  /** Where an anchored form stands when not at `anchor` (a place in the text, its anchor the editor). */
  positionAnchor?: MarkdownPopoverAnchor
  /** Shown under the fields, e.g. where the values the form does not hold are changed. */
  note?: Node
  /** The values are a draft not applied yet (given back by a question about it): a click outside applies them as a change does (D3). */
  draft?: boolean
}): () => void {
  const { editor } = options
  if (options.signal?.aborted || editor.isDestroyed || !editor.isEditable) return () => {}
  const doc = editor.view.dom.ownerDocument
  const previous = doc.activeElement as HTMLElement | null
  const dialog = doc.createElement('dialog')
  dialog.className = 'md-module-dialog'
  dialog.setAttribute('aria-label', options.title)
  const form = doc.createElement('form')
  const composition = trackMarkdownComposition(form)
  let closeButton: HTMLButtonElement | undefined
  if (!options.anchor) {
    // Same grammar as the anchored form: title on the left, an `x` close on the right — no
    // second, redundant title-less header underneath from the browser's own dialog chrome.
    const header = doc.createElement('div'); header.className = 'md-module-dialog-header'
    const title = doc.createElement('h3'); title.textContent = options.title
    closeButton = doc.createElement('button'); closeButton.type = 'button'; closeButton.className = 'md-picker-close'
    closeButton.setAttribute('aria-label', markdownStrings.close)
    closeButton.append(createIconElement(doc, 'x', 16))
    header.append(title, closeButton); form.append(header)
  }
  const fields = doc.createElement('div'); fields.className = 'md-context-fields'; form.append(fields)
  const controls = new Map<string, HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>()
  const sections = new Map<string, { root: HTMLDetailsElement; fields: HTMLElement }>()
  for (const field of options.fields) {
    let parent: HTMLElement = fields
    if (field.section) {
      let section = sections.get(field.section)
      if (!section) {
        const root = doc.createElement('details'); root.className = 'md-context-field-section md-context-field-wide'
        const summary = doc.createElement('summary'); summary.textContent = field.section
        const content = doc.createElement('div'); content.className = 'md-context-fields'
        root.append(summary, content); fields.append(root)
        section = { root, fields: content }; sections.set(field.section, section)
      }
      parent = section.fields
    }
    const label = doc.createElement('label'); label.textContent = field.label
    const wide = field.wide ?? (field.type === 'textarea' || field.type === 'text' || !field.type)
    label.className = `md-context-field${wide ? ' md-context-field-wide' : ''}`
    let input: HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement
    if (field.type === 'textarea') { input = doc.createElement('textarea'); input.rows = Math.max(1, Math.min(20, Math.floor(field.rows ?? 5))) }
    else if (field.type === 'select') {
      input = doc.createElement('select')
      for (const value of field.options ?? []) { const option = doc.createElement('option'); option.value = value; option.textContent = value; input.append(option) }
    } else {
      input = doc.createElement('input'); input.type = field.type ?? 'text'
      if (field.min !== undefined) input.min = String(field.min)
      if (field.max !== undefined) input.max = String(field.max)
      if (field.step !== undefined) input.step = String(field.step)
    }
    input.name = field.key; input.value = String(options.values[field.key] ?? '')
    label.append(input); parent.append(label); controls.set(field.key, input)
  }
  // A changed value is a change to apply: a click outside or the focus leaving the anchored form applies it (D3, contextToolbar.ts).
  const changed = trackMarkdownApplyForm(form)
  if (options.draft) changed()
  form.addEventListener('invalid', event => {
    const section = (event.target as HTMLElement).closest('details')
    if (section) section.open = true
  }, true)
  const focusInitial = () => {
    const input = [...controls.values()].find(input => !input.closest('details:not([open])'))
    const first = input ?? sections.values().next().value?.root.querySelector('summary')
    first?.focus({ preventScroll: true })
    // A prefilled value (a date, an ID) is replaced by what is typed, not appended to.
    if (first?.tagName === 'INPUT') (first as HTMLInputElement).select()
  }
  if (options.note) form.append(options.note)
  const error = doc.createElement('p'); error.setAttribute('role', 'alert'); form.append(error)
  const showError = (cause: unknown) => {
    // A normalization error may concern an optional field: never hide the place to fix it.
    sections.forEach(section => { section.root.open = true })
    error.textContent = cause instanceof Error ? cause.message : modulesStrings.checkValuesEntered
  }
  const actions = doc.createElement('div'); actions.className = 'md-module-actions'
  const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.textContent = markdownStrings.cancel; cancel.className = 'md-btn'
  const save = doc.createElement('button'); save.type = 'submit'; save.textContent = markdownStrings.apply; save.className = 'md-btn-primary'
  actions.append(cancel, save); form.append(actions); dialog.append(form)
  if (options.anchor) {
    return openMarkdownContextPopover({ editor, anchor: options.anchor, positionAnchor: options.positionAnchor, title: options.title, signal: options.signal,
      className: 'md-module-form-popover', render(container, close) {
        container.append(form)
        let active = true
        cancel.onclick = close
        form.onsubmit = event => {
          event.preventDefault()
          if (composition.active()) return
          try {
            if (!active || options.signal?.aborted || editor.isDestroyed || !editor.isEditable) { close(); return }
            options.save(Object.fromEntries([...controls].map(([key, input]) => [key, input.value])))
            close()
          } catch (cause) { showError(cause) }
        }
        focusInitial()
        return () => { active = false; composition.destroy() }
      },
    })
  }
  copyMarkdownOverlayTheme(editor.view.dom, dialog)
  doc.body.append(dialog)
  const unregister = registerMarkdownOverlay(editor, dialog, { modal: true })
  let closed = false
  const Observer = doc.defaultView?.MutationObserver
  const observer = Observer ? new Observer(() => { if (!editor.isEditable) close() }) : undefined
  const close = () => {
    if (closed) return
    closed = true; options.signal?.removeEventListener('abort', close); observer?.disconnect(); editor.off('destroy', close)
    composition.destroy(); unregister(); dialog.remove()
    // The editor takes the keyboard back with its selection, so the edited element stays selected (P7).
    if (previous === editor.view.dom && !editor.isDestroyed) editor.view.focus(); else if (previous?.isConnected) previous.focus()
  }
  cancel.onclick = close
  if (closeButton) closeButton.onclick = close
  dialog.addEventListener('cancel', event => { event.preventDefault(); if (!composition.active()) close() })
  dialog.addEventListener('close', close)
  form.onsubmit = event => {
    event.preventDefault()
    if (composition.active()) return
    try {
      if (closed || options.signal?.aborted || editor.isDestroyed || !editor.isEditable) { close(); return }
      options.save(Object.fromEntries([...controls].map(([key, input]) => [key, input.value])))
      close()
    } catch (cause) { showError(cause) }
  }
  options.signal?.addEventListener('abort', close, { once: true })
  editor.on('destroy', close)
  observer?.observe(editor.view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
  if (options.signal?.aborted) close()
  else {
    try { dialog.showModal(); focusInitial() }
    catch (error) { close(); throw error }
  }
  return close
}

export function downloadMarkdownFile(doc: Document, name: string, content: Blob): void {
  const ownerUrl = doc.defaultView?.URL ?? URL
  const url = ownerUrl.createObjectURL(content)
  const anchor = doc.createElement('a'); anchor.href = url; anchor.download = name
  try { doc.body.append(anchor); anchor.click() }
  finally { anchor.remove(); setTimeout(() => ownerUrl.revokeObjectURL(url), 1000) }
}

export function markdownModuleUrl(value: unknown, optional = false): string {
  if (optional && (value === '' || value == null)) return ''
  if (typeof value !== 'string' || !value.trim()) throw new TypeError(modulesStrings.urlRequired)
  const url = new URL(value, 'https://editor.invalid/')
  if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password) throw new TypeError(modulesStrings.urlProtocolRequired)
  return value.trim()
}
