/**
 * Motion primitives of the whole Atlas UI, taken 1:1 from the owner's motion prototype.
 * They started with AI output and
 * suggestions and now serve every
 * component (the file keeps its name). Components compose these and the motion.css tokens and
 * never write their own keyframes or durations (src/motionLiterals.guard.test.ts).
 *
 * Every primitive
 * - puts the DOM into its final state first, synchronously, and animates on top of it: content is
 *   ready at once, and a cancelled or unsupported animation leaves the final state;
 * - returns the Web Animations `Animation` (or a list), or `null` when nothing animates;
 * - reads durations and easings from the motion.css tokens in scope of the element;
 * - follows `prefers-reduced-motion`, read on every call: as in the prototype's `anim()`, only
 *   opacity, background colour and colour remain, at most 160 ms, linear.
 *
 * A preview can do what the prototype's two switches do, for the elements inside it (the gallery sets
 * both on the root, for every page; the application sets only the attribute, on <html>, from „Omezit
 * animace“ in the profile): `data-atlas-reduced-motion` forces the reduced path ("Omezený pohyb"), and the
 * custom property `--atlas-motion-scale: 3` stretches
 * every duration and delay ("Zpomalit 3×").
 *
 * The slow-down is carried by the tokens (motion.css registers them as <time> and multiplies them by the
 * scale), so a duration read with `motionMs` is already slowed and is never multiplied again. Only the fixed
 * prototype durations held here (`PROTOTYPE_MS`, the `riseIn` and `meterFill` choreography, explicit `ms`
 * arguments of the primitives) are scaled, with `literalMs`. A caller of the low-level `animateAtlas` passes
 * resolved milliseconds: `motionMs(el, token)` or `literalMs(el, ms)`.
 *
 * Entrances never keep a fill after they end, so the element shows its own styles again.
 * Exits (`collapseHeight`, `dismissOut`, `dropFromTrigger(…, "out")`, `foldOut`) make the element inert and
 * hidden from assistive technology immediately, keep it invisible when they end and leave its
 * removal to the caller (a Vue `<Transition>` leave hook, or `animation.finished.then(…)`).
 */

/** Values of motion.css at 1×, used when an element has no token in scope (detached, or no CSS loaded). */
export const motionTokenFallbacks = {
  "--atlas-motion-fast": 120,
  "--atlas-motion-base": 200,
  "--atlas-motion-slow": 280,
  "--atlas-motion-menu": 140,
  "--atlas-motion-surface": 160,
  "--atlas-motion-pop": 320,
  "--atlas-motion-glow": 1600,
  "--atlas-motion-loop": 1600,
  "--atlas-motion-spin": 800,
  "--atlas-motion-pulse": 1000,
  "--atlas-motion-defer": 150,
  "--atlas-ease-out": "cubic-bezier(.22, 1, .36, 1)",
  "--atlas-ease-inout": "cubic-bezier(.65, 0, .35, 1)",
  "--atlas-ease-travel": "cubic-bezier(.3, .2, .1, 1)",
  "--atlas-ai-reveal": 420,
  "--atlas-ai-stagger": 130,
  "--atlas-ai-gather": 520,
  "--atlas-ai-morph": 380,
  "--atlas-ai-glow": 1600,
  "--atlas-ai-solidify": 360,
  "--atlas-ai-flight": 1800,
  "--atlas-brand-release-intro": 1700,
} as const;

type DurationToken = { [K in keyof typeof motionTokenFallbacks]: (typeof motionTokenFallbacks)[K] extends number ? K : never }[keyof typeof motionTokenFallbacks];
type EasingToken = "--atlas-ease-out" | "--atlas-ease-inout" | "--atlas-ease-travel";

/**
 * The prototype's fixed durations that have no token (milliseconds at 1×). The primitives scale them with
 * `literalMs`, so they slow down with the preview like the tokens do.
 */
export const PROTOTYPE_MS = {
  /** `collapse()`: a row folds away. */
  collapse: 240,
  /** A panel drops back towards its button. */
  dropOut: 180,
  /** A result icon draws itself. */
  drawIcon: 240,
  /** A finished row flashes once. */
  flashRow: 500,
  /** A match meter fills, and the next one starts this much later. */
  meterFill: 400,
  meterStagger: 80,
  /** A flown copy lands: the target settles from 1.06. */
  settle: 200,
  /** `markerIn()`: the type icon pops in after the ring. */
  markerIcon: 240,
  markerIconDelay: 60,
  /** One warning ripple. */
  ripple: 600,
} as const;

/** The ripple's own curve (prototype `markerIn(…, warn)`). */
const RIPPLE_EASE = "cubic-bezier(.2, .7, .3, 1)";

/** How long a short confirmation stays ("Zkopírováno", "Uloženo"; the prototype's toast hold, plan O12). */
export const FEEDBACK_HOLD_MS = 1600;

/** `riseIn` of cards (the prototype's footer cards): `--atlas-rise` (8 px), 220 ms, 60 ms apart. The default. */
export const RISE_IN_CARDS = { ms: 220, stagger: 60 } as const;
/**
 * `riseIn` of list rows (the prototype's `setRows()`, search results; the page's reveal wave, plan 3.6 „Vlna zjevení“): 8 px, 200 ms,
 * 40 ms apart (a third of `--atlas-motion-fast`); the rows after the eighth start with the eighth.
 */
export const RISE_IN_ROWS = { distance: 8, ms: 200, stagger: 40, cap: 8 } as const;

