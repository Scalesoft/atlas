import { animateAtlas, motionEasing, motionMs, travelTiming } from "../motion/aiMotion";

/** The components of a kind under a root that the shared motion plays (a kind names its own with `itemSelector`). */
export const overviewItemSelector = "[data-overview-item]";

const inView = (rect: DOMRect, bottom: number) => rect.width > 0 && rect.height > 0 && rect.bottom > 0 && rect.top < bottom;
const clip = { overflow: "clip" };
/** The screen's pixels per CSS pixel of the element (a board's zoom scales its canvas); 1 for an element without a box. */
const scaleOf = (element: HTMLElement, rect: DOMRect) => (element.offsetWidth ? rect.width / element.offsetWidth : 0) || 1;

/**
 * A change of an overview's level, played once per root (an editor, a read view, a board's canvas) whatever number of overviews show
 * its switch (plan 3.4, owner 2. 11.), over the kind's components in view (`selector`), as `playViewChange` plays a sort, with FLIP:
 * - one that stays moves and resizes from where it was: it shrinks or grows to its new size (`travelTiming`);
 * - one that comes unfolds its height and fades in (`revealHeight`'s frames);
 * - one that goes folds its height away (`collapseHeight`'s frames), drawn by the animation alone (`display` in its frames), so what is
 *   below it closes up as it folds and nothing in the document changes: an editor's node views see no mutation.
 * Only Web Animations on the motion tokens (`animateAtlas`), reduced for reduced motion; a browser that cannot animate `display`
 * lets a leaving component go at once.
 */
export function playLevelChange(root: HTMLElement, change: () => void, selector = overviewItemSelector) {
  const bottom = root.ownerDocument.defaultView?.innerHeight ?? Infinity;
  const before = new Map<HTMLElement, { rect: DOMRect; display: string; scale: number }>();
  root.querySelectorAll<HTMLElement>(selector).forEach(element => {
    const rect = element.getBoundingClientRect();
    if (inView(rect, bottom)) before.set(element, { rect, display: getComputedStyle(element).display, scale: scaleOf(element, rect) });
  });
  change();
  // Everything is measured first, then animated: a read after an animation had started restyled the whole page, per row (3.6 perf).
  // The leaving ones are measured with the leaving ones still in the layout, as they fold away there.
  const plays: [HTMLElement, Keyframe[], { duration: number; easing?: string; fill?: FillMode }][] = [];
  for (const [element, { rect, display, scale }] of before) {
    if (!element.isConnected || getComputedStyle(element).display !== "none") continue;
    plays.push([element, [
      { display, height: `${rect.height / scale}px`, opacity: 1, ...clip },
      { display, height: "0px", opacity: 0, marginTop: "0px", marginBottom: "0px", paddingTop: "0px", paddingBottom: "0px", ...clip },
    ], { duration: motionMs(element, "--atlas-motion-base"), easing: motionEasing(element, "--atlas-ease-out"), fill: "none" }]);
  }
  root.querySelectorAll<HTMLElement>(selector).forEach(element => {
    const was = before.get(element)?.rect, rect = element.getBoundingClientRect();
    if (!inView(rect, bottom) || getComputedStyle(element).display === "none") return;
    if (!was) {
      plays.push([element, [{ height: "0px", opacity: 0, ...clip }, { height: `${rect.height / scaleOf(element, rect)}px`, opacity: 1, ...clip }],
        { duration: motionMs(element, "--atlas-motion-slow"), easing: motionEasing(element, "--atlas-ease-out"), fill: "none" }]);
      return;
    }
    // Measured on the screen, translated in the element's own pixels.
    const scale = scaleOf(element, rect), dx = (was.left - rect.left) / scale, dy = (was.top - rect.top) / scale, sx = was.width / rect.width, sy = was.height / rect.height;
    if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5 && Math.abs(sx - 1) < 0.01 && Math.abs(sy - 1) < 0.01) return;
    plays.push([element, [{ transformOrigin: "0 0", transform: `translate(${dx}px, ${dy}px) scale(${sx}, ${sy})` }, { transformOrigin: "0 0", transform: "none" }],
      travelTiming(element, Math.hypot(dx, dy))]);
  });
  for (const [element, frames, timing] of plays) animateAtlas(element, frames, timing);
}
