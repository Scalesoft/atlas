<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { FEEDBACK_HOLD_MS, motionMs } from "../../motion/aiMotion";
import { Button } from "../ui/button";

/**
 * Explicit saving (owner decision): the bar slides in while there are unsaved changes, offers
 * Zrušit and Uložit, and after a successful save shows a check for FEEDBACK_HOLD_MS (1.6 s) before it leaves.
 * The parent owns the data; it clears `dirty` when the save succeeded and sets `error` if not.
 *
 * Board 16 "Neuložené změny": the bar rises from below over --atlas-motion-base and goes back down over
 * --atlas-motion-surface. Its own presence goes with one Web Animation, so a change made while it is leaving (or a save
 * while it is arriving) continues from the frame it is in instead of starting over. The room it takes at the end of the
 * page (its height, margins and the gap of the grid or column it sits in) grows and shrinks with it, so nothing jumps
 * when it comes or goes, and when the user is scrolled to the bottom the page follows smoothly. The tokens carry
 * reduced motion: no distance, a 160 ms linear fade.
 */
import t from "../../i18n/settings-shell";
const props = withDefaults(defineProps<{ dirty: boolean; saving?: boolean; error?: string; saveLabel?: string; message?: string }>(), {
  saving: false,
  error: undefined,
  saveLabel: undefined,
  message: undefined,
});
const emit = defineEmits<{ save: []; cancel: [] }>();
const saved = ref(false);
let timer: ReturnType<typeof setTimeout> | undefined;
// P7: Uložit is disabled while saving and both buttons leave with the bar, which dropped the keyboard on <body>; it goes back
// to where it came into the bar from (the edited field).
let returnTo: HTMLElement | null = null;
function remember(event: FocusEvent) {
  if (event.relatedTarget instanceof HTMLElement && !bar.value?.contains(event.relatedTarget)) returnTo = event.relatedTarget;
}
function giveBack() {
  const active = document.activeElement;
  if ((!active || active === document.body || bar.value?.contains(active)) && returnTo?.isConnected) returnTo.focus({ preventScroll: true });
  returnTo = null;
}

watch(
  () => [props.saving, props.dirty, props.error] as const,
  ([saving, dirty, error], [wasSaving, wasDirty]) => {
    if (wasSaving && !saving && !dirty && !error) {
      saved.value = true;
      clearTimeout(timer);
      timer = setTimeout(() => (saved.value = false), FEEDBACK_HOLD_MS);
    }
    if (dirty) saved.value = false;
    if ((wasSaving || wasDirty) && !saving && !dirty) void nextTick(giveBack);
  },
);

const visible = computed(() => props.dirty || props.saving || saved.value);
const rendered = ref(visible.value);
const bar = ref<HTMLElement>();
let animation: Animation | null = null;
let revision = 0;
const animatable = typeof Element !== "undefined" && typeof Element.prototype.animate === "function";

/** The bar as it is drawn right now, so a reversed animation continues from it. */
function current(node: HTMLElement): Keyframe {
  const style = getComputedStyle(node);
  return { opacity: style.opacity, transform: style.transform, ...room(style), overflow: "clip" };
}
/** What the bar takes from the page: its height, margin and padding (the border stays, see pose). */
function room(style: CSSStyleDeclaration): Keyframe {
  return { height: style.height, marginTop: style.marginTop, marginBottom: style.marginBottom, paddingTop: style.paddingTop, paddingBottom: style.paddingBottom };
}
/**
 * Shown: in place. Hidden: lowered, transparent and with its room folded to nothing. The room is its real height, not a
 * negative margin that leaves the bar's box protruding below the page: a box that still sticks out at the last frame
 * lengthened the scrollable page, which went away with the bar and moved the content of a page scrolled to the bottom
 * in one step. The border cannot shrink smoothly (a width under one pixel is drawn as one), so its two pixels are given
 * back by the bottom margin.
 */
function pose(node: HTMLElement, shown: boolean): Keyframe {
  const style = getComputedStyle(node);
  if (shown) return { opacity: 1, transform: "none", ...room(style), overflow: "clip" };
  const border = (Number.parseFloat(style.borderTopWidth) || 0) + (Number.parseFloat(style.borderBottomWidth) || 0);
  // In a flex column the bar gives the gap back itself, the way a field's error does (disclosure.ts): its own margin on the side
  // of its neighbour goes to minus the gap, so the column (and what follows it) is as tall as without the bar.
  const own = columnGap(node);
  return {
    opacity: 0, transform: "translateY(calc(var(--atlas-rise) * 1.25))", height: "0px", marginTop: own.side === "top" ? `${-own.gap}px` : "0px", marginBottom: `${-border - (own.side === "bottom" ? own.gap : 0)}px`,
    paddingTop: "0px", paddingBottom: "0px", overflow: "clip",
  };
}
/** The gap of a flex column the bar sits in, and on which side of the bar it is taken back (the side of its neighbour; none outside a column). */
function columnGap(node: HTMLElement): { gap: number; side: "top" | "bottom" | null } {
  const parent = node.parentElement;
  if (!parent) return { gap: 0, side: null };
  const style = getComputedStyle(parent);
  const gap = style.display.includes("flex") && style.flexDirection.startsWith("column") ? Number.parseFloat(style.rowGap) || 0 : 0;
  if (!gap) return { gap: 0, side: null };
  return { gap, side: node.previousElementSibling ? "top" : node.nextElementSibling ? "bottom" : null };
}
/**
 * The bar sits in a grid or a column with a gap, and a box of no height still takes one gap more than the layout
 * without it, so that gap went away in one step when the bar was removed (and came in one step with it). A flex column
 * takes it back through the bar's own margin (pose). A grid track cannot get smaller than nothing, but a negative margin of its
 * neighbour can: the item after the bar, or the container of a bar that ends it, follows the bar down to minus the gap and back.
 */
