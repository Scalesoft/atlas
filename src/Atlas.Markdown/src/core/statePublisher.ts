export type SubscriberErrorHandler<State> = (error: unknown, state: State) => void

/** Ordered, reentrancy-safe delivery. A consumer cannot interrupt editor operations. */
export class StatePublisher<State> {
  private readonly listeners = new Set<(state: State) => void>()
  private readonly queue: State[] = []
  private notifying = false
  private closed = false
  private closing = false

  constructor(private current: State, private readonly onSubscriberError?: SubscriberErrorHandler<State>) {}

  get value(): State { return this.current }

  replace(next: State, notify: boolean): void {
    if (this.closed || this.closing) return
    this.current = next
    if (!notify) return
    this.queue.push(next)
    this.flush()
  }

  subscribe(listener: (state: State) => void): () => void {
    if (this.closed) return () => undefined
    this.listeners.add(listener)
    this.deliver(listener, this.current)
    return () => { this.listeners.delete(listener) }
  }

  close(finalState: State): void {
    if (this.closed || this.closing) return
    this.current = finalState
    this.closing = true
    this.queue.length = 0
    this.queue.push(finalState)
    this.flush()
  }

  private flush(): void {
    if (this.notifying) return
    this.notifying = true
    try {
      while (!this.closed && this.queue.length) {
        const state = this.queue.shift()!
        const finalDelivery = this.closing && state === this.current && this.queue.length === 0
        for (const listener of [...this.listeners]) {
          if (this.closed || (this.closing && !finalDelivery)) break
          if (this.listeners.has(listener)) this.deliver(listener, state)
        }
      }
    } finally {
      this.notifying = false
      if (this.closing && this.queue.length === 0) {
        this.closing = false
        this.closed = true
        this.listeners.clear()
      }
    }
  }

  private deliver(listener: (state: State) => void, state: State): void {
    try { listener(state) }
    catch (error) {
      try { this.onSubscriberError?.(error, state) }
      catch { /* Error reporting is also isolated from committed editor operations. */ }
    }
  }
}
