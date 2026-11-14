<script setup lang="ts">
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Checkbox } from "../../ui/checkbox";
import { Switch } from "../../ui/switch";
import EmptyState from "../EmptyState.vue";
import SettingsRow from "../SettingsRow.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { FlowStep, SummaryItem } from "../types";
import t from "../../../i18n/settings-modules";
import CopyField from "./CopyField.vue";
import { callbackUrl, emptyProviderDraft, issuerPattern, maxAdminMappings, providerIdPattern, providerSlug, scopesFromText, type OidcProviderDraft } from "./oidc";

/**
 * Add or edit one sign-in provider in five steps: 1 Název (button name and stable id) ·
 * 2 Spojení (issuer, client id and secret) · 3 Přístup (scopes and sign-up) · 4 Správci (claim
 * mappings that grant the wiki administrator) · 5 Souhrn. Store-free: the provider to edit comes in,
 * the finished draft goes out with `done`. It only changes the page's draft; the page saves every
 * provider together with its SaveBar, as the server takes them in one request.
 */
const props = withDefaults(defineProps<{ provider?: OidcProviderDraft; takenIds?: string[]; origin: string }>(), { provider: undefined, takenIds: () => [] });
const emit = defineEmits<{ done: [provider: OidcProviderDraft]; cancel: []; dirty: [value: boolean] }>();

const clone = (value: OidcProviderDraft): OidcProviderDraft => ({ ...value, scopes: [...value.scopes], adminMappings: value.adminMappings.map((mapping) => ({ ...mapping })) });
const draft = ref(clone(props.provider ?? emptyProviderDraft()));
const scopeText = ref(draft.value.scopes.join(" "));
const step = ref("identity");
// A new provider's id follows its name until the administrator types an id of their own.
const idTouched = ref(!!props.provider);
const start = JSON.stringify({ draft: draft.value, scopes: scopeText.value });
watch([draft, scopeText], () => emit("dirty", JSON.stringify({ draft: draft.value, scopes: scopeText.value }) !== start), { deep: true });
watch(
  () => draft.value.displayName,
  (name) => {
    if (!idTouched.value) draft.value.id = providerSlug(name);
  },
);

const id = computed(() => draft.value.id.trim());
const idError = computed(() => {
  if (!id.value) return undefined;
  if (!providerIdPattern.test(id.value)) return t.oidcWizard.idPattern;
  if (props.takenIds.includes(id.value)) return t.oidcWizard.idTaken;
  return undefined;
});
const issuerError = computed(() => (draft.value.issuer.trim() && !issuerPattern.test(draft.value.issuer.trim()) ? t.oidcWizard.issuerInvalid : undefined));
const scopes = computed(() => scopesFromText(scopeText.value));
const mappingsReady = computed(() => draft.value.adminMappings.every((mapping) => mapping.claimType.trim() && mapping.claimValue.trim()));
const callback = computed(() => callbackUrl(props.origin, id.value || "…"));

function hostOf(value: string) {
  try {
    return new URL(value).host || undefined;
  } catch {
    return undefined;
  }
}
function toggleClearSecret(value: boolean) {
  draft.value.clearSecret = value;
  if (value) draft.value.clientSecret = "";
}
function addMapping() {
  if (draft.value.adminMappings.length < maxAdminMappings) draft.value.adminMappings.push({ claimType: "groups", claimValue: "" });
}