export interface AtlasAnimationOptions {
  /** A supported pseudo-element, e.g. a native dialog's ::backdrop. */
  pseudoElement?: string;
  /** Resolved milliseconds (`motionMs` or `literalMs`); defaults to `--atlas-motion-base`. */
  duration?: number;
  /** Resolved milliseconds, like `duration`. */
  delay?: number;
  /** A CSS easing; defaults to `--atlas-ease-out`. */
  easing?: string;
  /** Defaults to `backwards`: the first frame covers the delay, nothing is kept after the end. */
  fill?: FillMode;
}

/** Longest animation under reduced motion (the prototype's `anim()`), at 1×. */
export const REDUCED_MOTION_MAX_MS = 160;
const REDUCED_PROPERTIES = ["opacity", "backgroundColor", "offset", "color"] as const;

/** Attribute of a preview (the gallery) that forces reduced motion on everything inside it. */
export const REDUCED_MOTION_PREVIEW = "data-atlas-reduced-motion";
/** Custom property of a preview (the gallery) that slows every token and prototype duration down. */
export const MOTION_SCALE_PROPERTY = "--atlas-motion-scale";

/**
 * Forces reduced motion on `target` (the document root) or lifts it: Atlas's attribute and the editor's
 * `data-md-motion="reduced"`, so the editor's own reduced path (geometry, scrolling, charts) follows too.
 */
export function forceReducedMotion(target: HTMLElement, reduced: boolean) {
  target.toggleAttribute(REDUCED_MOTION_PREVIEW, reduced);
  if (reduced) target.dataset.mdMotion = "reduced";
  else delete target.dataset.mdMotion;
}

/** Whether motion is reduced: the system preference, or a preview around `el` forcing it. */
export function prefersReducedMotion(el?: Element | null) {
  if (el && typeof el.closest === "function" && el.closest(`[${REDUCED_MOTION_PREVIEW}]`)) return true;
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    && window.matchMedia("(prefers-reduced-motion: reduce)")?.matches === true;
}

function computedValue(el: Element, name: string) {
  try {
    return getComputedStyle(el).getPropertyValue(name).trim();
  } catch {
    return "";
  }
}

function timeMs(value: string): number | undefined {
  const match = /^(-?\d*\.?\d+)(ms|s)?$/.exec(value.trim());
  if (!match) return undefined;
  return match[2] === "s" ? Number(match[1]) * 1000 : Number(match[1]);
}

/**
 * A duration token in milliseconds, as resolved for `el`, the preview's slow-down included. The registered
 * token resolves to a time ("0.36s"); an engine without `@property` leaves `calc(120ms * 3)`, read as well.
 */
export function motionMs(el: Element, name: DurationToken): number {
  const value = computedValue(el, name);
  const plain = timeMs(value);
  if (plain !== undefined) return plain;
  const scaled = /^calc\(\s*(\S+?)\s*\*\s*(\d*\.?\d+)\s*\)$/.exec(value);
  const base = scaled ? timeMs(scaled[1]!) : undefined;
  return base !== undefined ? base * Number(scaled![2]) : motionTokenFallbacks[name];
}

export function motionEasing(el: Element, name: EasingToken) {
  return computedValue(el, name) || motionTokenFallbacks[name];
}

/**
 * How much slower motion runs around `el`: 1, unless a preview sets `--atlas-motion-scale`
 * (the gallery's "Zpomalit 3×"). A storyboard's own pauses use it too, so a slowed scene keeps
 * its rhythm.
 */
export function motionScale(el: Element): number {
  const scale = Number.parseFloat(computedValue(el, MOTION_SCALE_PROPERTY));
  return Number.isFinite(scale) && scale > 0 ? scale : 1;
}

/**
 * A fixed prototype duration or delay (not a token) in milliseconds as it runs around `el`: multiplied by
 * the preview's slow-down. Tokens read with `motionMs` are already slowed; never pass them here.
 */
export function literalMs(el: Element, ms: number): number {
  return ms * motionScale(el);
}

/**
 * Timing for what travels `distance` px: a tree that unfolds and the rows it pushes ahead of it. On `--atlas-ease-out`
 * the first frame covers a quarter of the way, which over hundreds of pixels is a jump and not a motion; this leaves
 * from rest (`--atlas-ease-travel`) and takes longer the further it goes: `--atlas-motion-base` × 1.1 for a short
 * branch, up to 1.25× for most of a screen (220 → 250 ms at 1×); the chevron (`.tree-toggle`) takes 1.25× too and ends with it.
 * What folds away (`leaving`) takes the same
 * curve and is a little shorter, as every exit is. Pass the result on to `animateAtlas`.
 */
export function travelTiming(el: Element, distance: number, leaving = false): { duration: number; easing: string } {
  const factor = Math.min(1.25, Math.max(1.1, 0.8 + Math.abs(distance) / 500)) * (leaving ? 0.85 : 1);
  return { duration: motionMs(el, "--atlas-motion-base") * factor, easing: motionEasing(el, "--atlas-ease-travel") };
}

/** `behavior` for `scrollIntoView`/`scrollTo`: an explicit "smooth" would override reduced motion's CSS. */
export function scrollBehavior(el?: Element | null): ScrollBehavior {
  return prefersReducedMotion(el) ? "auto" : "smooth";
}

function reduceFrames(frames: Keyframe[]): Keyframe[] {
  const reduced = frames.map((frame) => {
    const kept: Record<string, unknown> = {};
    for (const property of REDUCED_PROPERTIES) if (property in frame) kept[property] = frame[property];
    return kept as Keyframe;
  });
  return reduced.some((frame) => "opacity" in frame || "backgroundColor" in frame) ? reduced : [{ opacity: 1 }, { opacity: 1 }];
}

function run(el: Element, frames: Keyframe[], options: KeyframeAnimationOptions): Animation | null {
  if (typeof el.animate !== "function") return null;
  try {
    const animation = el.animate(frames, options);
    // A cancelled animation rejects `finished`; that is never an error of the caller's flow.
    animation.finished?.catch(() => {});
    return animation;
  } catch {
    return null;
  }
}

