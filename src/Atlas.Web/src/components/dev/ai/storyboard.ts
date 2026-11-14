import { onBeforeUnmount, type Ref } from "vue";
import { motionScale } from "../../../motion/aiMotion";

/**
 * Pauses of a gallery storyboard (the beats between the moments of a prototype scene). They follow
 * the page's "Zpomalit 3×" like the animations do, and stop when the example unmounts (replay,
 * a switch, leaving the page): a pending pause then never resolves, so the scene simply ends.
 * Gallery-only; components never wait like this.
 */
export function useStoryboard(root: Ref<HTMLElement | undefined>) {
  const timers = new Set<number>();
  onBeforeUnmount(() => {
    for (const timer of timers) window.clearTimeout(timer);
    timers.clear();
  });
  function sleep(ms: number) {
    return new Promise<void>((resolve) => {
      const timer = window.setTimeout(() => { timers.delete(timer); resolve(); }, ms * (root.value ? motionScale(root.value) : 1));
      timers.add(timer);
    });
  }
  /** Waits for an animation (or several) to end; a cancelled one counts as ended. */
  async function settle(animations: Animation | null | undefined | (Animation | null | undefined)[]) {
    const list = Array.isArray(animations) ? animations : [animations];
    await Promise.all(list.map((animation) => animation?.finished.catch(() => undefined)));
  }
  return { sleep, settle };
}
