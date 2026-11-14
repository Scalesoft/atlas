/** Every pending callback a concealed surface can have. A hide stops all of them at once. */
export type TimerName =
  | 'hold' | 'progress' | 'withdraw' // access request hold and its forgiving withdrawal
  | 'lifetime' | 'grace' | 'cover' | 'opening' // release deadline, departure grace, rolling mist and the secret ring
  | 'leave' | 'copy' // control fade-out and the brief Copy confirmation

/** Named timers: starting a name replaces its previous timer, so nothing is ever orphaned. */
export function createTimers() {
  const active = new Map<TimerName, () => void>()
  function stop(...names: TimerName[]) {
    for (const name of names) { active.get(name)?.(); active.delete(name) }
  }
  function after(name: TimerName, ms: number, run: () => void) {
    stop(name)
    const id = setTimeout(() => { active.delete(name); run() }, ms)
    active.set(name, () => clearTimeout(id))
  }
  function every(name: TimerName, ms: number, run: () => void) {
    stop(name)
    const id = setInterval(run, ms)
    active.set(name, () => clearInterval(id))
  }
  /** Calls `frame` with progress 0–1 every `interval` ms, then `done` once `ms` have passed. */
  function tween(name: TimerName, ms: number, interval: number, frame: (t: number) => void, done: () => void) {
    stop(name)
    const start = Date.now()
    const id = setInterval(() => frame(Math.min(1, (Date.now() - start) / ms)), interval)
    const end = setTimeout(() => { clearInterval(id); active.delete(name); done() }, ms)
    active.set(name, () => { clearInterval(id); clearTimeout(end) })
  }
  return { after, every, tween, stop, has: (name: TimerName) => active.has(name), clear: () => stop(...active.keys()) }
}
export type Timers = ReturnType<typeof createTimers>
