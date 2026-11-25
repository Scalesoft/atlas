<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { ApiError, errorMessage } from "../../../api";
import { hubApi, hubErrorMessage, type HubBinding, type HubBindingDraft, type HubDraft, type HubGroups, type HubIdentity, type HubMode, type HubSettings } from "../../../hub";
import { reauthRequiredCode } from "../../../protection";
import t from "../../../i18n/settings-modules";
import { formatDateTime } from "../../../formatDate";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingNote from "../LoadingNote.vue";
import OptionPills from "../OptionPills.vue";
import ReauthDialog from "../ReauthDialog.vue";
import ResultBanner from "../ResultBanner.vue";
import SaveBar from "../SaveBar.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsRoute } from "../useSettingsRoute";
import { disclosureHooks } from "../disclosure";
import HubMigrationFlow from "./HubMigrationFlow.vue";
import type { HubMigrationResult } from "../../../hub";

const router = useSettingsRoute();
const state = ref<HubSettings>();
const healthLabels = t.hub.health;
const modeLabels = t.hub.mode;
const draft = ref({ issuer: "", baseUrl: "", token: "", mode: "Disabled" as HubMode });
const bindings = ref<Record<string, HubBindingDraft>>({});
const loading = ref(true);
const loadError = ref("");
const busy = ref(false);
const error = ref("");
const conflict = ref(false);
const result = ref("");
const testResult = ref("");
const query = ref("");
const selected = ref<string | null>(null);
const matches = ref<HubGroups>();
const confirming = ref<"connection" | string>();
const attempt = ref(0);
const migrating = ref(false);
let disposed = false;

const connectionDirty = computed(() => !!state.value && (draft.value.issuer !== state.value.issuer || draft.value.baseUrl !== state.value.baseUrl || draft.value.mode !== state.value.mode || draft.value.token.length > 0));
const bindingDirty = (binding: HubBinding) => {
  const value = bindings.value[binding.groupId];
  return !!value && (value.enabled !== binding.enabled || value.grantsWikiAdmin !== binding.grantsWikiAdmin);
};
const anyBindingDirty = computed(() => state.value?.bindings.some(bindingDirty) ?? false);
const dirty = computed(() => connectionDirty.value || anyBindingDirty.value);
const confirmedBinding = computed(() => state.value?.bindings.find(binding => binding.groupId === confirming.value));
const modeOptions = computed(() => (Object.keys(modeLabels) as HubMode[]).map(value => ({ value, label: modeLabels[value], disabled: busy.value || anyBindingDirty.value })));
const groupOptions = computed(() => (matches.value?.groups ?? []).map(group => ({ value: group.id, label: group.name, description: group.id, icon: "users", disabled: busy.value || state.value?.bindings.some(binding => binding.externalGroupId === group.id), disabledReason: t.hub.groupAlreadyAttached })));
const draftRequest = (): HubDraft => ({ issuer: draft.value.issuer, baseUrl: draft.value.baseUrl.trim(), token: draft.value.token || undefined, mode: draft.value.mode, expectedVersion: state.value?.version ?? null });