/** The prototype's `anim()`: one Web Animation on Atlas tokens, reduced for reduced motion. */
export function animateAtlas(el: Element | null | undefined, frames: Keyframe[], options: AtlasAnimationOptions = {}): Animation | null {
  if (!el || typeof el.animate !== "function") return null;
  let duration = options.duration ?? motionMs(el, "--atlas-motion-base");
  let easing = options.easing ?? motionEasing(el, "--atlas-ease-out");
  let effective = frames;
  if (prefersReducedMotion(el)) {
    effective = reduceFrames(frames);
    // The cap slows down with the preview, as in the prototype (min(d, 160) × scale).
    duration = Math.min(duration, literalMs(el, REDUCED_MOTION_MAX_MS));
    easing = "linear";
  }
  return run(el, effective, { duration, delay: options.delay ?? 0, easing, fill: options.fill ?? "backwards", ...(options.pseudoElement ? { pseudoElement: options.pseudoElement } : {}) });
}

/**
 * Keeps `el` in `frame` until the returned animation is cancelled. No time passes and nothing moves, so the whole frame
 * applies under reduced motion too (`animateAtlas` would keep only its opacity). For an element that is already in the
 * document but waits for its turn without taking room (the DisclosureGroup of settings/disclosure.ts).
 */
export function holdFrame(el: Element, frame: Keyframe): Animation | null {
  return run(el, [frame, frame], { duration: 0, fill: "forwards" });
}

/** Takes a leaving element out of use at once; it stays visible only to the eye while it animates out. */
function leave(el: Element) {
  el.setAttribute("inert", "");
  el.setAttribute("aria-hidden", "true");
}

/**
 * The element's height as a `height` keyframe sets it: offsetHeight is the border box, which the height of a content-box element
 * leaves its padding and border out of; taken whole, a fold first grew by them (N9, 34 → 40 → 0 px).
 */
function boxHeight(el: HTMLElement) {
  const style = getComputedStyle(el), height = el.offsetHeight;
  if (style.boxSizing !== "content-box") return height;
  return height - [style.paddingTop, style.paddingBottom, style.borderTopWidth, style.borderBottomWidth].reduce((sum, value) => sum + (parseFloat(value) || 0), 0);
}

/**
 * Shows a hidden element and unfolds its height (prototype `reveal()`, `--atlas-motion-slow` 280 ms).
 * The height clips with `overflow: clip`, not `hidden`: a hidden element is a scroll container, so scrolling a row into
 * view inside it (focus, find in page, a pointer) scrolled its content, which jumped back once the animation ended.
 */
export function revealHeight(el: HTMLElement, ms?: number): Animation | null {
  el.hidden = false;
  const height = boxHeight(el);
  return animateAtlas(el, [
    { height: "0px", opacity: 0, overflow: "clip" },
    { height: `${height}px`, opacity: 1, overflow: "clip" },
  ], { duration: ms === undefined ? motionMs(el, "--atlas-motion-slow") : literalMs(el, ms), fill: "none" });
}

/**
 * Content that grew while `revealHeight` was still unfolding the element (a list that loaded meanwhile) would stay
 * clipped to the height measured at the start and appear at once at the end. This unfolds on to the new height from
 * where the unfolding is now, in the time it has left. Null when there is nothing left to extend.
 */
export function extendReveal(el: HTMLElement, unfolding: Animation): Animation | null {
  const end = unfolding.effect?.getComputedTiming().endTime, now = unfolding.currentTime;
  if (unfolding.playState !== "running" || prefersReducedMotion(el) || typeof end !== "number" || typeof now !== "number" || end <= now) return null;
  const from = boxHeight(el), opacity = getComputedStyle(el).opacity;
  unfolding.cancel();
  const to = boxHeight(el);
  return animateAtlas(el, [
    { height: `${from}px`, opacity, overflow: "clip" },
    { height: `${to}px`, opacity: 1, overflow: "clip" },
  ], { duration: end - now, fill: "none" });
}

/**
 * Folds an element away (prototype `collapse()`, 240 ms). The caller removes it afterwards. `mark` false leaves its attributes
 * alone: a block of a live editor, which ProseMirror would redraw (and so drop the animation) on an attribute change.
 */
export function collapseHeight(el: HTMLElement, ms: number = PROTOTYPE_MS.collapse, mark = true): Animation | null {
  const height = boxHeight(el);
  if (mark) leave(el);
  return animateAtlas(el, [
    { height: `${height}px`, opacity: 1, overflow: "clip" },
    { height: "0px", opacity: 0, overflow: "clip", marginTop: "0px", marginBottom: "0px", paddingTop: "0px", paddingBottom: "0px" },
  ], { duration: literalMs(el, ms), fill: "forwards" });
}

/** Applies `mutate` at once and eases the element's height to the new size (prototype `grow()`). */
export function grow(el: HTMLElement, mutate: () => void, ms?: number): Animation | null {
  const before = boxHeight(el);
  mutate();
  const after = boxHeight(el);
  if (before === after) return null;
  return animateAtlas(el, [{ height: `${before}px` }, { height: `${after}px` }], { duration: ms === undefined ? motionMs(el, "--atlas-motion-slow") : literalMs(el, ms), fill: "none" });
}

/**
 * Elements rise into place one after another. Cards by default (`RISE_IN_CARDS`: `--atlas-rise`, 220 ms, 60 ms apart);
 * list rows with `RISE_IN_ROWS` (8 px, 200 ms, 40 ms apart, at most 8 steps). A second column starts later with `delay` (the
 * prototype uses 90 ms). The numbers are the prototype's milliseconds; they slow down with the preview. Under reduced motion
 * everything fades at once (no stagger).
 */
