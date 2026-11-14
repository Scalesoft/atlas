<script setup lang="ts">
import { onBeforeUnmount, ref, useId } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { FEEDBACK_HOLD_MS } from "../../../motion/aiMotion";
import t from "../../../i18n/settings-backups";
import { Button } from "../../ui/button";
import { disclosureHooks } from "../disclosure";

/**
 * A value someone copies into another system (the OIDC callback address for the client
 * registration, the public deploy key of Záloha do Gitu): the label, the value selectable as code
 * and a copy button that confirms in place. When the clipboard is refused it says so next to the
 * value instead of failing silently. `valueAttrs` puts a page's stable hook (`data-…`) on the value
 * element itself. OneTimeSecret is for a secret shown once; this is for a public value that can be
 * shown any time.
 */
const props = withDefaults(defineProps<{ label: string; value: string; copyLabel?: string; valueAttrs?: Record<string, string> }>(), { copyLabel: undefined, valueAttrs: undefined });
const uid = useId();
const state = ref<"idle" | "copied" | "failed">("idle");
let timer: ReturnType<typeof setTimeout> | undefined;

async function copy() {
  clearTimeout(timer);
  try {
    await navigator.clipboard.writeText(props.value);
    state.value = "copied";
    timer = setTimeout(() => (state.value = "idle"), FEEDBACK_HOLD_MS);
  } catch {
    state.value = "failed";
  }
}
onBeforeUnmount(() => clearTimeout(timer));
</script>

<template>
  <div class="copy-field" :data-state="state">
    <span :id="`${uid}-label`" class="copy-field-label">{{ label }}</span>
    <div class="copy-field-value">
      <code v-bind="valueAttrs" class="st-mono" :aria-labelledby="`${uid}-label`" data-copy-value>{{ value }}</code>
      <Button variant="outline" size="sm" type="button" data-copy-field @click="copy">
        <!-- Both states share one cell, so the button keeps the width of the longer one and the value beside it does not re-wrap when it confirms. -->
        <span class="copy-field-action">
          <span :class="{ 'is-away': state === 'copied' }"><AtlasIcon name="copy" :size="15" />{{ copyLabel ?? t.copyField.copy }}</span>
          <span :class="{ 'is-away': state !== 'copied' }"><AtlasIcon name="check" :size="15" />{{ t.copyField.copied }}</span>
        </span>
      </Button>
    </div>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="state === 'failed'" class="copy-field-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ t.copyField.failed }}</p>
    </Transition>
  </div>
</template>

<style scoped>
.copy-field { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.copy-field-label { color: var(--muted); font-size: 12px; font-weight: 600; }
/* The 32 px button sits 2 px inside the field's edge (1 px border + 1 px padding), which makes the field 36 px high like an input,
   and its corners are the field's 10 px less that 2 px: 8 px, concentric. The value brings its own 6 px above and below. */
.copy-field-value { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 1px 1px 1px 12px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: color-mix(in srgb, var(--canvas) 50%, var(--surface)); }
.copy-field-value :deep([data-copy-field]) { border-radius: calc(var(--st-radius-sm) - 2px); }
.copy-field-value code { flex: 1 1 220px; min-width: 0; padding-block: 6px; overflow-wrap: anywhere; user-select: all; font-size: 13px; }
/* The small button pads 10 px beside an icon and 12 px beside text, and gaps 6 px: the cell keeps the look of icon plus label. */
.copy-field-action { display: inline-grid; margin-inline: -2px; }
.copy-field-action > span { grid-area: 1 / 1; display: inline-flex; align-items: center; justify-content: center; gap: 6px; }
.copy-field-action > .is-away { visibility: hidden; }
[data-state="copied"] .copy-field-value :deep(svg) { color: var(--success); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.copy-field-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
</style>