const secretSummary = computed(() => {
  if (draft.value.clientSecret) return draft.value.hasSecret ? t.oidcWizard.secretReplace : t.oidcWizard.secretNew;
  if (draft.value.clearSecret) return t.oidcWizard.secretClear;
  return draft.value.hasSecret ? t.oidcWizard.secretKeep : t.oidcWizard.secretNone;
});
const steps = computed<FlowStep[]>(() => [
  {
    id: "identity",
    label: t.oidcWizard.steps.identity.label,
    icon: "identity",
    title: t.oidcWizard.steps.identity.title,
    hint: t.oidcWizard.steps.identity.hint,
    summary: draft.value.displayName.trim() || undefined,
    ready: !!draft.value.displayName.trim() && !!id.value && !idError.value,
  },
  {
    id: "connection",
    label: t.oidcWizard.steps.connection.label,
    icon: "link",
    title: t.oidcWizard.steps.connection.title,
    hint: t.oidcWizard.steps.connection.hint,
    summary: hostOf(draft.value.issuer.trim()),
    ready: issuerPattern.test(draft.value.issuer.trim()) && !!draft.value.clientId.trim(),
  },
  {
    id: "access",
    label: t.oidcWizard.steps.access.label,
    icon: "user-check",
    title: t.oidcWizard.steps.access.title,
    summary: scopes.value.join(" ") || undefined,
    ready: scopes.value.length > 0,
  },
  {
    id: "admins",
    label: t.oidcWizard.steps.admins.label,
    icon: "shield-check",
    title: t.oidcWizard.steps.admins.title,
    hint: t.oidcWizard.steps.admins.hint,
    summary: draft.value.adminMappings.length ? t.oidcWizard.rules(draft.value.adminMappings.length) : t.oidcWizard.noRules,
    ready: mappingsReady.value,
  },
  {
    id: "summary",
    label: t.oidcWizard.steps.summary.label,
    icon: "circle-check",
    title: t.oidcWizard.steps.summary.title,
    hint: t.oidcWizard.steps.summary.hint,
    manual: true,
    nextLabel: props.provider ? t.oidcWizard.applyChanges : t.oidcWizard.addProvider,
    nextIcon: "check",
  },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "name", icon: "identity", label: t.oidcWizard.summary.button, value: draft.value.displayName.trim() },
  { key: "id", icon: "hash", label: t.oidcWizard.summary.id, value: id.value, code: true },
  { key: "issuer", icon: "link", label: t.oidcWizard.summary.issuer, value: draft.value.issuer.trim(), code: true },
  { key: "client", icon: "key-round", label: t.oidcWizard.summary.clientId, value: draft.value.clientId.trim(), code: true },
  { key: "secret", icon: "lock", label: t.oidcWizard.summary.clientSecret, value: secretSummary.value },
  { key: "scopes", icon: "list", label: t.oidcWizard.summary.scopes, value: scopes.value.join(" "), code: true },
  { key: "signup", icon: "user-check", label: t.oidcWizard.summary.signup, value: draft.value.allowSignup ? t.oidcWizard.summary.signupCreate : t.oidcWizard.summary.signupExisting },
  {
    key: "admins",
    icon: "shield-check",
    label: t.oidcWizard.summary.admins,
    value: draft.value.adminMappings.length ? draft.value.adminMappings.map((mapping) => `${mapping.claimType.trim()} = ${mapping.claimValue.trim()}`).join(", ") : t.oidcWizard.summary.adminsManual,
  },
  { key: "callback", icon: "external", label: t.oidcWizard.summary.callback, value: callback.value, code: true },
]);

