<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { scrollBehavior } from "../../motion/aiMotion";
import AtlasIcon from "../AtlasIcon.vue";
import type { SettingsTab } from "./types";

// Parts of one detail page (a vault: Stav, Přístupy, Ochrana, Hesla, Audit) as real links with
// aria-current, so each part can be linked and reached with Back. Refresh 2.5: the parts of a page
// are underlined tabs with words only (boards 07–10); `segmented` is the quieter switch for the parts of one tab
// (AI → Modely) and keeps its icons. The underline is one element that glides under the current tab (board 16:
// 200 ms, the move curve); it is placed without motion first. The segmented switch has the same kind of element: the
// raised segment (a surface with its shadow) glides to the chosen one instead of appearing there.
const props = withDefaults(defineProps<{ options: SettingsTab[]; modelValue: string; label: string; variant?: "tabs" | "segmented" }>(), { variant: "tabs" });
const nav = ref<HTMLElement>();
const indicator = ref({ left: 0, top: 0, width: 0, height: 2, ready: false, shown: false });
function place() {
  const current = nav.value?.querySelector<HTMLElement>('a[aria-current="page"]');
  if (!current) { indicator.value = { ...indicator.value, shown: false }; return; }
  // The underline sits on the bottom 2 px of the tab, which overlaps the line of the bar by 1 px; the segment covers its link.
  indicator.value = props.variant === "tabs"
    ? { left: current.offsetLeft, top: current.offsetTop + current.offsetHeight - 2, width: current.offsetWidth, height: 2, ready: indicator.value.ready, shown: true }
    : { left: current.offsetLeft, top: current.offsetTop, width: current.offsetWidth, height: current.offsetHeight, ready: indicator.value.ready, shown: true };
  if (!indicator.value.ready) requestAnimationFrame(() => { indicator.value = { ...indicator.value, ready: true }; });
  reveal(current);
}
/** A phone scrolls the tabs sideways (below): the current one is scrolled into the bar, with a little of its neighbour, so
 * opening a later part (Činnost) does not leave its tab, and only a stub of its underline, past the edge. The bar scrolls
 * itself, never the page; the first placement is instant, later ones glide unless motion is reduced. */
function reveal(current: HTMLElement) {
  const bar = nav.value;
  if (!bar || bar.scrollWidth <= bar.clientWidth + 1) return;
  const room = 24;
  const start = Math.max(0, current.offsetLeft - room);
  const end = current.offsetLeft + current.offsetWidth + room - bar.clientWidth;
  const left = bar.scrollLeft > start ? start : bar.scrollLeft < end ? end : bar.scrollLeft;
  if (Math.abs(left - bar.scrollLeft) < 1) return;
  if (typeof bar.scrollTo === "function") bar.scrollTo({ left, behavior: indicator.value.ready ? scrollBehavior(bar) : "auto" });
  else bar.scrollLeft = left;
}
let observer: ResizeObserver | undefined;
onMounted(() => {
  place();
  if (typeof ResizeObserver === "function" && nav.value) {
    observer = new ResizeObserver(() => place());
    observer.observe(nav.value);
  }
  void document.fonts?.ready.then(place);
});
onBeforeUnmount(() => observer?.disconnect());
watch(() => [props.modelValue, props.variant, props.options.map((item) => `${item.id}${item.count ?? ""}${item.label}`).join("|")], () => void nextTick(place));
</script>

<template>
  <nav ref="nav" class="settings-tabs" :data-variant="variant" :aria-label="label">
    <a v-for="item in options" :key="item.id" :href="item.href" :aria-current="modelValue === item.id ? 'page' : undefined" :data-tab="item.id">
      <AtlasIcon v-if="variant === 'segmented'" :name="item.icon" :size="15" />{{ item.label }}<span v-if="item.count !== undefined" class="settings-tab-count">{{ item.count }}</span>
    </a>
    <span class="settings-tabs-indicator" :class="{ 'is-ready': indicator.ready }" :data-shown="indicator.shown || undefined" :style="{ width: `${indicator.width}px`, height: `${indicator.height}px`, transform: `translate(${indicator.left}px, ${indicator.top}px)` }" aria-hidden="true" />
  </nav>
</template>

