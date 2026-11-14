/**
 * Back and Forward return the reader to where they were on a long page (NV3-1). The address is the key: the page scroll of each
 * address is remembered while the reader scrolls, and on history navigation it is restored once the page is tall enough again (the
 * content of a page arrives after the address changes). A new address starts at the top as before; a wheel, touch or key press during
 * the wait means the reader took over, so nothing is forced.
 */
const WAIT_MS = 4000;
const SETTLE_MS = 500;
const TAKEOVER = ["wheel", "touchstart", "keydown", "pointerdown"] as const;

export function installScrollMemory(win: Window = window): () => void {
  const saved = new Map<string, number>();
  let restoring = false;
  let waiting = 0;

  // A page that shrinks under the reader (the arrival's skeleton, a shorter page) clamps the scroll to its end while the address is still the
  // old one: that clamp is the page's height, not the reader's place, and must not replace the place they left it at.
  const remember = () => {
    if (restoring) return;
    const y = Math.round(win.scrollY), clamped = y >= win.document.documentElement.scrollHeight - win.innerHeight - 1 && (saved.get(win.location.hash) ?? 0) > y;
    if (!clamped) saved.set(win.location.hash, y);
  };
  const stop = () => { restoring = false; win.cancelAnimationFrame(waiting); TAKEOVER.forEach(type => win.removeEventListener(type, stop)); };
  const restore = () => {
    stop();
    const target = saved.get(win.location.hash);
    if (!target) return;
    restoring = true;
    TAKEOVER.forEach(type => win.addEventListener(type, stop, { passive: true }));
    const started = win.performance.now();
    let settledSince = 0;
    // The page grows (and settles) after the address changes: keep the wanted position until it has held for a moment.
    const tick = () => {
      const now = win.performance.now();
      const reachable = win.document.documentElement.scrollHeight - win.innerHeight;
      const there = reachable >= target - 2 && Math.abs(Math.round(win.scrollY) - target) <= 2;
      settledSince = there ? settledSince || now : 0;
      if ((settledSince && now - settledSince > SETTLE_MS) || now - started > WAIT_MS) {
        if (reachable < target - 2) win.scrollTo({ top: Math.max(0, reachable), behavior: "instant" });
        stop();
        return;
      }
      if (reachable >= target - 2 && !there) win.scrollTo({ top: target, behavior: "instant" });
      waiting = win.requestAnimationFrame(tick);
    };
    waiting = win.requestAnimationFrame(tick);
  };

  win.addEventListener("scroll", remember, { passive: true });
  win.addEventListener("popstate", restore);
  return () => { stop(); win.removeEventListener("scroll", remember); win.removeEventListener("popstate", restore); };
}
