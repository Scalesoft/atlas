import type { IRetryPolicy, RetryContext } from '@microsoft/signalr'

export const INITIAL_CONNECT_RETRY_MS = 500
export const SNAPSHOT_RETRY_MS = 1000
export const OUTBOX_RETRY_MS = 1000

/** SignalR's default policy gives up after four tries; collaboration retries indefinitely. */
export class IndefiniteBackoff implements IRetryPolicy {
  nextRetryDelayInMilliseconds(context: RetryContext): number {
    switch (context.previousRetryCount) {
      case 0: return 0
      case 1: return 2000
      case 2: return 5000
      default: return 10000
    }
  }
}

export function joinRetryDelay(attempt: number): number {
  return Math.min(500 * (attempt + 1), 3000)
}

export function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds))
}
