export interface DebouncedLookupOptions<T> {
  find: (text: string, signal: AbortSignal) => Promise<T>;
  /** Called with the answer for the latest text, or with `empty()` when there is nothing to show (short text, failed lookup). */
  onResult: (value: T) => void;
  /** The value that means "nothing to show"; a function, so a list is a new one each time. */
  empty: () => T;
  /** Wait this long after the last `update`, and look only from this many characters of the trimmed text. */
  delayMs: number;
  minLength: number;
  /** When given, a failed lookup is reported here instead of answering `empty()`: for a field that says its suggestions are down. */
  onError?: (failure: unknown) => void;
}

/**
 * Debounce, abort and generation of a lookup, without a component: every `update` drops the pending timer and aborts the
 * request in flight, so a late answer for older text is never shown. A failed lookup is an empty result, never an error:
 * these are courtesies (suggestions) and the flow they sit in does not wait for them.
 */
export function createDebouncedLookup<T>({ find, onResult, empty, delayMs, minLength, onError }: DebouncedLookupOptions<T>) {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let controller: AbortController | undefined;
  let generation = 0;

  function stop() {
    generation++;
    clearTimeout(timer);
    timer = undefined;
    controller?.abort();
    controller = undefined;
  }

  return {
    /** `delay` 0 asks at once (an opening, a text whose answer is known). */
    update(text: string, delay = delayMs) {
      stop();
      const value = text.trim();
      if (value.length < minLength) return onResult(empty());
      const current = generation;
      const run = async () => {
        timer = undefined;
        const request = controller = new AbortController();
        try {
          const result = await find(value, request.signal);
          if (current === generation && !request.signal.aborted) onResult(result);
        } catch (failure) {
          if (current === generation && !request.signal.aborted) {
            if (onError) onError(failure);
            else onResult(empty());
          }
        }
      };
      if (delay > 0) timer = setTimeout(run, delay);
      else void run();
    },
    /** The flow ended or the field went away: nothing pending may answer any more. */
    cancel: stop,
  };
}
