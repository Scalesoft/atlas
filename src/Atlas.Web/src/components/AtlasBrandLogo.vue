<script setup lang="ts">
import { onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, useId } from "vue";
import { prefersReducedMotion } from "../motion/aiMotion";
import { bloomPages } from "./brand/bloomGeometry";
import { whenIdle } from "../idle";

const props = withDefaults(defineProps<{ motion?: "ambient" | "hero" | "static" }>(), { motion: "ambient" });
const id = `atlas-bloom-${useId()}`;
const folds = [60, 24, -24, -60];
const sways = [-5, -2.8, 2.8, 5];

// The ambient shine is one finite pass every --atlas-brand-wave, started from here. Between two passes nothing in the
// logo animates, so a page that just sits open spends no frame on it; the pass itself only moves composited layers
// (styles below). A looping CSS animation would keep ticking through the rest of the cycle as well.
const logo = ref<HTMLElement>();
/** Counts the passes; the light's element is created anew for each, which starts its finite animation. */
const passes = ref(0);
let nextPass: number | undefined;
/** A registered <time> token in milliseconds. Undefined where the motion styles are not loaded: no pass is scheduled then. */
function tokenMs(name: string) {
  const time = logo.value && /^(-?\d*\.?\d+)(ms|s)$/.exec(getComputedStyle(logo.value).getPropertyValue(name).trim());
  return time ? Number(time[1]) * (time[2] === "s" ? 1000 : 1) : undefined;
}
// A pass reads the tokens and the light's animations, which brings the page's styles up to date: it waits for an idle moment, when they
// are, instead of forcing that work in the middle of a gesture (a hover on a large board: 464 ms at 4× CPU). `run` drops a pass that was
// already waiting when the logo stopped or started again.
let run = 0;
function later(ms: number) {
  const mine = run;
  return window.setTimeout(() => whenIdle(() => { if (mine === run) pass(); }), ms);
}
function pass() {
  const period = tokenMs("--atlas-brand-wave");
  if (!period) return;
  nextPass = later(period);
  // Reduced motion shows no shine at all; a preview that pauses animations (the gallery) keeps the pass it is holding.
  const held = logo.value?.querySelector(".bloom-shine-band")?.getAnimations?.().some((animation) => animation.playState === "paused");
  if (!held && !prefersReducedMotion(logo.value)) passes.value++;
}
function start() {
  window.clearTimeout(nextPass);
  run++;
  if (props.motion !== "ambient") return;
  const delay = tokenMs("--atlas-brand-wave-delay");
  if (delay !== undefined) nextPass = later(delay);
}
onMounted(start);
// Kept alive off the document (the sidebar while settings hold the rail) no token can be read and nothing shows a
// pass: the passes stop there and begin again, after the same delay as on a first appearance, when the logo is back.
// The light of the last pass goes too; put back into the document its animation would run once more at once.
onActivated(start);
onDeactivated(() => { window.clearTimeout(nextPass); run++; passes.value = 0; });
onBeforeUnmount(() => { window.clearTimeout(nextPass); run++; });
</script>

