<script lang="ts">
// The parent a visible banner has just left in the update that is running: a banner that its owner keys per message is taken out
// and a new one put in its place in one update, and that one must not unfold from nothing (the page below would fall by the
// banner's height and glide back).
let replaced: Element | null = null;
</script>

<script setup lang="ts">
import t from "../../i18n/settings-shell";
import { nextTick, onBeforeUnmount, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { animateAtlas, motionMs } from "../../motion/aiMotion";
import { disclosureHooks } from "./disclosure";

// The persistent result line after a change ("Síť je přidaná"), shown where the flow returns to.
// It stays until dismissed or the page changes; the owner of the message decides both.
//
// It unfolds with its own margin when it arrives (the disclosure of a field's error), so the page below glides down
// instead of jumping by its height, and its close button folds it away before it tells the owner to take it out. The owner
// removing it by itself (a `v-if`) cannot be animated from here: a Transition does not leave when its own component goes.
// Inside the notice of the settings shell the shell unfolds the whole notice (and replaces a message in place), so there the
// banner leaves that to it.
//
// A banner that takes the place of one that was there (a new message, the owner keys it) does not unfold: the container stays
// and only the text fades in, so nothing below it moves.
//
// Its spacing is one rule: the 16 px under it in plain flow, and nothing of its own where the page spaces its parts by a gap
// (a grid or a column with a gap): the margin came on top of the gap and doubled the room under it (34 px in a page body of gap 18,
// 28 in a card of gap 12). The margin is set before the unfold reads it, so the unfold and the fold carry the right one.
withDefaults(defineProps<{ message: string; icon?: string }>(), { icon: "circle-check" });
const emit = defineEmits<{ dismiss: [] }>();
const banner = ref<HTMLElement>();
const sheltered = (element: Element) => !!element.parentElement?.closest(".settings-shell-notice");
function spacedByParent(element: HTMLElement) {
  const parent = element.parentElement;
  if (!parent) return false;
  const style = getComputedStyle(parent);
  return /flex|grid/.test(style.display) && (Number.parseFloat(style.rowGap) || 0) > 0;
}
onBeforeUnmount(() => {
  const node = banner.value;
  const parent = node?.parentElement;
  if (!node || !parent || node.getBoundingClientRect().height < 8) return;
  replaced = parent;
  void nextTick(() => { if (replaced === parent) replaced = null; });
});
const unfold = {
  onEnter(element: Element, done: () => void) {
    if (spacedByParent(element as HTMLElement)) (element as HTMLElement).style.marginBottom = "0px";
    if (sheltered(element)) { done(); return; }
    if (replaced && replaced === element.parentElement) {
      const text = element.querySelector(".result-banner-text");
      animateAtlas(text, [{ opacity: 0 }, { opacity: 1 }], { duration: motionMs(element, "--atlas-motion-fast"), fill: "none" });
      done();
      return;
    }
    disclosureHooks.onEnter(element, done);
  },
};
function dismiss() {
  const node = banner.value;
  if (!node || sheltered(node)) emit("dismiss");
  else disclosureHooks.onLeave(node, () => emit("dismiss"));
}
</script>

<template>
  <Transition appear :css="false" v-bind="unfold">
    <p ref="banner" class="result-banner" role="status" data-result-banner>
      <span class="result-banner-check"><AtlasIcon :name="icon" :size="15" /></span>
      <span class="result-banner-text">{{ message }}</span>
      <button type="button" class="result-banner-close" :aria-label="t.dismissBanner" @click="dismiss"><AtlasIcon name="close" :size="15" /></button>
    </p>
  </Transition>
</template>

<style scoped>
.result-banner {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 0 0 16px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--success) 40%, var(--line));
  border-radius: var(--st-radius);
  background: color-mix(in srgb, var(--success-bg) 70%, var(--surface));
  color: var(--text);
  font-size: 13.5px;
  font-weight: 600;
}
.result-banner-text { flex: 1; }
.result-banner-check { display: grid; place-items: center; width: 26px; height: 26px; border-radius: 50%; color: var(--on-accent); background: var(--success); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out) var(--st-stagger) both; }
.result-banner-close { display: grid; place-items: center; width: 30px; height: 30px; border-radius: 8px; color: var(--muted); cursor: pointer; transition: background-color var(--atlas-motion-fast); }
@media (hover: hover) and (pointer: fine) {
  .result-banner-close:hover { background: var(--hover); color: var(--text); }
}
.result-banner-close:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; }
</style>
