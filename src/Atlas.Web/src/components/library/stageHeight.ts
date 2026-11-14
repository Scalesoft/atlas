import { onBeforeUnmount, type Ref } from "vue";
import { animateAtlas, motionMs, motionTokenFallbacks } from "../../motion/aiMotion";

const BUSY = '[aria-busy="true"]';

/**
 * Hooks for a `<Transition>` whose old and new child share one grid cell (the tabs of Knihovna, Karty / Seznam):
 * the two cross-fade there, and the cell eases from the old height to the new one instead of collapsing in one
 * frame when the old child is removed (everything below it would jump). Each child must have `align-self: start`,
 * so its own height is its natural one.
 *
 * A cell that is to get shorter holds its height while the old child fades out (`--atlas-motion-fast`), so that child
 * is not cut off at its bottom while it is still visible, and the real height of what arrives has had that time to
 * show; a part that is still loading (`aria-busy`) is waited for up to four times as long, because its placeholder
 * says nothing about its real height. Content that arrives while the height still moves (a list replacing its
 * skeleton) re-aims the same animation from where it is and at the speed it has, in the direction it is going;
 * against that direction it turns on the curve that eases both ends. A re-aim at the height it already aims at is no
 * re-aim. Once the cell has settled, later growth is left alone. Reduced motion keeps only the fade (animateAtlas
 * drops the height).
 */
export function useStageHeight(stage: Ref<HTMLElement | null | undefined>) {
  let animation: Animation | null = null;
  let observer: ResizeObserver | undefined;
  let from = 0;
  let aimedAt = 0;
  let release: ReturnType<typeof setTimeout> | undefined;
  // When the shrink that is being held started to wait; undefined when nothing is held.
  let holdStart: number | undefined;
  // The last two heights of the cell while it moves (one per frame): its speed when the animation is re-aimed.
  let samples: { at: number; height: number }[] = [];
  let sampling = 0;

  function sample() {
    const node = stage.value;
    if (!node || !animation) { sampling = 0; return; }
    samples = [...samples.slice(-1), { at: performance.now(), height: node.getBoundingClientRect().height }];
    sampling = requestAnimationFrame(sample);
  }
  /** Pixels per millisecond, signed; 0 before there are two samples. */
  function speed() {
    const [before, now] = samples;
    return before && now ? (now.height - before.height) / Math.max(1, now.at - before.at) : 0;
  }
  function settle() {
    observer?.disconnect();
    observer = undefined;
    animation = null;
    samples = [];
    holdStart = undefined;
    clearTimeout(release);
    cancelAnimationFrame(sampling);
    sampling = 0;
  }
  /** The curve of a re-aim: the speed it has, or a rest-to-rest curve when it has to go back. */
  function continuing(node: HTMLElement, moving: number, distance: number, duration: number) {
    if (Math.abs(moving) < 0.05) return undefined;
    if (Math.sign(moving) !== Math.sign(distance)) return getComputedStyle(node).getPropertyValue("--atlas-ease-inout").trim() || motionTokenFallbacks["--atlas-ease-inout"];
    // The first control point of the ease-out token is (.22, 1), a slope of 4.5; here it has the slope that gives `moving`.
    const slope = (Math.abs(moving) * duration) / Math.abs(distance);
    return `cubic-bezier(0.22, ${Math.min(1, slope * 0.22).toFixed(3)}, 0.36, 1)`;
  }
  /**
   * A shrink waits (its animation has a long delay and holds its first frame): after the old part has faded it starts, at
   * once if the new part is loaded, else when it is, at the latest after `cap`. The real height, if it comes first,
   * re-aims the animation from the held one.
   */
  function hold(held: Animation, entering: HTMLElement, fade: number, cap: number, started: number) {
    const check = () => {
      if (animation !== held) return;
      const waited = performance.now() - started;
      if (waited < cap && (entering.matches(BUSY) || entering.querySelector(BUSY))) { release = setTimeout(check, 40); return; }
      holdStart = undefined;
      held.effect?.updateTiming({ delay: waited });
    };
    release = setTimeout(check, Math.max(0, fade - (performance.now() - started)));
  }
  function aim(entering: HTMLElement, start?: number) {
    const node = stage.value;
    if (!node) return;
    const first = start !== undefined;
    const current = start ?? node.getBoundingClientRect().height;
    const natural = entering.offsetHeight;
    const moving = first ? 0 : speed();
    const distance = natural - current;
    if (!first && Math.abs(natural - aimedAt) < 1) return;
    animation?.cancel();
    clearTimeout(release);
    aimedAt = natural;
    if (Math.abs(distance) < 1) { settle(); return; }
    const duration = motionMs(node, "--atlas-motion-base");
    const fade = motionMs(node, "--atlas-motion-fast");
    // A shrink that is still being held (the old part is fading, the new one loading) stays held when the placeholder's
    // height changes; growing, or a held shrink that has been released, does not wait.
    if (first) holdStart = distance < 0 ? performance.now() : undefined;
    else if (distance >= 0) holdStart = undefined;
    const cap = fade * 4;
    const waited = holdStart === undefined ? 0 : performance.now() - holdStart;
    const holding = holdStart !== undefined && waited < cap;
    const next = animateAtlas(node, [
      { height: `${current}px`, overflow: "clip" },
      { height: `${natural}px`, overflow: "clip" },
    ], { duration, delay: holding ? cap - waited : 0, easing: first ? undefined : continuing(node, moving, distance, duration), fill: "backwards" });
    animation = next;
    if (!next) { settle(); return; }
    next.addEventListener("finish", () => { if (animation === next) settle(); });
    if (holding) hold(next, entering, fade, cap, holdStart!);
    if (!sampling) { samples = []; sampling = requestAnimationFrame(sample); }
  }

  /** `@before-leave` of the Transition: the cell still holds the old child alone, so this is its height. */
  function onBeforeLeave() {
    from = stage.value?.getBoundingClientRect().height ?? 0;
  }
  /** `@enter` of the Transition: both children are in the cell; aim at the new one's natural height. */
  function onEnter(element: Element) {
    const entering = element as HTMLElement;
    observer?.disconnect();
    aim(entering, from);
    if (!animation || typeof ResizeObserver !== "function") return;
    observer = new ResizeObserver(() => { if (animation) aim(entering); });
    observer.observe(entering);
  }
  onBeforeUnmount(() => { animation?.cancel(); settle(); });

  return { onBeforeLeave, onEnter };
}