function onNext(current: FlowStep) {
  if (current.id !== "summary") return;
  emit("done", {
    ...clone(draft.value),
    id: id.value,
    displayName: draft.value.displayName.trim(),
    issuer: draft.value.issuer.trim(),
    clientId: draft.value.clientId.trim(),
    scopes: scopes.value,
    adminMappings: draft.value.adminMappings.map((mapping) => ({ claimType: mapping.claimType.trim(), claimValue: mapping.claimValue.trim() })),
  });
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="provider ? t.oidc.wizardEdit : t.oidc.wizardAdd" :context="provider?.displayName || t.oidcWizard.context" @next="onNext" @cancel="emit('cancel')">
    <template #identity>
      <SettingsRow v-slot="{ id: fieldId, describedBy }" :label="t.oidcWizard.name.label" :help="t.oidcWizard.name.help">
        <input :id="fieldId" v-model="draft.displayName" class="st-input" maxlength="200" :aria-describedby="describedBy" data-step-focus data-provider-name />
      </SettingsRow>
      <SettingsRow v-slot="{ id: fieldId, describedBy, invalid }" :label="t.oidcWizard.id.label" :help="t.oidcWizard.id.help" :error="idError">
        <input :id="fieldId" v-model="draft.id" class="st-input st-mono" maxlength="40" autocomplete="off" spellcheck="false" :aria-describedby="describedBy" :aria-invalid="invalid" data-provider-id @input="idTouched = true" />
      </SettingsRow>
      <p v-if="provider?.savedId && id && id !== provider.savedId" class="st-note" role="status"><AtlasIcon name="triangle-alert" :size="15" />{{ t.oidcWizard.id.changed }}</p>
    </template>

    <template #connection>
      <SettingsRow v-slot="{ id: fieldId, describedBy, invalid }" :label="t.oidcWizard.issuer.label" :help="t.oidcWizard.issuer.help" :error="issuerError">
        <input :id="fieldId" v-model="draft.issuer" class="st-input" inputmode="url" maxlength="2048" autocomplete="off" spellcheck="false" placeholder="https://hub.example.cz" :aria-describedby="describedBy" :aria-invalid="invalid" data-step-focus data-provider-issuer />
      </SettingsRow>
      <SettingsRow v-slot="{ id: fieldId }" :label="t.oidcWizard.clientId">
        <input :id="fieldId" v-model="draft.clientId" class="st-input st-mono" maxlength="2048" autocomplete="off" spellcheck="false" data-provider-client />
      </SettingsRow>
      <SettingsRow v-slot="{ id: fieldId, describedBy }" :label="t.oidcWizard.secret.label" :help="t.oidcWizard.secret.help">
        <input
          :id="fieldId"
          v-model="draft.clientSecret"
          class="st-input"
          type="password"
          autocomplete="new-password"
          maxlength="8192"
          :disabled="draft.clearSecret"
          :placeholder="draft.hasSecret ? t.oidcWizard.secret.placeholderStored : t.oidcWizard.secret.placeholder"
          :aria-describedby="describedBy"
          data-provider-secret
        />
        <label v-if="draft.hasSecret" class="oidc-wizard-check">
          <Checkbox :model-value="draft.clearSecret" data-provider-clear-secret @update:model-value="toggleClearSecret($event === true)" />{{ t.oidcWizard.secret.clear }}
        </label>
      </SettingsRow>
      <CopyField :label="t.common.copyCallback" :value="callback" :copy-label="t.common.copyAddress" />
    </template>

    <template #access>
      <SettingsRow v-slot="{ id: fieldId, describedBy }" :label="t.oidcWizard.scopes.label" :help="t.oidcWizard.scopes.help">
        <input :id="fieldId" v-model="scopeText" class="st-input st-mono" maxlength="4000" placeholder="openid profile" spellcheck="false" :aria-describedby="describedBy" data-step-focus data-provider-scopes />
      </SettingsRow>
      <SettingsRow
        v-slot="{ id: fieldId, describedBy }"
        :label="t.oidcWizard.signup.label"
        :help="t.oidcWizard.signup.help"
      >
        <Switch :id="fieldId" v-model="draft.allowSignup" :aria-describedby="describedBy" data-provider-signup />
      </SettingsRow>
    </template>

    <template #admins>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.oidcWizard.mappings.note }}</p>
      <ul v-if="draft.adminMappings.length" class="oidc-mappings" :aria-label="t.oidcWizard.mappings.label">
        <li v-for="(mapping, index) in draft.adminMappings" :key="index" class="oidc-mapping" :data-mapping="index">
          <label class="oidc-mapping-field"><span>{{ t.oidcWizard.mappings.claimType }}</span><input v-model="mapping.claimType" class="st-input st-mono" :aria-label="t.oidcWizard.mappings.claimType" :placeholder="t.oidcWizard.mappings.claimTypePlaceholder" maxlength="200" spellcheck="false" /></label>
          <label class="oidc-mapping-field"><span>{{ t.oidcWizard.mappings.claimValue }}</span><input v-model="mapping.claimValue" class="st-input st-mono" :aria-label="t.oidcWizard.mappings.claimValue" maxlength="500" spellcheck="false" /></label>
          <Button variant="ghost" size="sm" type="button" :aria-label="t.oidcWizard.mappings.removeRule(index + 1)" data-remove-mapping @click="draft.adminMappings.splice(index, 1)"><AtlasIcon name="trash" :size="15" />{{ t.common.remove }}</Button>
        </li>
      </ul>
      <EmptyState v-else compact icon="shield-check" :title="t.oidcWizard.mappings.emptyTitle" :text="t.oidcWizard.mappings.emptyText" />
      <div class="st-actions">
        <Button variant="outline" size="sm" type="button" :disabled="draft.adminMappings.length >= maxAdminMappings" data-add-mapping @click="addMapping"><AtlasIcon name="plus" :size="15" />{{ t.oidcWizard.mappings.add }}</Button>
      </div>
      <p class="st-note"><AtlasIcon name="history" :size="15" />{{ t.oidcWizard.mappings.appliesNote }}</p>
    </template>

    <template #summary>
      <SummaryList :items="summary" :label="t.oidcWizard.summary.label" />
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.oidc-wizard-check { display: flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; }
.oidc-mappings { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; container: oidc-mappings / inline-size; }
.oidc-mapping { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto; align-items: end; gap: 8px; padding: 10px 12px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out); }
.oidc-mapping-field { display: grid; gap: 4px; min-width: 0; }
.oidc-mapping-field > span { color: var(--muted); font-size: 12px; font-weight: 600; }
@container oidc-mappings (max-width: 520px) { .oidc-mapping { grid-template-columns: minmax(0, 1fr); } }
</style>
