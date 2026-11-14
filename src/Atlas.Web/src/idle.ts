/** Runs `work` once the browser is idle (at the latest after `timeout` ms); a timer where idle callbacks are missing. */
export function whenIdle(work: () => void, timeout = 2000): void {
  if (typeof window.requestIdleCallback === "function") window.requestIdleCallback(() => work(), { timeout });
  else window.setTimeout(work, 1);
}

/** Runs `work` right after the next frame has painted: a task queued from that frame's animation callback runs after its paint. */
export function afterPaint(work: () => void): void {
  requestAnimationFrame(() => setTimeout(work));
}

const RESTED_MS = 800;
/** An idle period this long (the browser grants at most 50 ms) means no task or frame is waiting. */
const FREE_IDLE_MS = 40;
/**
 * Like `whenIdle`, for work long enough to delay a keystroke (a whole-document projection): it waits until keys and
 * pointer have rested for a moment and for a long idle period, not a short gap between the page's own tasks
 * (that preference ends after `timeout` ms; the rest never does).
 */
export function whenRested(work: () => void, timeout = 3000): void {
  let lastInput = -Infinity;
  const since = performance.now();
  const input = () => { lastInput = performance.now(); };
  const events = ["keydown", "pointerdown"] as const;
  events.forEach(type => window.addEventListener(type, input, { capture: true, passive: true }));
  const requestIdle: (callback: IdleRequestCallback, options: IdleRequestOptions) => unknown = typeof window.requestIdleCallback === "function"
    ? (callback, options) => window.requestIdleCallback(callback, options)
    : callback => window.setTimeout(() => callback({ didTimeout: true, timeRemaining: () => 0 }), 1);
  const attempt = () => requestIdle(deadline => {
    const rest = RESTED_MS - (performance.now() - lastInput);
    if (rest > 0) window.setTimeout(attempt, rest);
    else if (!deadline.didTimeout && deadline.timeRemaining() < FREE_IDLE_MS && performance.now() - since < timeout) attempt();
    else {
      events.forEach(type => window.removeEventListener(type, input, { capture: true }));
      work();
    }
  }, { timeout: Math.max(1, timeout - (performance.now() - since)) });
  attempt();
}
