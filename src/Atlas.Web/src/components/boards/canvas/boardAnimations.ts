import type { Geometry } from "../../../boards/boardApi";
import type { ItemMotion } from "../../../boards/boardMotion";
import { whenIdle } from "../../../idle";
import { animateAtlas, MOTION_SCALE_PROPERTY, motionEasing, motionMs, motionScale } from "../../../motion/aiMotion";

/**
 * A planned motion (boardMotion.ts) as the canvas hands it to a node or an edge; `key` replays it, `from` is where a glide starts and
 * `to` where it ends: the item's new place, which Vue Flow's own position of the node may not have reached when the node plays (COL2-2).
 */
export type PlayedMotion = ItemMotion & { key: number; from?: Geometry; to?: Geometry };
/** The glide each node wrapper is running, so that a move arriving mid-glide starts where the card is drawn. */
const glides = new WeakMap<Element, Animation>();
/** Where a gliding wrapper is drawn right now, from its animated transform; undefined without one. */
function drawnAt(wrapper: Element): { x: number; y: number } | undefined {
  const glide = glides.get(wrapper);
  if (!glide || glide.playState !== "running" || typeof DOMMatrixReadOnly === "undefined") return undefined;
  const matrix = new DOMMatrixReadOnly(getComputedStyle(wrapper).transform);
  return matrix.isIdentity ? undefined : { x: matrix.m41, y: matrix.m42 };
}

/** The stagger of items that enter together (spec §5: 40 ms, the first eight). */
const STAGGER_MS = 40;

/**
 * The motion tokens of the board around `el`, as last read: a drop, a paste or an undo plays the motion of what it changed right after
 * changing the board, and reading a token then brought the whole page's styles up to date first (≈ 400 ms over 12 drops of 50 cards on
 * a large board at 1× CPU). They are read again once the page is idle, when its styles are up to date and a read costs nothing, so a
 * changed token (the gallery's slow-down) holds from the next motion on. A board's items share its tokens.
 */
const tokens = new WeakMap<Element, Map<string, { value: string | number; read: (host: Element) => string | number }>>();
const rereading = new WeakSet<Element>();
function token<T extends string | number>(el: Element, name: string, read: (host: Element) => T): T {
  const host = el.closest(".board-canvas") ?? el;
  let known = tokens.get(host);
  if (!known) tokens.set(host, (known = new Map()));
  let entry = known.get(name);
  if (!entry) known.set(name, (entry = { value: read(host), read }));
  if (!rereading.has(host)) {
    rereading.add(host);
    const entries = known;
    whenIdle(() => { rereading.delete(host); for (const each of entries.values()) each.value = each.read(host); });
  }
  return entry.value as T;
}
const ms = (el: Element, name: Parameters<typeof motionMs>[1]) => token(el, name, host => motionMs(host, name));
const literal = (el: Element, value: number) => value * token(el, MOTION_SCALE_PROPERTY, motionScale);
const travel = (el: Element) => token(el, "--atlas-ease-travel", host => getComputedStyle(host).getPropertyValue("--atlas-ease-travel").trim() || "cubic-bezier(.3, .2, .1, 1)");
/** animateAtlas on the board's easing as read above (it would read it from each element). */
const animate = (el: HTMLElement | SVGElement | null, frames: Keyframe[], options: Parameters<typeof animateAtlas>[2] = {}) =>
  animateAtlas(el, frames, { easing: el ? token(el, "--atlas-ease-out", host => motionEasing(host, "--atlas-ease-out")) : undefined, ...options });
/** The transform that puts a group's outline (drawn at `at` in its node, of `at`'s size when known) over the box `from`, from its top left
 * corner. The size is the item's: reading the node's laid out size right after a change laid the whole page out (≈ 290 ms over 12 drops of 50
 * grouped cards on a large board). */
function boxTransform(el: HTMLElement, from: Geometry, at: Placed) {
  const width = at.width || el.offsetWidth || from.width, height = at.height || el.offsetHeight || from.height;
  return `translate(${from.x - at.x}px, ${from.y - at.y}px) scale(${from.width / width}, ${from.height / height})`;
}
/** Where a node is drawn, with its size when the caller knows it. */
type Placed = { x: number; y: number; width?: number; height?: number };

/**
 * Runs a motion on a node's own element (enter, leave, fade, the group changes) or its Vue Flow wrapper (glide), on the Atlas
 * tokens (interaction spec §5). `animateAtlas` keeps only the fade under reduced motion, at most 160 ms; a glide is planned as a
 * fade there. A group's (a frame's) outline and name are the node's `.board-group-body` (`.board-frame-body`) and `.board-group-title`
 * (`.board-frame-title`).
 */
