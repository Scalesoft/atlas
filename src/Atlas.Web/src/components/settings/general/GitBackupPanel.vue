<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { errorMessage, post } from "../../../api";
import t from "../../../i18n/settings-backups";
import ConfirmDialog from "../ConfirmDialog.vue";
import ReauthDialog from "../ReauthDialog.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import SummaryList from "../SummaryList.vue";
import { useDraftGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import type { SummaryItem } from "../types";
import { disclosureHooks } from "../disclosure";
import CopyField from "./CopyField.vue";
import { backupDestinationNote, useBackupDestinationChange } from "./backupDestination";
import { branchError, defaultBranch, gitBackupEndpoint as endpoint, remoteUrlError, remoteUrlPlaceholder, type GitBackupDraft, type GitBackupSettings, type GitBackupStatus } from "./gitBackup";

/**
 * Zálohy → Git (`general/backups/git`): the deploy key (generated on the server, the public half to
 * copy into the Git server) and the repository with its branch and the access test, saved with the SaveBar.
 * BackupsPage owns the server state, its refresh and "Synchronizovat teď"; this panel owns the draft.
 * A clean form follows the server; a draft stays as typed, and the key and the trusted server keys,
 * which the draft does not own, always come from the server.
 */
const props = defineProps<{ state: GitBackupStatus }>();
const emit = defineEmits<{ changed: [state: GitBackupStatus]; dirty: [value: boolean]; refresh: [] }>();
const flash = useSettingsFlash();
// A new repository or key is a security change (SEC1-2): a refused send asks for a fresh sign-in and goes again with the proof.
const destination = useBackupDestinationChange();

const saving = ref(false);
const saveError = ref("");
const showErrors = ref(false);
const busy = ref<"" | "key" | "test">("");
const actionError = ref("");
const testResult = ref<{ ok: boolean; message: string }>();
const confirmingKey = ref(false);
const keyError = ref("");
const draft = reactive<GitBackupDraft>({ enabled: false, remoteUrl: "", branch: defaultBranch });
const original = ref("");
let disposed = false;

const dirty = computed(() => JSON.stringify(draft) !== original.value);
const publicKey = computed(() => props.state.settings.publicKey);
const urlError = computed(() => remoteUrlError(draft.remoteUrl, draft.enabled));
const branchProblem = computed(() => branchError(draft.branch));
const enabledError = computed(() => (draft.enabled && !publicKey.value ? t.gitPanel.needKeyFirst : undefined));
const testBlocked = computed(() =>
  !publicKey.value ? t.gitPanel.needKey : !draft.remoteUrl.trim() || urlError.value ? t.gitPanel.needUrl : branchProblem.value ? t.gitPanel.needBranch : undefined,
);

function applyDraft(settings: GitBackupSettings) {
  Object.assign(draft, { enabled: settings.enabled, remoteUrl: settings.remoteUrl, branch: settings.branch || defaultBranch });
  showErrors.value = false;
  original.value = JSON.stringify(draft);
}
applyDraft(props.state.settings);
// Another administrator's change reaches a clean form; a draft stays as typed.
watch(() => props.state.settings, (settings) => {
  if (!dirty.value && !saving.value) applyDraft(settings);
});
watch(dirty, (value) => emit("dirty", value), { immediate: true });
const payload = (): GitBackupDraft => ({ enabled: draft.enabled, remoteUrl: draft.remoteUrl.trim(), branch: draft.branch.trim() });

async function save() {
  if (saving.value) return;
  showErrors.value = true;
  if (urlError.value || branchProblem.value || enabledError.value) {
    saveError.value = t.gitPanel.fixFields;
    return;
  }
  saving.value = true;
  saveError.value = "";
  testResult.value = undefined;
  try {
    await destination.send<GitBackupStatus>(endpoint, "PUT", payload(), t.gitPanel.changeTitle, t.gitPanel.save, (result) => {
      if (disposed) return;
      applyDraft(result.settings);
      emit("changed", result);
    });
  } catch (failure) {
    if (!disposed) saveError.value = errorMessage(failure);
  } finally {
    saving.value = false;
  }
}
function discard() {
  applyDraft(props.state.settings);
  saveError.value = "";
  testResult.value = undefined;
}

/** A first key is generated at once; replacing one asks first, because the old deploy key stops working. */
function requestKey() {
  if (busy.value || saving.value) return;
  keyError.value = "";
  if (publicKey.value) confirmingKey.value = true;
  else void generateKey();
}
async function generateKey() {
  if (busy.value) return;
  const replacing = !!publicKey.value;
  busy.value = "key";
  actionError.value = "";
  keyError.value = "";
  try {
    await destination.send<GitBackupStatus>(`${endpoint}/key`, "POST", undefined, t.gitPanel.key.reauthTitle, t.gitPanel.key.generate, (result) => {
      if (disposed) return;
      emit("changed", result);
      testResult.value = undefined;
      flash.notify(replacing ? t.gitPanel.key.replaced : t.gitPanel.key.generated, { icon: "key-round" });
    });
    confirmingKey.value = false;
  } catch (failure) {
    if (disposed) return;
    if (confirmingKey.value) keyError.value = errorMessage(failure);
    else actionError.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}
function cancelKey() {
  if (busy.value === "key") return;
  confirmingKey.value = false;
  keyError.value = "";
}

async function testAccess() {
  if (busy.value || saving.value || testBlocked.value) return;
  busy.value = "test";
  testResult.value = undefined;
  try {
    const result = await post<{ message: string }>(`${endpoint}/test`, { remoteUrl: draft.remoteUrl.trim(), branch: draft.branch.trim() });
    if (!disposed) testResult.value = { ok: true, message: result.message };
  } catch (failure) {
    if (!disposed) testResult.value = { ok: false, message: errorMessage(failure) };
  } finally {
    busy.value = "";
  }
  // The first connection trusts the server key: show its fingerprint.
  if (!disposed) emit("refresh");
}

// A result belongs to the address and branch it was run with.
watch(() => `${draft.remoteUrl.trim()}\n${draft.branch.trim()}`, () => (testResult.value = undefined));

useDraftGuard(dirty, { save, discard });
onBeforeUnmount(() => {
  disposed = true;
  emit("dirty", false);
});

const hostKeyItems = computed<SummaryItem[]>(() => props.state.settings.hostKeys.map((value, index) => ({ key: `host-${index}`, icon: "shield-check", label: t.gitPanel.key.hostKey, value, code: true })));
</script>

<template>
  <div class="git-backup" data-git-backup>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="actionError" class="git-backup-error" role="alert" data-git-key-error><AtlasIcon name="triangle-alert" :size="15" />{{ actionError }}</p>
    </Transition>

    <SettingsSection :title="t.gitPanel.key.title" icon="key-round" :description="t.gitPanel.key.description">
      <template #actions>
        <Button :variant="publicKey ? 'outline' : 'default'" size="sm" type="button" :disabled="!!busy || saving" :aria-busy="busy === 'key'" data-git-generate-key @click="requestKey">
          <AtlasIcon :name="busy === 'key' ? 'loader-circle' : 'key-round'" :size="15" :class="{ 'atlas-spinner': busy === 'key' }" />{{ publicKey ? t.gitPanel.key.generateNew : t.gitPanel.key.generate }}
        </Button>
      </template>
      <template v-if="publicKey">
        <CopyField :label="t.gitPanel.key.publicKey" :value="publicKey" :copy-label="t.gitPanel.key.copy" :value-attrs="{ 'data-git-public-key': '' }" />
        <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.gitPanel.key.paste }}</p>
      </template>
      <p v-else class="st-note" data-git-no-key><AtlasIcon name="info" :size="15" />{{ t.gitPanel.key.none }}</p>
      <template v-if="hostKeyItems.length">
        <SummaryList :items="hostKeyItems" :label="t.gitPanel.key.hostKeys" data-git-host-keys />
        <p class="st-note"><AtlasIcon name="shield-check" :size="15" />{{ t.gitPanel.key.hostKeyNote }}</p>
      </template>
    </SettingsSection>

    <SettingsSection :title="t.gitPanel.repository.title" icon="git-branch" :description="t.gitPanel.repository.description">
      <template #actions>
        <Button variant="outline" size="sm" type="button" :disabled="!!busy || saving || !!testBlocked" :title="testBlocked" :aria-busy="busy === 'test'" data-git-test @click="testAccess">
          <AtlasIcon :name="busy === 'test' ? 'loader-circle' : 'plug'" :size="15" :class="{ 'atlas-spinner': busy === 'test' }" />{{ t.gitPanel.repository.test }}
        </Button>
      </template>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.gitPanel.repository.enabled" :help="t.gitPanel.repository.enabledHelp" :error="showErrors ? enabledError : undefined">
        <Switch :id="id" v-model="draft.enabled" :aria-describedby="describedBy" :disabled="saving" data-git-enabled />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.gitPanel.repository.url" :help="t.gitPanel.repository.urlHelp" :error="showErrors ? urlError : undefined">
        <input :id="id" v-model="draft.remoteUrl" class="st-input st-mono" spellcheck="false" autocomplete="off" :placeholder="remoteUrlPlaceholder" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-git-remote-url />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.gitPanel.repository.branch" :help="t.gitPanel.repository.branchHelp" :error="showErrors ? branchProblem : undefined">
        <input :id="id" v-model="draft.branch" class="st-input st-mono git-backup-branch" spellcheck="false" autocomplete="off" :placeholder="defaultBranch" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-git-branch />
      </SettingsRow>
      <Transition name="st-fade">
        <StatusBadge v-if="testResult" :tone="testResult.ok ? 'success' : 'danger'" :label="testResult.message" role="status" :data-ok="String(testResult.ok)" data-git-test-result />
      </Transition>
      <p class="st-note git-backup-security" data-git-security-note>
        <AtlasIcon name="shield-alert" :size="15" />{{ t.gitPanel.repository.security }}
      </p>
    </SettingsSection>

    <!-- display: contents keeps the bar sticky to the page while giving tests a stable hook. -->
    <div class="git-backup-save" data-git-save>
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.gitPanel.save" @save="save" @cancel="discard" />
    </div>

    <ConfirmDialog :open="confirmingKey" :title="t.gitPanel.key.confirmTitle" :confirm-label="t.gitPanel.key.confirm" confirm-icon="key-round" :busy="busy === 'key'" :error="keyError" @confirm="generateKey" @cancel="cancelKey">
      <p>{{ t.gitPanel.key.confirmText }}</p>
    </ConfirmDialog>
    <ReauthDialog :open="!!destination.pending.value" :title="destination.pending.value?.title ?? ''" :confirm-label="destination.pending.value?.confirmLabel ?? ''" :busy="destination.busy.value" :error="destination.error.value || undefined" @confirm="destination.confirm" @cancel="destination.cancel">
      <p class="st-note" data-backup-destination-reauth><AtlasIcon name="shield-alert" :size="15" />{{ backupDestinationNote() }}</p>
    </ReauthDialog>
  </div>
</template>

<style scoped>
/* The sections stack like the body of a settings page. */
/* A column of flex items, not a grid: the action error unfolds and gives the gap above it back (settings/disclosure.ts). */
.git-backup { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.git-backup-branch { max-width: 240px; }
.git-backup-security { color: var(--text); }
.git-backup-security > svg { color: var(--warning); }
.git-backup-save { display: contents; }
.git-backup-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