function reset() {
  if (!state.value) return;
  draft.value = { issuer: state.value.issuer, baseUrl: state.value.baseUrl, token: "", mode: state.value.mode };
  bindings.value = Object.fromEntries(state.value.bindings.map(binding => [binding.groupId, { enabled: binding.enabled, grantsWikiAdmin: binding.grantsWikiAdmin, expectedVersion: binding.version }]));
  error.value = "";
}
function accept(value: HubSettings) { state.value = value; reset(); }
async function load() {
  loading.value = true;
  loadError.value = "";
  try {
    const value = await hubApi.settings();
    if (!disposed) { accept(value); conflict.value = false; matches.value = undefined; selected.value = null; }
  } catch (failure) { if (!disposed) loadError.value = errorMessage(failure); }
  finally { loading.value = false; }
}
function failed(failure: unknown) {
  error.value = errorMessage(failure);
  if (failure instanceof ApiError && failure.status === 409) conflict.value = true;
}
async function save(proof?: string | null) {
  if (busy.value || conflict.value || !connectionDirty.value || !state.value) return;
  if (!draft.value.issuer || !draft.value.baseUrl.trim()) { error.value = t.hub.needProviderAndUrl; return; }
  if ((state.value.mode === "Enforced" || draft.value.mode === "Enforced") && proof === undefined) { confirming.value = "connection"; attempt.value++; return; }
  busy.value = true;
  error.value = "";
  try {
    const value = await hubApi.save(draftRequest(), proof);
    if (!disposed) { accept(value); confirming.value = undefined; result.value = t.hub.connectionSaved; matches.value = undefined; selected.value = null; }
  } catch (failure) {
    if (failure instanceof ApiError && failure.code === reauthRequiredCode) { confirming.value = "connection"; attempt.value++; }
    else { confirming.value = undefined; failed(failure); }
  } finally { busy.value = false; }
}
async function saveBinding(binding: HubBinding, proof?: string | null) {
  const value = bindings.value[binding.groupId];
  if (busy.value || conflict.value || !value || !bindingDirty(binding)) return;
  if ((value.grantsWikiAdmin !== binding.grantsWikiAdmin || (value.enabled && !binding.enabled && value.grantsWikiAdmin)) && proof === undefined) { confirming.value = binding.groupId; attempt.value++; return; }
  busy.value = true;
  error.value = "";
  try {
    const saved = await hubApi.binding(binding.groupId, { ...value }, proof);
    if (!disposed) { accept(saved); confirming.value = undefined; result.value = t.hub.bindingSaved; }
  } catch (failure) {
    if (failure instanceof ApiError && failure.code === reauthRequiredCode) { confirming.value = binding.groupId; attempt.value++; }
    else { confirming.value = undefined; failed(failure); }
  } finally { busy.value = false; }
}
function confirmed(proof: string | null) {
  if (confirming.value === "connection") return save(proof);
  const binding = state.value?.bindings.find(item => item.groupId === confirming.value);
  if (binding) return saveBinding(binding, proof);
}
async function test() {
  if (busy.value) return;
  busy.value = true; error.value = ""; testResult.value = "";
  try { const checked = await hubApi.test(draftRequest()); if (!disposed) testResult.value = checked.message; }
  catch (failure) { failed(failure); }
  finally { busy.value = false; }
}
async function search() {
  if (busy.value || dirty.value || !state.value?.connectionId) return;
  busy.value = true; error.value = ""; selected.value = null; matches.value = undefined;
  const searched = query.value;
  try { const found = await hubApi.search(searched); if (!disposed && query.value === searched) matches.value = found; }
  catch (failure) { failed(failure); }
  finally { busy.value = false; }
}
async function attach() {
  if (busy.value || dirty.value || conflict.value || !selected.value || !state.value) return;
  busy.value = true; error.value = "";
  try {
    const saved = await hubApi.attach(selected.value, state.value.version);
    if (!disposed) { accept(saved); selected.value = null; result.value = t.hub.groupAttached; }
  } catch (failure) { failed(failure); }
  finally { busy.value = false; }
}
async function verify(userId?: string) {
  if (busy.value || dirty.value || state.value?.mode === "Disabled") return;
  busy.value = true; error.value = "";
  try {
    const checked = await hubApi.verify(userId);
    if (!disposed) result.value = t.hub.verifyResult(checked.verified, checked.failed, checked.deferred);
    const refreshed = await hubApi.settings();
    if (!disposed && state.value) state.value = { ...state.value, identities: refreshed.identities, health: refreshed.health };
  } catch (failure) { failed(failure); }
  finally { busy.value = false; }
}
function identityLabel(identity: HubIdentity) {
  if (state.value?.mode === "Disabled") return t.hub.health.Disabled;
  if (identity.lastErrorCode) return t.hub.identity.failed;
  if (identity.verifiedUntil && Date.parse(identity.verifiedUntil) <= Date.now()) return t.hub.identity.expired;
  const label = ({ Active: t.hub.identity.Active, Disabled: t.hub.identity.Disabled, Missing: t.hub.identity.Missing, Unverified: t.hub.identity.Unverified } as Record<string, string>)[identity.state] ?? t.hub.identity.Unverified;
  return state.value?.mode === "Preview" ? t.hub.identity.preview(label) : label;
}
const date = (value: string | null) => value ? formatDateTime(value) : t.hub.neverVerified;
async function migrationApplied(value: HubMigrationResult) {
  migrating.value = false;
  result.value = t.hub.migrationDone(value.convertedGroups, value.removedGrants, value.removedWikiAdmins, value.isolatedPages);
  await load();
}
watch(draft, () => { testResult.value = ""; }, { deep: true });
watch(query, () => { selected.value = null; matches.value = undefined; });
useDraftGuard(dirty, { discard: reset });
onMounted(load);
onBeforeUnmount(() => { disposed = true; draft.value.token = ""; });
</script>

