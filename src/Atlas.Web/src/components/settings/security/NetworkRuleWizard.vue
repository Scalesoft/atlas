<script setup lang="ts">
import t from "../../../i18n/settings-security";
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import { disclosureHooks } from "../disclosure";
import ImpactPreview from "../ImpactPreview.vue";
import SettingsRow from "../SettingsRow.vue";
import StatusBadge from "../StatusBadge.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { ChoiceOption, FlowStep, ImpactItem, SummaryItem } from "../types";
import { formatAddressCount, formatAddress, parseCidr, rangeWarnings, type ParsedRange } from "./cidr";
import type { NetworkKind, NetworkRuleDraft } from "./types";

/**
 * Přidat důvěryhodnou síť: 1 Druh (LAN / VPN / jiná) · 2 Rozsah (name and CIDR with live
 * normalisation and the warnings of ./cidr.ts: whole internet, too broad, public, overlap) ·
 * 3 Dopad · 4 Potvrzení with a fresh sign-in. The active rules and the impact come in as props;
 * the rule and the proof go out via `save`, and the page shows `busy` and a refusal (`error`).
 */
const props = withDefaults(
  defineProps<{
    existing: { name: string; cidr: string }[];
    impact: (range: ParsedRange, kind: NetworkKind) => ImpactItem[];
    busy?: boolean;
    error?: string;
  }>(),
  { busy: false, error: undefined },
);
const emit = defineEmits<{ save: [rule: NetworkRuleDraft, proofId: string | null]; cancel: [] }>();

const step = ref("kind");
const kind = ref<string | null>(null);
const name = ref("");
const cidr = ref("");
const touched = ref(false);

const kindOptions = computed<ChoiceOption[]>(() => [
  { value: "lan", label: t.rule.lan, description: t.rule.lanHint, icon: "building-2" },
  { value: "vpn", label: "VPN", description: t.rule.vpnHint, icon: "globe" },
  { value: "other", label: t.rule.other, description: t.rule.otherHint, icon: "map-pin" },
]);
const kindLabel = computed(() => kindOptions.value.find((option) => option.value === kind.value)?.label);
const placeholders = computed<Record<string, { name: string; cidr: string }>>(() => ({
  lan: { name: t.rule.placeholderLan, cidr: "10.0.0.0/16" },
  vpn: { name: t.rule.placeholderVpn, cidr: "10.4.0.0/22" },
  other: { name: t.rule.placeholderOther, cidr: "198.51.100.7" },
}));

const parsed = computed(() => parseCidr(cidr.value));
const range = computed(() => (parsed.value.ok ? parsed.value : undefined));
const warnings = computed(() => (range.value ? rangeWarnings(range.value, props.existing) : []));
const blocking = computed(() => warnings.value.some((warning) => warning.blocking));
const nameError = computed(() => (touched.value && !name.value.trim() ? t.rule.nameError : undefined));
const cidrError = computed(() => {
  if (!touched.value && !cidr.value) return undefined;
  if (!parsed.value.ok) return parsed.value.error;
  return undefined;
});
const canContinue = computed(() => !!name.value.trim() && !!range.value && !blocking.value);

watch(kind, (value, previous) => {
  if (previous && value !== previous) touched.value = false;
});

const impact = computed<ImpactItem[]>(() => (range.value && kind.value ? props.impact(range.value, kind.value as NetworkKind) : []));

