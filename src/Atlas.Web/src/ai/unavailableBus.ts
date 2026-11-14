// A tiny event bus between api.ts (which sees a `503 ai_unavailable`) and the capabilities state (which refreshes).
// It lives apart from capabilities.ts so api.ts and the capability fetch, which uses api.ts, do not import each other.
type Listener = () => void;
const listeners = new Set<Listener>();

/** Called by api.ts when a response is a Problem Details with `code: "ai_unavailable"`. */
export function notifyAiUnavailable() {
  for (const listener of [...listeners]) listener();
}

/** Subscribes to the outage signal; returns the unsubscribe function. */
export function onAiUnavailable(listener: Listener): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}
