/**
 * A pointer gesture under way (a block or file drag, a selection by mouse). A checkpoint serialises the whole page (0.6–1 s at 4× CPU on
 * a long one): a timed one waits for the gesture's end and then for an idle moment, so it never lands in the middle of a drag.
 * shortcut: a gesture whose end never arrives (released outside the window) holds timed saves at most GESTURE_MAX_MS; track pointer
 * capture if that ever delays a save noticeably.
 */
let gestureSince: number | undefined;
const GESTURE_MAX_MS = 30_000;
const SETTLE_MS = 600;
const afterGesture = new Set<() => void>();
const inGesture = () => gestureSince !== undefined && performance.now() - gestureSince < GESTURE_MAX_MS;
/** Where the browser has no idle callbacks (Safari) a timed save goes at once, as before. */
const whenIdle = (run: () => void) => typeof requestIdleCallback === "function" ? void requestIdleCallback(run, { timeout: 1000 }) : run();
if (typeof window !== "undefined") {
  let pointer = false, dragging = false;
  const update = () => {
    const active = pointer || dragging;
    if (active) { gestureSince ??= performance.now(); return; }
    if (gestureSince === undefined) return;
    gestureSince = undefined;
    const waiting = [...afterGesture];
    afterGesture.clear();
    // Not in the drop's own frames: the moved blocks settle for about half a second, and the serialisation would stall them.
    for (const run of waiting) setTimeout(run, SETTLE_MS);
  };
  const listen = (type: string, change: (event: Event) => void) => window.addEventListener(type, event => { change(event); update(); }, { capture: true, passive: true });
  // A press holds a button; a script's bare event (no button) starts no gesture that its missing release would never end.
  listen("pointerdown", event => { if ((event as MouseEvent).buttons) pointer = true; });
  for (const type of ["pointerup", "pointercancel"]) listen(type, () => { pointer = false; });
  listen("dragstart", () => { dragging = true; });
  for (const type of ["dragend", "drop"]) listen(type, () => { dragging = false; });
}

/** Resolves when no pointer gesture is under way and the browser is idle: for a timed save that waited on the server past its own gesture check. */
export function untilQuiet(): Promise<void> {
  return new Promise(resolve => {
    const check = () => {
      if (!inGesture()) { resolve(); return; }
      afterGesture.add(go);
      setTimeout(() => { if (afterGesture.delete(go)) go(); }, GESTURE_MAX_MS);
    };
    const go = () => whenIdle(check);
    check();
  });
}

/**
 * When a page's checkpoint goes (Atlas 3.6-B, after boardSession's pattern): 1.5 s after the last change, at the latest 10 s after
 * the first unsaved one while typing goes on, and at once on `flush` (blur, a hidden tab, leaving, publishing). One request at a
 * time: a flush asked for while one runs is answered by one more save after it, never by a second request beside it.
 * A failure `retry` accepts (the server still projecting the shared log) is tried again by itself 1, 2, 4, 8… s later, at most
 * 30 s apart, until a save goes or the page is left: the page never stays unsaved waiting for the next keystroke. Changes meanwhile (a
 * colleague typing) wait for that retry: while several type the server saves the page itself (3.6), so the retry backs off instead of
 * asking again every 1.5 s.
 * A timed save (not `flush`) waits for a pointer gesture's end and goes when the browser is idle (see `inGesture`); `save` is told it
 * is one (`timed`), so it need not wait for the server the way an explicit save does.
 */
export function createCheckpointScheduler(save: (timed: boolean) => Promise<void>, { idleMs = 1500, maxMs = 10_000, retry = (_: unknown): boolean => false } = {}) {
  let idle: ReturnType<typeof setTimeout> | undefined, max: ReturnType<typeof setTimeout> | undefined, retryTimer: ReturnType<typeof setTimeout> | undefined;
  let running: Promise<void> | undefined, again = false, explicit = false, retries = 0, generation = 0;
  const stop = () => { clearTimeout(idle); clearTimeout(max); clearTimeout(retryTimer); idle = max = retryTimer = undefined; generation++; afterGesture.delete(quietly); };
  function flush(timed = false): Promise<void> {
    stop();
    if (!timed) explicit = true;
    if (running) { again = true; return running; }
    running = (async () => {
      try { do { again = false; const wasExplicit = explicit; explicit = false; await save(!wasExplicit); } while (again); retries = 0; }
      catch (error) {
        if (retry(error)) {
          clearTimeout(idle); clearTimeout(max); idle = max = undefined;
          retryTimer = setTimeout(quietly, Math.min(1000 * 2 ** retries++, 30_000));
        }
        else retries = 0;
        throw error;
      }
      finally { running = undefined; }
    })();
    return running;
  }
  // A failed timed save is reported by `save` itself (the page's save state); nothing waits on its promise here.
  function quietly() {
    if (inGesture()) { afterTheGesture(); return; }
    const scheduled = generation;
    // A drag can begin between the gesture's pointer and its drag events: checked again when it is time.
    whenIdle(() => { if (scheduled !== generation) return; if (inGesture()) afterTheGesture(); else flush(true).catch(() => {}); });
  }
  /** Waits for the gesture's end, at most GESTURE_MAX_MS (an end that never arrives). */
  function afterTheGesture() {
    afterGesture.add(quietly);
    setTimeout(() => { if (afterGesture.delete(quietly)) quietly(); }, GESTURE_MAX_MS);
  }
  return {
    /** A change waits for the pause after it, and for the longest wait counted from the first change not yet saved. */
    changed() {
      if (retryTimer) return;
      clearTimeout(idle);
      idle = setTimeout(quietly, idleMs);
      max ??= setTimeout(quietly, maxMs);
    },
    flush,
    /** A timed save now, still waiting for a pointer gesture to end: for what is due but not urgent (a retry the server just made possible). */
    soon: quietly,
    /** The page is gone: nothing scheduled goes any more (a request already sent finishes). */
    cancel() { stop(); retries = 0; },
    get busy() { return !!running; },
    /** Retryable failures in a row so far: a save state shows „Chyba“ only after a few (the retry still goes on). */
    get retries() { return retries; },
  };
}
