<script setup lang="ts">
import { useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import type { ChoiceOption } from "./types";

/**
 * A compact row of clickable pills for short choices (filters, periods, a mode switch). Always a
 * radiogroup: one Tab stop, arrows move and select. Each pill has an icon and a word.
 */
withDefaults(defineProps<{ options: ChoiceOption[]; label: string; hideLabel?: boolean; disabled?: boolean }>(), {
  hideLabel: false,
});
const model = defineModel<string>({ default: "" });
const uid = useId();
</script>

<template>
  <div class="option-pills">
    <span :id="`${uid}-label`" class="option-pills-label" :class="{ 'st-sr-only': hideLabel }">{{ label }}</span>
    <div class="option-pills-row">
      <RadioGroupRoot :model-value="model" :disabled="disabled" loop class="option-pills-group" :aria-labelledby="`${uid}-label`" @update:model-value="!disabled && (model = String($event))">
          <RadioGroupItem v-for="option in options" :key="option.value || '__all'" :value="option.value" :disabled="disabled || option.disabled" class="option-pill" :data-tone="option.tone" :data-value="option.value || 'all'">
            <AtlasIcon v-if="option.icon" :name="option.icon" :size="14" />
            <span>{{ option.label }}</span>
            <span v-if="option.badge" class="option-pill-badge">{{ option.badge }}</span>
          </RadioGroupItem>
      </RadioGroupRoot>
    </div>
  </div>
</template>

<style scoped>
.option-pills { display: grid; gap: 6px; min-width: 0; }
.option-pills-label { font-size: 12.5px; font-weight: 650; color: var(--muted); }
.option-pills-row { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; min-width: 0; }
.option-pills-group { display: flex; flex-wrap: wrap; gap: 6px; min-width: 0; }
.option-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 32px;
  padding: 0 12px 0 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: var(--surface);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition:
    background-color var(--atlas-motion-fast) var(--atlas-ease-out),
    border-color var(--atlas-motion-fast) var(--atlas-ease-out),
    color var(--atlas-motion-fast),
    transform var(--atlas-motion-base) var(--atlas-ease-out);
}
.option-pill > svg { color: var(--muted); transition: color var(--atlas-motion-fast); }
@media (hover: hover) and (pointer: fine) {
  .option-pill:hover:not([data-disabled]) { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); }
}
.option-pill:active:not([data-disabled]) { transform: scale(var(--atlas-press)); transition-duration: var(--atlas-motion-fast); }
.option-pill:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.option-pill[data-state="checked"] { border-color: var(--accent); background: var(--accent); color: var(--on-accent); }
.option-pill[data-state="checked"] > svg { color: var(--on-accent); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.option-pill[data-disabled] { opacity: .5; cursor: not-allowed; }
.option-pill-badge { padding: 0 6px; border-radius: 999px; font-size: 11px; font-weight: 650; background: var(--hover); color: var(--muted); }
.option-pill[data-state="checked"] .option-pill-badge { background: color-mix(in srgb, var(--on-accent) 20%, transparent); color: var(--on-accent); }
</style>
