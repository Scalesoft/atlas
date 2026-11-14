/** Share successful imports, coalesce concurrent requests, allow retry after failure. */
export function createMarkdownModuleLoader<T>(load: () => Promise<T>): () => Promise<T> {
  let pending: Promise<T> | undefined
  return () => pending ??= Promise.resolve().then(load).catch(error => { pending = undefined; throw error })
}

/** Work after the first render while the editor idles: requestIdleCallback where the browser has it (at the latest after a second), a timer elsewhere (Safari, jsdom). */
export const whenMarkdownIdle = (work: () => void): void => { if (typeof requestIdleCallback === 'function') requestIdleCallback(work, { timeout: 1000 }); else setTimeout(work, 1) }
