<script setup lang="ts">
import { useId } from "vue";
import { bloomPages } from "./bloomGeometry";

withDefaults(defineProps<{
  variant: "pages" | "scan" | "gather" | "settings" | "bookmark" | "network" | "shelf";
  active?: boolean;
}>(), { active: false });
const id = `atlas-motion-${useId()}`;
const book = "M50 62C38 53 22 53 12 56V24C25 21 40 26 50 34C60 26 75 21 88 24V56C77 53 62 54 50 62Z";
const links = ["M26 24Q29 31 34 36", "M74 24Q71 31 66 36", "M25 66Q30 61 36 57", "M75 66Q70 61 64 57"];
const nodes = [[23, 19], [77, 19], [21, 70], [79, 70]];
const sliders = [[30, 37, 7], [37, 49, -6], [62, 37, 7], [70, 49, -6]];
</script>

<template>
  <svg class="atlas-motion-mark" :data-active="active" viewBox="0 0 100 90" fill="none" aria-hidden="true" focusable="false">
    <defs>
      <linearGradient :id="id" x1="0" y1="0" x2="1" y2=".8">
        <stop class="mark-violet" /><stop offset=".52" class="mark-blue" /><stop offset="1" class="mark-teal" />
      </linearGradient>
      <clipPath :id="`${id}-clip`"><path :d="book" /></clipPath>
      <linearGradient :id="`${id}-scan`">
        <stop class="mark-teal" stop-opacity="0" /><stop offset=".5" class="mark-teal" stop-opacity=".7" /><stop offset="1" class="mark-teal" stop-opacity="0" />
      </linearGradient>
    </defs>
    <g :stroke="`url(#${id})`" :fill="`url(#${id})`" fill-opacity=".08" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
      <template v-if="variant === 'network'">
        <g v-for="(link, i) in links" :key="link">
          <path :d="link" fill="none" opacity=".2" />
          <path class="knowledge-in" :style="{ '--i': i }" :d="link" pathLength="100" fill="none" />
        </g>
        <g transform="translate(27.6 24) scale(.7)" stroke-width="2.5" fill-opacity=".1"><path v-for="page in bloomPages" :key="page" :d="page" /></g>
        <circle v-for="(node, i) in nodes" :key="i" class="source-light" :style="{ '--i': i }" :cx="node[0]" :cy="node[1]" r="4.5" stroke-width="1.8" />
      </template>
      <template v-else-if="variant === 'bookmark'">
        <g class="ribbon">
          <path d="M34 22Q34 18 38 18H62Q66 18 66 22V70Q66 72 64 71L50 61L36 71Q34 72 34 70Z" />
          <path d="M40 25H60" opacity=".3" fill="none" />
          <g transform="translate(37 30) scale(.41)" stroke-width="2.8" fill-opacity=".13"><path v-for="page in bloomPages" :key="page" :d="page" /></g>
        </g>
        <path class="save-check" d="M72 52L77 57L86 45" fill="none" stroke-width="2.4" />
      </template>
      <template v-else-if="variant === 'settings'">
        <path d="M18 25Q34 21 49 33Q65 21 82 25V63Q66 59 49 70Q34 59 18 63Z" />
        <path d="M49 33V70M26 37H41M26 49H41M58 37H74M58 49H74" fill="none" opacity=".55" />
        <circle v-for="(slider, i) in sliders" :key="i" class="adjust" :style="{ '--i': i, '--travel': `${slider[2]}px` }" :cx="slider[0]" :cy="slider[1]" r="2.8" fill-opacity="1" />
      </template>
      <template v-else-if="variant === 'shelf'">
        <path d="M17 71H83" fill="none" opacity=".5" />
        <g v-for="(top, i) in [24, 18, 29]" :key="i" class="volume" :style="{ '--i': i }">
          <rect :x="22 + i * 20" :y="top" width="15" :height="66 - top" rx="2.5" />
          <path :d="`M${26 + i * 20} 57H${33 + i * 20}M${26 + i * 20} ${top + 7}H${33 + i * 20}`" fill="none" opacity=".4" />
        </g>
      </template>
      <template v-else>
        <path :d="book" />
        <path d="M50 34V62M12 61Q34 57 50 67Q68 57 88 61" fill="none" />
        <path d="M20 34Q32 33 42 40M20 42Q32 41 42 48M58 40Q70 33 80 34M58 48Q70 41 80 42" opacity=".3" fill="none" />
        <template v-if="variant === 'pages'">
          <path v-for="i in 2" :key="i" class="flip" :style="{ '--i': i - 1 }" d="M50 62V34C60 26 75 21 88 24V56C77 53 62 54 50 62Z" fill="var(--surface)" fill-opacity=".85" />
        </template>
        <g v-else-if="variant === 'scan'" :clip-path="`url(#${id}-clip)`"><rect class="scan" x="43" y="18" width="14" height="50" :fill="`url(#${id}-scan)`" fill-opacity="1" stroke="none" /></g>
        <template v-else>
          <path v-for="i in 3" :key="i" class="gather" :style="{ '--i': i - 1 }" :d="`M${14 + i * 3} ${28 + i * 7}Q33 ${25 + i * 7} 47 ${36 + i * 6}M53 ${36 + i * 6}Q68 ${25 + i * 7} ${86 - i * 3} ${28 + i * 7}`" fill="none" />
        </template>
      </template>
    </g>
  </svg>
</template>