let gapFold: { node: HTMLElement; property: "marginTop" | "marginBottom"; animation: Animation } | null = null;
function gapNeighbour(node: HTMLElement) {
  const parent = node.parentElement;
  if (!parent) return null;
  const style = getComputedStyle(parent);
  const gap = style.display.includes("grid") ? Number.parseFloat(style.rowGap) || 0 : 0;
  if (!gap) return null;
  const following = node.nextElementSibling;
  if (following instanceof HTMLElement && !["none", "contents"].includes(getComputedStyle(following).display) && !["absolute", "fixed"].includes(getComputedStyle(following).position)) return { node: following, property: "marginTop" as const, gap };
  return node.previousElementSibling ? { node: parent, property: "marginBottom" as const, gap } : null;
}
function releaseGap() {
  gapFold?.animation.cancel();
  gapFold = null;
}
async function present(show: boolean) {
  const mine = ++revision;
  const node = bar.value;
  const from = node && rendered.value ? current(node) : undefined;
  const foldFrom = gapFold?.node.isConnected ? getComputedStyle(gapFold.node)[gapFold.property] : undefined;
  const previous = animation;
  animation = null;
  previous?.cancel();
  releaseGap();
  if (show) rendered.value = true;
  await nextTick();
  const next = bar.value;
  if (mine !== revision) return;
  if (!next) { rendered.value = show; return; }
  const start = from ?? pose(next, false);
  const timing = {
    duration: motionMs(next, show ? "--atlas-motion-base" : "--atlas-motion-surface"),
    easing: getComputedStyle(next).getPropertyValue("--atlas-ease-out").trim() || "ease-out",
    fill: "forwards" as const,
  };
  const neighbour = gapNeighbour(next);
  if (neighbour) {
    const { node: element, property, gap } = neighbour;
    const natural = Number.parseFloat(getComputedStyle(element)[property]) || 0;
    const opened = `${natural}px`, folded = `${natural - gap}px`;
    const fold = element.animate([{ [property]: foldFrom ?? (show ? folded : opened) }, { [property]: show ? opened : folded }], timing);
    fold.finished.catch(() => {});
    gapFold = { node: element, property, animation: fold };
  }
  const running = next.animate([start, pose(next, show)], timing);
  running.finished.catch(() => {});
  animation = running;
  running.addEventListener("finish", () => {
    if (animation !== running) return;
    animation = null;
    running.cancel();
    releaseGap();
    rendered.value = show;
  });
}
watch(visible, (show) => {
  if (!animatable) { rendered.value = show; return; }
  void present(show);
}, { flush: "pre" });
onBeforeUnmount(() => { clearTimeout(timer); revision++; animation?.cancel(); releaseGap(); });
</script>

<template>
  <div v-if="rendered" ref="bar" class="save-bar" :data-state="saved ? 'saved' : error ? 'error' : 'dirty'" :inert="!visible || undefined" role="region" :aria-label="t.saveBar.region" @focusin="remember">
    <p class="save-bar-message" aria-live="polite">
      <template v-if="saved"><span class="save-bar-check"><AtlasIcon name="check" :size="15" /></span>{{ t.saveBar.saved }}</template>
      <template v-else-if="error"><AtlasIcon name="circle-x" :size="16" />{{ error }}</template>
      <template v-else-if="saving"><AtlasIcon name="loader-circle" :size="16" class="atlas-spinner" />{{ t.saveBar.saving }}</template>
      <template v-else><span class="save-bar-dot" aria-hidden="true" />{{ message ?? t.saveBar.message }}</template>
    </p>
    <div v-if="!saved" class="save-bar-actions">
      <Button variant="outline" type="button" :disabled="saving" data-save-bar-cancel @click="emit('cancel')">{{ t.cancel }}</Button>
      <!-- The label keeps its width while saving; the message beside it says what is happening. -->
      <Button type="button" :disabled="saving" :aria-busy="saving" data-save-bar-save @click="emit('save')">{{ saveLabel ?? t.saveBar.save }}</Button>
    </div>
  </div>
</template>

<style scoped>
.save-bar {
  position: sticky;
  bottom: 14px;
  z-index: 5;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 10px 16px;
  margin-top: 8px;
  padding: 10px 12px 10px 16px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius);
  background: color-mix(in srgb, var(--surface) 94%, transparent);
  backdrop-filter: blur(6px);
  box-shadow: var(--shadow-menu);
}
.save-bar[data-state="saved"] { border-color: color-mix(in srgb, var(--success) 40%, var(--line)); }
.save-bar[data-state="error"] { border-color: color-mix(in srgb, var(--error) 45%, var(--line)); }
.save-bar-message { display: flex; align-items: center; gap: 8px; margin: 0; font-size: 13.5px; font-weight: 600; }
[data-state="error"] .save-bar-message { color: var(--error); }
[data-state="saved"] .save-bar-message { color: var(--success); }
.save-bar-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--warning); box-shadow: 0 0 0 4px var(--warning-bg); }
.save-bar-check { display: grid; place-items: center; width: 22px; height: 22px; border-radius: 50%; color: var(--on-accent); background: var(--success); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.save-bar-actions { display: flex; gap: 8px; margin-left: auto; }
</style>
