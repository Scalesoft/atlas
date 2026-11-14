<script setup lang="ts">
import { reactive, watch } from "vue";
import type { AiQuotaOverrides } from "../../../../administration";
import SettingsRow from "../../SettingsRow.vue";
import InlineReveal from "../../InlineReveal.vue";
import OptionPills from "../../OptionPills.vue";
import { quotaFields } from "./aiAccess";
import t from "../../../../i18n/settings-ai";
const props = defineProps<{ draft: AiQuotaOverrides; disabled?: boolean; inheritLabel?: string }>();
const customText = reactive<Record<keyof AiQuotaOverrides, string>>({ searchAnswerDailyLimit: "20", chatAnswerDailyLimit: "20", pageSummaryDailyLimit: "20", agentDailyLimit: "20" });
watch(() => quotaFields.map(({ key }) => props.draft[key]), () => {
  for (const { key } of quotaFields) if (props.draft[key] !== null && props.draft[key]! > 0) customText[key] = String(props.draft[key]);
}, { immediate: true });
function customInput(key: keyof AiQuotaOverrides, event: Event) {
  const text = (event.target as HTMLInputElement).value;
  customText[key] = text;
  // Zero is only the explicit “Bez limitu” choice. An invalid custom entry must never become unlimited.
  props.draft[key] = /^\d+$/.test(text) && Number(text) >= 1 ? Number(text) : -1;
}
function fieldError(key: keyof AiQuotaOverrides) {
  const value = props.draft[key];
  return value !== null && (!Number.isInteger(value) || value < 0 || value > 10_000) ? t.quota.invalid : undefined;
}
function mode(key: keyof AiQuotaOverrides) { return props.draft[key] === null ? "inherit" : props.draft[key] === 0 ? "unlimited" : "custom"; }
const modeOptions = () => [{ value: "inherit", label: props.inheritLabel || t.quota.inherit }, { value: "unlimited", label: t.quota.unlimited }, { value: "custom", label: t.quota.custom }];
function choose(key: keyof AiQuotaOverrides, value: string) {
  const text = customText[key];
  props.draft[key] = value === "inherit" ? null : value === "unlimited" ? 0
    : /^\d+$/.test(text) && Number(text) >= 1 ? Number(text) : -1;
}
</script>
<template>
  <div class="ai-quota-fields">
    <SettingsRow v-for="field in quotaFields" :key="field.key" v-slot="{ id, describedBy, invalid }" :label="field.label" :data-quota="field.key" :error="fieldError(field.key)" :help="t.quota.help">
      <OptionPills :model-value="mode(field.key)" :options="modeOptions()" :label="field.label" hide-label :disabled="disabled" @update:model-value="choose(field.key, $event)" />
      <InlineReveal :open="mode(field.key) === 'custom'">
        <input :value="customText[field.key]" @input="customInput(field.key, $event)" class="st-input" type="number" min="1" max="10000" step="1" :aria-label="t.quota.customLabel(field.label)" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="disabled || mode(field.key) !== 'custom'" />
      </InlineReveal>
    </SettingsRow>
  </div>
</template>
<style scoped>.ai-quota-fields { display: grid; gap: 8px; padding: 8px 0; }</style>