export function riseIn(elements: Iterable<Element>, { distance, ms = RISE_IN_CARDS.ms, stagger = RISE_IN_CARDS.stagger, cap = Infinity, delay = 0, from = 0, rise = true }: {
  /** In px; the `--atlas-rise` token by default. */
  distance?: number; ms?: number; stagger?: number; delay?: number;
  /** How many rows wait a step more than the one before; the ones after start with the last of them. */
  cap?: number;
  /** The opacity it starts from: rows already on the screen (the page's blocks as Upravit hands over, plan 3.5) come up from it. */
  from?: number;
  /** Whether it rises too; off where another motion moves the rows (a FLIP). */
  rise?: boolean;
} = {}): Animation[] {
  const list = [...elements], first = list[0];
  if (!first) return [];
  // The tokens are read once for the wave: read per row, between the rows' animations, each read restyled the page (3.6-G).
  const scale = motionScale(first), easing = motionEasing(first, "--atlas-ease-out"), step = prefersReducedMotion(first) ? 0 : stagger;
  const shift = distance === undefined ? computedValue(first, "--atlas-rise") || "8px" : `${distance}px`;
  const frames = rise ? [{ opacity: from, transform: `translateY(${shift})` }, { opacity: 1, transform: "none" }] : [{ opacity: from }, { opacity: 1 }];
  return list.flatMap((el, index) => animateAtlas(el, frames, { duration: ms * scale, easing, delay: (delay + Math.min(index, cap - 1) * step) * scale }) ?? []);
}

/**
 * A page reveals from the top down (owner 2026-11-02): the first blocks of `root` rise in row after row (`RISE_IN_ROWS`), the rest draw
 * as they are. One wave for every page that draws anew: its first render and the hand-over into editing. At most `limit` rows, so the
 * last one does not wait long. The rows are not measured: finding those in view laid the whole page out before its first paint (206 ms
 * at 4x CPU, 3.6 perf). A page draws anew from its top, and a row of these below the fold settles unseen.
 */
export function revealWave(root: Element | null | undefined, limit = RISE_IN_ROWS.cap): Animation[] {
  // The text is on screen at once (owner: show fast); the wave only settles it, so the first paint and LCP are not held back.
  return riseIn([...root?.children ?? []].slice(0, limit), { ...RISE_IN_ROWS, from: 0.6 });
}

/** The dashes of a suggestion outline join into a solid line: the suggestion became a running task (360 ms). */
export function solidify(rect: SVGElement): Animation | null {
  // The outline's own stroke (light or dark mode) is where the colour starts.
  const from = computedValue(rect, "stroke");
  rect.style.strokeDasharray = "11 0";
  rect.style.stroke = "var(--line)";
  return animateAtlas(rect, [
    { strokeDasharray: "6 5", ...(from && from !== "none" ? { stroke: from } : {}) },
    { strokeDasharray: "11 0", stroke: "var(--line)" },
  ], { duration: motionMs(rect, "--atlas-ai-solidify"), easing: motionEasing(rect, "--atlas-ease-inout") });
}

/**
 * An accepted change glows once where it happened (1.6 s). Under reduced motion it becomes the
 * prototype's glowfade: the background alone, same length, since a fading tint is not movement.
 * It ends on the element's own background and keeps no fill.
 */
export function glow(el: Element): Animation | null {
  const reduced = prefersReducedMotion(el);
  const frames: Keyframe[] = reduced
    ? [{ offset: 0, backgroundColor: "var(--atlas-ai-glow-color)" }]
    : [
      { offset: 0, backgroundColor: "var(--atlas-ai-glow-color)", boxShadow: "0 0 0 3px var(--atlas-ai-glow-color)" },
      { offset: 1, boxShadow: "0 0 0 3px transparent" },
    ];
  return run(el, frames, { duration: motionMs(el, "--atlas-ai-glow"), easing: motionEasing(el, "--atlas-ease-out"), fill: "backwards" });
}

/** A panel opens from its trigger (−8 px, scale .97, 200 ms) or closes back towards it (−6 px, .98, 180 ms). */
export function dropFromTrigger(panel: HTMLElement, direction: "in" | "out"): Animation | null {
  if (direction === "in") {
    return animateAtlas(panel, [
      { opacity: 0, transform: "translateY(-8px) scale(.97)" },
      { opacity: 1, transform: "none" },
    ], { duration: motionMs(panel, "--atlas-motion-base") });
  }
  leave(panel);
  return animateAtlas(panel, [
    { opacity: 1 },
    { opacity: 0, transform: "translateY(-6px) scale(.98)" },
  ], { duration: literalMs(panel, PROTOTYPE_MS.dropOut), fill: "forwards" });
}

const DRAWN_SHAPES = "path, circle, line, polyline, polygon, rect, ellipse";

/** A result icon draws itself where a spinner was: every stroke from its own length to 0 (240 ms). */
export function drawIcon(svg: SVGElement): Animation[] {
  return [...svg.querySelectorAll<SVGGeometryElement>(DRAWN_SHAPES)].flatMap((shape) => {
    const length = typeof shape.getTotalLength === "function" ? shape.getTotalLength() : 40;
    shape.style.strokeDasharray = String(length);
    shape.style.strokeDashoffset = "0";
    if (typeof shape.animate !== "function") return [];
    return animateAtlas(shape, [{ strokeDashoffset: length }, { strokeDashoffset: 0 }], { duration: literalMs(shape, PROTOTYPE_MS.drawIcon) }) ?? [];
  });
}

