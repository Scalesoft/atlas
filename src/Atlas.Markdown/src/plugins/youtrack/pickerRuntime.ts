import { normalizeYouTrackIssue, type YouTrackIssue } from './types'
import { copyMarkdownOverlayTheme, createIconElement, createSuggestionField, markdownStrings, openMarkdownContextPopover } from '../../core'
import { youtrackStrings } from './pickerStrings'
import type { YouTrackPickerContext } from './picker'

let pickerSequence = 0

/**
 * Shared anchored editor panel, with a standalone dialog fallback. Search never mutates the document. The field is the editor's
 * shared search field (createSuggestionField, P8): results as one types, the first preselected, Enter or Tab takes it, the first Esc
 * clears the text and the next one closes.
 */
export function pickYouTrackIssue({ client, signal, ownerDocument: document, themeElement, registerOverlay, initialQuery, editor, anchor, positionAnchor, helpText, host }: YouTrackPickerContext): Promise<YouTrackIssue | null> {
  if (signal.aborted || editor && (editor.isDestroyed || !editor.isEditable)) return Promise.resolve(null)
  return new Promise(resolve => {
    const previousFocus = document.activeElement as HTMLElement | null
    const anchored = !!editor && !!anchor
    const nativeDialog = anchored ? undefined : document.createElement('dialog')
    const dialog = nativeDialog ?? document.createElement('div')
    let closePanel: (() => void) | undefined
    const releaseOverlay = nativeDialog ? registerOverlay?.(nativeDialog) : undefined
    dialog.setAttribute('aria-label', youtrackStrings.pickerLabel)
    if (nativeDialog) dialog.setAttribute('aria-modal', 'true')
    dialog.dataset.testid = 'youtrack-picker'
    dialog.className = nativeDialog ? 'md-picker' : 'md-picker-content'
    if (themeElement) copyMarkdownOverlayTheme(themeElement, dialog)
    const heading = document.createElement('h2')
    heading.textContent = youtrackStrings.pickerHeading
    heading.className = 'md-picker-heading'
    const close = document.createElement('button')
    close.type = 'button'
    close.className = 'md-picker-close'
    close.setAttribute('aria-label', markdownStrings.close)
    close.append(createIconElement(document, 'x', 16))
    heading.append(close)
    const label = document.createElement('label')
    label.textContent = youtrackStrings.queryLabel
    label.className = 'md-picker-label'
    const input = document.createElement('input')
    input.type = 'search'
    input.placeholder = youtrackStrings.queryPlaceholder
    input.className = 'md-picker-input'
    label.append(input)
    const id = `md-youtrack-picker-${++pickerSequence}`
    const help = document.createElement('p')
    help.id = `${id}-help`
    help.className = 'md-picker-status'
    help.textContent = helpText ?? ''
    help.hidden = !helpText
    if (helpText) input.setAttribute('aria-describedby', help.id)
    const footer = document.createElement('footer')
    footer.className = 'md-picker-footer'
    const cancel = document.createElement('button')
    cancel.type = 'button'
    cancel.textContent = markdownStrings.cancel
    cancel.className = 'md-btn'
    footer.append(cancel)
    if (nativeDialog) dialog.append(heading)
    let settled = false
    const field = createSuggestionField<YouTrackIssue>({ input, label: youtrackStrings.resultsLabel, testId: 'youtrack-results',
      emptyText: youtrackStrings.searchResultsStatus(0), errorText: youtrackStrings.searchFailedStatus,
      async search(query, { signal: requestSignal }) {
        const isReference = /^[A-Za-z0-9][A-Za-z0-9_]*-[0-9]+$/.test(query) || /^https?:\/\//i.test(query)
        const resolved = client.resolve && isReference ? await client.resolve(query, { signal: requestSignal }) : undefined
        const response = client.resolve && isReference ? (resolved ? [resolved] : []) : await client.search(query, { signal: requestSignal })
        if (!Array.isArray(response)) throw new TypeError('Invalid YouTrack search response.')
        return response.map(normalizeYouTrackIssue)
      },
      item: (issue, index) => ({ id: String(index), title: `${issue.key} — ${issue.title}`, ...issue.state ? { description: issue.state } : {} }),
      pick: issue => finish(issue) })
    dialog.append(label, help, field.element, footer)

    const finish = (issue: YouTrackIssue | null) => {
      if (settled) return
      settled = true
      field.destroy()
      signal.removeEventListener('abort', onAbort)
      releaseOverlay?.()
      closePanel?.()
      dialog.remove()
      if (nativeDialog && previousFocus?.isConnected) previousFocus.focus({ preventScroll: true })
      resolve(issue)
    }
    const onAbort = () => finish(null)
    if (nativeDialog) signal.addEventListener('abort', onAbort, { once: true })
    cancel.onclick = () => finish(null)
    close.onclick = () => finish(null)
    if (nativeDialog) {
      nativeDialog.oncancel = event => { event.preventDefault(); finish(null) }
      nativeDialog.onclose = () => finish(null)
    }
    dialog.onkeydown = event => {
      // The field took the first Esc (it cleared its text, P3); this one closes, before a search field's own native clear.
      if (event.key !== 'Escape' || event.isComposing || event.defaultPrevented) return
      event.preventDefault()
      event.stopPropagation()
      finish(null)
    }

    if (host) {
      host.container.append(dialog); closePanel = host.close; host.cleanup = () => finish(null)
    } else if (editor && anchor) {
      let rendered = false
      closePanel = openMarkdownContextPopover({
        editor, anchor, positionAnchor, title: youtrackStrings.pickerLabel,
        className: 'md-youtrack-picker', trackSelection: false, signal,
        render(container) {
          rendered = true
          container.append(dialog)
          return () => finish(null)
        },
      })
      if (!rendered) { finish(null); return }
    } else if (nativeDialog) {
      document.body.append(nativeDialog)
      // jsdom does not implement showModal.
      if (typeof nativeDialog.showModal === 'function') nativeDialog.showModal()
      else nativeDialog.setAttribute('open', '')
    }
    if (initialQuery?.trim()) field.query(initialQuery.trim())
    input.focus()
  })
}