function validateRange() {
  touched.value = true;
  return canContinue.value;
}
const steps = computed<FlowStep[]>(() => [
  { id: "kind", label: t.rule.kindStep, icon: "network", title: t.rule.kindTitle, hint: t.rule.kindHint, summary: kindLabel.value, ready: !!kind.value },
  { id: "range", label: t.rule.rangeStep, icon: "map-pin", title: t.rule.rangeTitle, hint: t.rule.rangeHint, summary: range.value ? `${name.value || t.rule.unnamed} · ${range.value.cidr}` : undefined, ready: canContinue.value, validate: validateRange },
  { id: "impact", label: t.rule.impactStep, icon: "activity", title: t.rule.impactTitle, hint: t.rule.impactHint },
  { id: "confirm", label: t.rule.confirmStep, icon: "circle-check", title: t.rule.confirmTitle, hint: t.rule.confirmHint, hideNext: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "name", icon: kind.value === "vpn" ? "globe" : kind.value === "other" ? "map-pin" : "building-2", label: t.rule.network, value: name.value.trim() },
  { key: "cidr", icon: "map-pin", label: t.rule.range, value: range.value ? `${range.value.cidr} · ${formatAddressCount(range.value.size)}` : "", code: true },
  { key: "kind", icon: "network", label: t.rule.kind, value: kindLabel.value },
  { key: "state", icon: "circle-check", label: t.rule.status },
]);
function confirm(proofId: string | null) {
  if (props.busy || !canContinue.value || !range.value || !kind.value) return;
  emit("save", { name: name.value.trim(), kind: kind.value as NetworkKind, cidr: range.value.cidr }, proofId);
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.rule.label" :context="t.rule.context" :busy="busy" @cancel="emit('cancel')">
    <template #kind>
      <ChoiceCards v-model="kind" :options="kindOptions" :label="t.rule.kindLabel" hide-label :columns="3" />
    </template>
    <template #range>
      <div class="network-range">
        <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.rule.name" :help="t.rule.nameHelp" :error="nameError">
          <input :id="id" v-model="name" class="st-input" :placeholder="placeholders[kind ?? 'lan']?.name" :aria-describedby="describedBy" :aria-invalid="invalid" data-network-name data-step-focus @blur="touched = touched || !!cidr" />
        </SettingsRow>
        <SettingsRow v-slot="{ id, describedBy, invalid }" :label="kind === 'other' ? t.rule.addressOrRange : t.rule.rangeCidr" :help="kind === 'other' ? t.rule.otherHelp : t.rule.cidrHelp" :error="cidrError">
          <input :id="id" v-model="cidr" class="st-input st-mono" :placeholder="placeholders[kind ?? 'lan']?.cidr" :aria-describedby="describedBy" :aria-invalid="invalid" spellcheck="false" autocomplete="off" data-network-cidr />
          <Transition :css="false" v-bind="disclosureHooks">
            <div v-if="range" class="network-normalized" aria-live="polite" data-network-normalized>
              <AtlasIcon name="check" :size="15" class="network-normalized-check" />
              <code class="st-mono">{{ range.cidr }}</code>
              <strong data-network-count>{{ formatAddressCount(range.size) }}</strong>
              <span v-if="!range.single" class="network-bounds">{{ formatAddress(range.first, range.family) }} – {{ formatAddress(range.last, range.family) }}</span>
              <StatusBadge v-if="range.adjusted" tone="info" icon="arrow-right-left" size="sm" :label="t.rule.adjusted" />
            </div>
          </Transition>
          <TransitionGroup tag="ul" name="st-fade" class="network-warnings">
            <li v-for="warning in warnings" :key="warning.kind + warning.message" :data-blocking="warning.blocking || undefined">
              <AtlasIcon :name="warning.blocking ? 'circle-x' : 'triangle-alert'" :size="14" />{{ warning.message }}
            </li>
          </TransitionGroup>
        </SettingsRow>
      </div>
    </template>
    <template #impact>
      <ImpactPreview :items="impact" :unchanged="t.rule.unchanged" />
    </template>
    <template #confirm>
      <ConfirmWithReauth :key="JSON.stringify([kind, name, cidr, canContinue])" :disabled="!kind || !canContinue" :cancel-label="t.rule.cancel" :confirm-label="t.rule.add" :busy="busy" :error="error" @confirm="confirm" @cancel="emit('cancel')">
        <SummaryList :items="summary" :label="t.rule.summary">
          <template #value-state><StatusBadge tone="success" icon="circle-check" :label="t.rule.activeAfterSave" size="sm" /></template>
        </SummaryList>
      </ConfirmWithReauth>
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.network-range { display: grid; }
.network-normalized {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 10px;
  padding: 8px 12px;
  border-radius: var(--st-radius-sm);
  background: color-mix(in srgb, var(--success-bg) 70%, var(--surface));
  font-size: 13px;
}
.network-normalized-check { color: var(--success); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
.network-normalized code { font-size: 14px; font-weight: 650; }
.network-bounds { color: var(--muted); font-size: 12px; }
.network-warnings { display: grid; gap: 4px; margin: 0; padding: 0; list-style: none; }
.network-warnings li { display: flex; align-items: flex-start; gap: 6px; color: var(--warning); font-size: 12.5px; font-weight: 550; }
.network-warnings li > svg { flex-shrink: 0; margin-top: 2px; }
.network-warnings li[data-blocking] { color: var(--error); }
</style>
