<script setup lang="ts">
import t from "../../i18n/settings-flow";
import { computed, onMounted, ref, useId, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { disclosureHooks } from "./disclosure";
import ChoiceCards from "./ChoiceCards.vue";
import { minimumEnd, presetEnd, validityPresets } from "./grantValidity";
import type { ChoiceOption } from "./types";

/**
 * The validity of a grant, the one component for it (owner decision 27. 9.): a new grant in
 * GrantFlow's Platnost step and an existing one edited in place (AccessGrantEditor). "Od" and "Do"
 * are both optional; the tiles 7 / 30 / 90 dní set Do that many days after Od (or now) and Bez
 * omezení clears it. Typing a date of one's own deselects the tile; changing Od moves a chosen
 * preset's end along. Values are `datetime-local` strings in the browser's zone; the parent
 * converts them and checks them live with `validityError` (./grantValidity.ts: an end after the start
 * and, when new or changed, in the future), passes the reason as `error` and keeps Save disabled while
 * there is one; the reason stands right under the dates. Approving an access request
 * starts now (`hideStart`), starts on a preset (`initialPreset`, e.g. 30 dní) and has an end whenever
 * the API always bounds it (`allowNoEnd` false: Bez omezení is shown but unavailable, with the reason).
 */
const props = withDefaults(
  defineProps<{ label?: string; hideLabel?: boolean; error?: string; disabled?: boolean; hideStart?: boolean; initialPreset?: string; allowNoEnd?: boolean; noEndReason?: string }>(),
  { label: undefined, hideLabel: false, error: undefined, disabled: false, hideStart: false, initialPreset: undefined, allowNoEnd: true, noEndReason: undefined },
);
const startAt = defineModel<string>("startAt", { default: "" });
const expiresAt = defineModel<string>("expiresAt", { default: "" });

const options: ChoiceOption[] = validityPresets.map((preset) => ({
  value: preset.value,
  label: preset.label,
  description: preset.days === null ? t.validity.noEnd : t.validity.endsAfter(preset.days),
  icon: preset.days === null ? "check" : "calendar",
  disabled: preset.days === null && !props.allowNoEnd,
  disabledReason: preset.days === null && !props.allowNoEnd ? props.noEndReason ?? t.validity.noEndReason : undefined,
}));

// The tile follows the values: none without an end, the chosen preset while its end is untouched.
const preset = ref<string | null>(expiresAt.value ? null : "none");
let presetValue = expiresAt.value;
function applyPreset(value: string) {
  const chosen = validityPresets.find((item) => item.value === value);
  if (!chosen) return;
  preset.value = chosen.value;
  presetValue = presetEnd(chosen, startAt.value);
  expiresAt.value = presetValue;
}
const choice = computed({
  get: () => preset.value,
  set: (value: string | string[] | null) => {
    if (typeof value === "string") applyPreset(value);
  },
});
watch(expiresAt, (value) => {
  if (value !== presetValue) preset.value = value ? null : "none";
});
watch(startAt, () => {
  if (preset.value && preset.value !== "none") applyPreset(preset.value);
});
// After mount: a model emitted during setup does not reach the input.
onMounted(() => {
  if (props.initialPreset && !expiresAt.value) applyPreset(props.initialPreset);
});
// The picker offers no end before the next minute or before Od; the parent's `error` marks a typed one.
const endMinimum = computed(() => minimumEnd(startAt.value));
const errorId = `grant-validity-error-${useId()}`;
</script>

<template>
  <div class="grant-validity">
    <ChoiceCards v-model="choice" :options="options" :label="props.label ?? t.validity.label" :hide-label="hideLabel" :columns="4" :disabled="disabled" name="validity" />
    <div class="grant-validity-dates">
      <label v-if="!hideStart"><span><AtlasIcon name="clock" :size="14" />{{ t.validity.from }} <small>{{ t.validity.optional }}</small></span><input v-model="startAt" class="st-input" type="datetime-local" name="startAt" :disabled="disabled" /></label>
      <label><span><AtlasIcon name="calendar" :size="14" />{{ t.validity.to }} <small>{{ t.validity.optional }}</small></span><input v-model="expiresAt" class="st-input" type="datetime-local" name="expiresAt" :min="endMinimum" :disabled="disabled" :aria-invalid="error ? true : undefined" :aria-describedby="error ? errorId : undefined" /></label>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="error" :id="errorId" class="grant-validity-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ error }}</p>
      </Transition>
      <p>{{ hideStart ? t.validity.hintNow : t.validity.hint }}</p>
    </div>
  </div>
</template>

<style scoped>
.grant-validity { display: grid; gap: 10px; min-width: 0; }
.grant-validity-dates { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 220px), 1fr)); gap: 10px; }
.grant-validity-dates label { display: grid; gap: 5px; font-size: 13px; font-weight: 600; }
.grant-validity-dates label > span { display: inline-flex; align-items: center; gap: 6px; }
.grant-validity-dates small { color: var(--muted); font-size: 12px; font-weight: 500; }
.grant-validity-dates p { grid-column: 1 / -1; margin: 0; color: var(--muted); font-size: 12px; }
/* Right under the dates it concerns; it fades in (a plain fade under reduced motion too). */
.grant-validity-dates .grant-validity-error { display: flex; align-items: center; gap: 6px; color: var(--error); font-size: 13px; font-weight: 550; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
</style>
