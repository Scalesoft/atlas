<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from "vue";
import { animateAtlas, motionMs } from "../../../motion/aiMotion";

/**
 * The life of a card's content while it resolves (spec §5): `pending` shows its placeholder only after `--atlas-motion-defer`, so
 * an answer that comes sooner never flashes one, and the content (`ready`, or `locked` for a target the reader may not see)
 * cross-fades with whatever was there: the two layers lie on top of each other in one grid cell while one fades out and the
 * other in. Content that is there from the first render stands at once. Reduced motion keeps a fade of at most 160 ms
 * (`animateAtlas`); the tokens, and a preview's slow-down, are read from the element.
 * `src/motion/crossFade.ts` is not used on purpose: it cross-fades the whole document with a view transition, which takes
 * the pointer from the page for the length of the fade, once for every card that resolves.
 */
const props = defineProps<{ state: "pending" | "locked" | "ready" }>();

const root = ref<HTMLElement>();
const waited = ref(false);
let timer: ReturnType<typeof setTimeout> | undefined;
function schedule() {
  clearTimeout(timer);
  waited.value = false;
  if (props.state === "pending" && root.value) timer = setTimeout(() => { waited.value = true; }, motionMs(root.value, "--atlas-motion-defer"));
}
onMounted(schedule);
watch(() => props.state, schedule);
onBeforeUnmount(() => clearTimeout(timer));

function fade(el: Element, from: number, to: number, done: () => void, leaving: boolean) {
  const animation = animateAtlas(el, [{ opacity: from }, { opacity: to }], { fill: leaving ? "forwards" : "backwards" });
  if (!animation) { done(); return; }
  animation.finished.then(done, done);
}
const enter = (el: Element, done: () => void) => fade(el, 0, 1, done, false);
function leave(el: Element, done: () => void) {
  // A layer on its way out is no longer something to press.
  el.setAttribute("inert", "");
  fade(el, 1, 0, done, true);
}
</script>

<template>
  <div ref="root" class="bcard-swap" :aria-busy="state === 'pending' || undefined">
    <Transition :css="false" @enter="enter" @leave="leave">
      <div v-if="state === 'ready'" key="ready" class="bcard-layer"><slot /></div>
      <div v-else-if="state === 'locked'" key="locked" class="bcard-layer"><slot name="locked" /></div>
      <div v-else-if="waited" key="pending" class="bcard-layer" data-board-pending><slot name="pending" /></div>
    </Transition>
  </div>
</template>

<style scoped>
.bcard-swap { display: grid; width: 100%; height: 100%; min-width: 0; min-height: 0; }
.bcard-layer { grid-area: 1 / 1; display: grid; min-width: 0; min-height: 0; }
</style>
