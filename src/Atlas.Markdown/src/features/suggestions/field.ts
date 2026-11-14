import { createSuggestionList, type MarkdownSuggestionItem } from './list'
import { createSuggestionSearch } from './search'

/**
 * A panel's search field over the editor's suggestion list (P8, P3), for a picker built in the DOM (YouTrack „Vložit úkol“, a contact
 * from the directory): the search runs as one types (debounced), the first row is preselected, Enter and Tab take it, also when they
 * come before the answer (the answer's first row then), the arrows move. Esc in a field with text clears it (the field is marked
 * `data-md-own-escape` meanwhile, so the panel leaves it that key); the next Esc is the panel's. The list goes where the caller puts
 * `element`; the rows of the last answer stay while the next one loads.
 */
export function createSuggestionField<T>(options: {
  input: HTMLInputElement
  label: string
  testId?: string
  emptyText?: string
  errorText?: string
  search: (query: string, context: { signal: AbortSignal }) => Promise<readonly T[]> | readonly T[]
  item: (value: T, index: number) => MarkdownSuggestionItem
  pick: (value: T) => void
  debounceMs?: number
}) {
  const { input } = options
  let values: readonly T[] = [], loading = false, pending = false, skeleton: ReturnType<typeof setTimeout> | undefined
  const debounceMs = options.debounceMs ?? 150
  const list = createSuggestionList({ document: input.ownerDocument, label: options.label, testId: options.testId, emptyText: options.emptyText,
    onSelect: id => { const value = values[Number(id)]; if (value !== undefined) options.pick(value) },
    onActiveChange: id => { if (id) input.setAttribute('aria-activedescendant', id); else input.removeAttribute('aria-activedescendant') } })
  // D7: the list keeps its room from the start (suggestions.css), so the panel never grows in steps while one types.
  list.element.classList.add('md-suggestion-field-list')
  for (const [name, value] of [['role', 'combobox'], ['aria-autocomplete', 'list'], ['aria-controls', list.element.id], ['autocomplete', 'off']] as const) input.setAttribute(name, value)
  const take = () => list.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }))
  const search = createSuggestionSearch<T>({
    search: async (query, context) => [...await options.search(query, context)], debounceMs, limit: 30,
    onChange: ({ status, items }) => {
      loading = status === 'loading'
      clearTimeout(skeleton)
      if (status === 'idle') { values = []; pending = false; list.setStatus('idle', ''); return }
      // D7: the sized row skeleton only when the answer is not in 100 ms after the request starts (after the debounce); faster shows directly.
      if (status === 'loading') { if (!values.length) { list.setStatus('idle', ''); skeleton = setTimeout(() => list.setStatus('loading'), debounceMs + 100) } return }
      if (status === 'error') { values = []; pending = false; list.setStatus('error', options.errorText, () => search.retry()); return }
      values = items
      list.update(items.map(options.item))
      if (pending) { pending = false; take() }
    },
  })
  const typed = () => {
    pending = false
    if (input.value) input.setAttribute('data-md-own-escape', ''); else input.removeAttribute('data-md-own-escape')
    search.query(input.value.trim())
  }
  const keydown = (event: KeyboardEvent) => {
    if (event.isComposing || event.keyCode === 229 || event.altKey || event.ctrlKey || event.metaKey) return
    if (event.key === 'Escape') {
      if (!input.value) return
      event.preventDefault(); event.stopPropagation()
      input.value = ''; typed()
      return
    }
    if (['ArrowDown', 'ArrowUp'].includes(event.key)) { if (list.onKeyDown(event)) event.preventDefault(); return }
    if (event.key !== 'Enter' && !(event.key === 'Tab' && !event.shiftKey && input.value.trim())) return
    if (!input.value.trim()) { if (event.key === 'Enter') event.preventDefault(); return }
    event.preventDefault()
    // Typed faster than the answer: the answer's first row (P8).
    if (loading) pending = true; else take()
  }
  input.addEventListener('input', typed)
  input.addEventListener('keydown', keydown)
  return {
    element: list.element,
    /** Starts with `query` in the field (a picker opened with the typed text). */
    query(query: string) { input.value = query; typed() },
    retry: () => search.retry(),
    destroy() { clearTimeout(skeleton); search.destroy(); list.destroy(); input.removeEventListener('input', typed); input.removeEventListener('keydown', keydown) },
  }
}
