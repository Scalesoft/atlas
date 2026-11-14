// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from 'vitest'
import { pickYouTrackIssue } from './pickerRuntime'
import type { YouTrackClient, YouTrackIssue } from './types'

const issue: YouTrackIssue = { id: '2-42', key: 'YT-42', title: '<script>plain title</script>', url: 'https://tracker.example/issue/YT-42' }
const lifetimes: AbortController[] = []

function start(search: YouTrackClient['search'] = vi.fn(async () => [issue])) {
  const lifetime = new AbortController()
  lifetimes.push(lifetime)
  const result = pickYouTrackIssue({ client: { search }, signal: lifetime.signal, ownerDocument: document })
  const dialog = document.querySelector('dialog')!
  const input = dialog.querySelector('input')!
  // Typed: the shared search field searches as one types (P8).
  const searchFor = (query: string) => { input.value = query; input.dispatchEvent(new Event('input')) }
  const rows = () => [...dialog.querySelectorAll<HTMLButtonElement>('[role=option]')]
  const key = (name: string, init: KeyboardEventInit = {}) => { const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init }); input.dispatchEvent(event); return event }
  return { dialog, input, lifetime, result, searchFor, search, rows, key }
}

afterEach(() => {
  lifetimes.splice(0).forEach(lifetime => lifetime.abort())
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

describe('YouTrack picker', () => {
  it('uses an optional host resolver for a pasted reference and safely renders state', async () => {
    const lifetime = new AbortController(); lifetimes.push(lifetime)
    const search = vi.fn(async () => [])
    const resolve = vi.fn(async () => ({ ...issue, state: '<b>Open</b>' }))
    const result = pickYouTrackIssue({ client: { search, resolve }, signal: lifetime.signal, ownerDocument: document })
    const input = document.querySelector('dialog input') as HTMLInputElement
    input.value = issue.url
    input.dispatchEvent(new Event('input'))
    await vi.waitFor(() => expect(document.querySelector('[role=option]')).not.toBeNull())
    expect(resolve).toHaveBeenCalledWith(issue.url, { signal: expect.any(AbortSignal) })
    expect(search).not.toHaveBeenCalled()
    expect(document.querySelector('dialog b')).toBeNull()
    expect(document.querySelector('[role=option]')!.textContent).toContain('<b>Open</b>')
    ;(document.querySelector('[role=option]') as HTMLButtonElement).click()
    expect((await result)?.state).toBe('<b>Open</b>')
  })
  it('does not submit an IME composition with Enter', () => {
    const picker = start()
    picker.input.value = 'query'
    picker.input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', isComposing: true }))
    expect(picker.search).not.toHaveBeenCalled()
  })

  it('preselects the first result; the arrows move with the keyboard in the field and Enter takes the row (P8)', async () => {
    const second = { ...issue, key: 'SECOND' }
    const picker = start(vi.fn(async () => [issue, second]))
    picker.searchFor('query')
    await vi.waitFor(() => expect(picker.rows()).toHaveLength(2))
    expect(picker.rows().map(row => row.getAttribute('aria-selected'))).toEqual(['true', 'false'])
    expect(picker.key('ArrowDown').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(picker.input)
    expect(picker.input.getAttribute('aria-activedescendant')).toBe(picker.rows()[1]!.id)
    picker.key('Enter')
    expect((await picker.result)?.key).toBe('SECOND')
  })
  it('Enter typed before the answer takes the first row of the answer; Tab takes the first row too (P8)', async () => {
    let answer: ((issues: readonly YouTrackIssue[]) => void) | undefined
    const picker = start(vi.fn(() => new Promise<readonly YouTrackIssue[]>(resolve => { answer = resolve })))
    picker.searchFor('vpn')
    expect(picker.key('Enter').defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(answer).toBeDefined())
    answer!([issue, { ...issue, key: 'SECOND' }])
    expect(await picker.result).toEqual(issue)
    const next = start()
    next.searchFor('YT')
    await vi.waitFor(() => expect(next.rows()).toHaveLength(1))
    expect(next.key('Tab').defaultPrevented).toBe(true)
    expect(await next.result).toEqual(issue)
  })

  it('searches using the injected service and returns the selected result as text-safe data', async () => {
    const picker = start()
    picker.searchFor('YT-42')
    await vi.waitFor(() => expect(picker.rows()).toHaveLength(1))
    expect(picker.search).toHaveBeenCalledWith('YT-42', { signal: expect.any(AbortSignal) })
    expect(picker.dialog.querySelector('script')).toBeNull()
    picker.rows()[0]!.click()
    expect(await picker.result).toEqual(issue)
    expect(document.querySelector('dialog')).toBeNull()
  })

  it('aborts superseded requests and ignores results returned out of order', async () => {
    const resolvers: Array<(issues: readonly YouTrackIssue[]) => void> = []
    const search = vi.fn((_query: string, _context: { signal: AbortSignal }) => new Promise<readonly YouTrackIssue[]>(resolve => resolvers.push(resolve)))
    const picker = start(search)
    picker.searchFor('first')
    await vi.waitFor(() => expect(search).toHaveBeenCalledOnce())
    picker.searchFor('second')
    expect(search.mock.calls[0][1].signal.aborted).toBe(true)
    await vi.waitFor(() => expect(search).toHaveBeenCalledTimes(2))
    resolvers[1]([{ ...issue, key: 'SECOND' }])
    await vi.waitFor(() => expect(picker.dialog.textContent).toContain('SECOND'))
    resolvers[0]([{ ...issue, key: 'STALE' }])
    await Promise.resolve()
    expect(picker.dialog.textContent).not.toContain('STALE')
    picker.lifetime.abort()
    expect(await picker.result).toBeNull()
    expect(search.mock.calls[1][1].signal.aborted).toBe(true)
  })

  it('keeps the picker usable after network failure and rejects unsafe service data', async () => {
    const search = vi.fn(async () => [issue] as readonly YouTrackIssue[])
      .mockRejectedValueOnce(new Error('Network unavailable'))
      .mockResolvedValueOnce([{ ...issue, url: 'javascript:alert(1)' }])
    const picker = start(search)
    picker.searchFor('first')
    await vi.waitFor(() => expect(picker.dialog.querySelector('[role="alert"]')).not.toBeNull())
    picker.searchFor('unsafe')
    await vi.waitFor(() => expect(picker.dialog.querySelector('[role="alert"]')).not.toBeNull())
    // Only the list's own „Zkusit znovu“ (an error offers the retry), no issue.
    expect(picker.rows().map(row => row.textContent)).toEqual(['Zkusit znovu'])
    picker.searchFor('retry')
    await vi.waitFor(() => expect(picker.rows()).toHaveLength(1))
    picker.rows()[0]!.click()
    expect(await picker.result).toEqual(issue)
  })

  it('closes and cancels the request when dismissed by Escape', async () => {
    const search = vi.fn((_query: string, _context: { signal: AbortSignal }) => new Promise<readonly YouTrackIssue[]>(() => {}))
    const picker = start(search)
    picker.searchFor('pending')
    await vi.waitFor(() => expect(search).toHaveBeenCalledOnce())
    picker.dialog.dispatchEvent(new Event('cancel', { cancelable: true }))
    expect(await picker.result).toBeNull()
    expect(search.mock.calls[0][1].signal.aborted).toBe(true)
    expect(document.querySelector('dialog')).toBeNull()
  })

  it('the first Escape clears a typed search, the second closes (P3)', async () => {
    const picker = start()
    picker.searchFor('YT-42')
    expect(picker.key('Escape').defaultPrevented).toBe(true)
    expect(picker.input.value).toBe('')
    expect(document.querySelector('dialog')).not.toBeNull()
    expect(picker.key('Escape').defaultPrevented).toBe(true)
    expect(await picker.result).toBeNull()
    expect(document.querySelector('dialog')).toBeNull()
  })

  it('searches for an initial query as soon as it opens', async () => {
    const lifetime = new AbortController(); lifetimes.push(lifetime)
    const search = vi.fn(async () => [issue])
    const result = pickYouTrackIssue({ client: { search }, signal: lifetime.signal, ownerDocument: document, initialQuery: '  login  ' })
    const input = document.querySelector('dialog input') as HTMLInputElement
    expect(input.value).toBe('login')
    await vi.waitFor(() => expect(search).toHaveBeenCalledWith('login', { signal: expect.any(AbortSignal) }))
    await vi.waitFor(() => expect(document.querySelector('[role=option]')).not.toBeNull())
    lifetime.abort(); expect(await result).toBeNull()
  })
})