export function playNodeMotion(el: HTMLElement, motion: PlayedMotion, at: Placed): Animation | null {
  const outline = el.querySelector<HTMLElement>(".board-group-body, .board-frame-body"), name = el.querySelector<HTMLElement>(".board-group-title, .board-frame-title");
  switch (motion.kind) {
    case "enter":
      if (motion.variant === "draw" && outline && motion.from) {
        // The outline draws itself in around the members from their common bounds, then the name fades in.
        animate(name, [{ opacity: 0 }, { opacity: 1 }], { duration: ms(el, "--atlas-motion-slow"), delay: ms(el, "--atlas-motion-fast") });
        return animate(outline, [{ transformOrigin: "0 0", transform: boxTransform(el, motion.from, at), opacity: 0 }, { transformOrigin: "0 0", transform: "none", opacity: 1 }],
          { duration: ms(el, "--atlas-motion-slow") });
      }
      return animate(el, [{ opacity: 0, transform: "scale(.96)" }, { opacity: 1, transform: "none" }],
        { duration: ms(el, "--atlas-motion-slow"), delay: literal(el, STAGGER_MS) * motion.order });
    case "leave":
      // An ungrouped group's outline dissolves outward; anything else shrinks away.
      return animate(el, [{ opacity: 1, transform: "none" }, { opacity: 0, transform: motion.variant === "dissolve" ? "scale(1.02)" : "scale(.96)" }],
        { duration: ms(el, "--atlas-motion-surface"), fill: "forwards" });
    case "gather":
      return animate(el, [{ transform: "none" }, { transform: "scale(.98)", offset: 0.45 }, { transform: "none" }], { duration: ms(el, "--atlas-motion-slow") });
    case "freed":
      return animate(el, [{ outlineColor: "var(--accent)", opacity: 0.7 }, { opacity: 1 }], { duration: ms(el, "--atlas-motion-slow") });
    case "tuck":
    case "untuck": {
      const card = `translate(${motion.toward?.x ?? 0}px, ${motion.toward?.y ?? 0}px) scale(.6)`;
      const frames: Keyframe[] = [{ transform: "none", opacity: 1 }, { transform: card, opacity: 0 }];
      return animate(el, motion.kind === "tuck" ? frames : frames.reverse(),
        { duration: ms(el, "--atlas-motion-base"), easing: travel(el), delay: motion.kind === "untuck" ? literal(el, STAGGER_MS) * motion.order : 0 });
    }
    case "refit": {
      if (!outline || !motion.from) return null;
      // The outline follows its members from where it was and lights up briefly; the name travels along.
      const shift = `translate(${motion.from.x - at.x}px, ${motion.from.y - at.y}px)`;
      animate(name, [{ transform: shift }, { transform: "none" }], { duration: ms(el, "--atlas-motion-base"), easing: travel(el) });
      animate(outline, [{ borderColor: "var(--accent)", opacity: 0.6 }, { opacity: 1 }], { duration: ms(el, "--atlas-motion-slow"), fill: "none" });
      return animate(outline, [{ transformOrigin: "0 0", transform: boxTransform(el, motion.from, at) }, { transformOrigin: "0 0", transform: "none" }],
        { duration: ms(el, "--atlas-motion-base"), easing: travel(el) });
    }
    case "fade":
      return animate(el, [{ opacity: 0.35 }, { opacity: 1 }], { duration: ms(el, "--atlas-motion-base") });
    case "glide": {
      const wrapper = el.parentElement;
      if (!wrapper || !motion.from) return null;
      const from = drawnAt(wrapper) ?? motion.from, to = motion.to ?? at;
      glides.get(wrapper)?.cancel();
      const glide = animate(wrapper, [{ transform: `translate(${from.x}px,${from.y}px)` }, { transform: `translate(${to.x}px,${to.y}px)` }],
        { duration: ms(el, "--atlas-motion-base"), easing: travel(el) });
      if (glide) glides.set(wrapper, glide);
      return glide;
    }
  }
}

/** A connector that enters draws itself from its start to its end (a dashed one fades in); one that leaves fades out. */
export function playEdgeMotion(path: SVGPathElement, group: SVGElement, motion: PlayedMotion, dashed: boolean): Animation | null {
  if (motion.kind === "leave") return animate(group, [{ opacity: 1 }, { opacity: 0 }], { duration: ms(group, "--atlas-motion-surface"), fill: "forwards" });
  if (motion.kind !== "enter") return null;
  const length = typeof path.getTotalLength === "function" ? path.getTotalLength() : 0;
  if (dashed || !length) return animate(group, [{ opacity: 0 }, { opacity: 1 }], { duration: ms(group, "--atlas-motion-slow") });
  return animate(path, [{ strokeDasharray: `${length}`, strokeDashoffset: length, opacity: 0.2 }, { strokeDasharray: `${length}`, strokeDashoffset: 0, opacity: 1 }],
    { duration: ms(path, "--atlas-motion-slow") });
}
