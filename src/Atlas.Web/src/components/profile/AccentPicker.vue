<script setup lang="ts" generic="Id extends string">
import { computed, useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import { defaultThemeAccent } from "../../profile";
import type { AccentChoice } from "./accentChoice";
import t from "../../i18n/profile";

/**
 * The accent of the interface, the colour it leans to (Já → Vzhled): a row of round swatches with the name
 * of the chosen one below. A radiogroup: one Tab stop, arrows move and select. Each swatch is drawn in the
 * previewed mode, so in dark it shows the light tone the dark theme uses.
 * Not for content colours: the icon and colour of a space or page is AppearancePicker (60 tones).
 * An accent with a `swatch` (its own light and dark colour) is drawn in exactly that colour instead of the one of its `hue`: the
 * collaborator palette („Moje barva“, collaboratorColors.ts), whose swatches must be the colours of the carets.
 */
const props = withDefaults(defineProps<{ accents: readonly AccentChoice<Id>[]; mode: "light" | "dark"; label: string; disabled?: boolean }>(), { disabled: false });
const model = defineModel<Id>({ required: true });
const swatchStyle = (accent: AccentChoice<Id>) => accent.swatch
  ? { "--swatch": props.mode === "dark" ? accent.swatch.dark : accent.swatch.light, "--swatch-ring": props.mode === "dark" ? accent.swatch.dark : accent.swatch.light, "--swatch-ink": "#ffffff" }
  : { "--swatch-h": accent.hue };
const uid = useId();
const current = computed(() => props.accents.find((accent) => accent.id === model.value));
</script>

<template>
  <div class="accent-picker" :data-mode="mode">
    <RadioGroupRoot
      :model-value="model"
      :disabled="disabled"
      loop
      orientation="horizontal"
      class="accent-swatches"
      :aria-label="label"
      :aria-describedby="`${uid}-name`"
      @update:model-value="model = $event as Id"
    >
      <span v-for="accent in accents" :key="accent.id" class="accent-swatch-cell" :data-value="accent.id">
        <RadioGroupItem :value="accent.id" class="accent-swatch" :style="swatchStyle(accent)" :aria-label="accent.label" :title="accent.label">
          <Transition name="accent-check"><AtlasIcon v-if="model === accent.id" name="check" :size="16" class="accent-swatch-check" /></Transition>
        </RadioGroupItem>
      </span>
    </RadioGroupRoot>
    <p :id="`${uid}-name`" class="accent-picker-name">
      <Transition name="accent-name" mode="out-in"><span :key="model">{{ current?.label ?? model }}<template v-if="model === defaultThemeAccent"> · {{ t.appearance.accent.default }}</template></span></Transition>
    </p>
  </div>
</template>

<style scoped>
.accent-picker { display: grid; gap: 8px; min-width: 0; container: accent-picker / inline-size; }
.accent-swatches { display: flex; flex-wrap: wrap; gap: 8px; }
/* Eight swatches and their gaps need 344 px (376 with the larger touch swatches). In a column that only fits seven, the
   eighth stood alone on a second row: it wraps as two rows of four instead. */
@container accent-picker (max-width: 343px) { .accent-swatches { display: grid; grid-template-columns: repeat(4, max-content); } }
@media (pointer: coarse) {
  @container accent-picker (max-width: 375px) { .accent-swatches { display: grid; grid-template-columns: repeat(4, max-content); } }
}
.accent-swatch-cell { display: inline-flex; }
/* A swatch rings faintly when pointed at (nothing moves), presses to .98 and rings fully when chosen (board 12, motion board 16). */
.accent-swatch {
  --swatch: oklch(60% 0.13 var(--swatch-h));
  --swatch-ring: oklch(44% 0.12 var(--swatch-h));
  --swatch-ink: #ffffff;
  --swatch-edge: rgb(0 0 0 / 0.08);
  display: inline-grid;
  place-items: center;
  width: 36px;
  height: 36px;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: var(--swatch);
  color: var(--swatch-ink);
  cursor: pointer;
  box-shadow: inset 0 0 0 1px var(--swatch-edge);
  transition:
    transform var(--atlas-motion-fast) var(--atlas-ease-out),
    box-shadow var(--atlas-motion-base) var(--atlas-ease-out),
    background-color var(--atlas-motion-base) var(--atlas-ease-out);
}
.accent-picker[data-mode="dark"] .accent-swatch {
  --swatch: oklch(78% 0.12 var(--swatch-h));
  --swatch-ring: oklch(84% 0.1 var(--swatch-h));
  --swatch-ink: oklch(20% 0.05 var(--swatch-h));
  --swatch-edge: rgb(255 255 255 / 0.08);
}
/* Pointed at, a swatch shows the ring it would get when chosen, faintly: it never leaves the pointer. */
@media (hover: hover) and (pointer: fine) {
  .accent-swatch:hover:not([data-disabled]):not([data-state="checked"]) { box-shadow: inset 0 0 0 1px var(--swatch-edge), 0 0 0 2px var(--surface), 0 0 0 4px color-mix(in srgb, var(--swatch-ring) 45%, transparent); }
}
.accent-swatch:active:not([data-disabled]) { transform: scale(var(--atlas-press)); }
.accent-swatch[data-state="checked"] { box-shadow: inset 0 0 0 1px var(--swatch-edge), 0 0 0 2px var(--surface), 0 0 0 4px var(--swatch-ring); }
.accent-swatch:focus-visible { outline: var(--focus-ring); outline-offset: var(--focus-offset); }
.accent-swatch[data-disabled] { cursor: not-allowed; opacity: .55; }
/* The tick pops in on the new swatch and fades on the old one instead of vanishing in one frame. */
.accent-check-enter-active { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.accent-check-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out); }
.accent-picker-name { min-height: 20px; margin: 0; color: var(--muted); font-size: 13px; }
.accent-name-enter-active { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.accent-name-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (pointer: coarse) { .accent-swatch { width: 40px; height: 40px; } }
</style>
