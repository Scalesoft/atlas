<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { ApiError, api, errorMessage, post } from "../../../api";
import { reauthRequiredCode, securityChange } from "../../../protection";
import EmptyState from "../EmptyState.vue";
import ConfirmDialog from "../ConfirmDialog.vue";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import ResultBanner from "../ResultBanner.vue";
import SaveBar from "../SaveBar.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import InlineReveal from "../InlineReveal.vue";
import ReauthDialog from "../ReauthDialog.vue";
import StatusBadge from "../StatusBadge.vue";
import t from "../../../i18n/settings-modules";
import CopyField from "./CopyField.vue";
import OidcProviderWizard from "./OidcProviderWizard.vue";
import { callbackUrl, maxOidcProviders, oidcSaveBody, providerDraft, type OidcProviderDraft, type OidcSettings, type OidcTestResult } from "./oidc";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsDocument } from "../useSettingsDocument";
import { disclosureHooks } from "../disclosure";

/**
 * Firemní přihlášení (module `oidc`): the public origin and the providers with their state, the
 * discovery test and the callback address. A provider is added or edited in OidcProviderWizard,
 * which changes only the draft; the SaveBar saves the whole configuration in one PUT (with the
 * CAS version), as before. The client secret stays in the draft only until the next save attempt.
 * The providers decide who signs in and who becomes a wiki administrator, so the save is a security
 * change confirmed by a fresh sign-in (SEC-A-1), and removing a saved provider says first how many
 * accounts sign in through it (UX-N-12).
 */
const endpoint = "/admin/modules/oidc";
const origin = ref("");
const editOrigin = ref(false);
const providers = ref<OidcProviderDraft[]>([]);
const baseline = ref("");
const originError = ref("");
const notice = ref("");
const tests = ref<Record<string, OidcTestResult>>({});
const testing = ref("");
const testError = ref<Record<string, string>>({});
/** Index of the provider in the wizard, "new" for a new one, null when the list is shown. */
const editing = ref<number | "new" | null>(null);
const wizardDirty = ref(false);
/** The fresh sign-in of the save: open while it is asked for, the proof it gave while the save runs. */
const confirming = ref(false), attempt = ref(0);
let proof: string | null = null, refused = false;

const snapshot = computed(() => JSON.stringify({ origin: origin.value, providers: providers.value }));
const { saved: state, loading, loadError, saving, saveError, conflict, dirty, load, save, reset, isDisposed } = useSettingsDocument<OidcSettings>({
  load: () => api<OidcSettings>(endpoint),
  save: async (current) => {
    try {
      return await securityChange<OidcSettings>(endpoint, "PUT", oidcSaveBody(current.version, origin.value, providers.value), proof);
    } catch (failure) {
      refused = failure instanceof ApiError && failure.code === reauthRequiredCode;
      throw failure;
    } finally {
      proof = null;
      // The typed secret went out with this request; it is never kept for a retry.
      providers.value.forEach((provider) => (provider.clientSecret = ""));
    }
  },
  fill: (current) => {
    origin.value = current.publicOrigin;
    providers.value = current.providers.map(providerDraft);
    baseline.value = snapshot.value;
    originError.value = "";
    tests.value = {};
    testError.value = {};
  },
  dirty: () => snapshot.value !== baseline.value,
  beforeSave: () => {
    notice.value = "";
    originError.value = originMissing() ? t.oidc.originRequired : "";
    if (!originError.value) return undefined;
    editOrigin.value = true;
    return t.oidc.originMissing;
  },
  onSaved: () => {
    notice.value = t.oidc.saved;
  },
});
const pending = computed(() => dirty.value || wizardDirty.value);

function discard() {
  editing.value = null;
  wizardDirty.value = false;
  reset();
}
function reloadAfterConflict() {
  conflict.value = false;
  discard();
  void load();
}

const guard = useDraftGuard(pending, { save, discard });
// While the wizard is open its draft is not part of the configuration yet: leaving can only discard it.
watch(editing, (value) => guard?.register(value === null ? { save, discard } : { discard }));

