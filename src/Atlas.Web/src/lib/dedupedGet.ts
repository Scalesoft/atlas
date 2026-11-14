const inFlight = new Map<string, { promise: Promise<unknown>; controller: AbortController; readers: number }>();

/**
 * One request for readers that ask for the same thing at the same moment (a page shows its relations in the footer and in the dock):
 * `start` runs once per `key` while it is in flight. A reader that gives up (`signal`) drops its own answer from the map, so nobody
 * joins the request any more and an answer started before a change never stands for the new state; when the last reader gave up,
 * the request itself is aborted (the signal `start` gets). A reader without a signal keeps it running.
 */
export function dedupedGet<T>(key: string, start: (signal: AbortSignal) => Promise<T>, signal?: AbortSignal): Promise<T> {
  // A reader that gave up already would join and never leave (its abort event is past).
  if (signal?.aborted) return Promise.reject(signal.reason);
  let entry = inFlight.get(key);
  if (!entry) {
    const controller = new AbortController();
    const created = { promise: start(controller.signal).finally(() => { if (inFlight.get(key) === created) inFlight.delete(key); }), controller, readers: 0 };
    inFlight.set(key, entry = created);
  }
  const shared = entry;
  shared.readers++;
  if (!signal) return shared.promise as Promise<T>;
  const leave = () => {
    if (inFlight.get(key) === shared) inFlight.delete(key);
    if (--shared.readers === 0) shared.controller.abort();
  };
  signal.addEventListener("abort", leave, { once: true });
  return shared.promise.finally(() => signal.removeEventListener("abort", leave)) as Promise<T>;
}
