<script setup lang="ts">
import t from "../../../i18n/settings-security";
import { computed, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmWithReauth from "../ConfirmWithReauth.vue";
import ImpactPreview from "../ImpactPreview.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { FlowStep, ImpactItem, SummaryItem } from "../types";
import type { ProtectionState, ReauthTarget } from "../../../protection";

import { currentSetting, inheritedFromText, inheritedMode, modeInfo, modeOptions, settingOptions } from "./protection";
import type { ProtectionMode, ProtectionSetting } from "./types";
import ModeMatrix from "./ModeMatrix.vue";

/**
 * Change of a protection mode in three steps on the V4 state of the level (GET …/protection):
 * 1 Režim (ChoiceCards with the LAN/VPN vs elsewhere table; a vault or secret gets a fourth
 * "Převzít (nyní: …)" card) · 2 Dopad (ImpactPreview from the API's impact counts) · 3 Potvrzení
 * (SummaryList + ConfirmWithReauth for the level's scope). The chosen setting and the proof go out
 * via `save`; the page sends them and shows `busy` and a refusal (`error`) here.
 */
const props = withDefaults(
  defineProps<{
    /** "Výchozí ochrana celé instance", "Trezor Servery a síť", "Heslo Router". */
    target: string;
    state: ProtectionState;
    /** Active trusted networks; null when the caller cannot read them (only wiki administrators manage networks). */
    activeNetworks: number | null;
    /** Instance only: vaults and secrets with their own mode, which the change leaves alone. */
    overrides?: { vaults: number; secrets: number };
    networksHref?: string;
    /** A mode already picked (a card on the page), preselected in step 1. */
    initial?: ProtectionSetting;
    busy?: boolean;
    error?: string;
  }>(),
  { overrides: undefined, networksHref: undefined, initial: undefined, busy: false, error: undefined },
);
const emit = defineEmits<{ save: [setting: ProtectionSetting, proofId: string | null]; cancel: [] }>();

const step = ref("mode");
const current = computed(() => currentSetting(props.state));
const choice = ref<string | null>(props.initial && props.initial !== current.value ? props.initial : null);
const instance = computed(() => props.state.level === "instance");
const inherited = computed(() => (instance.value ? undefined : { mode: inheritedMode(props.state.chain).mode, from: inheritedFromText(props.state.chain) }));
const options = computed(() => (inherited.value ? settingOptions(current.value, inherited.value) : modeOptions(props.state.effective)));
const next = computed(() => (choice.value ?? current.value) as ProtectionSetting);
const effective = (setting: ProtectionSetting): ProtectionMode => (setting === "inherit" ? (inherited.value?.mode ?? "always") : setting);
const label = (setting: ProtectionSetting) => (setting === "inherit" ? t.wizard.inheritLabel(modeInfo[effective(setting)].label) : modeInfo[setting].label);
const reauthTarget = computed<ReauthTarget>(() =>
  props.state.level === "vault" ? { vaultId: props.state.targetId! } : props.state.level === "secret" ? { secretId: props.state.targetId! } : {},
);

const impact = computed<{ items: ImpactItem[]; unchanged: string[] }>(() => {
  const mode = modeInfo[effective(next.value)];
  const counts = props.state.impact;
  if (props.state.level === "instance")
    return {
      items: [
        { icon: "lock", label: t.wizard.vaultsNoOwn(counts?.vaults ?? 0, mode.label), detail: t.wizard.affects(counts?.secrets ?? 0) },
      ],
      unchanged: props.overrides ? [t.wizard.vaultsOwn(props.overrides.vaults), t.wizard.secretsOwn(props.overrides.secrets)] : [],
    };
  if (props.state.level === "vault")
    return {
      items: [
        { icon: mode.icon, label: t.wizard.vaultSecrets(mode.label), detail: t.wizard.followVault(counts?.secrets ?? 0) },
        ...(next.value === "inherit" ? [{ icon: "arrow-right-left", label: t.wizard.vaultFollows }] : []),
      ],
      unchanged: t.wizard.unchangedVault,
    };
  return {
    items: [
      { icon: mode.icon, label: t.wizard.thisSecret(mode.label) },
      ...(next.value === "inherit" ? [{ icon: "arrow-right-left", label: t.wizard.secretFollows }] : []),
    ],
    unchanged: t.wizard.unchangedSecret,
  };
});
const warnings = computed(() => {
  const list: ImpactItem[] = [];
  const mode = effective(next.value);
  if (mode === "outsideTrustedNetworks" && props.activeNetworks === 0)
    list.push({ icon: "triangle-alert", tone: "warning", label: t.wizard.noNetwork, detail: t.wizard.noNetworkDetail });
  if (mode === "never") list.push({ icon: "triangle-alert", tone: "warning", label: t.wizard.neverWarning, detail: t.wizard.neverDetail });
  if (mode === "outsideTrustedNetworks") list.push({ icon: "info", tone: "info", label: t.wizard.outsideWarning, detail: t.wizard.outsideDetail });
  return list;
});
const unchangedChoice = computed(() => !choice.value || choice.value === current.value);
const steps = computed<FlowStep[]>(() => [
  { id: "mode", label: t.wizard.modeStep, icon: "fingerprint", title: t.wizard.modeTitle, hint: props.target, summary: choice.value ? label(next.value) : undefined, ready: !unchangedChoice.value },
  { id: "impact", label: t.wizard.impactStep, icon: "activity", title: t.wizard.impactTitle, hint: t.wizard.impactHint },
  { id: "confirm", label: t.wizard.confirmStep, icon: "circle-check", title: t.wizard.confirmTitle, hint: t.wizard.confirmHint, hideNext: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "target", icon: "lock", label: t.wizard.target, value: props.target },
  { key: "before", icon: "history", label: t.wizard.before, value: label(current.value) },
  { key: "after", icon: modeInfo[effective(next.value)].icon, label: t.wizard.after, value: label(next.value) },
  { key: "matrix", icon: "network", label: t.wizard.matrix },
]);
function confirm(proofId: string | null) {
  if (props.busy || unchangedChoice.value) return;
  emit("save", next.value, proofId);
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.wizard.label" :context="target" :busy="busy" @cancel="emit('cancel')">
    <template #mode>
      <ChoiceCards v-model="choice" :options="options" :label="t.wizard.modes" hide-label :columns="inherited ? 2 : 3" data-protection-modes>
        <template #option="{ option }">
          <ModeMatrix :mode="effective(option.value as ProtectionSetting)" />
        </template>
      </ChoiceCards>
      <p v-if="unchangedChoice" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.wizard.chooseOther }}</p>
      <p v-else-if="effective(next) === 'outsideTrustedNetworks' && activeNetworks === 0" class="st-note protection-no-networks" role="status" data-no-networks-warning>
        <AtlasIcon name="triangle-alert" :size="15" />{{ t.wizard.noNetworksNote }}
        <a v-if="networksHref" class="st-link" :href="networksHref">{{ t.wizard.addNetwork }}<AtlasIcon name="arrow-right" :size="13" /></a>
      </p>
    </template>
    <template #impact>
      <ImpactPreview :items="[...impact.items, ...warnings]" :unchanged="impact.unchanged" :before="label(current)" :after="label(next)" />
      <p v-if="effective(next) === 'outsideTrustedNetworks' && activeNetworks !== null" class="st-note">
        <AtlasIcon name="network" :size="15" />{{ t.wizard.activeNetworks(activeNetworks) }}
        <a v-if="networksHref" class="st-link" :href="networksHref">{{ t.wizard.reviewNetworks }}<AtlasIcon name="arrow-right" :size="13" /></a>
      </p>
    </template>
    <template #confirm>
      <ConfirmWithReauth :key="JSON.stringify([choice, reauthTarget, unchangedChoice])" :disabled="unchangedChoice" :cancel-label="t.wizard.cancel" :confirm-label="t.wizard.save(label(next))" :target="reauthTarget" :busy="busy" :error="error" @confirm="confirm" @cancel="emit('cancel')">
        <SummaryList :items="summary" :label="t.wizard.summary">
          <template #value-matrix><ModeMatrix :mode="effective(next)" /></template>
        </SummaryList>
      </ConfirmWithReauth>
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.protection-no-networks { color: var(--warning); font-weight: 550; }
.protection-no-networks > svg { color: var(--warning); }
</style>
