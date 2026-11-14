import { hydrateMarkdownWithin, observeMarkdownProximity } from './proximity'

interface RenderJob {
  run(): Promise<boolean>
  signal: AbortSignal
  resolve(value: boolean): void
  reject(error: unknown): void
  started: boolean
  abort(): void
}
interface Queue { running: number; jobs: RenderJob[] }
const queues = new WeakMap<Document, Queue>()
const abortError = () => new DOMException('Module preparation was cancelled.', 'AbortError')

/** Limits actual asynchronous renderer work, including work that ignores cancellation. */
function enqueue(document: Document, signal: AbortSignal, run: () => Promise<boolean>): Promise<boolean> {
  let queue = queues.get(document)
  if (!queue) { queue = { running: 0, jobs: [] }; queues.set(document, queue) }
  const owned = queue
  const drain = () => {
    while (owned.running < 2 && owned.jobs.length) {
      const job = owned.jobs.shift()!
      if (job.signal.aborted) { job.abort(); continue }
      job.started = true; owned.running++
      Promise.resolve().then(() => job.signal.aborted ? false : job.run()).then(job.resolve, job.reject).finally(() => {
        job.signal.removeEventListener('abort', job.abort); owned.running--; drain()
      })
    }
  }
  return new Promise((resolve, reject) => {
    const job: RenderJob = { run, signal, resolve, reject, started: false, abort() {
      if (!job.started) {
        const index = owned.jobs.indexOf(job); if (index >= 0) owned.jobs.splice(index, 1)
        signal.removeEventListener('abort', job.abort)
      }
      reject(abortError())
    } }
    if (signal.aborted) { reject(abortError()); return }
    signal.addEventListener('abort', job.abort, { once: true }); owned.jobs.push(job); drain()
  })
}

interface ActivationEntry { container: HTMLElement; prepare(): Promise<boolean>; destroy(): void }
const entries = new WeakMap<Document, Set<ActivationEntry>>()
export interface MarkdownModulePreparation { readonly ready: number; readonly fallback: number }

/** Force modules under this root to finish or report their readable error fallback. */
export async function prepareMarkdownModulesForExport(root: HTMLElement, options: { signal?: AbortSignal } = {}): Promise<MarkdownModulePreparation> {
  if (options.signal?.aborted) throw abortError()
  // Lazy node views under the root build their full look first (plan 3.6-G).
  hydrateMarkdownWithin(root)
  const selected = [...entries.get(root.ownerDocument) ?? []].filter(entry => entry.container === root || root.contains(entry.container))
  const work = Promise.all(selected.map(entry => entry.prepare()))
  let release = () => {}
  const aborted = new Promise<never>((_resolve, reject) => {
    const cancel = () => reject(abortError())
    options.signal?.addEventListener('abort', cancel, { once: true })
    release = () => options.signal?.removeEventListener('abort', cancel)
  })
  try {
    const results = await Promise.race([work, aborted])
    if (options.signal?.aborted) throw abortError()
    return Object.freeze({ ready: results.filter(Boolean).length, fallback: results.filter(value => !value).length })
  } finally { release() }
}

export function createMarkdownModuleActivation(options: {
  container: HTMLElement
  signal: AbortSignal
  inline?: boolean
  render(): Promise<boolean>
  prepare?(): Promise<boolean>
}) {
  const { container, signal } = options, document = container.ownerDocument, view = document.defaultView
  let stopped = false, active = false, work: Promise<boolean> | undefined, request: AbortController | undefined
  let proximity: ReturnType<typeof observeMarkdownProximity> | undefined
  let revision = 0
  const setState = (state: string) => { if (!stopped) container.dataset.mdRenderState = state }
  const schedule = (render = options.render) => {
    request?.abort(); request = new AbortController(); const current = ++revision
    setState('queued')
    work = enqueue(document, request.signal, async () => {
      if (stopped || current !== revision) return false
      setState('loading')
      const ready = await render()
      if (current === revision) setState(ready ? 'ready' : 'fallback')
      return ready
    })
    void work.catch(() => {})
    return work
  }
  const activate = () => {
    if (stopped) return Promise.resolve(false)
    if (!active) { active = true; proximity?.dispose(); proximity = undefined; return schedule() }
    return work ?? schedule()
  }
  const forced = () => { void activate().catch(() => {}) }
  const beforePrint = () => {
    container.dataset.mdPrintFallback = container.dataset.mdRenderState === 'ready' ? 'false' : 'true'
    void entry.prepare().catch(() => {})
  }
  const afterPrint = () => { delete container.dataset.mdPrintFallback }
  let exporting: Promise<boolean> | undefined
  const entry: ActivationEntry = { container, prepare() {
    if (exporting) return exporting
    const result = activate().then(ready => stopped || !options.prepare ? ready : schedule(options.prepare))
    exporting = result
    void result.finally(() => { if (exporting === result) exporting = undefined }).catch(() => {})
    return result
  }, destroy() {
    if (stopped) return
    stopped = true; request?.abort(); proximity?.dispose(); proximity = undefined
    signal.removeEventListener('abort', entry.destroy)
    container.removeEventListener('focusin', forced); container.removeEventListener('pointerdown', forced, true)
    view?.removeEventListener('beforeprint', beforePrint); view?.removeEventListener('afterprint', afterPrint)
    entries.get(document)?.delete(entry)
  } }
  let registered = entries.get(document)
  if (!registered) { registered = new Set(); entries.set(document, registered) }
  registered.add(entry)
  signal.addEventListener('abort', entry.destroy, { once: true })
  container.addEventListener('focusin', forced); container.addEventListener('pointerdown', forced, true)
  view?.addEventListener('beforeprint', beforePrint); view?.addEventListener('afterprint', afterPrint)
  setState('deferred')
  if (signal.aborted) entry.destroy()
  else if (options.inline) forced()
  // The page's one proximity observer (proximity.ts): near the viewport, in its idle batches.
  else proximity = observeMarkdownProximity(container, forced)
  return {
    activate: forced,
    setReady(ready: boolean) {
      if (!active || stopped) return
      setState(ready ? 'ready' : 'fallback')
      work = Promise.resolve(ready)
    },
    requestRender() { if (active && !stopped) void schedule().catch(() => {}) },
    requestPreparation() { if (active && !stopped && options.prepare) void schedule(options.prepare).catch(() => {}) },
    destroy: entry.destroy,
  }
}