/**
 * A row that just got its result flashes once: the hover colour fades to transparent (500 ms;
 * prototype "Založení a publikace", the rows of the pre-publish check). It ends on the row's own
 * background. Under reduced motion it stays a background fade, at most 160 ms.
 */
export function flashRow(row: Element, ms: number = PROTOTYPE_MS.flashRow): Animation | null {
  return animateAtlas(row, [{ backgroundColor: "var(--hover)" }, { backgroundColor: "transparent" }], { duration: literalMs(row, ms) });
}

/** Match meters fill from the left one after another (400 ms, 80 ms apart). */
export function meterFill(bars: Iterable<Element>, { ms = PROTOTYPE_MS.meterFill, stagger = PROTOTYPE_MS.meterStagger }: { ms?: number; stagger?: number } = {}): Animation[] {
  return [...bars].flatMap((bar, index) => {
    (bar as HTMLElement).style.transformOrigin = "0 50%";
    return animateAtlas(bar, [{ transform: "scaleX(0)" }, { transform: "scaleX(1)" }], { duration: literalMs(bar, ms), delay: literalMs(bar, index * stagger) }) ?? [];
  });
}

/** A new status pops in: "Publikováno" (scale .8 → 1.06 → 1, `--atlas-motion-pop` 320 ms; CSS: `st-pop`). */
export function popStatus(el: Element): Animation | null {
  return animateAtlas(el, [
    { transform: "scale(.8)", opacity: 0 },
    { transform: "scale(1.06)", opacity: 1, offset: 0.6 },
    { transform: "none" },
  ], { duration: motionMs(el, "--atlas-motion-pop") });
}

/** A dismissed suggestion leaves quickly and without a trace (scale .6 and fade, 160 ms). */
export function dismissOut(el: Element): Animation | null {
  leave(el);
  return animateAtlas(el, [
    { opacity: 1 },
    { opacity: 0, transform: "scale(.6)" },
  ], { duration: motionMs(el, "--atlas-motion-surface"), fill: "forwards" });
}

/**
 * A copy of `html` flies from `from` to `to` along an arc (prototype `fly()`, 520 ms). `to` stays in
 * the DOM and accessible the whole time; it is only kept transparent until the copy lands and then
 * settles from 1.06 (200 ms). Returns the flight; the copy removes itself.
 */
export function flyGhost(from: Element, to: Element, html: string, ms?: number): Animation | null {
  const host = to.closest(".atlas, [data-atlas-motion]") ?? document.body;
  const template = document.createElement("template");
  template.innerHTML = html.trim();
  const ghost = document.createElement("div");
  ghost.append(template.content);
  ghost.setAttribute("aria-hidden", "true");
  ghost.setAttribute("data-atlas-motion", "");
  Object.assign(ghost.style, { position: "fixed", left: "0", top: "0", zIndex: "60", pointerEvents: "none", willChange: "transform" });
  host.append(ghost);

  const duration = ms === undefined ? motionMs(to, "--atlas-ai-gather") : literalMs(to, ms);
  // A transformed ancestor (a page that rose in keeps its transform) makes it the containing block
  // of a fixed element, so place the copy relative to where its (0, 0) actually is.
  const origin = ghost.getBoundingClientRect();
  const offset = (rect: DOMRect) => ({ left: rect.left - origin.left, top: rect.top - origin.top });
  const a = offset(from.getBoundingClientRect());
  const b = offset(to.getBoundingClientRect());
  ghost.style.transform = `translate(${a.left}px,${a.top}px)`;
  const flight = animateAtlas(ghost, [
    { transform: `translate(${a.left}px,${a.top}px) scale(.92)`, opacity: 0 },
    { opacity: 1, offset: 0.18 },
    { transform: `translate(${(a.left + b.left) / 2}px,${Math.min(a.top, b.top) - 26}px) scale(1.04)`, offset: 0.55 },
    { transform: `translate(${b.left}px,${b.top}px) scale(1)`, opacity: 1 },
  ], { duration, easing: motionEasing(to, "--atlas-ease-inout"), fill: "forwards" });

  const settle = literalMs(to, PROTOTYPE_MS.settle);
  const landing = duration / (duration + settle);
  animateAtlas(to, [
    { opacity: 0, transform: "scale(1.06)" },
    { opacity: 0, transform: "scale(1.06)", offset: landing },
    { opacity: 1, transform: "scale(1.06)", offset: landing, easing: motionEasing(to, "--atlas-ease-out") },
    { opacity: 1, transform: "none" },
  ], { duration: duration + settle, easing: "linear" });

  if (flight) void flight.finished.then(() => ghost.remove(), () => ghost.remove());
  else ghost.remove();
  return flight;
}

/**
 * Text turns into a component in place (prototype `morphChip()`, 380 ms): `span` is replaced by
 * `element` at once, which then eases from the text's width with a slight overshoot, and glows.
 * Returns the morph; the glow runs alongside it.
 */
export function morphInto(span: Element, element: HTMLElement, ms?: number): Animation | null {
  const from = span.getBoundingClientRect().width;
  span.replaceWith(element);
  const to = element.getBoundingClientRect().width;
  const morph = animateAtlas(element, [
    { width: `${from}px`, opacity: 0.35, transform: "scale(.96)" },
    { width: `${to + 3}px`, opacity: 1, transform: "scale(1.03)", offset: 0.7 },
    { width: `${to}px`, opacity: 1, transform: "none" },
  ], { duration: ms === undefined ? motionMs(element, "--atlas-ai-morph") : literalMs(element, ms), fill: "none" });
  glow(element);
  return morph;
}

/**
 * Finished, verified text appears sentence by sentence (prototype `revealWords()`): each child of
 * `container` rises 6 px out of a 4 px blur (420 ms), 130 ms after the previous one. This is not
 * token streaming; the whole text is in the DOM before the first sentence moves.
 */