<template>
  <div class="hub-module" role="region" :aria-label="t.hub.regionLabel">
    <ConflictNote v-if="conflict" :text="t.common.conflictChanges" :reload-label="t.common.reload" :dismissible="false" :busy="busy || loading" data-hub-conflict @reload="load" />
    <ErrorState v-if="loadError" :title="t.hub.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.hub.loading" />
    <template v-else-if="state">
      <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="error" class="hub-error" role="alert">{{ error }}</p>
      </Transition>
      <!-- The migration flow and the settings page swap like the other tab parts: the old one leaves, the new one rises in. -->
      <Transition name="st-step" mode="out-in">
      <HubMigrationFlow v-if="migrating && state.version" key="migration" :version="state.version" @cancel="migrating = false" @applied="migrationApplied" />
      <div v-else key="settings" class="hub-sections">
      <SettingsSection :title="t.hub.connection.title" icon="plug" :description="t.hub.connection.description">
        <SettingsRow :label="t.hub.connection.provider" :label-for="false" class="reveal-lead">
          <span v-if="state.connectionId">{{ state.providers.find(provider => provider.issuer === state?.issuer)?.displayName ?? state.issuer }}</span>
          <OptionPills v-else-if="state.providers.length" v-model="draft.issuer" :label="t.hub.connection.provider" hide-label :options="state.providers.map(provider => ({ value: provider.issuer, label: provider.displayName, disabled: busy }))" />
          <p v-if="!state.providers.length" class="st-note">{{ t.hub.connection.noProviders }}</p>
        </SettingsRow>
        <InlineReveal :open="!!draft.issuer" class="reveal-tail reveal-tail-bleed">
          <SettingsRow v-slot="{ id, describedBy }" :label="t.hub.connection.url" :help="t.hub.connection.urlHelp">
            <input :id="id" v-model="draft.baseUrl" class="st-input" inputmode="url" autocomplete="off" maxlength="2048" :aria-describedby="describedBy" :disabled="busy || anyBindingDirty" data-hub-url />
          </SettingsRow>
          <SettingsRow v-slot="{ id, describedBy }" :label="t.hub.connection.token" :help="t.hub.connection.tokenHelp">
            <input :id="id" v-model="draft.token" type="password" class="st-input" autocomplete="new-password" maxlength="8192" :placeholder="state.hasToken ? t.hub.connection.tokenStored : t.hub.connection.tokenPlaceholder" :aria-describedby="describedBy" :disabled="busy || anyBindingDirty" data-hub-token />
          </SettingsRow>
          <SettingsRow :label="t.hub.connection.mode" :label-for="false">
            <OptionPills v-model="draft.mode" :label="t.hub.connection.modeLabel" hide-label :options="modeOptions" data-hub-mode />
            <p class="st-note">{{ t.hub.connection.modeNote[draft.mode] }}</p>
          </SettingsRow>
          <Button variant="outline" type="button" :disabled="busy || !draft.baseUrl || anyBindingDirty" data-hub-test @click="test">{{ t.hub.connection.test }}</Button>
          <p v-if="testResult" class="st-note" role="status">{{ testResult }}</p>
        </InlineReveal>
        <SaveBar :dirty="connectionDirty" :saving="busy && connectionDirty" :error="error" :save-label="t.hub.connection.save" @save="save()" @cancel="reset" />
      </SettingsSection>

      <SettingsSection :title="t.hub.groups.title" icon="users" :description="t.hub.groups.description">
        <p v-if="!state.connectionId || dirty" class="st-note">{{ t.hub.groups.saveFirst }}</p>
        <template v-else>
          <SearchableChoiceList v-model="selected" v-model:query="query" :options="groupOptions" :label="t.hub.groups.listLabel" remote :empty-text="matches ? t.hub.groups.noMatch : t.hub.groups.enterName" data-hub-groups />
          <div class="st-actions">
            <Button variant="outline" type="button" :disabled="busy" data-hub-search @click="search">{{ t.hub.groups.search }}</Button>
            <Button type="button" :disabled="busy || !selected || conflict" data-hub-attach @click="attach">{{ t.hub.groups.attach }}</Button>
          </div>
          <p v-if="matches?.hasMore" class="st-note">{{ t.hub.groups.hasMore }}</p>
        </template>
        <div v-for="binding in state.bindings" :key="binding.groupId" class="hub-binding" :data-hub-binding="binding.groupId">
          <SettingsListRow :title="binding.name" :description="binding.externalGroupId" icon="users">
            <template #meta><StatusBadge :label="t.hub.groups.managedInHub" tone="info" size="sm" /></template>
            <template #actions><Button as-child variant="ghost" size="sm"><a :href="router.href(`people/groups/${binding.groupId}`)">{{ t.hub.groups.groupAccess }}</a></Button><Button as-child variant="ghost" size="sm"><a :href="binding.hubUrl" target="_blank" rel="noopener noreferrer">{{ t.hub.groups.openHub }}</a></Button></template>
          </SettingsListRow>
          <template v-if="bindings[binding.groupId]">
            <SettingsRow v-slot="{ id }" :label="t.hub.groups.useMembership">
              <Switch :id="id" v-model="bindings[binding.groupId]!.enabled" :disabled="busy || connectionDirty || (anyBindingDirty && !bindingDirty(binding))" />
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy }" :label="t.hub.groups.wikiAdmin" :help="t.hub.groups.wikiAdminHelp">
              <Switch :id="id" v-model="bindings[binding.groupId]!.grantsWikiAdmin" :aria-describedby="describedBy" :disabled="busy || connectionDirty || (anyBindingDirty && !bindingDirty(binding))" data-hub-admin />
            </SettingsRow>
            <SaveBar :dirty="bindingDirty(binding)" :saving="busy && bindingDirty(binding)" :error="error" :save-label="t.hub.groups.save" @save="saveBinding(binding)" @cancel="reset" />
          </template>
        </div>
      </SettingsSection>

      <SettingsSection :title="t.hub.verification.title" icon="shield-check" :description="t.hub.verification.description(state.pollSeconds, state.maxValiditySeconds / 60)">
        <template #actions><Button variant="outline" size="sm" :disabled="busy || dirty || state.mode === 'Disabled'" data-hub-verify @click="verify()">{{ t.hub.verification.verifyNow }}</Button></template>
        <div v-if="state.health" data-hub-health>
          <StatusBadge :label="healthLabels[state.health.state]" :tone="state.health.state === 'Error' || state.health.state === 'Stale' ? 'warning' : 'neutral'" />
          <p class="st-note">{{ t.hub.verification.counts(state.health.known, state.health.pending, state.health.expired, state.health.errors) }}</p>
          <p class="st-help">{{ t.hub.verification.lastSuccess(date(state.health.lastSuccessAt)) }}</p>
        </div>
        <SettingsList v-if="state.identities.length" :label="t.hub.verification.listLabel">
          <SettingsListRow v-for="identity in state.identities" :key="identity.externalIdentityId" :title="identity.displayName" :description="t.hub.verification.identityDescription(date(identity.lastAttemptAt), date(identity.lastSuccessAt), date(identity.verifiedUntil))" icon="account">
            <template #meta><StatusBadge :label="identityLabel(identity)" :tone="identity.lastErrorCode ? 'warning' : 'neutral'" size="sm" /><span v-if="identity.lastErrorCode">{{ hubErrorMessage(identity.lastErrorCode) }}</span></template>
            <template #actions><Button variant="ghost" size="sm" :disabled="busy || dirty || state.mode === 'Disabled'" @click="verify(identity.userId)">{{ t.hub.verification.verifyAccount }}</Button></template>
          </SettingsListRow>
        </SettingsList>
        <p v-else class="st-note">{{ t.hub.verification.none }}</p>
      </SettingsSection>
      <SettingsSection :title="t.hub.migration.title" icon="list" :description="t.hub.migration.description">
        <div class="st-actions"><Button variant="outline" :disabled="busy || dirty || conflict || !state.version || state.mode === 'Disabled'" data-hub-migration @click="migrating = true">{{ t.hub.migration.prepare }}</Button></div>
        <p v-if="state.mode === 'Disabled'" class="st-note">{{ t.hub.migration.testFirst }}</p>
      </SettingsSection>
      </div>
      </Transition>
    </template>
    <ReauthDialog :open="!!confirming" :title="t.hub.confirm.title" :confirm-label="t.hub.confirm.confirm" :busy="busy" :error="error" :attempt="attempt" @confirm="confirmed" @cancel="confirming = undefined">
      <p v-if="confirming === 'connection'"><template v-for="(part, index) in t.hub.confirm.connection(draft.baseUrl, modeLabels[draft.mode], draft.mode === 'Enforced')" :key="index"><strong v-if="index % 2">{{ part }}</strong><template v-else>{{ part }}</template></template></p>
      <p v-else-if="confirmedBinding"><template v-for="(part, index) in t.hub.confirm.binding(confirmedBinding.name, !!bindings[confirmedBinding.groupId]?.enabled, !!bindings[confirmedBinding.groupId]?.grantsWikiAdmin)" :key="index"><strong v-if="index % 2">{{ part }}</strong><template v-else>{{ part }}</template></template></p>
      <p>{{ t.hub.confirm.emergency }}</p>
    </ReauthDialog>
  </div>
</template>

<style scoped>
/* A column of flex items, not a grid: the error note unfolds and gives the gap above it back (settings/disclosure.ts). */
.hub-module, .hub-sections { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.hub-binding { border-top: 1px solid var(--line); padding-top: 12px; margin-top: 12px; }
.hub-error { color: var(--error); font-size: 13px; margin: 0; }
</style>
