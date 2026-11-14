<script setup lang="ts">
import { computed, useId, useSlots } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import CommandIcon from "../CommandIcon.vue";
import { disclosureHooks } from "./disclosure";

/**
 * Label and help on the left, control on the right (stacked on narrow screens), the "odkud"
 * origin and the field's own error below the control. The default slot receives the ids to
 * wire up: `<input :id="id" :aria-describedby="describedBy" :aria-invalid="invalid">`.
 */
// `icon` (refresh 2.5, boards Profil and Prostor – Obecné) puts a quiet tile before the label, so a long form reads
// by its pictures; the label column keeps the same width with or without it.
const props = withDefaults(
  defineProps<{ label: string; help?: string; error?: string; origin?: string; stacked?: boolean; labelFor?: boolean; icon?: string }>(),
  { help: undefined, error: undefined, origin: undefined, stacked: false, labelFor: true, icon: undefined },
);
const slots = useSlots();
const id = `settings-row-${useId()}`;
const helpId = `${id}-help`;
const originId = `${id}-origin`;
const errorId = `${id}-error`;
const describedBy = computed(() =>
  [props.help ? helpId : "", props.origin || slots.origin ? originId : "", props.error ? errorId : ""].filter(Boolean).join(" ") || undefined,
);
</script>

<template>
  <div class="settings-row" :class="{ 'is-stacked': stacked }" :data-invalid="error ? 'true' : undefined">
    <div class="settings-row-layout">
    <div class="settings-row-text" :data-icon="icon ? 'true' : undefined">
      <span v-if="icon" class="st-icon-tile settings-row-icon" data-tone="neutral" aria-hidden="true"><CommandIcon :name="icon" :size="16" /></span>
      <label v-if="labelFor" class="settings-row-label" :for="id">{{ label }}</label>
      <span v-else :id="`${id}-label`" class="settings-row-label">{{ label }}</span>
      <p v-if="help" :id="helpId" class="settings-row-help">{{ help }}</p>
    </div>
    <div class="settings-row-control">
      <slot :id="id" :label-id="`${id}-label`" :described-by="describedBy" :invalid="error ? true : undefined" />
      <div v-if="origin || $slots.origin" :id="originId" class="settings-row-origin">
        <slot name="origin"><AtlasIcon name="arrow-right-left" :size="13" />{{ origin }}</slot>
      </div>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="error" :id="errorId" class="settings-row-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ error }}</p>
      </Transition>
    </div>
    </div>
  </div>
</template>

<style scoped>
/* Refresh 2.5 (board 10): label and help in the first column, the control in the second, at the same
   ratio in every card (--st-label-col / --st-control-col), so controls line up down the page. Inside a
   card the row reaches the card edges and keeps the card inset (--st-row-pad-x, settings.css). */
.settings-row {
  container-type: inline-size;
  overflow-wrap: anywhere;
  padding: 16px var(--st-row-pad-x, 0px);
  border-top: 1px solid var(--st-line-soft, color-mix(in srgb, var(--line) 70%, transparent));
}
.settings-row:first-child { border-top: 0; }
.settings-row-layout { display: grid; gap: 8px var(--st-col-gap, 24px); }
.settings-row-text { display: grid; gap: 2px; min-width: 0; align-content: start; }
.settings-row-text[data-icon] { grid-template-columns: 32px minmax(0, 1fr); column-gap: 12px; }
.settings-row-text[data-icon] > :not(.settings-row-icon) { grid-column: 2; }
.settings-row-icon { grid-row: 1 / span 2; width: 32px; height: 32px; border-radius: 10px; }
.settings-row-label { display: block; font-size: 14px; font-weight: 600; line-height: 1.4; }
.settings-row-help { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
/* The space between the parts is a margin and not the grid gap: the error unfolds with its own margin (disclosure.ts). */
.settings-row-control { display: grid; min-width: 0; align-content: start; justify-items: stretch; }
/* `:not(:first-child)`, never `* + *`: a sibling combinator over any element made Chrome restyle the editor's blocks after every inserted block (~20 ms per Enter on 150 blocks, J12.5). */
.settings-row-control > :deep(:not(:first-child)) { margin-top: 6px; }
/* A switch or a short control keeps its own width at the start of the control column. */
.settings-row-control > :where(button[role="switch"], .status-badge, .option-pills) { justify-self: start; }
.settings-row-origin { display: flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; }
.settings-row-origin > svg { flex-shrink: 0; }
.settings-row-error { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
.settings-row-error > svg { flex-shrink: 0; margin-top: 2px; }
@container (min-width: 600px) {
  .settings-row:not(.is-stacked) .settings-row-layout { grid-template-columns: var(--st-label-col, minmax(0, 1fr)) var(--st-control-col, minmax(0, 1.3fr)); align-items: start; }
  /* A lone switch sits on the first line of the label. */
  .settings-row:not(.is-stacked) .settings-row-control:has(> button[role="switch"]:only-child) { padding-top: 1px; }
}
</style>
