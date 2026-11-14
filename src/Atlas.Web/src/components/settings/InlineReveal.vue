<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from "vue";
import { animateAtlas, motionMs } from "../../motion/aiMotion";

// Own presence together with the animation: CSS keyframes that always started at zero/full
// height jumped on reversal, and a cancelled close could release the new opening's clip.
const open = defineModel<boolean>("open", { default: false });
const props = withDefaults(defineProps<{ id?: string; keepMounted?: boolean }>(), { id: undefined, keepMounted: false });
const content = ref<HTMLElement>();
const inner = ref<HTMLElement>();
const rendered = ref(open.value);
const settled = ref(open.value);
let animation: Animation | null = null;
let revision = 0;
let observer: ResizeObserver | undefined;
let targetHeight = 0;
let focusReturn: HTMLElement | undefined;

function controller() {
  return props.id ? [...document.querySelectorAll<HTMLElement>("[aria-controls]")]
    .find(node => node.getAttribute("aria-controls")?.split(/\s+/).includes(props.id!)) : undefined;
}
function rememberController() { focusReturn = controller(); }

function snapshot() {
  const node = content.value;
  return node && rendered.value
    ? { height: `${node.getBoundingClientRect().height}px`, opacity: getComputedStyle(node).opacity }
    : { height: "0px", opacity: "0" };
}
function cancelAnimation() {
  const previous = animation;
  animation = null;
  previous?.cancel();
}
function settle() {
  rendered.value = open.value;
  settled.value = open.value;
  cancelAnimation();
}
function animate(from: Keyframe) {
  const node = content.value;
  if (!node) return;
  targetHeight = inner.value?.getBoundingClientRect().height ?? 0;
  const expanding = open.value;
  const next = animateAtlas(node, [
    { ...from, overflow: "clip" },
    { height: `${expanding ? targetHeight : 0}px`, opacity: expanding ? 1 : 0, overflow: "clip" },
  ], { duration: motionMs(node, expanding ? "--atlas-motion-slow" : "--atlas-motion-base"), fill: "both" });
  animation = next;
  if (!next) { settle(); return; }
  next.addEventListener("finish", () => { if (animation === next) settle(); });
  next.addEventListener("cancel", () => { if (animation === next) settle(); });
}
watch(open, async (value) => {
  const current = ++revision;
  const from = snapshot();
  cancelAnimation();
  // Return focus before a collapsing field becomes inert; explicit reveal ids connect its trigger.
  if (!value && props.id && content.value?.contains(document.activeElement)) {
    const trigger = controller() ?? focusReturn;
    if (trigger?.isConnected) trigger.focus({ preventScroll: true });
  }
  settled.value = false;
  if (value) rendered.value = true;
  await nextTick();
  if (current !== revision) return;
  animate(from);
}, { flush: "pre" });

// Loaded content, nested disclosures and line wrapping can change the destination mid-flight.
// Observe the natural inner height, never the animated outer box (which would create a loop).
watch(inner, node => {
  observer?.disconnect();
  if (!node || typeof ResizeObserver !== "function") return;
  observer = new ResizeObserver(() => {
    if (!open.value || !animation) return;
    const height = node.getBoundingClientRect().height;
    if (Math.abs(height - targetHeight) < 1) return;
    const from = snapshot();
    cancelAnimation();
    animate(from);
  });
  observer.observe(node);
}, { flush: "post" });
onBeforeUnmount(() => { revision++; observer?.disconnect(); cancelAnimation(); });
</script>

<template>
  <div class="inline-reveal-root">
    <div
      :id="id" ref="content" class="inline-reveal" :class="{ 'is-settled': settled }" data-inline-reveal
      :data-state="open ? 'open' : 'closed'"
      :hidden.attr="rendered ? undefined : keepMounted ? 'until-found' : ''"
      :inert="(!open && rendered) || undefined" :aria-hidden="!open || undefined"
      @beforematch="open = true"
      @focusin="rememberController"
    >
      <div v-if="rendered || keepMounted" ref="inner" class="inline-reveal-inner"><slot /></div>
    </div>
  </div>
</template>

<style scoped>
.inline-reveal { overflow: clip; }
.inline-reveal.is-settled[data-state="open"] { overflow: visible; }
/* Contain child margins so they are included in the measured destination height. */
.inline-reveal-inner { display: flow-root; min-width: 0; }
</style>
