const interrupts = ["wheel", "touchstart", "keydown", "pointerdown"] as const;

/**
 * Brings an element to the middle of the view once it exists, and keeps it there while the page around it still lays out (blocks
 * rendered after it, `content-visibility` sizes coming in above it: a deep block of a long page ended far off screen, U4-2), until
 * it has stayed put for a while, the time is up, or the reader scrolls or presses something. `found` runs once, when it is first
 * brought into view (a focus, a glow). Returns the stop.
 */
export function revealSettled(find: () => HTMLElement | null | undefined, found: (element: HTMLElement) => void, { frames = 120, still = 15 } = {}) {
  let left = frames, quiet = 0, frame = 0, shown: HTMLElement | undefined;
  const stop = () => {
    cancelAnimationFrame(frame);
    for (const type of interrupts) window.removeEventListener(type, stop, true);
  };
  for (const type of interrupts) window.addEventListener(type, stop, { capture: true, passive: true });
  const centre = (element: HTMLElement) => element.scrollIntoView({ block: "center" });
  const step = () => {
    if (!shown) {
      const element = find();
      if (element) { shown = element; centre(element); found(element); }
    } else {
      const box = shown.getBoundingClientRect();
      // Away from the middle by more than a quarter of the view: the page moved under it.
      if (Math.abs((box.top + box.bottom) / 2 - window.innerHeight / 2) > window.innerHeight / 4) { centre(shown); quiet = 0; } else quiet++;
    }
    if (--left > 0 && quiet < still) frame = requestAnimationFrame(step); else stop();
  };
  step();
  return stop;
}