<style scoped>
.atlas-motion-mark { display: block; width: 100%; height: 100%; overflow: visible; }
.mark-violet { stop-color: #7956d9; }.mark-blue { stop-color: #4c78e5; }.mark-teal { stop-color: #159daa; }
html[data-mode="dark"] .mark-violet { stop-color: #bd9cff; }html[data-mode="dark"] .mark-blue { stop-color: #89b5ff; }html[data-mode="dark"] .mark-teal { stop-color: #63dfd3; }
.knowledge-in, .flip, .scan, .gather, .save-check { opacity: 0; }
.knowledge-in { stroke-dasharray: 16 184; stroke-dashoffset: 16; stroke-width: 1.7; }
[data-active="true"] .knowledge-in { animation: knowledge-in var(--atlas-brand-mark-knowledge) linear infinite; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-knowledge)); }
[data-active="true"] .source-light { animation: source-light var(--atlas-brand-mark-source) var(--atlas-ease-inout) infinite; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-knowledge)); }
.flip { transform-origin: 50px 58px; }
[data-active="true"] .flip { animation: page-flip var(--atlas-brand-mark-flip) var(--atlas-ease-inout) infinite; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-flip)); }
[data-active="true"] .scan { animation: page-scan var(--atlas-brand-mark-scan) var(--atlas-ease-inout) infinite; }
[data-active="true"] .gather { animation: page-gather var(--atlas-brand-mark-gather) var(--atlas-ease-out) infinite; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-gather)); }
.ribbon { transform-origin: 50px 19px; }
[data-active="true"] .ribbon { animation: ribbon-in var(--atlas-brand-mark-ribbon) var(--atlas-ease-out) both; }
.save-check { stroke-dasharray: 24; stroke-dashoffset: 24; }
[data-active="true"] .save-check { animation: saved var(--atlas-brand-mark-saved) var(--atlas-brand-mark-saved-delay) var(--atlas-ease-out) both; }
[data-active="true"] .adjust { animation: adjust var(--atlas-brand-mark-adjust) var(--atlas-ease-inout) both; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-adjust)); }
.volume { transform-origin: 50px 67px; }
[data-active="true"] .volume { animation: volume-in var(--atlas-brand-mark-volume) var(--atlas-ease-out) both; animation-delay: calc(var(--i) * var(--atlas-brand-mark-stagger-volume)); }
@keyframes knowledge-in { 0% { stroke-dashoffset: 16; opacity: 0; } 5% { opacity: .85; } 32%, 100% { stroke-dashoffset: -100; opacity: 0; } }
@keyframes source-light { 0%, 20%, 100% { fill-opacity: .08; } 5% { fill-opacity: .36; } }
@keyframes page-flip { 0%, 10% { transform: scaleX(1); opacity: 0; } 18% { opacity: .85; } 70% { transform: scaleX(-1); opacity: .65; } 82%, 100% { transform: scaleX(-1); opacity: 0; } }
@keyframes page-scan { 0%, 12% { transform: translateX(-25px); opacity: 0; } 25%, 65% { opacity: .9; } 82%, 100% { transform: translateX(25px); opacity: 0; } }
@keyframes page-gather { 0% { transform: translateY(-16px); opacity: 0; } 35%, 68% { transform: translateY(0); opacity: .85; } 95%, 100% { transform: translateY(0); opacity: 0; } }
@keyframes ribbon-in { from { transform: translateY(-8px) rotate(-4deg); opacity: .25; } 65% { transform: translateY(1px) rotate(.7deg); opacity: 1; } to { transform: translateY(0) rotate(0); opacity: 1; } }
@keyframes saved { to { opacity: 1; stroke-dashoffset: 0; } }
@keyframes adjust { 0%, 100% { transform: translateX(0); } 40%, 65% { transform: translateX(var(--travel)); } }
@keyframes volume-in { from { transform: translateY(7px) rotate(-5deg); opacity: .4; } 75% { transform: translateY(-.6px) rotate(.5deg); opacity: 1; } to { transform: translateY(0) rotate(0); opacity: 1; } }
/* Plan O1: every loop runs only while the mark is active (the activity is running). Reduced motion (O2): loops stand
   and their strokes stay out; the one-shot illustrations fade in (160 ms, linear) to their last frame. */
@media (prefers-reduced-motion: reduce) {
  .atlas-motion-mark[data-active="true"] :is(.knowledge-in, .source-light, .flip, .scan, .gather, .adjust) { animation: none; }
  .atlas-motion-mark[data-active="true"] :is(.ribbon, .volume) { animation: st-fade-in var(--atlas-motion-surface) linear both; }
  .atlas-motion-mark[data-active="true"] .save-check { opacity: 1; stroke-dashoffset: 0; animation: st-fade-in var(--atlas-motion-surface) linear both; }
  .knowledge-in, .flip, .scan, .gather { display: none; }
}
:global([data-atlas-reduced-motion] .atlas-motion-mark[data-active="true"] :is(.knowledge-in, .source-light, .flip, .scan, .gather, .adjust)) { animation: none; }
:global([data-atlas-reduced-motion] .atlas-motion-mark[data-active="true"] :is(.ribbon, .volume)) { animation: st-fade-in var(--atlas-motion-surface) linear both; }
:global([data-atlas-reduced-motion] .atlas-motion-mark[data-active="true"] .save-check) { opacity: 1; stroke-dashoffset: 0; animation: st-fade-in var(--atlas-motion-surface) linear both; }
:global([data-atlas-reduced-motion] .atlas-motion-mark :is(.knowledge-in, .flip, .scan, .gather)) { display: none; }
</style>
