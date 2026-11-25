<script setup lang="ts">
import { shallowRef, useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import { useSegmentedThumb } from "../library/segmentedThumb";
import type { ChoiceOption } from "./types";

/**
 * A compact row of clickable pills for short choices (filters, periods, a mode switch). Always a
 * radiogroup: one Tab stop, arrows move and select. Each pill has an icon and a word.
 * `segmented` is the quiet look of a view switch (Karty | Seznam, Náhled | Markdown): one raised chip under the options
 * glides to the chosen one (segmentedThumb.ts). The behaviour is the same.
 */
const props = withDefaults(defineProps<{ options: ChoiceOption[]; label: string; hideLabel?: boolean; disabled?: boolean; segmented?: boolean }>(), {
  hideLabel: false,
});
const model = defineModel<string>({ default: "" });
const uid = useId();
const track = shallowRef<HTMLElement | null>(null);
const bindTrack = (instance: unknown) => { track.value = props.segmented ? ((instance as { $el?: HTMLElement } | null)?.$el ?? null) : null; };
const thumb = useSegmentedThumb(track, '[data-state="checked"]', model);
</script>

<template>
  <div class="option-pills" :class="{ 'is-segmented': segmented }">
    <span :id="`${uid}-label`" class="option-pills-label" :class="{ 'st-sr-only': hideLabel }">{{ label }}</span>
    <div class="option-pills-row">
      <RadioGroupRoot :ref="bindTrack" v-bind="segmented ? thumb : {}" :model-value="model" :disabled="disabled" loop class="option-pills-group" :aria-labelledby="`${uid}-label`" @update:model-value="!disabled && (model = String($event))">
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
/* Segmented: the raised chip is the thumb of the group (segmentedThumb.css); without it (reduced motion) the chosen option draws its own. */
.is-segmented .option-pills-group { --thumb-inset: 2px; --thumb-radius: 7px; flex-wrap: nowrap; gap: 2px; padding: 2px; border-radius: 9px; background: color-mix(in srgb, var(--line) 50%, var(--surface)); }
.is-segmented .option-pill { height: 30px; padding: 0 10px; border: 0; border-radius: 7px; background: transparent; color: var(--muted); font-weight: 500; }
@media (hover: hover) and (pointer: fine) { .is-segmented .option-pill:hover:not([data-disabled]) { color: var(--text); } }
.is-segmented .option-pill > svg { color: currentColor; animation: none; }
.is-segmented .option-pill[data-state="checked"] { background: var(--surface); color: var(--text); font-weight: 600; box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 8%, transparent); }
.is-segmented .option-pills-group[data-thumb] .option-pill[data-state="checked"] { background: transparent; box-shadow: none; }
</style>
