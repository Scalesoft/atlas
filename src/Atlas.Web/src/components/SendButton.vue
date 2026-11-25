<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import { animateAtlas, motionMs, paperPlaneFlight, PAPER_PLANE_ICON_PATHS } from "../motion/aiMotion";

/**
 * The paper-plane send button beside a field that sends with Enter (the assistant's question, a comment of the page's discussion and
 * its quick reply): one implementation, for whoever does not know that Enter sends. A submit button of its form (the form's own submit
 * decides what is sent). Dim while there is nothing to send (`ready` false): it stays reachable by Tab, says so with aria-disabled and
 * does nothing. When the send starts (`busy`) the plane itself takes off with the message (aiMotion paperPlaneFlight) and the button stays
 * empty until the send ends; then a fresh plane glides in. Under reduced motion nothing flies and the icon stays.
 * `tone`: `quiet` (a comment: muted, the accent with text) or `ai` (the assistant: its gradient).
 * Why not Button: the plane's flight needs the icon's own element and both tones share it; the look is Button's ghost/icon-sm.
 */
const props = withDefaults(defineProps<{ ready: boolean; busy?: boolean; label: string; title?: string; tone?: "quiet" | "ai" }>(), { busy: false, title: undefined, tone: "quiet" });

const plane = ref<SVGSVGElement>();
const away = ref(false);
let flight: Animation | null = null;
let returning = 0;
function planeBack() {
  away.value = false;
  animateAtlas(plane.value, [
    { opacity: 0, transform: "translate(-7px, 7px) scale(.5)" },
    { opacity: 1, transform: "translate(1px, -1px) scale(1.08)", offset: 0.65 },
    { opacity: 1, transform: "none" },
  ], { duration: plane.value ? motionMs(plane.value, "--atlas-motion-pop") : undefined });
}
watch(() => props.busy, async busy => {
  const current = ++returning;
  if (busy) {
    flight?.cancel();
    flight = plane.value ? paperPlaneFlight(plane.value) : null;
    away.value = !!flight;
    return;
  }
  if (!away.value) return;
  await flight?.finished.catch(() => {});
  if (current === returning && !props.busy) planeBack();
});
onBeforeUnmount(() => { returning++; flight?.cancel(); });
function press(event: MouseEvent) {
  // aria-disabled is not disabled: an empty field must not submit.
  if (!props.ready || props.busy) event.preventDefault();
}
</script>

<template>
  <button type="submit" class="send-button" :data-tone="tone" :aria-label="label" :title="title ?? label" :aria-disabled="!ready || busy || undefined" :aria-busy="busy || undefined"
    :data-ready="ready || undefined" @click="press">
    <svg ref="plane" class="send-button-plane" :class="{ 'is-away': away }" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path v-for="d in PAPER_PLANE_ICON_PATHS" :key="d" :d="d" /></svg>
  </button>
</template>

<style scoped>
.send-button { display: grid; flex: none; place-items: center; width: 32px; height: 32px; padding: 0; overflow: visible; border: 0; border-radius: 8px; background: none; color: var(--muted); font: inherit; cursor: pointer; opacity: .55;
  transition: color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), opacity var(--atlas-motion-fast) var(--atlas-ease-out), filter var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
.send-button[data-ready] { background: var(--accent-soft); color: var(--accent); opacity: 1; }
@media (hover: hover) and (pointer: fine) { .send-button[data-ready]:hover:not([aria-disabled="true"]) { background: color-mix(in srgb, var(--accent) 22%, transparent); } }
/* The assistant's: its AI gradient, dimmed while empty. */
.send-button[data-tone="ai"] { background: linear-gradient(135deg, var(--accent), var(--ai-ink)); color: var(--on-accent, #fff); opacity: .45; }
.send-button[data-tone="ai"][data-ready] { opacity: 1; }
@media (hover: hover) and (pointer: fine) { .send-button[data-tone="ai"][data-ready]:hover:not([aria-disabled="true"]) { background: linear-gradient(135deg, var(--accent), var(--ai-ink)); filter: brightness(1.08); } }
.send-button:active:not([aria-disabled="true"]) { scale: var(--atlas-press); }
.send-button[aria-disabled="true"] { cursor: default; }
.send-button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.send-button svg { pointer-events: none; }
/* The plane has flown off with the message; the button stays empty until a new one glides in. */
.send-button-plane.is-away { opacity: 0; }
</style>