export function revealSentences(container: Element, options: { ms?: number; stagger?: number } = {}): Animation[] {
  const ms = options.ms === undefined ? motionMs(container, "--atlas-ai-reveal") : literalMs(container, options.ms);
  const stagger = options.stagger === undefined ? motionMs(container, "--atlas-ai-stagger") : literalMs(container, options.stagger);
  return [...container.children].flatMap((sentence, index) => animateAtlas(sentence, [
    { opacity: 0, filter: "blur(4px)", transform: "translateY(6px)" },
    { opacity: 1, filter: "blur(0)", transform: "none" },
  ], { duration: ms, delay: index * stagger }) ?? []);
}

/**
 * A suggestion marker appears where it stays (prototype `markerIn()`): its dashed ring spins into
 * place (−140°, scale .5 → 1, 280 ms) and the type icon pops in 60 ms later (scale .4 → 1, 240 ms).
 * Nothing around it moves and focus stays where it was. Markers in the editor come from the assist
 * module (plan I2); this is for markers outside it and for the gallery.
 */
export function markerIn(ring: Element, icon?: Element | null): Animation[] {
  return [
    animateAtlas(ring, [{ transform: "rotate(-140deg) scale(.5)", opacity: 0 }, { transform: "none", opacity: 1 }], { duration: motionMs(ring, "--atlas-motion-slow") }),
    icon ? animateAtlas(icon, [{ opacity: 0, transform: "scale(.4)" }, { opacity: 1, transform: "none" }], { duration: literalMs(icon, PROTOTYPE_MS.markerIcon), delay: literalMs(icon, PROTOTYPE_MS.markerIconDelay) }) : null,
  ].filter((animation): animation is Animation => !!animation);
}

/**
 * A risk (a password in the text) gets one ripple around its marker, never a blinking loop:
 * `el` is the ring overlay, from .7 to transparent at 2.4× its size (600 ms). It keeps no fill.
 */
export function rippleOnce(el: Element): Animation | null {
  return animateAtlas(el, [{ opacity: 0.7, transform: "scale(1)" }, { opacity: 0, transform: "scale(2.4)" }], { duration: literalMs(el, PROTOTYPE_MS.ripple), easing: RIPPLE_EASE });
}

/**
 * A card moves to its new place without reloading its content (prototype tile "Přemístění": the
 * answer card into the Asistent panel, 380 ms ease-in-out). `mutate` puts it there at once; the card
 * then travels from where it was, easing its size from the old one.
 */
export function relocate(el: HTMLElement, mutate: () => void, ms?: number): Animation | null {
  const before = el.getBoundingClientRect();
  mutate();
  const after = el.getBoundingClientRect();
  const dx = before.left - after.left, dy = before.top - after.top;
  if (!dx && !dy && before.width === after.width && before.height === after.height) return null;
  return animateAtlas(el, [
    { transform: `translate(${dx}px,${dy}px)`, width: `${before.width}px`, height: `${before.height}px` },
    { transform: "none", width: `${after.width}px`, height: `${after.height}px` },
  ], { duration: ms === undefined ? motionMs(el, "--atlas-ai-morph") : literalMs(el, ms), easing: motionEasing(el, "--atlas-ease-inout"), fill: "none" });
}

function cornerRadius(el: Element, fallback: number) {
  try {
    const radius = Number.parseFloat(getComputedStyle(el).borderTopLeftRadius);
    return Number.isFinite(radius) && radius > 0 ? radius : fallback;
  } catch {
    return fallback;
  }
}

/**
 * How far the element's own drop shadows reach past each edge of its border box: offset, spread and the blur
 * (a Gaussian of σ = blur / 2, spent at 3 σ). Inner shadows stay inside the box.
 */
function shadowReach(el: Element) {
  const reach = { top: 0, right: 0, bottom: 0, left: 0 };
  let value = "";
  try { value = getComputedStyle(el).boxShadow ?? ""; } catch { return reach; }
  if (!value || value === "none") return reach;
  // One shadow per comma outside a colour function; its lengths are x, y, blur and spread in that order.
  for (const shadow of value.split(/,(?![^(]*\))/)) {
    if (/\binset\b/.test(shadow)) continue;
    const lengths = shadow.replace(/[a-z-]+\([^)]*\)/gi, " ").split(/\s+/).filter((part) => /^-?(?:\d+\.?\d*|\.\d+)(?:px)?$/.test(part)).map(Number.parseFloat);
    const [x = 0, y = 0, blur = 0, spread = 0] = lengths;
    const tail = spread + blur * 1.5;
    reach.top = Math.max(reach.top, tail - y);
    reach.right = Math.max(reach.right, tail + x);
    reach.bottom = Math.max(reach.bottom, tail + y);
    reach.left = Math.max(reach.left, tail - x);
  }
  return { top: Math.ceil(reach.top), right: Math.ceil(reach.right), bottom: Math.ceil(reach.bottom), left: Math.ceil(reach.left) };
}

/**
 * The clip of a fully unfolded panel: its whole box and the reach of its shadow. `unfoldFrom` ends on it and a
 * reversed fold returns to it (AppDialog), so taking the clip away when the animation ends changes nothing. A
 * clip that ended on the border box cut the shadow for the whole unfolding, and the shadow appeared in one frame
 * afterwards (the search palette: the strip under it went from 131.8 to 116.2 of luminance in that frame).
 */
export function unfoldedClip(panel: Element): string {
  const { top, right, bottom, left } = shadowReach(panel);
  return `inset(${-top}px ${-right}px ${-bottom}px ${-left}px round ${cornerRadius(panel, 14)}px)`;
}

