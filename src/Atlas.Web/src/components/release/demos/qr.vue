<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import { userColor } from "../../../live/collaboratorColors";
import CollaborationPointer from "../../CollaborationPointer.vue";
import { createPointerGlide } from "../../../live/pointerGlide";
import { drawContactQr } from "../../../contacts/contactQr";
import t from "../../../i18n/components-release";
import { useDemoLoop } from "./demoClock";

// A person's card on hover and their business card as a QR code: a pointer comes to the mention, the card opens and the code rises
// ring by ring from its centre (contacts/contactQr, the real drawing; slower here so the eye can follow it). Sample data only.
const d = t.demos.qr;
const [first, last] = d.mention.split(" ");
const VCARD = `BEGIN:VCARD\nVERSION:3.0\nN:${last};${first}\nFN:${d.mention}\nTITLE:${d.role}\nEMAIL:jana.novakova@example.test\nTEL:+420 600 000 000\nEND:VCARD`;
const root = ref<HTMLElement>();
const chip = ref<HTMLElement>();
const hand = ref<{ $el: HTMLElement }>();
// Sample people in their colours (userColor): Jana of the card, and whoever points at her.
const jana = userColor("00000004"), pointing = userColor("00000007");
const card = ref(false), code = ref(""), round = ref(0);
const glide = createPointerGlide(() => root.value!, (element, x, y) => { element.style.transform = `translate3d(${x}px, ${y}px, 0)`; });
onBeforeUnmount(() => glide.stop());
/** The code once drawn; each round shows it anew, so its rings rise again. */
const drawn = drawContactQr(VCARD).then(({ svg }) => svg.replace(" data-shown", ""), () => "");

function target(): [number, number] {
  const box = root.value!.getBoundingClientRect(), rect = chip.value!.getBoundingClientRect();
  return [rect.left - box.left + rect.width * .6, rect.top - box.top + rect.height * .6];
}
useDemoLoop(root, (at) => {
  const to = target(), from: [number, number] = [to[0] - 60, root.value!.clientHeight + 30];
  at(0, () => { card.value = false; code.value = ""; glide.to(hand.value!.$el, ...from, true); });
  for (let s = 0; s <= 800; s += 50) {
    const k = s / 800, e = k * k * (3 - 2 * k);
    at(200 + s, () => glide.to(hand.value!.$el, from[0] + (to[0] - from[0]) * e, from[1] + (to[1] - from[1]) * e));
  }
  at(1150, () => { card.value = true; });
  at(1700, async () => { const svg = await drawn; round.value++; code.value = svg; });
  at(6400, () => { card.value = false; glide.to(hand.value!.$el, from[0], from[1]); });
  return 7000;
}, async () => {
  card.value = true;
  const to = target();
  glide.to(hand.value!.$el, to[0], to[1], true);
  code.value = await drawn;
});
</script>

<template>
  <div ref="root" class="release-demo demo-qr" aria-hidden="true">
    <div class="demo-qr-person">
      <span ref="chip" class="demo-qr-chip">@{{ d.mention }}</span>
      <div class="demo-qr-card" :class="{ 'is-open': card }">
        <span class="demo-qr-avatar" :style="{ background: jana }">JN</span>
        <span><b>{{ d.mention }}</b><small>{{ d.role }}</small></span>
      </div>
    </div>
    <div class="demo-qr-side" :class="{ 'is-open': card }">
      <div class="demo-qr-code"><div v-if="code" :key="round" class="demo-qr-svg" v-html="code" /></div>
      <small>{{ d.scan }}</small>
    </div>
    <CollaborationPointer ref="hand" :color="pointing" />
  </div>
</template>

<style scoped>
.demo-qr { position: relative; display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 12px; align-items: center; height: 100%; padding: 12px 14px; overflow: hidden; font-size: 13px; }
.demo-qr-person { display: grid; gap: 8px; justify-items: start; min-width: 0; }
.demo-qr-chip { padding: 1px 8px; border-radius: 999px; background: color-mix(in srgb, var(--accent) 12%, transparent); color: var(--accent); font-weight: 600; white-space: nowrap; }
.demo-qr-card, .demo-qr-side { opacity: 0; translate: 0 var(--atlas-rise, 8px); transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out); }
.demo-qr-card.is-open, .demo-qr-side.is-open { opacity: 1; translate: none; }
.demo-qr-card { display: flex; align-items: center; gap: 8px; width: 100%; padding: 8px 10px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); box-shadow: var(--refresh-card-shadow-hover, 0 6px 18px rgb(0 0 0 / .08)); }
.demo-qr-card > span:last-child { min-width: 0; }
.demo-qr-card b, .demo-qr-card small { display: block; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.demo-qr-card small { color: var(--muted); font-size: 11.5px; }
.demo-qr-avatar { display: grid; flex: none; place-items: center; width: 30px; height: 30px; border-radius: 50%; color: white; font-size: 11px; font-weight: 700; }
.demo-qr-side { display: grid; justify-items: center; gap: 4px; }
.demo-qr-side small { color: var(--muted); font-size: 10.5px; }
/* The contact card's code, its rings rising slower than in the card: contactQr.css's ring and step, each scaled by a factor of the demo. */
.demo-qr-code { --demo-ring-slow: 2.1; --demo-step-slow: 6.7;
  width: 104px; height: 104px; border-radius: 12px; overflow: hidden; background: #fff; box-shadow: 0 0 0 1px var(--line); }
.demo-qr-svg, .demo-qr-svg :deep(svg) { width: 100%; height: 100%; }
.demo-qr-svg :deep(.atlas-qr-band) { animation-duration: calc(var(--atlas-motion-base) * var(--demo-ring-slow));
  animation-delay: calc(var(--qr-band) * var(--atlas-motion-fast) * var(--demo-step-slow) / 5); }
@media (prefers-reduced-motion: reduce) { .demo-qr-svg :deep(.atlas-qr-band) { animation-delay: initial; } }
:global([data-atlas-reduced-motion]) .demo-qr-svg .atlas-qr-band { animation-delay: initial; }
</style>
