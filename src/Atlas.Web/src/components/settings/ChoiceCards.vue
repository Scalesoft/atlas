<script setup lang="ts">
import { computed, useId, useSlots } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Checkbox } from "../ui/checkbox";
import { Field, FieldContent, FieldDescription, FieldLabel, FieldTitle } from "../ui/field";
import { RadioGroup, RadioGroupItem } from "../ui/radio-group";
import InlineReveal from "./InlineReveal.vue";
import type { ChoiceOption } from "./types";

/**
 * Choice tiles on shadcn-vue's "Choice Card" pattern (FieldLabel > Field horizontal >
 * FieldContent with FieldTitle + FieldDescription, plus a RadioGroupItem or a Checkbox), with the
 * icon first: compact selection controls with a two-line label, not big cards. Single choice is a
 * radiogroup (one Tab stop, arrows move and select); `multiple` uses checkboxes. The next steps
 * for a choice can open below (`#reveal`). `size="hero"` is reserved for a flow's first decision.
 */
const props = withDefaults(
  defineProps<{
    options: ChoiceOption[];
    label: string;
    hideLabel?: boolean;
    description?: string;
    multiple?: boolean;
    /** Columns on wide containers (2–3); always one column on narrow ones. */
    columns?: number;
    size?: "compact" | "hero";
    /** Values whose selection opens `#reveal`; default is any selection. */
    revealFor?: string[];
    disabled?: boolean;
    name?: string;
  }>(),
  { hideLabel: false, description: undefined, multiple: false, columns: 2, size: "compact", revealFor: undefined, disabled: false, name: undefined },
);
const model = defineModel<string | string[] | null>({ default: null });
const slots = useSlots();
const uid = useId();
const labelId = `choice-label-${uid}`;
const descriptionId = `choice-description-${uid}`;
const revealId = `choice-reveal-${uid}`;
const itemId = (value: string) => `choice-${uid}-${value}`;

const single = computed(() => (typeof model.value === "string" ? model.value : undefined));
const selectedList = computed(() => (Array.isArray(model.value) ? model.value : model.value ? [model.value] : []));
const isSelected = (value: string) => selectedList.value.includes(value);
const revealOpen = computed(() => {
  if (!slots.reveal) return false;
  if (props.multiple) return selectedList.value.length > 0;
  const value = single.value;
  return !!value && (!props.revealFor || props.revealFor.includes(value));
});

// Reka checks a radio reached by an arrow only if the key is still down a moment after focus moved: a quick tap (scripted
// keys, fast typists) released it first and the focus moved without the choice. The arrow selects what it focused (SH-10).
function arrowSelects(event: KeyboardEvent) {
  if (!["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(event.key) || props.disabled) return;
  setTimeout(() => {
    const option = props.options.find(item => itemId(item.value) === document.activeElement?.id);
    if (option && !option.disabled && model.value !== option.value) model.value = option.value;
  }, 0);
}
function toggle(option: ChoiceOption, checked: boolean | "indeterminate") {
  if (props.disabled || option.disabled) return;
  const current = selectedList.value.filter((value) => value !== option.value);
  model.value = checked === true ? [...current, option.value] : current;
}
</script>

<template>
  <div class="choice-cards" :data-size="size" :data-columns="columns">
    <div class="choice-cards-heading" :class="{ 'st-sr-only': hideLabel }">
      <p :id="labelId" class="choice-cards-label">{{ label }}</p>
      <p v-if="description" :id="descriptionId" class="choice-cards-description">{{ description }}</p>
    </div>

    <RadioGroup
      v-if="!multiple"
      :model-value="single"
      :disabled="disabled"
      :name="name"
      loop
      class="choice-grid"
      :aria-labelledby="labelId"
      :aria-describedby="description ? descriptionId : undefined"
      @update:model-value="model = String($event)"
      @keydown="arrowSelects"
    >
      <FieldLabel
        v-for="option in options"
        :key="option.value"
        :for="itemId(option.value)"
        class="choice-tile"
        :data-tone="option.tone"
        :data-value="option.value"
        :data-selected="isSelected(option.value) || undefined"
      >
        <Field orientation="horizontal" :data-disabled="option.disabled || disabled || undefined">
          <span v-if="option.icon" class="choice-tile-icon" :data-tone="option.tone"><AtlasIcon :name="option.icon" :size="size === 'hero' ? 24 : 18" /></span>
          <FieldContent class="choice-tile-content">
            <FieldTitle class="choice-tile-title">{{ option.label }}<span v-if="option.badge" class="choice-badge">{{ option.badge }}</span></FieldTitle>
            <FieldDescription v-if="option.disabled && option.disabledReason" class="choice-tile-description">{{ option.disabledReason }}</FieldDescription>
            <FieldDescription v-else-if="option.description" class="choice-tile-description">{{ option.description }}</FieldDescription>
            <div v-if="$slots.option" class="choice-extra"><slot name="option" :option="option" :selected="isSelected(option.value)" /></div>
          </FieldContent>
          <RadioGroupItem
            :id="itemId(option.value)"
            :value="option.value"
            :disabled="option.disabled"
            class="choice-tile-control"
            :aria-controls="revealOpen && isSelected(option.value) ? revealId : undefined"
          />
        </Field>
      </FieldLabel>
    </RadioGroup>

    <div v-else class="choice-grid" role="group" :aria-labelledby="labelId" :aria-describedby="description ? descriptionId : undefined">
      <FieldLabel
        v-for="option in options"
        :key="option.value"
        :for="itemId(option.value)"
        class="choice-tile"
        :data-tone="option.tone"
        :data-value="option.value"
        :data-selected="isSelected(option.value) || undefined"
      >
        <Field orientation="horizontal" :data-disabled="option.disabled || disabled || undefined">
          <span v-if="option.icon" class="choice-tile-icon" :data-tone="option.tone"><AtlasIcon :name="option.icon" :size="18" /></span>
          <FieldContent class="choice-tile-content">
            <FieldTitle class="choice-tile-title">{{ option.label }}<span v-if="option.badge" class="choice-badge">{{ option.badge }}</span></FieldTitle>
            <FieldDescription v-if="option.disabled && option.disabledReason" class="choice-tile-description">{{ option.disabledReason }}</FieldDescription>
            <FieldDescription v-else-if="option.description" class="choice-tile-description">{{ option.description }}</FieldDescription>
          </FieldContent>
          <Checkbox
            :id="itemId(option.value)"
            :model-value="isSelected(option.value)"
            :disabled="disabled || option.disabled"
            :value="option.value"
            class="choice-tile-control"
            @update:model-value="toggle(option, $event)"
          />
        </Field>
      </FieldLabel>
    </div>

    <InlineReveal v-if="$slots.reveal" :id="revealId" :open="revealOpen">
      <Transition name="st-step" mode="out-in">
        <div :key="multiple ? 'multiple' : single" class="choice-reveal"><slot name="reveal" :value="model" /></div>
      </Transition>
    </InlineReveal>
  </div>
</template>

<style scoped>
.choice-cards { display: grid; gap: 10px; min-width: 0; container-type: inline-size; }
.choice-cards-heading { display: grid; gap: 3px; }
.choice-cards-label { margin: 0; font-size: 14px; font-weight: 650; }
.choice-cards-description { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.55; }
.choice-grid { display: grid; gap: 10px; grid-template-columns: minmax(0, 1fr); }
@container (min-width: 520px) {
  .choice-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  [data-columns="1"] > .choice-grid { grid-template-columns: minmax(0, 1fr); }
}
@container (min-width: 640px) {
  [data-columns="4"] > .choice-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); }
}
@container (min-width: 760px) {
  [data-columns="3"] > .choice-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
}

