import { markdownModuleUrl } from '../modules/forms'
import { createIconElement, legacyIconName } from '../../ui/iconMarkup'
import { markdownStrings } from '../../ui/strings'
import { suggestionsStrings } from '../../ui/strings/suggestions'

export interface MarkdownSuggestionItem {
  id: string
  title: string
  description?: string
  icon?: string
  avatar?: string
  disabled?: boolean
  /** The current state (a block's own kind): marked with a check, never the preselected row. */
  checked?: boolean
  /** The key that does the same without the menu („Ctrl+Alt+2“), shown at the row's end. */
  shortcut?: string
  /** Deletes or removes something: a red row. */
  destructive?: boolean
  /** A line above the row parts its group from the rows before (not above the first row). */
  separated?: boolean
}
export type SuggestionStatus = 'idle' | 'loading' | 'ready' | 'error'
let nextList = 0

/** Framework-neutral presentation used by typing suggestions and hosted pickers. */
export function createSuggestionList(options: {
  document: Document; label: string; onSelect: (id: string) => void
  testId?: string; emptyText?: string; onActiveChange?: (id: string) => void
  /** A short visible heading of a command list ("Vložit"); decorative, the listbox keeps its `label`. */
  heading?: string
}) {
  const doc = options.document, element = doc.createElement('div')
  element.className = 'md-suggestion-list slash-menu'
  element.id = `md-suggestions-${++nextList}`
  element.setAttribute('role', 'listbox'); element.setAttribute('aria-label', options.label)
  let heading: HTMLElement | undefined
  if (options.heading) {
    heading = doc.createElement('div'); heading.className = 'md-suggestion-heading'; heading.setAttribute('aria-hidden', 'true')
    heading.textContent = options.heading
    element.classList.add('has-heading'); element.append(heading)
  }
  element.dataset.testid = options.testId ?? 'suggestion-list'
  let items: MarkdownSuggestionItem[] = [], selected = -1
  let retryAction: (() => void) | undefined
  let rows: HTMLButtonElement[] = []
  const active = () => {
    rows.forEach((row, index) => { row.setAttribute('aria-selected', String(index === selected)); row.classList.toggle('on', index === selected) })
    const id = rows[selected]?.id ?? ''
    element.dataset.activeOption = id; options.onActiveChange?.(id)
  }
  const select = (index: number) => {
    const item = items[index]
    if (item && !item.disabled) options.onSelect(item.id)
  }
  const reveal = () => {
    const row = rows[selected]; if (!row) return
    const bounds = element.getBoundingClientRect(), item = row.getBoundingClientRect()
    const top = bounds.top + element.clientTop, bottom = top + element.clientHeight
    if (item.top < top) element.scrollTop -= top - item.top
    else if (item.bottom > bottom) element.scrollTop += item.bottom - bottom
  }
  const clear = () => { items = []; rows = []; selected = -1; retryAction = undefined; element.replaceChildren(...(heading ? [heading] : [])); active() }
  const status = (state: SuggestionStatus, message?: string, retry?: () => void) => {
    clear(); element.setAttribute('aria-busy', String(state === 'loading'))
    const text = doc.createElement('div'); text.className = 'empty'
    text.setAttribute('role', state === 'error' ? 'alert' : 'status')
    text.textContent = message ?? (state === 'loading' ? markdownStrings.loading : options.emptyText ?? suggestionsStrings.emptyResults)
    element.append(text)
    if (retry) {
      const button = doc.createElement('button'); button.type = 'button'; button.textContent = markdownStrings.retry; button.className = 'row'
      retryAction = retry; button.tabIndex = -1; button.id = `${element.id}-retry`
      button.setAttribute('role', 'option'); button.setAttribute('aria-selected', 'true')
      element.dataset.activeOption = button.id; options.onActiveChange?.(button.id)
      button.addEventListener('mousedown', event => event.preventDefault()); button.addEventListener('click', retry); element.append(button)
    }
  }
  return {
    element,
    update(next: MarkdownSuggestionItem[]) {
      clear(); element.setAttribute('aria-busy', 'false'); element.scrollTop = 0
      items = next
      if (!next.length) { status('ready'); return }
      rows = next.map((item, index) => {
        const row = doc.createElement('button'); row.type = 'button'; row.tabIndex = -1
        if (item.separated && index) { const line = doc.createElement('div'); line.className = 'sep'; line.setAttribute('aria-hidden', 'true'); element.append(line) }
        row.className = item.destructive ? 'row danger' : 'row'; row.id = `${element.id}-opt-${index}`; row.setAttribute('role', 'option')
        row.setAttribute('aria-disabled', String(!!item.disabled)); row.disabled = !!item.disabled
        const icon = doc.createElement('span'); icon.className = 'ic'; icon.setAttribute('aria-hidden', 'true')
        let avatarUrl = ''
        try { avatarUrl = markdownModuleUrl(item.avatar, true) } catch { /* Keep the readable initials for an invalid host image. */ }
        if (avatarUrl) {
          const avatar = doc.createElement('img'); avatar.src = avatarUrl; avatar.alt = ''; icon.append(avatar)
        } else if (item.icon?.startsWith('svg:')) {
          icon.append(createIconElement(doc, legacyIconName(item.icon.slice(4)), 18))
        } else icon.textContent = item.icon ?? item.title.slice(0, 1)
        const body = doc.createElement('span'); body.className = 'body'
        const title = doc.createElement('span'); title.className = 't'; title.textContent = item.title; body.append(title)
        if (item.description) { const description = doc.createElement('span'); description.className = 'd'; description.textContent = item.description; body.append(description) }
        row.append(icon, body)
        if (item.shortcut) { const keys = doc.createElement('kbd'); keys.className = 'k'; keys.textContent = item.shortcut; row.append(keys); row.setAttribute('aria-keyshortcuts', item.shortcut) }
        if (item.checked) { row.setAttribute('aria-checked', 'true'); const check = createIconElement(doc, 'check', 16); check.classList.add('ck'); row.append(check) }
        row.addEventListener('mousedown', event => event.preventDefault())
        row.addEventListener('click', () => select(index))
        row.addEventListener('mouseenter', () => { if (!item.disabled) { selected = index; active() } })
        element.append(row); return row
      })
      const preselect = items.findIndex(item => !item.disabled && !item.checked)
      selected = preselect >= 0 ? preselect : items.findIndex(item => !item.disabled); active()
    },
    setStatus: status,
    onKeyDown(event: KeyboardEvent): boolean {
      if (event.isComposing || event.keyCode === 229) return false
      if (retryAction && event.key === 'Enter') { retryAction(); return true }
      const enabled = items.map((item, index) => item.disabled ? -1 : index).filter(index => index >= 0)
      if (!enabled.length) return false
      const index = enabled.indexOf(selected)
      if (event.key === 'Enter') { select(selected); return true }
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? enabled.length - 1
        : event.key === 'ArrowDown' ? (index + 1) % enabled.length
          : event.key === 'ArrowUp' ? (index - 1 + enabled.length) % enabled.length : undefined
      if (next === undefined) return false
      selected = enabled[next]!; active(); reveal(); return true
    },
    destroy() { clear(); element.remove() },
  }
}