<template>
  <span ref="logo" class="atlas-brand-logo" :data-motion="motion" aria-hidden="true">
    <svg viewBox="0 0 64 64" fill="none" focusable="false">
      <defs>
        <linearGradient :id="id" x1="8" y1="12" x2="57" y2="53" gradientUnits="userSpaceOnUse">
          <stop class="bloom-violet" /><stop offset=".52" class="bloom-blue" /><stop offset="1" class="bloom-teal" />
        </linearGradient>
        <linearGradient :id="`${id}-feather`">
          <stop stop-color="white" stop-opacity="0" /><stop offset=".22" stop-color="white" stop-opacity=".15" />
          <stop offset=".5" stop-color="white" stop-opacity=".9" /><stop offset=".78" stop-color="white" stop-opacity=".15" />
          <stop offset="1" stop-color="white" stop-opacity="0" />
        </linearGradient>
        <linearGradient :id="`${id}-spine`" x1="0" y1="0" x2="0" y2="1">
          <stop offset=".55" stop-color="white" /><stop offset=".7" stop-color="white" stop-opacity=".5" />
          <stop offset=".81" stop-color="white" stop-opacity="0" />
        </linearGradient>
        <mask :id="`${id}-base`" maskUnits="userSpaceOnUse" x="0" y="0" width="64" height="64">
          <rect width="64" height="64" :fill="`url(#${id}-spine)`" />
        </mask>
        <mask :id="`${id}-sweep`" maskUnits="userSpaceOnUse" x="-4" y="-4" width="72" height="72">
          <rect class="bloom-light" x="0" y="-4" width="24" height="72" :fill="`url(#${id}-feather)`" />
        </mask>
      </defs>
      <g v-for="(page, i) in bloomPages" :key="page" class="bloom-page"
         :style="{ '--page': i, '--fold': `${folds[i]}deg`, '--sway': `${sways[i]}deg` }">
        <g class="bloom-drift">
          <path class="bloom-outline" :d="page" :stroke="`url(#${id})`" :fill="`url(#${id})`"
                fill-opacity=".13" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" />
          <!-- The hero's light travels inside the moving pages (this mask); the ambient one is the layer below, and here
               its light is switched off like in the static mark, so the still picture is the same in all three. -->
          <g :mask="`url(#${id}-base)`">
            <path :d="page" :mask="`url(#${id}-sweep)`" stroke="#f3ffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" />
          </g>
        </g>
      </g>
    </svg>
    <!-- Ambient: the same light band over the same strokes, as layers the compositor moves (one pass, see the script).
         The four highlights are one shape under one mask: where two pages cross, the page in front used to cover the
         light of the one behind it, so a crossing shows one light, not two added up. -->
    <span v-if="motion === 'ambient' && passes" :key="passes" class="bloom-shine">
      <span class="bloom-shine-band">
        <svg class="bloom-shine-strokes" viewBox="0 0 64 64" fill="none" focusable="false">
          <g :mask="`url(#${id}-base)`">
            <path v-for="page in bloomPages" :key="page" :d="page" stroke="#f3ffff" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" />
          </g>
        </svg>
      </span>
    </span>
  </span>
</template>