/** Whether a reader can see the trigger: not hidden (a collapsed sidebar keeps its layout box) and inside the window. */
function seen(trigger: Element, box: DOMRect) {
  let visibility = "visible";
  try { visibility = getComputedStyle(trigger).visibility; } catch { /* an engine without computed styles: trust the box */ }
  if (visibility === "hidden" || visibility === "collapse") return false;
  const width = typeof window === "undefined" ? Infinity : window.innerWidth, height = typeof window === "undefined" ? Infinity : window.innerHeight;
  return box.right > 0 && box.bottom > 0 && box.left < width && box.top < height;
}

/**
 * A panel unfolds out of the control that opened it (prototype "Hledání s odpovědí", moment 1: the search
 * grows from the search field). It takes `--atlas-motion-base`, 200 ms, like every dialog (board „Pohyb
 * v refreshi“: the search and the dialogs open together; owner decision 2026-10-10, the prototype had 280 ms).
 * A clip-path opens from the trigger's rectangle (its radius, else 8 px) to the whole panel and the reach of its
 * shadow (`unfoldedClip`) while the opacity goes .7 → 1.
 *
 * A trigger inside the panel is where the window starts. One beside it and near (the sidebar's search field,
 * left of the palette on a laptop) gets a window of its size in the panel's nearest corner and the panel is
 * carried there, so the first frame is the trigger's own box, and travels to its place while it opens: every
 * edge moves one way only, nothing overshoots and the final position is untouched.
 *
 * Near means no further than the trigger's own larger side (owner decision 2026-10-11): a field 209 px wide
 * carries the palette 70 px at 1280 and 150 px at 1440, but not 390 px at 1920 or 1,150 px at 3440, where a
 * panel crossing the screen in 200 ms is the opposite of calm. Further away, and from a trigger nobody sees
 * (a collapsed sidebar, a closed drawer, off the window), the panel opens in place from its nearest edge; there
 * is no third look in between. Without any trigger only the opacity changes. Reduced motion keeps the opacity,
 * at most 160 ms.
 */
export function unfoldFrom(panel: HTMLElement, trigger?: Element | null): Animation | null {
  panel.hidden = false;
  const to = panel.getBoundingClientRect();
  const from = trigger?.getBoundingClientRect();
  const duration = motionMs(panel, "--atlas-motion-base");
  if (!trigger || !from || !from.width || !from.height || !to.width || !to.height) {
    return animateAtlas(panel, [{ opacity: 0.7 }, { opacity: 1 }], { duration });
  }
  const clamp = (value: number, size: number) => Math.round(Math.min(Math.max(value, 0), size));
  const radius = cornerRadius(trigger, 8);
  // In place: the trigger's rectangle clamped into the panel, so a trigger outside it opens the nearest edge.
  const inPlace = () => {
    const top = clamp(from.top - to.top, to.height);
    const left = clamp(from.left - to.left, to.width);
    const bottom = clamp(to.bottom - from.bottom, to.height - top);
    const right = clamp(to.right - from.right, to.width - left);
    return animateAtlas(panel, [
      { clipPath: `inset(${top}px ${right}px ${bottom}px ${left}px round ${radius}px)`, opacity: 0.7 },
      { clipPath: unfoldedClip(panel), opacity: 1 },
    ], { duration });
  };
  if (!seen(trigger, from)) return inPlace();
  const width = Math.min(from.width, to.width), height = Math.min(from.height, to.height);
  const left = clamp(from.left - to.left, to.width - width);
  const top = clamp(from.top - to.top, to.height - height);
  const right = Math.round(to.width - left - width), bottom = Math.round(to.height - top - height);
  const dx = Math.round(from.left - to.left - left), dy = Math.round(from.top - to.top - top);
  if (Math.hypot(dx, dy) > Math.max(from.width, from.height)) return inPlace();
  const travels = dx !== 0 || dy !== 0;
  return animateAtlas(panel, [
    { clipPath: `inset(${top}px ${right}px ${bottom}px ${left}px round ${radius}px)`, opacity: 0.7, ...(travels ? { transform: `translate(${dx}px, ${dy}px)` } : {}) },
    { clipPath: unfoldedClip(panel), opacity: 1, ...(travels ? { transform: "none" } : {}) },
  ], { duration });
}

/**
 * The panel `unfoldFrom` opened closes the way every dialog does: it sinks 8 px to .98 and fades over
 * `--atlas-motion-surface`, 160 ms, shorter than the opening on the same curve (the prototype closed the search
 * in 240 ms at .985). A fold that interrupts the unfolding keeps the clip it had reached. Like every exit it is
 * inert and hidden from assistive technology at once and stays invisible; the caller hides or removes it when
 * the animation ends.
 */
export function foldOut(panel: HTMLElement, from: Keyframe = { opacity: 1, transform: "none" }): Animation | null {
  leave(panel);
  return animateAtlas(panel, [
    from,
    { opacity: 0, transform: "translateY(8px) scale(.98)", ...(from.clipPath ? { clipPath: from.clipPath } : {}) },
  ], { duration: motionMs(panel, "--atlas-motion-surface"), fill: "forwards" });
}

/** The paper plane of sending (the composer's send icon), pointing up and right as icons of sending do. 24 × 24. */
export const PAPER_PLANE_ICON_PATHS = ["M14.536 21.686a.5.5 0 0 0 .937-.024l6.5-19a.496.496 0 0 0-.635-.635l-19 6.5a.5.5 0 0 0-.024.937l7.93 3.18a2 2 0 0 1 1.112 1.11z", "m21.854 2.147-10.94 10.939"] as const;

