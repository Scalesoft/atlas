// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createSuggestionList } from './list'
import { createSuggestionSearch } from './search'
import { slashItems } from '../../extensions/SlashCommands'
import { iconMarkup } from '../../ui/iconMarkup'

afterEach(() => { document.body.replaceChildren(); vi.useRealTimers() })

it('keeps focus in the editor, skips unavailable items, supports Home/End, and never selects on Tab or IME', () => {
  const input = document.createElement('input'); document.body.append(input); input.focus()
  const select = vi.fn()
  const list = createSuggestionList({ document, label: 'Results', onSelect: select })
  document.body.append(list.element)
  list.update([{ id: 'a', title: 'First' }, { id: 'b', title: 'Disabled', disabled: true }, { id: 'c', title: 'Last' }])
  const key = (key: string, isComposing = false) => list.onKeyDown(new KeyboardEvent('keydown', { key, isComposing }))
  expect(key('ArrowDown')).toBe(true)
  expect(list.element.querySelector('[aria-selected=true]')?.textContent).toContain('Last')
  key('Home'); expect(list.element.querySelector('[aria-selected=true]')?.textContent).toContain('First')
  key('End'); expect(key('Tab')).toBe(false); expect(key('Enter', true)).toBe(false)
  expect(select).not.toHaveBeenCalled(); expect(document.activeElement).toBe(input)
  key('Enter'); expect(select).toHaveBeenCalledWith('c')
  expect([...list.element.querySelectorAll('button')].every(button => button.tabIndex === -1)).toBe(true)
})

it('clears stale selectable rows while loading and presents retry after failure', () => {
  const select = vi.fn(), retry = vi.fn()
  const list = createSuggestionList({ document, label: 'Results', onSelect: select })
  list.update([{ id: 'a', title: 'Old' }]); list.setStatus('loading')
  expect(list.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(false)
  expect(list.element.getAttribute('aria-busy')).toBe('true')
  list.setStatus('error', 'Search failed', retry)
  expect(list.element.querySelector('[role=alert]')?.textContent).toContain('Search failed')
  list.element.querySelector('button')?.click(); expect(retry).toHaveBeenCalledOnce()
  expect(list.onKeyDown(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(true)
  expect(retry).toHaveBeenCalledTimes(2)
  expect(list.element.querySelector('button')?.getAttribute('role')).toBe('option')
  expect(select).not.toHaveBeenCalled()
})

it('preserves relative contact avatars while rejecting executable URLs', () => {
  const list = createSuggestionList({ document, label: 'Contacts', onSelect: vi.fn() })
  list.update([{ id: 'relative', title: 'Alice', avatar: '/people/alice.png' }, { id: 'unsafe', title: 'Bob', avatar: 'javascript:alert(1)' }])
  expect(list.element.querySelector('img')?.getAttribute('src')).toBe('/people/alice.png')
  expect(list.element.querySelectorAll('img')).toHaveLength(1)
})

it('aborts old queries and rejects late responses even when a source ignores abort', async () => {
  vi.useFakeTimers()
  const pending: Array<{ signal: AbortSignal; resolve: (items: string[]) => void }> = []
  const onChange = vi.fn()
  const search = createSuggestionSearch<string>({ debounceMs: 20, onChange,
    search: (_query, { signal }) => new Promise(resolve => pending.push({ signal, resolve })) })
  search.query('a'); await vi.advanceTimersByTimeAsync(20)
  search.query('b'); expect(pending[0]!.signal.aborted).toBe(true)
  expect(onChange.mock.lastCall?.[0]).toMatchObject({ status: 'loading', items: [] })
  await vi.advanceTimersByTimeAsync(20)
  pending[1]!.resolve(['new']); await Promise.resolve()
  pending[0]!.resolve(['old']); await Promise.resolve()
  expect(onChange.mock.lastCall?.[0]).toMatchObject({ status: 'ready', items: ['new'] })
  search.query('c'); await vi.advanceTimersByTimeAsync(20); search.destroy()
  const calls = onChange.mock.calls.length; pending[2]!.resolve(['destroyed']); await Promise.resolve()
  expect(onChange).toHaveBeenCalledTimes(calls)
})

it('gives every slash item its own drawing, drawn from the registry', async () => {
  const icons = slashItems.map(item => item.icon)
  expect(new Set(icons).size, `duplicate slash icons: ${icons.join(', ')}`).toBe(icons.length)
  for (const icon of icons) {
    expect(icon, `${icon} is not an icon name`).toMatch(/^svg:/)
    expect(iconMarkup[icon.slice(4)], icon).toBeTruthy()
  }
})

it('keeps a decorative heading above the rows of a command list through updates and states', () => {
  const list = createSuggestionList({ document, label: 'Slash menu příkazy', heading: 'Vložit', onSelect: vi.fn() })
  const heading = () => list.element.querySelector('.md-suggestion-heading')
  expect(list.element.classList.contains('has-heading')).toBe(true)
  expect(list.element.getAttribute('aria-label')).toBe('Slash menu příkazy')
  list.update([{ id: 'table', title: 'Tabulka', description: 'Mřížka s hlavičkou', icon: 'svg:table' }])
  expect(list.element.firstElementChild).toBe(heading())
  expect(heading()?.textContent).toBe('Vložit')
  expect(heading()?.getAttribute('aria-hidden')).toBe('true')
  expect(list.element.querySelectorAll('[role=option]')).toHaveLength(1)
  list.setStatus('loading')
  expect(heading()).not.toBeNull()
  const plain = createSuggestionList({ document, label: 'Lidé', onSelect: vi.fn() })
  plain.update([{ id: 'a', title: 'Alena' }])
  expect(plain.element.querySelector('.md-suggestion-heading')).toBeNull()
  expect(plain.element.classList.contains('has-heading')).toBe(false)
})