<style scoped>
.settings-tabs { position: relative; display: flex; flex-wrap: wrap; gap: 2px 4px; border-bottom: 1px solid var(--st-line-soft, var(--line)); }
/* A phone keeps the tabs on one line and lets them scroll sideways. */
@media (max-width: 680px) {
  .settings-tabs:not([data-variant="segmented"]) { flex-wrap: nowrap; overflow-x: auto; scrollbar-width: none; margin-inline: -4px; padding-inline: 4px; }
  .settings-tabs:not([data-variant="segmented"]) a { flex-shrink: 0; }
}
.settings-tabs a {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-height: 40px;
  margin-bottom: -1px;
  padding: 0 12px;
  border-radius: 8px 8px 0 0;
  color: var(--muted);
  font-size: 14px;
  font-weight: 500;
  text-decoration: none;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.settings-tabs a > svg { opacity: .8; }
/* One underline glides to the current tab; it is placed without motion first and does not travel under reduced motion. */
.settings-tabs-indicator {
  position: absolute;
  left: 0;
  top: 0;
  height: 2px;
  border-radius: 2px 2px 0 0;
  background: var(--accent);
  opacity: 0;
  pointer-events: none;
}
/* The raised segment of the segmented switch: behind the labels, the radius of the links it covers. */
.settings-tabs[data-variant="segmented"] .settings-tabs-indicator { z-index: 0; border-radius: 8px; background: var(--surface); box-shadow: 0 1px 2px rgb(24 24 27 / 0.08); }
.settings-tabs[data-variant="segmented"] a { z-index: 1; }
.settings-tabs-indicator[data-shown] { opacity: 1; }
.settings-tabs-indicator.is-ready { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout), width var(--atlas-motion-base) var(--atlas-ease-inout), height var(--atlas-motion-base) var(--atlas-ease-inout), opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .settings-tabs a:hover { background: var(--hover); color: var(--text); }
}
.settings-tabs a:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; --focus-offset: -2px; }
.settings-tabs a[aria-current="page"] { color: var(--accent); font-weight: 600; }
.settings-tabs a[aria-current="page"] > svg { opacity: 1; }
.settings-tab-count { padding: 0 7px; border-radius: 999px; font-size: 11px; font-weight: 650; background: var(--hover); color: var(--muted); }
.settings-tabs a[aria-current="page"] .settings-tab-count { background: var(--accent-soft); color: var(--accent); }

.settings-tabs[data-variant="segmented"] {
  display: inline-flex;
  flex-wrap: wrap;
  gap: 2px;
  padding: 3px;
  border: 0;
  border-radius: 10px;
  background: var(--hover);
  justify-self: start;
}
.settings-tabs[data-variant="segmented"] a { min-height: 30px; margin: 0; padding: 0 12px; border-radius: 8px; font-size: 13px; }
@media (hover: hover) and (pointer: fine) {
  .settings-tabs[data-variant="segmented"] a:hover { background: color-mix(in srgb, var(--surface) 60%, transparent); }
}
.settings-tabs[data-variant="segmented"] a[aria-current="page"] { color: var(--text); }
/* Reduced motion: the underline does not travel; the current tab's own line fades in instead, the current segment takes
   its surface itself (and fades with the colour transition of the links). */
@media (prefers-reduced-motion: reduce) {
  .settings-tabs-indicator { display: none; }
  .settings-tabs[data-variant="segmented"] a { transition-property: color, background-color, box-shadow; }
  .settings-tabs[data-variant="segmented"] a[aria-current="page"] { background: var(--surface); box-shadow: 0 1px 2px rgb(24 24 27 / 0.08); }
  .settings-tabs:not([data-variant="segmented"]) a { transition-property: color, background-color, box-shadow; }
  .settings-tabs:not([data-variant="segmented"]) a[aria-current="page"] { box-shadow: inset 0 -2px 0 var(--accent); }
}
[data-atlas-reduced-motion] .settings-tabs-indicator { display: none; }
[data-atlas-reduced-motion] .settings-tabs[data-variant="segmented"] a { transition-property: color, background-color, box-shadow; }
[data-atlas-reduced-motion] .settings-tabs[data-variant="segmented"] a[aria-current="page"] { background: var(--surface); box-shadow: 0 1px 2px rgb(24 24 27 / 0.08); }
[data-atlas-reduced-motion] .settings-tabs:not([data-variant="segmented"]) a { transition-property: color, background-color, box-shadow; }
[data-atlas-reduced-motion] .settings-tabs:not([data-variant="segmented"]) a[aria-current="page"] { box-shadow: inset 0 -2px 0 var(--accent); }
</style>