/* Keep the label's hit area still. Moving the whole tile on hover can repeatedly lose/re-enter
   hover at its lower edge; border and shadow provide feedback without moving the target. */
.choice-tile {
  position: relative;
  min-width: 0;
  border-color: var(--line);
  border-radius: 12px;
  background: var(--surface);
  color: var(--text);
  cursor: pointer;
  transition:
    box-shadow var(--atlas-motion-base) var(--atlas-ease-out),
    border-color var(--atlas-motion-fast) var(--atlas-ease-out),
    background-color var(--atlas-motion-fast) var(--atlas-ease-out),
    scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
/* Pressed like every other control; `scale` rather than `transform`, so the tile's transform stays none and its hit area as it was. */
.choice-tile:active:not(:has([data-disabled], :disabled)) { scale: var(--atlas-press); }
.choice-tile :deep([data-slot="field"]) { padding: 12px 14px; gap: 12px; }
[data-size="hero"] .choice-tile :deep([data-slot="field"]) { padding: 18px; }
@media (hover: hover) and (pointer: fine) {
  .choice-tile:hover:not(:has([data-disabled], :disabled)) { border-color: color-mix(in srgb, var(--accent) 45%, var(--line)); box-shadow: var(--st-hover-shadow); }
}
.choice-tile:has(:focus-visible) { outline: 2px solid var(--accent); outline-offset: -2px; }
.choice-tile:has([data-disabled], :disabled) { cursor: not-allowed; transform: none; box-shadow: none; }
/* InlineReveal clips external rings; keep selection on the tile's own border. */
.choice-tile[data-selected] { border-color: var(--accent); background: color-mix(in srgb, var(--accent-soft) 40%, var(--surface)); box-shadow: none; }
.choice-tile-icon {
  display: grid;
  place-items: center;
  flex-shrink: 0;
  width: 34px;
  height: 34px;
  border-radius: 10px;
  color: var(--accent);
  background: var(--accent-soft);
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out);
}
[data-size="hero"] .choice-tile-icon { width: 46px; height: 46px; border-radius: 13px; }
.choice-tile-icon[data-tone="warning"] { color: var(--warning); background: var(--warning-bg); }
.choice-tile-icon[data-tone="success"] { color: var(--success); background: var(--success-bg); }
.choice-tile-icon[data-tone="danger"] { color: var(--error); background: var(--error-bg); }
.choice-tile-icon[data-tone="neutral"] { color: var(--muted); background: var(--hover); }
.choice-tile[data-selected] .choice-tile-icon { color: var(--on-accent); background: var(--accent); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.choice-tile-content { min-width: 0; gap: 3px; }
.choice-tile-title { flex-wrap: wrap; gap: 4px 8px; font-weight: 650; }
[data-size="hero"] .choice-tile-title { font-size: 15.5px; }
.choice-tile-description { font-size: 12.5px; line-height: 1.45; }
.choice-tile[data-selected] .choice-tile-description { color: var(--text); }
.choice-badge { padding: 0 7px; border-radius: 999px; font-size: 11px; font-weight: 600; color: var(--accent); background: var(--accent-soft); }
.choice-tile[data-selected] .choice-badge { background: var(--surface); }
.choice-tile-control { margin-top: 2px; }
.choice-tile[data-selected] .choice-tile-control :deep([data-slot="radio-group-indicator"]),
.choice-tile[data-selected] .choice-tile-control :deep([data-slot="checkbox-indicator"]) { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.choice-extra { margin-top: 6px; }
.choice-reveal { padding-top: 4px; }
</style>
