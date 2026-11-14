<script setup lang="ts">
import { computed } from "vue";
import { EFFORT_STEPS as STEPS, type AnswerEffort } from "../../ai/answerEffort";
import t from "../../i18n/ai-answer";
import { Slider } from "../ui/slider";

/**
 * How much of the wiki the next answer reads (server AnswerEffort), as a meter people keep in view: five steps from Rychle
 * to Naplno on a pill track, each a little livelier in the AI colours. Rychle rests; Vyváženě sends a sheen towards the
 * handle; from Důkladně on, sparks fly into the handle, more and faster with every step; Do hloubky adds twinkling stars and
 * a glow round the handle, Naplno lets it breathe. The motion is styles/ai.css (`.ai-effort-*`); reduced motion keeps the
 * colours only. Composes ui/slider; a plain ordered choice stays a plain Slider, this one is only the answer effort. In the
 * assistant it is the content of AiEffortPicker's popover.
 */
const model = defineModel<AnswerEffort>({ required: true });

/** Sparks flying into the handle and stars twinkling in the fill, per step. */
const SPARKS = [0, 0, 3, 5, 8];
const STARS = [0, 0, 0, 3, 5];

const level = computed(() => Math.max(0, STEPS.findIndex((step) => step.value === model.value)));
const current = computed(() => STEPS[level.value]!);
// Scattered but fixed places and phases: the same step looks the same on every render. The track is 24 px high; a spark
// drifts from its lane to the handle's middle (--dy) while it flies.
const sparks = computed(() => Array.from({ length: SPARKS[level.value]! }, (_, i) => {
  const y = 5 + ((i * 7) % 14);
  return { "--y": `${y}px`, "--dy": `${12 - y}px`, "--size": `${2 + (i % 3) * .5}px`, "--delay": `${-((i * .37) % 1).toFixed(2)}` };
}));
const stars = computed(() => Array.from({ length: STARS[level.value]! }, (_, i) => ({
  "--x": `${14 + ((i * 23) % 62)}%`, "--y": `${4 + ((i * 5) % 12)}px`, "--delay": `${-((i * .29) % 1).toFixed(2)}`,
})));
const valueText = (value: number) => t.effort.value(STEPS[value]!.label, STEPS[value]!.sources);
function choose(value: number[] | undefined) {
  if (value) model.value = STEPS[value[0]!]!.value;
}
</script>

<template>
  <div class="ai-effort" :data-level="level" data-ai-effort>
    <div class="ai-effort-head" aria-hidden="true">
      <span>{{ t.effort.faster }}</span>
      <span class="ai-effort-name"><strong>{{ current.label }}</strong><small>{{ t.effort.upTo(current.sources) }}</small></span>
      <span>{{ t.effort.deeper }}</span>
    </div>
    <Slider class="ai-effort-slider" :model-value="[level]" :min="0" :max="STEPS.length - 1" :step="1" :thumb-label="t.effort.label" :value-text="valueText" @update:model-value="choose">
      <span class="ai-effort-fill" :style="{ '--p': level / (STEPS.length - 1) }" aria-hidden="true">
        <i v-for="(style, i) in sparks" :key="`spark-${i}`" class="ai-effort-spark" :style="style" />
        <i v-for="(style, i) in stars" :key="`star-${i}`" class="ai-effort-star" :style="style" />
      </span>
      <span class="ai-effort-stops" aria-hidden="true">
        <i v-for="(step, i) in STEPS" :key="step.value" :style="{ left: `${(i / (STEPS.length - 1)) * 100}%` }" :data-lit="i <= level || undefined" />
      </span>
    </Slider>
  </div>
</template>

<style scoped>
/* Geometry: a 24 px pill; the handle is a 24 px box with an 18 px white disc, so reka's in-bounds offset puts its middle at
   12 px + p × (width − 24 px), exactly where the stops and the end of the fill are. */
.ai-effort { display: grid; gap: 6px; }
.ai-effort-head { display: grid; grid-template-columns: 1fr auto 1fr; align-items: end; color: var(--muted); font-size: 11px; }
.ai-effort-head > :last-child { text-align: right; }
.ai-effort-name { display: grid; justify-items: center; line-height: 1.2; }
.ai-effort-name strong { background: linear-gradient(90deg, var(--ai-effort-from), var(--ai-effort-to)); -webkit-background-clip: text; background-clip: text; color: transparent; font-size: 13px; font-weight: 650; }
.ai-effort-name small { font-size: 11px; }
.ai-effort :deep([data-slot="slider-track"]) { height: 24px; background: color-mix(in srgb, var(--text) 8%, transparent); }
.ai-effort :deep([data-slot="slider-range"]) { display: none; }
.ai-effort :deep([data-slot="slider-thumb"]) { width: 24px; height: 24px; border: 3px solid transparent; background: #fff padding-box; box-shadow: var(--ai-effort-thumb-shadow); }
.ai-effort :deep([data-slot="slider-thumb"]:focus-visible) { outline: 2px solid var(--accent); outline-offset: 1px; }
.ai-effort-fill { position: absolute; inset-block: 0; left: 0; width: calc(24px + (100% - 24px) * var(--p)); overflow: hidden; border-radius: inherit; background: linear-gradient(90deg, var(--ai-effort-from), var(--ai-effort-to)); transition: width var(--atlas-motion-slow) var(--atlas-ease-out); }
.ai-effort-stops { position: absolute; inset: 0 12px; pointer-events: none; }
.ai-effort-stops > i { position: absolute; top: 50%; width: 4px; height: 4px; border-radius: 50%; background: color-mix(in srgb, var(--text) 26%, transparent); translate: -50% -50%; transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.ai-effort-stops > i[data-lit] { background: rgb(255 255 255 / .7); }
</style>
