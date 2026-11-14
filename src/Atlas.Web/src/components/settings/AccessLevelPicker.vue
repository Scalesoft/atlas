<script setup lang="ts">
import t from "../../i18n/settings-grants";
import { computed, useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import type { ChoiceOption } from "./types";

/**
 * One level on an access staircase (vault access Vidí/Čte/Spravuje, page or space roles
 * Čte/Upravuje/Spravuje): a segmented radiogroup, lowest level first, where the chosen level
 * visibly includes the ones below it (tinted, with a check). One Tab stop, arrow keys move and
 * select. Levels are plain options; the component knows nothing about what they grant.
 */
const props = withDefaults(defineProps<{ options: ChoiceOption[]; label: string; hideLabel?: boolean; disabled?: boolean; readonly?: boolean }>(), {
  hideLabel: false,
  disabled: false,
  readonly: false,
});
const model = defineModel<string | null>({ default: null });
const uid = useId();
const selectedIndex = computed(() => props.options.findIndex((option) => option.value === model.value));
const included = computed(() => props.options.slice(0, Math.max(0, selectedIndex.value)).map((option) => option.label));
function state(index: number) {
  if (selectedIndex.value < 0 || index > selectedIndex.value) return "above";
  return index === selectedIndex.value ? "selected" : "included";
}
</script>

<template>
  <div class="level-ladder" data-access-level-picker>
    <span :id="`${uid}-label`" :class="hideLabel ? 'st-sr-only' : 'level-ladder-label'">{{ label }}</span>
    <RadioGroupRoot
      :model-value="model ?? undefined"
      :disabled="disabled || readonly"
      :data-readonly="readonly || undefined"
      orientation="horizontal"
      loop
      class="level-ladder-steps"
      :aria-labelledby="`${uid}-label`"
      :aria-describedby="included.length ? `${uid}-includes` : undefined"
      @update:model-value="model = String($event)"
    >
      <RadioGroupItem
        v-for="(option, index) in options"
        :key="option.value"
        :value="option.value"
        :disabled="option.disabled"
        class="level-step"
        :data-level="state(index)"
        :data-value="option.value"
        :data-level-color="option.value"
        :style="{ '--step': index }"
      >
        <span class="level-step-bar" aria-hidden="true" />
        <span class="level-step-head">
          <span class="level-step-icon"><AtlasIcon :name="state(index) === 'included' ? 'check' : option.icon ?? 'circle-help'" :size="16" /></span>
          <span class="level-step-title">{{ option.label }}</span>
        </span>
        <span v-if="option.description" class="level-step-description">{{ option.description }}</span>
      </RadioGroupItem>
    </RadioGroupRoot>
    <p :id="`${uid}-includes`" class="level-ladder-includes" aria-live="polite">
      <template v-if="selectedIndex >= 0">
        <AtlasIcon name="circle-check" :size="14" />
        <span v-if="included.length">{{ t.level.includes(options[selectedIndex]!.label, t.list(included)) }}</span>
        <span v-else>{{ t.level.lowest(options[selectedIndex]!.label) }}</span>
      </template>
    </p>
  </div>
</template>

<style scoped>
.level-ladder { display: grid; gap: 8px; min-width: 0; container-type: inline-size; }
.level-ladder-label { font-size: 13px; font-weight: 650; }
.level-ladder-steps {
  display: grid;
  grid-auto-flow: column;
  grid-auto-columns: minmax(0, 1fr);
  gap: 0;
  padding: 3px;
  border: 1px solid var(--line);
  /* A step's 10 px plus the 3 px of padding and the 1 px of border: the corners of the steps follow the track's. */
  border-radius: calc(var(--atlas-radius-md) + 4px);
  background: color-mix(in srgb, var(--canvas) 60%, var(--surface));
}
.level-step {
  position: relative;
  display: grid;
  gap: 3px;
  align-content: start;
  min-width: 0;
  padding: 10px 12px 10px;
  border-radius: var(--atlas-radius-md);
  color: var(--muted);
  font: inherit;
  text-align: left;
  cursor: pointer;
  transition:
    background-color var(--atlas-motion-base) var(--atlas-ease-out),
    color var(--atlas-motion-base) var(--atlas-ease-out),
    box-shadow var(--atlas-motion-base) var(--atlas-ease-out),
    transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
.level-step + .level-step::after {
  content: "";
  position: absolute;
  left: 0;
  top: 12px;
  bottom: 12px;
  width: 1px;
  background: var(--line);
  transition: opacity var(--atlas-motion-base);
}
@media (hover: hover) and (pointer: fine) {
  .level-step:hover:not([data-disabled]) { color: var(--text); background: var(--hover); }
}
.level-step:active:not([data-disabled]) { transform: scale(var(--atlas-press)); }
.level-step:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; z-index: 1; }
/* The staircase: each step's bar is taller than the one below it. */
.level-step-bar {
  display: block;
  width: 26px;
  height: calc(4px + var(--step) * 4px);
  margin-bottom: 4px;
  align-self: end;
  border-radius: 3px;
  background: var(--line);
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out);
}
.level-step-head { display: flex; align-items: center; gap: 7px; min-width: 0; }
.level-step-icon { display: grid; place-items: center; flex-shrink: 0; width: 24px; height: 24px; border-radius: 7px; background: var(--hover); transition: background-color var(--atlas-motion-base), color var(--atlas-motion-base); }
.level-step-title { font-size: 14px; font-weight: 650; color: var(--text); }
.level-step-description { font-size: 12px; line-height: 1.4; }
.level-step[data-level="included"] { background: color-mix(in srgb, var(--level-bg, var(--accent-soft)) 70%, transparent); color: var(--text); }
.level-step[data-level="included"] .level-step-bar { background: color-mix(in srgb, var(--level, var(--accent)) 45%, var(--line)); }
.level-step[data-level="included"] .level-step-icon { color: var(--level, var(--accent)); background: var(--surface); }
.level-step[data-level="included"] .level-step-icon :deep(svg) { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out) calc(var(--step) * var(--st-stagger)) both; }
.level-step[data-level="selected"] { background: var(--surface); color: var(--text); box-shadow: 0 0 0 1.5px var(--level, var(--accent)), var(--st-card-shadow); }
.level-step[data-level="selected"] .level-step-bar { background: var(--level, var(--accent)); }
.level-step[data-level="selected"] .level-step-icon { color: var(--surface); background: var(--level, var(--accent)); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.level-step[data-level="included"]::after, .level-step[data-level="selected"]::after, .level-step[data-level="selected"] + .level-step::after { opacity: 0; }
.level-step[data-disabled] { cursor: not-allowed; opacity: .55; }
.level-ladder-includes { display: flex; align-items: center; gap: 6px; min-height: 18px; margin: 0; color: var(--muted); font-size: 12.5px; }
.level-ladder-includes > svg { color: var(--success); flex-shrink: 0; }
@container (max-width: 420px) {
  .level-step { padding: 8px; }
  .level-step-description { display: none; }
  .level-step-title { font-size: 13px; }
}
</style>