function startAdding() {
  if (providers.value.length >= maxOidcProviders) return;
  notice.value = "";
  editing.value = "new";
}
function applyProvider(provider: OidcProviderDraft) {
  if (editing.value === "new") {
    providers.value.push(provider);
    if (!origin.value.trim()) origin.value = location.origin;
  } else if (editing.value !== null) providers.value.splice(editing.value, 1, provider);
  editing.value = null;
  wizardDirty.value = false;
}
function cancelWizard() {
  editing.value = null;
  wizardDirty.value = false;
}
function originMissing() {
  return providers.value.length > 0 && !origin.value.trim();
}
/** The SaveBar: an incomplete draft is refused at once (save() says why), a complete one asks for the fresh sign-in first. */
function requestSave() {
  if (originMissing()) {
    void save();
    return;
  }
  confirming.value = true;
  attempt.value++;
}
async function confirmSave(verified: string | null) {
  proof = verified;
  refused = false;
  await save();
  // A refused fresh sign-in (reauth_required) asks again in a new confirmation; any other outcome is the SaveBar's to show.
  if (refused) attempt.value++;
  else confirming.value = false;
}
const removing = ref<number | null>(null);
const removed = computed(() => (removing.value === null ? undefined : providers.value[removing.value]));
const removedAccounts = computed(() => (removed.value ? (saved(removed.value)?.linkedAccounts ?? 0) : 0));
function askRemove(index: number) {
  notice.value = "";
  // A provider that was never saved signs nobody in yet: it leaves the draft at once.
  if (!providers.value[index]?.savedId) providers.value.splice(index, 1);
  else removing.value = index;
}
function remove() {
  if (removing.value !== null) providers.value.splice(removing.value, 1);
  removing.value = null;
}

const saved = (provider: OidcProviderDraft) => state.value?.providers.find((item) => item.id === provider.savedId);
function changed(provider: OidcProviderDraft) {
  const original = saved(provider);
  return !original || provider.clientSecret !== "" || JSON.stringify(provider) !== JSON.stringify(providerDraft(original));
}
async function test(provider: OidcProviderDraft) {
  const id = provider.savedId;
  if (!id || dirty.value || testing.value) return;
  testing.value = id;
  testError.value = { ...testError.value, [id]: "" };
  try {
    const result = await post<OidcTestResult>(`${endpoint}/test`, { id });
    if (!isDisposed()) tests.value = { ...tests.value, [id]: result };
  } catch (failure) {
    if (!isDisposed()) testError.value = { ...testError.value, [id]: errorMessage(failure) };
  } finally {
    testing.value = "";
  }
}

const shownOrigin = computed(() => origin.value.trim() || location.origin);
const takenIds = computed(() => providers.value.filter((_, index) => index !== editing.value).map((provider) => provider.id));
const editedProvider = computed(() => (typeof editing.value === "number" ? providers.value[editing.value] : undefined));
const mappingTestable = (provider: OidcProviderDraft) => !dirty.value && !saving.value && !state.value?.restartRequired && !!provider.savedId && provider.adminMappings.length > 0;

onMounted(load);
onBeforeUnmount(() => {
  providers.value.forEach((provider) => (provider.clientSecret = ""));
});
</script>