<style scoped>
.atlas-brand-logo { position: relative; display: inline-flex; width: 100%; height: 100%; flex: none; }
svg { display: block; width: 100%; height: 100%; overflow: visible; filter: drop-shadow(0 3px 5px rgb(76 120 229 / .14)); }
.bloom-violet { stop-color: #7956d9; }
.bloom-blue { stop-color: #4c78e5; }
.bloom-teal { stop-color: #159daa; }
html[data-mode="dark"] .bloom-violet { stop-color: #bd9cff; }
html[data-mode="dark"] .bloom-blue { stop-color: #89b5ff; }
html[data-mode="dark"] .bloom-teal { stop-color: #63dfd3; }
.bloom-page, .bloom-drift { transform-origin: 32px 51px; }
/* Timing is the approved brand choreography (--atlas-brand-*, motion.css). The shine keeps passing in every logo (owner,
   2026-10-05, overriding plan O1 for the shine); the drift loop runs only in the hero on the login and setup screens. */
.bloom-page { animation: bloom-living var(--atlas-brand-living) var(--atlas-ease-inout) both; animation-delay: calc(var(--page) * var(--atlas-brand-page-stagger)); }
.bloom-light { opacity: 0; transform: translateX(-28px); animation: bloom-wave var(--atlas-brand-wave) linear var(--atlas-brand-wave-delay) infinite; }
[data-motion="hero"] .bloom-page { animation: bloom-unfold var(--atlas-brand-hero) var(--atlas-ease-out) both; animation-delay: calc(var(--page) * var(--atlas-brand-page-stagger)); }
[data-motion="hero"] .bloom-outline { animation: bloom-ink var(--atlas-brand-ink) var(--atlas-ease-out) both; animation-delay: calc(var(--page) * var(--atlas-brand-page-stagger)); }
[data-motion="hero"] .bloom-drift { animation: bloom-drift var(--atlas-brand-drift) var(--atlas-ease-inout) infinite; animation-delay: calc(var(--atlas-brand-drift-delay) + var(--page) * var(--atlas-brand-drift-stagger)); }
[data-motion="static"] * { animation: none; }
[data-motion="static"] .bloom-light, [data-motion="ambient"] .bloom-light { display: none; }
/* The ambient shine is the travelling part of bloom-wave (its first 46 %) on layers the compositor moves: a feathered
   window (the band, in the mask's units 24 of 72 wide) slides across the highlight strokes, and the strokes slide back by
   the same distance, so they stand still under it. The light animated inside the SVG mask repainted the whole filtered
   mark on the main thread on every frame, also while it rested. The strokes lie on the outlines, so they need no shadow. */
.bloom-shine { position: absolute; inset: -6.25%; overflow: hidden; pointer-events: none; }
.bloom-shine-band { position: absolute; inset: 0 auto 0 0; width: 33.3333%; opacity: 0; mask-image: linear-gradient(90deg, transparent, rgb(0 0 0 / .15) 22%, rgb(0 0 0 / .9) 50%, rgb(0 0 0 / .15) 78%, transparent); }
.bloom-shine-strokes { position: absolute; left: 16.6667%; top: 5.5556%; width: 266.6667%; height: 88.8889%; filter: none; }
.bloom-shine-band { animation: bloom-pass calc(var(--atlas-brand-wave) * .46) linear both; }
.bloom-shine-strokes { animation: bloom-pass-hold calc(var(--atlas-brand-wave) * .46) linear both; }
@keyframes bloom-unfold { from { transform: rotate(var(--fold)) scale(.55); opacity: 0; } 18% { opacity: 1; } to { transform: rotate(0) scale(1); opacity: 1; } }
@keyframes bloom-ink { from { stroke-dasharray: 160; stroke-dashoffset: 160; fill-opacity: 0; } 70% { fill-opacity: .03; } to { stroke-dasharray: 160; stroke-dashoffset: 0; fill-opacity: .13; } }
@keyframes bloom-living { 0%, 100% { transform: rotate(0); } 45% { transform: rotate(var(--sway)); } }
@keyframes bloom-drift { 0%, 100% { transform: rotate(0); } 50% { transform: rotate(calc(var(--sway) * .28)); } }
@keyframes bloom-wave { 0% { transform: translateX(-28px); opacity: 0; } 8%, 36% { opacity: .75; } 46%, 100% { transform: translateX(86px); opacity: 0; } }
/* bloom-wave's 0 %, 8 %, 36 % and 46 % on the pass's own length; the band's travel in its own widths, the strokes' in theirs. */
@keyframes bloom-pass { from { transform: translateX(-100%); opacity: 0; } 17.4%, 78.3% { opacity: .75; } to { transform: translateX(375%); opacity: 0; } }
@keyframes bloom-pass-hold { from { transform: translateX(37.5%); } to { transform: translateX(-140.625%); } }
/* Reduced motion: no transform, no loop; the leaves fade in (160 ms, linear) to the last frame, the shine stays out. */
@media (prefers-reduced-motion: reduce) {
  .atlas-brand-logo:not([data-motion="static"]) .bloom-page { animation: st-fade-in var(--atlas-motion-surface) linear both; }
  .atlas-brand-logo .bloom-outline, .atlas-brand-logo .bloom-drift { animation: none; }
  .atlas-brand-logo .bloom-light, .atlas-brand-logo .bloom-shine { display: none; }
}
:global([data-atlas-reduced-motion] .atlas-brand-logo:not([data-motion="static"]) .bloom-page) { animation: st-fade-in var(--atlas-motion-surface) linear both; }
:global([data-atlas-reduced-motion] .atlas-brand-logo .bloom-outline), :global([data-atlas-reduced-motion] .atlas-brand-logo .bloom-drift) { animation: none; }
:global([data-atlas-reduced-motion] .atlas-brand-logo .bloom-light), :global([data-atlas-reduced-motion] .atlas-brand-logo .bloom-shine) { display: none; }
</style>
