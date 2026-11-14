import type { MarkdownReferenceProvider, MarkdownReferenceValue } from '../core/plugins/types'
import { createSuggestionList } from '../features/suggestions/list'
import { createSuggestionSearch } from '../features/suggestions/search'
import { toolbarStrings } from './strings/toolbar'

export type LinkTarget = MarkdownReferenceValue & { readonly detail?: string }
export type LinkSearch = ReturnType<typeof createLinkSearch>

/** An address rather than a page name: a scheme (https:, mailto:), an anchor or path, or a host name (www.…, example.cz/…). */
export const isLinkAddress = (value: string) => /^(?:[a-z][a-z\d+.-]*:|[#/?.]|www\.)/i.test(value) || /^[^\s/]+\.[a-z]{2,}(?:[/:?#]|$)/i.test(value)

/**
 * The link field's page search (UX WR3-3), loaded with the first link field of an editor whose reference providers search
 * (the pages plugin: the source and order of `[[`). It makes the field a combobox over the editor's suggestion list: the first
 * page is preselected, Enter links it, Enter typed before the answer takes its first page. An address stays an address, and a
 * bare host name gets https://. Lazy, so the page's eager editor code does not grow with it.
 */
export function createLinkSearch(options: {
  input: HTMLInputElement; label: HTMLElement; sources: readonly MarkdownReferenceProvider[]
  pick: (target: LinkTarget) => void; error: (message: string) => void
}) {
  const { input } = options
  let targets: readonly LinkTarget[] = [], pending = false, loading = false, shown = false
  const list = createSuggestionList({ document: input.ownerDocument, label: toolbarStrings.linkResultsLabel, testId: 'link-suggestions', emptyText: toolbarStrings.linkNoPage,
    onSelect: id => { const target = targets[Number(id)]; if (target) options.pick(target) },
    onActiveChange: id => { if (id) input.setAttribute('aria-activedescendant', id); else input.removeAttribute('aria-activedescendant') } })
  // The list's rows inside the field's own panel, not its floating frame.
  list.element.style.cssText = 'width:100%;max-height:240px;padding:0;border:0;box-shadow:none;background:transparent'
  list.element.hidden = true
  input.after(list.element)
  options.label.textContent = toolbarStrings.linkTargetLabel
  Object.assign(input, { type: 'text', placeholder: toolbarStrings.linkSearchPlaceholder, autocomplete: 'off' })
  for (const [name, value] of [['aria-label', toolbarStrings.linkTargetLabel], ['role', 'combobox'], ['aria-autocomplete', 'list'], ['aria-controls', list.element.id], ['aria-expanded', 'false']]) input.setAttribute(name!, value!)
  const show = (on: boolean) => { shown = on; list.element.hidden = !on; input.setAttribute('aria-expanded', String(on)) }
  const search = createSuggestionSearch<LinkTarget>({
    search: async (query, context) => (await Promise.all(options.sources.map(source => source.search!(query, context)))).flat(),
    onChange: ({ status, items }) => {
      loading = status === 'loading'
      if (status === 'idle') return
      if (status === 'loading') { list.setStatus('loading'); return }
      if (status === 'error') { pending = false; list.setStatus('error', toolbarStrings.linkSearchError, () => search.retry()); return }
      targets = items
      list.update(items.map((item, index) => ({ id: String(index), title: item.label, description: item.detail ?? item.url })))
      if (pending) { pending = false; if (items[0]) options.pick(items[0]); else options.error(toolbarStrings.linkNoPage) }
    },
  })
  return {
    input(value: string) {
      const text = value.trim()
      pending = false
      show(!!text && !isLinkAddress(text))
      search.query(shown ? text : '')
    },
    /** ↑ ↓ move in the list while it shows. */
    keydown: (event: KeyboardEvent) => shown && (event.key === 'ArrowDown' || event.key === 'ArrowUp') && list.onKeyDown(event),
    /** Enter on a non-empty value: null when the search took it (a page name), otherwise the address to link. */
    confirm(value: string): string | null {
      if (!shown) return /^[a-z][a-z\d+.-]*:|^[#/?.]/i.test(value) ? value : `https://${value}`
      if (!list.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }))) {
        if (loading) pending = true
        else options.error(toolbarStrings.linkNoPage)
      }
      return null
    },
    destroy() { search.destroy(); list.destroy() },
  }
}