<template>
  <div class="oidc-module" :aria-label="t.oidc.regionLabel" role="region">
    <ConflictNote v-if="conflict" :text="t.common.conflictChanges" :reload-label="t.common.reload" :busy="loading || saving" data-module-conflict @reload="reloadAfterConflict" @dismiss="conflict = false" />
    <ErrorState v-if="loadError" :title="t.oidc.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading && !state" :label="t.oidc.loading" />
    <template v-else-if="state">
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="state.restartRequired" class="st-note oidc-callout" role="status" data-oidc-restart>
          <AtlasIcon name="hourglass" :size="15" />{{ t.oidc.restart }}
        </p>
      </Transition>
      <ResultBanner v-if="notice" :message="notice" @dismiss="notice = ''" />

      <!-- The list and the wizard swap like the tabs and the authentication parts do: the old one leaves, the new one rises in. -->
      <Transition name="st-step" mode="out-in">
      <SettingsSection v-if="editing !== null" key="wizard" :label="editing === 'new' ? t.oidc.wizardAdd : t.oidc.wizardEdit">
        <OidcProviderWizard :key="String(editing)" :provider="editedProvider" :taken-ids="takenIds" :origin="shownOrigin" @done="applyProvider" @cancel="cancelWizard" @dirty="wizardDirty = $event" />
      </SettingsSection>

      <div v-else key="list" class="oidc-list">
        <SettingsSection :title="t.oidc.providers" icon="identity" :count="providers.length">
          <template v-if="providers.length" #actions>
            <Button size="sm" type="button" :disabled="providers.length >= maxOidcProviders || saving" data-add-provider @click="startAdding"><AtlasIcon name="plus" :size="15" />{{ t.oidc.wizardAdd }}</Button>
          </template>
          <SettingsList v-if="providers.length" :label="t.oidc.providersLabel">
            <SettingsListRow v-for="(provider, index) in providers" :key="provider.savedId ?? `new-${index}`" :title="provider.displayName" icon="identity" :data-provider="provider.id">
              <template #description><code class="st-mono">{{ provider.issuer }}</code></template>
              <template #meta>
                <StatusBadge v-if="!provider.savedId" tone="warning" icon="pencil" :label="t.oidc.unsavedNew" size="sm" />
                <StatusBadge v-else-if="changed(provider)" tone="warning" icon="pencil" :label="t.oidc.unsavedChanges" size="sm" />
                <StatusBadge
                  :tone="provider.clientSecret || (provider.hasSecret && !provider.clearSecret) ? 'success' : 'neutral'"
                  icon="key-round"
                  :label="provider.clientSecret ? t.oidc.newSecret : provider.hasSecret && !provider.clearSecret ? t.oidc.secretStored : t.oidc.noSecret"
                  size="sm"
                />
                <StatusBadge v-if="provider.allowSignup" tone="info" icon="user-check" :label="t.oidc.signupAllowed" size="sm" />
                <StatusBadge v-if="provider.adminMappings.length" tone="accent" icon="shield-check" :label="t.oidc.adminRules(provider.adminMappings.length)" size="sm" />
              </template>
              <template #actions>
                <Button variant="ghost" size="sm" type="button" :disabled="dirty || !provider.savedId || !!testing" :title="dirty ? t.oidc.saveFirst : undefined" data-provider-test @click="test(provider)">
                  <AtlasIcon :name="testing === provider.savedId ? 'loader-circle' : 'refresh'" :size="15" :class="{ 'atlas-spinner': testing === provider.savedId }" />{{ t.oidc.testReachability }}
                </Button>
                <Button variant="ghost" size="sm" type="button" :disabled="saving" data-edit-provider @click="editing = index; notice = ''"><AtlasIcon name="pencil" :size="15" />{{ t.common.edit }}</Button>
                <Button variant="ghost" size="sm" type="button" :disabled="saving" data-remove-provider @click="askRemove(index)"><AtlasIcon name="trash" :size="15" />{{ t.common.remove }}</Button>
              </template>
              <div class="oidc-provider-details">
                <CopyField :label="t.common.copyCallback" :value="callbackUrl(shownOrigin, provider.id)" :copy-label="t.common.copyAddress" />
                <Transition name="st-fade">
                  <StatusBadge
                    v-if="provider.savedId && tests[provider.savedId]"
                    :tone="tests[provider.savedId]!.ok ? 'success' : 'danger'"
                    :label="tests[provider.savedId]!.message"
                    data-provider-test-result
                  />
                </Transition>
                <Transition :css="false" v-bind="disclosureHooks">
                  <p v-if="provider.savedId && testError[provider.savedId]" class="oidc-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ testError[provider.savedId] }}</p>
                </Transition>
                <a v-if="mappingTestable(provider)" class="st-link" :href="`/api/auth/oidc/${encodeURIComponent(provider.id)}/test-mapping`" target="_blank" rel="noopener noreferrer" data-provider-test-mapping>
                  {{ t.oidc.testMapping }}<AtlasIcon name="external" :size="14" />
                </a>
                <p v-else-if="provider.adminMappings.length" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.oidc.testMappingFirst }}</p>
              </div>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else compact icon="identity" :title="t.oidc.emptyTitle" :text="t.oidc.emptyText">
            <Button type="button" data-add-provider @click="startAdding"><AtlasIcon name="plus" :size="16" />{{ t.oidc.wizardAdd }}</Button>
          </EmptyState>
          <p v-if="providers.length" class="st-note"><AtlasIcon name="lock" :size="15" />{{ t.oidc.secretEncrypted }}</p>
          <p v-if="providers.length || !state.builtinEnabled" class="st-note" data-oidc-builtin>
            <AtlasIcon :name="state.builtinEnabled ? 'account' : 'triangle-alert'" :size="15" />{{
              state.builtinEnabled
                ? t.oidc.builtinOn
                : t.oidc.builtinOff
            }}
          </p>
        </SettingsSection>

        <SettingsSection :label="t.oidc.origin" data-origin-settings>
          <div class="oidc-origin-summary">
            <span class="st-icon-tile oidc-origin-icon" data-tone="neutral" aria-hidden="true"><AtlasIcon name="globe" :size="17" /></span>
            <div><strong>{{ t.oidc.origin }}</strong><span>{{ shownOrigin }}</span></div>
            <Button variant="ghost" size="sm" type="button" :aria-expanded="editOrigin" @click="editOrigin = !editOrigin">{{ editOrigin ? t.oidc.hide : t.oidc.change }}</Button>
          </div>
          <InlineReveal :open="editOrigin" class="reveal-tail">
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.oidc.originLabel" :help="t.oidc.originHelp" :error="originError" stacked>
              <input :id="id" v-model="origin" class="st-input" inputmode="url" maxlength="2048" autocomplete="off" spellcheck="false" :placeholder="shownOrigin" :aria-describedby="describedBy" :aria-invalid="invalid" data-oidc-origin />
            </SettingsRow>
          </InlineReveal>
        </SettingsSection>

        <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.oidc.save" @save="requestSave" @cancel="discard" />
      </div>
      </Transition>
    </template>
    <ReauthDialog :open="confirming" :title="t.oidc.save" :confirm-label="t.oidc.save" :busy="saving" :error="saveError || undefined" :attempt="attempt" @confirm="confirmSave" @cancel="confirming = false">
      <p class="st-note"><AtlasIcon name="shield-check" :size="15" />{{ t.oidc.saveNote }}</p>
    </ReauthDialog>
    <ConfirmDialog :open="removing !== null" :title="t.oidc.removeTitle(removed?.displayName ?? '')" :confirm-label="t.common.remove" confirm-icon="trash" @confirm="remove" @cancel="removing = null">
      <p data-remove-provider-impact>{{
        removedAccounts
          ? t.oidc.removeLinked(removedAccounts)
          : t.oidc.removeNone
      }}</p>
      <p v-if="!state?.builtinEnabled && providers.length === 1" class="st-note"><AtlasIcon name="triangle-alert" :size="15" />{{ t.oidc.removeLastBlocked }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
/* The module is a column of flex items, not a grid: the restart note and the result banner unfold and give the gap above them back (settings/disclosure.ts). */
.oidc-module { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
.oidc-list { display: grid; gap: 16px; min-width: 0; }
/* One row of the card, set like a list row (a 32 px tile, title 14 px, value 13 px), so it lines up with the provider rows
   and card headers above it; a bare 18 px glyph and 12 px text made it read as a footnote. */
.oidc-origin-summary { display: flex; align-items: center; gap: 12px; color: var(--muted); }
.oidc-origin-icon { width: 32px; height: 32px; border-radius: 10px; }
.oidc-origin-summary > div { flex: 1; min-width: 0; }
.oidc-origin-summary strong { display: block; color: var(--text); font-size: 14px; font-weight: 600; line-height: 1.4; }
.oidc-origin-summary div > span { display: block; overflow-wrap: anywhere; font-size: 13px; line-height: 1.45; margin-top: 2px; }
.oidc-callout { padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--warning) 40%, var(--line)); border-radius: var(--st-radius); background: color-mix(in srgb, var(--warning-bg) 55%, var(--surface)); color: var(--text); font-size: 13px; }
.oidc-callout > svg { color: var(--warning); }
/* A note hangs its icon in its start padding (settings.css). This one is framed: the icon and the text keep the frame's own
   12 px and 10 px, or the icon would sit in the rounded corner. */
.settings-ui .st-note.oidc-callout:has(> svg:first-child) { padding-inline-start: calc(12px + var(--st-note-icon) + 8px); }
.settings-ui .st-note.oidc-callout > svg:first-child { inset-inline-start: 12px; top: 12px; }
/* A column of flex items, not a grid: the test failure note unfolds and gives the gap above it back (settings/disclosure.ts). */
.oidc-provider-details { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; padding: 0 0 14px 48px; }
.oidc-provider-details > .copy-field { align-self: stretch; }
.oidc-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
/* The details sit in the default slot of SettingsListRow, whose root is the container: a narrow row drops the indent under the icon. */
@container (max-width: 480px) { .oidc-provider-details { padding-left: 0; } }
</style>
