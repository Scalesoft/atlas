<script setup lang="ts">
import AtlasIcon from "../AtlasIcon.vue";
import type { DecisionStep } from "./types";

// A decision explained left to right (stacked on phones): address → how it was determined →
// matching rule → result. Each link is an icon, a label, a value and a tone.
defineProps<{ steps: DecisionStep[]; label: string }>();
</script>

<template>
  <div class="diagnosis-frame">
  <ol class="diagnosis" :aria-label="label">
    <li v-for="(step, index) in steps" :key="step.label" :data-tone="step.tone" :style="{ '--i': index }">
      <span class="diagnosis-icon"><AtlasIcon :name="step.icon" :size="17" /></span>
      <span class="diagnosis-text">
        <small>{{ step.label }}</small>
        <strong>{{ step.value }}</strong>
        <span v-if="step.detail">{{ step.detail }}</span>
      </span>
      <AtlasIcon v-if="index < steps.length - 1" name="arrow-right" :size="15" class="diagnosis-arrow" />
    </li>
  </ol>
  </div>
</template>

<style scoped>
.diagnosis-frame { container: diagnosis / inline-size; min-width: 0; }
.diagnosis { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 138px), 1fr)); gap: 10px; margin: 0; padding: 0; list-style: none; }
.diagnosis li {
  position: relative;
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 12px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius-sm);
  background: color-mix(in srgb, var(--canvas) 45%, var(--surface));
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
  animation-delay: calc(var(--i) * var(--st-stagger));
}
.diagnosis-icon { display: grid; place-items: center; flex-shrink: 0; width: 32px; height: 32px; border-radius: 9px; color: var(--muted); background: var(--hover); }
[data-tone="success"] .diagnosis-icon { color: var(--success); background: var(--success-bg); }
[data-tone="info"] .diagnosis-icon { color: var(--info); background: var(--info-bg); }
[data-tone="warning"] .diagnosis-icon { color: var(--warning); background: var(--warning-bg); }
[data-tone="danger"] .diagnosis-icon { color: var(--error); background: var(--error-bg); }
[data-tone="success"]:last-child, [data-tone="info"]:last-child { border-color: color-mix(in srgb, var(--accent) 40%, var(--line)); }
.diagnosis-text { display: grid; gap: 1px; min-width: 0; }
.diagnosis-text small { color: var(--muted); font-size: 11.5px; font-weight: 600; text-transform: uppercase; letter-spacing: .04em; }
.diagnosis-text strong { font-size: 13.5px; overflow-wrap: anywhere; }
.diagnosis-text span { color: var(--muted); font-size: 12px; line-height: 1.45; }
.diagnosis-arrow { position: absolute; right: -13px; top: 50%; margin-top: -8px; z-index: 1; color: var(--muted); background: var(--surface); border-radius: 50%; }
@container diagnosis (max-width: 760px) { .diagnosis-arrow { display: none; } }
/* Two tiles side by side on a phone left each sentence a word or two a line ("Nové / přihlášení / před / vydáním"):
   below the width of two readable tiles, the steps follow one under another. */
@container diagnosis (max-width: 440px) { .diagnosis { grid-template-columns: minmax(0, 1fr); } }
</style>
