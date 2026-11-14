/** At most one `send` per interval: the first value at once, then only the latest of those that came meanwhile. */
export function throttleLatest<T>(intervalMs: number, send: (value: T) => void) {
  let last = -Infinity;
  let queued: { value: T } | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const fire = (value: T) => { last = Date.now(); send(value); };
  return {
    push(value: T) {
      const wait = last + intervalMs - Date.now();
      if (wait <= 0 && timer === undefined) return fire(value);
      queued = { value };
      timer ??= setTimeout(() => {
        timer = undefined;
        const next = queued;
        queued = undefined;
        if (next) fire(next.value);
      }, Math.max(wait, 0));
    },
    cancel() { clearTimeout(timer); timer = undefined; queued = undefined; },
  };
}
