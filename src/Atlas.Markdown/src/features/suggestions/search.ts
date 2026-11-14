import type { SuggestionStatus } from './list'

export interface SuggestionSearchState<T> { status: SuggestionStatus; items: T[]; error?: unknown }
export interface SuggestionSearchOptions<T> {
  search: (query: string, options: { signal: AbortSignal }) => Promise<T[]> | T[]
  onChange: (state: SuggestionSearchState<T>) => void
  debounceMs?: number
  limit?: number
  minQueryLength?: number
  /** What is known for a query at once (P17): shown instead of the loading state, the answer replaces it. */
  cached?: (query: string) => T[]
}

/** One request owner per editor/picker. Aborting also invalidates non-cooperating sources. */
export function createSuggestionSearch<T>(options: SuggestionSearchOptions<T>) {
  let generation = 0, disposed = false, query = ''
  let controller: AbortController | undefined, timer: ReturnType<typeof setTimeout> | undefined
  const cancel = () => { generation++; controller?.abort(); clearTimeout(timer) }
  const run = (value: string, delay: number) => {
    if (disposed) return
    cancel(); query = value
    if (value.length < (options.minQueryLength ?? 1)) { options.onChange({ status: 'idle', items: [] }); return }
    const current = generation; controller = new AbortController(); const signal = controller.signal
    let known: T[] = []
    try { known = options.cached?.(value) ?? [] } catch { /* the answer still comes */ }
    options.onChange(known.length ? { status: 'ready', items: known.slice(0, options.limit ?? 20) } : { status: 'loading', items: [] })
    timer = setTimeout(async () => {
      try {
        const items = await options.search(value, { signal })
        if (!disposed && current === generation) options.onChange({ status: 'ready', items: items.slice(0, options.limit ?? 20) })
      } catch (error) {
        if (!disposed && current === generation) options.onChange({ status: 'error', items: [], error })
      }
    }, delay)
  }
  return { query: (value: string) => run(value, options.debounceMs ?? 200), retry: () => run(query, 0), destroy() { disposed = true; cancel() } }
}
