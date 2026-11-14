import type { Ref } from "vue";
import { animateAtlas, motionMs } from "../motion/aiMotion";

/**
 * The box around a swapping view eases from the height of the view that left to the one that arrives, so nothing below it jumps.
 * Wire `rememberHeight` to the Transition's `before-leave` and `easeHeight` to its `enter`.
 */
export function useHeightEase(box: Ref<HTMLElement | undefined>) {
  let leavingHeight = 0;
  return {
    rememberHeight() { leavingHeight = box.value?.offsetHeight ?? 0; },
    easeHeight() {
      const node = box.value;
      const from = leavingHeight;
      leavingHeight = 0;
      if (!node || !from) return;
      const to = node.offsetHeight;
      if (Math.abs(to - from) < 2) return;
      animateAtlas(node, [{ height: `${from}px`, overflow: "clip" }, { height: `${to}px`, overflow: "clip" }], { duration: motionMs(node, "--atlas-motion-base"), fill: "none" });
    },
  };
}