/** The flying copy: the same plane turned to head right (+x), so it can turn along its path; paper with an ink edge. */
const PAPER_PLANE_SVG = `<svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true" style="display:block;overflow:visible">`
  + `<g data-plane transform="rotate(45 12 12)" fill="var(--surface, #fff)" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round">`
  + `<path d="${PAPER_PLANE_ICON_PATHS[0]}"/><path d="${PAPER_PLANE_ICON_PATHS[1]}" fill="none"/></g></svg>`;

/**
 * A sent question leaves as a paper plane (a folded message): the send icon `icon` itself takes off, where it sits and as it
 * points, climbs, swings once through a light loop, banking into it, and glides off, shrinking and fading into the distance.
 * It keeps the reader company while the answer is prepared. Pure decoration on a fixed layer above everything: it never takes
 * focus, room or a pointer, sending never waits for it, and under reduced motion there is no flight at all (null; the caller
 * then leaves its icon in place). It heads slightly left of straight up, into the panel, and turns along its own tangent.
 * The caller hides its icon for as long as the returned flight runs; the plane removes itself.
 */
export function paperPlaneFlight(icon: Element): Animation | null {
  if (prefersReducedMotion(icon) || typeof icon.animate !== "function") return null;
  const host = icon.closest(".atlas, [data-atlas-motion]") ?? document.body;
  const plane = document.createElement("div");
  plane.innerHTML = PAPER_PLANE_SVG;
  plane.setAttribute("aria-hidden", "true");
  plane.setAttribute("data-atlas-motion", "");
  plane.setAttribute("data-paper-plane", "");
  Object.assign(plane.style, { position: "fixed", left: "0", top: "0", zIndex: "60", pointerEvents: "none", willChange: "transform, opacity", color: "var(--ai-ink, var(--accent))", filter: "drop-shadow(0 2px 3px color-mix(in srgb, var(--text, #000) 14%, transparent))" });
  host.append(plane);

  // As in flyGhost: a transformed ancestor becomes the containing block of a fixed element.
  const origin = plane.getBoundingClientRect();
  const box = icon.getBoundingClientRect();
  const x0 = box.left + box.width / 2 - origin.left - 12;
  const y0 = box.top + box.height / 2 - origin.top - 12;
  // It leaves at the icon's size and heading (up and right, −45°) and becomes its own size and course within the first tenth.
  const startScale = box.width > 0 ? box.width / 24 : 0.67;
  const heading = { x: -0.34, y: -0.94 };
  const left = { x: -0.94, y: 0.34 };
  // Three parts: it climbs off the button (easing out of the push), swings through one loop to the left while its own way
  // forward nearly stops, then glides off ever faster. The loop is a circle travelled once, so it ends where it began.
  const radius = 22;
  const at = (t: number) => {
    const climb = Math.min(t / 0.25, 1);
    const glide = Math.max(0, (t - 0.6) / 0.4);
    const along = 90 * (1 - (1 - climb) ** 2) + 30 * t + 260 * glide * glide;
    const turn = 2 * Math.PI * Math.min(1, Math.max(0, (t - 0.25) / 0.38));
    const forward = radius * Math.sin(turn), aside = radius * (1 - Math.cos(turn));
    return { x: x0 + heading.x * (along + forward) + left.x * aside, y: y0 + heading.y * (along + forward) + left.y * aside };
  };
  const steps = 48;
  const frames: Keyframe[] = Array.from({ length: steps + 1 }, (_, index) => {
    const t = index / steps;
    const point = at(t);
    const ahead = at(Math.min(1, t + 0.01)), behind = at(Math.max(0, t - 0.01));
    const course = Math.atan2(ahead.y - behind.y, ahead.x - behind.x) * 180 / Math.PI;
    const settle = Math.min(1, t / 0.12);
    const ease = settle * settle * (3 - 2 * settle);
    const angle = -45 + (course + 45) * ease;
    const away = Math.max(0, (t - 0.12) / 0.88);
    const size = startScale + (1 - startScale) * ease - 0.5 * away * away;
    // It banks into the loop (the wings seen narrower), levels out after it.
    const bank = 1 - 0.38 * Math.sin(Math.PI * Math.min(1, Math.max(0, (t - 0.25) / 0.38)));
    const opacity = t > 0.66 ? Math.max(0, (1 - t) / 0.34) : 1;
    return { transform: `translate(${point.x}px, ${point.y}px) rotate(${angle}deg) scale(${size}, ${size * bank})`, opacity, offset: t };
  });
  plane.style.opacity = "0";
  const flight = run(plane, frames, { duration: motionMs(icon, "--atlas-ai-flight"), easing: "linear", fill: "forwards" });
  if (flight) void flight.finished.then(() => plane.remove(), () => plane.remove());
  else plane.remove();
  return flight;
}

const glowFrames: Keyframe[] = [
  { offset: 0, backgroundColor: "var(--atlas-ai-glow-color)", boxShadow: "0 0 0 3px var(--atlas-ai-glow-color)" },
  { offset: 1, boxShadow: "0 0 0 3px transparent" },
];

/** `glow`, but capped like every other animation under reduced motion (spec §1.9: a fade of at most 160 ms). */
export function softGlow(element: Element, delay = 0) {
  return animateAtlas(element, glowFrames, { duration: motionMs(element, "--atlas-motion-glow"), delay });
}

/** Brings a block into view and lets it glow once, so the eye finds it. */
export function revealBlock(element: HTMLElement) {
  // A lazy part of the editor it is in builds its full look before it is seen (@atlas/markdown proximity.ts).
  element.dispatchEvent(new Event("md-reveal", { bubbles: true }));
  element.scrollIntoView({ behavior: scrollBehavior(element), block: "center" });
  softGlow(element);
}
