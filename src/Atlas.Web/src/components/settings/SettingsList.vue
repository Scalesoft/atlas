<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import { rowKeys } from "../../keyboard/navigation";
import { outOfReach } from "../../lib/outOfReach";
import { animateAtlas, motionMs } from "../../motion/aiMotion";

// A list of SettingsListRow items; ↑ ↓ Home End walk the rows (rowKeys), so no list binds them again. Rows added later rise in, removed ones fade where they stand, the rest glide
// to their new place (motion tokens; only fades with reduced motion). A row that leaves is taken out of the flow at once
// (settings.css) so the rows after it can glide up, and it stays exactly where it was and out of reach of the keyboard
// and of assistive technology while it fades; the list itself eases from its old height to the new one, so the card
// around it and what is below it follow the rows instead of jumping when the leaving rows are gone.
withDefaults(defineProps<{ label?: string }>(), { label: undefined });
const group = ref<{ $el: HTMLElement } | null>(null);
let height = 0;
let rows = 0;
let easing: Animation | null = null;
const rowsOf = (node: HTMLElement) => node.querySelectorAll(":scope > li:not(.st-list-leave-active)").length;

/**
 * The row is about to leave. Out of the flow in a grid its static position is the start of the list, which Vue's move
 * transition would glide it to while it fades, so it is pinned where it stands (`top` is relative to the list, which is
 * positioned) and taken out of reach of the keyboard and of assistive technology like a page that fades out (outOfReach). The
 * places are those from before the update: Vue lets the leaving rows go one after another, and each one that left the flow
 * had already pulled the next up by then.
 */
const tops = new WeakMap<Element, number>();
function leaving(element: Element) {
  (element as HTMLElement).style.top = `${tops.get(element) ?? (element as HTMLElement).offsetTop}px`;
  outOfReach(element);
}

// The rows are rendered by the TransitionGroup (it calls the slot), so its updates are the list's updates.
function beforeUpdate() {
  const node = group.value?.$el;
  if (!node) return;
  // The height as it is drawn now (a running easing included), so a change made while the list still eases continues from there.
  height = node.offsetHeight;
  rows = rowsOf(node);
  for (const row of node.children) tops.set(row, (row as HTMLElement).offsetTop);
}
function updated() {
  const node = group.value?.$el;
  const from = height;
  height = 0;
  if (!node || !from || !rows) return;
  // Only a change of the rows is eased, and only then is a running easing replaced: a re-render with the same rows (a reload, a busy
  // flag, a keystroke that keeps the same matches) must not cancel it, and a row that opens its details animates the height itself.
  if (rowsOf(node) === rows) return;
  easing?.cancel();
  easing = null;
  const to = node.offsetHeight;
  if (Math.abs(to - from) < 2) return;
  easing = animateAtlas(node, [{ height: `${from}px`, overflow: "clip" }, { height: `${to}px`, overflow: "clip" }], { duration: motionMs(node, "--atlas-motion-base"), fill: "none" });
}
onBeforeUnmount(() => easing?.cancel());
</script>

<template>
  <TransitionGroup ref="group" tag="ul" name="st-list" class="settings-list" :aria-label="label" @before-leave="leaving" @keydown="rowKeys($event, ':scope > li:not(.st-list-leave-active)')" @vue:before-update="beforeUpdate" @vue:updated="updated">
    <slot />
  </TransitionGroup>
</template>

<style scoped>
/* `align-content: start`: while the list eases to a height that is not its content's, the rows keep their height instead of
   stretching to fill it (a grid's auto tracks do). */
.settings-list { position: relative; display: grid; align-content: start; margin: 0; padding: 0; list-style: none; min-width: 0; }
</style>
