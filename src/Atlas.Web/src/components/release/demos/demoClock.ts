import { onBeforeUnmount, onMounted, type Ref } from "vue";
import { motionScale, prefersReducedMotion } from "../../../motion/aiMotion";

/** One scheduled step of a demo's round, in ms from the start of the round. */
export type At = (ms: number, step: () => void) => void;

/**
 * Plays a release demo (components/release/demos) round after round: `round(at)` resets the picture, schedules its steps and returns
 * the length of one round in ms (Atlas's slowed preview stretches it). Under reduced motion, the system's or Atlas's own, or inside
 * `[data-still]`, only `still()` draws the finished picture. Everything stops when the demo leaves.
 */
export function useDemoLoop(root: Ref<HTMLElement | undefined>, round: (at: At) => number, still: () => void) {
  const timers = new Set<ReturnType<typeof setTimeout>>();
  let stopped = false;
  const later = (ms: number, step: () => void) => {
    const timer = setTimeout(() => { timers.delete(timer); if (!stopped) step(); }, ms);
    timers.add(timer);
  };
  function play() {
    const scale = motionScale(root.value!);
    const length = round((ms, step) => later(ms * scale, step));
    later(length * scale, play);
  }
  onMounted(() => {
    const el = root.value!;
    if (prefersReducedMotion(el) || el.closest("[data-still]")) return still();
    // A host that brings the demo in with motion of its own (ReleaseSplash's cards) says when it stands: the first round waits for it.
    const at = Number(el.closest<HTMLElement>("[data-demo-start]")?.dataset.demoStart);
    if (at > performance.now()) later(at - performance.now(), play);
    else play();
  });
  onBeforeUnmount(() => { stopped = true; timers.forEach(clearTimeout); });
}

/** Types `text` into `set` from `from` ms, one character every `every` ms, as a person types; returns when it ends. */
export function typeText(at: At, from: number, text: string, set: (text: string) => void, every = 75): number {
  const chars = [...text];
  chars.forEach((_, index) => at(from + index * every, () => set(chars.slice(0, index + 1).join(""))));
  return from + chars.length * every;
}
