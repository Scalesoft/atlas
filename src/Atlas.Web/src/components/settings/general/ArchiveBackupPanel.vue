<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { errorMessage, post } from "../../../api";
import { formatDateTime } from "../../../formatDate";
import t from "../../../i18n/settings-backups";
import ActivityTimeline from "../ActivityTimeline.vue";
import ChoiceCards from "../ChoiceCards.vue";
import CopyField from "./CopyField.vue";
import InlineReveal from "../InlineReveal.vue";
import OptionPills from "../OptionPills.vue";
import ReauthDialog from "../ReauthDialog.vue";
import SaveBar from "../SaveBar.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import type { StorageProfile } from "../../../storageProfiles";
import type { ChoiceOption } from "../types";
import { useDraftGuard } from "../dirtyGuard";
import { disclosureHooks } from "../disclosure";
import { backupDestinationNote, useBackupDestinationChange } from "./backupDestination";
import { archiveEndpoint as endpoint, backupActivity, completeDownloadNote, displayedRuns, downloadAuthorizeUrl, downloadable, downloadUrl, intervalOptions, knownIntervals, type BackupMode, type BackupRun, type BackupSettings, type BackupState } from "./backups";

/**
 * Zálohy → Archivy ZIP (`general/backups`): the schedule and destination (saved with the SaveBar),
 * the destination test and the history of runs with their archives. BackupsPage owns the server
 * state, its refresh and "Zálohovat teď"; this panel owns the draft. A clean form follows the server,
 * a draft and a typed password stay as they are when the state is refreshed.
 */
const props = withDefaults(defineProps<{ state: BackupState; profiles?: StorageProfile[]; profilesError?: string }>(), { profiles: () => [], profilesError: "" });
const emit = defineEmits<{ changed: [state: BackupState]; dirty: [value: boolean] }>();

// A new folder is a security change (SEC1-2): a refused save asks for a fresh sign-in and goes again with the proof.
const destination = useBackupDestinationChange();
const saving = ref(false);
const saveError = ref("");
const testing = ref(false);
const testResult = ref<{ ok: boolean; message: string }>();
const password = ref("");
const intervalChoice = ref("1440");
const customMinutes = ref("");
const draft = reactive({ enabled: false, intervalMinutes: 1440, mode: "complete" as BackupMode, destination: "", username: "", domain: "", retentionCount: 10, storageProfileId: "", folder: "" });
const retention = ref("10");
const original = ref("");
const showErrors = ref(false);
/** Shown once, as the server returns it once: without it a complete archive restores the content only. */
const newKey = ref<BackupState["newBackupKey"]>();
let disposed = false;

const dirty = computed(() => !!password.value || JSON.stringify(draft) !== original.value);
const destinationKind = computed<"local" | "smb">({
  get: () => (draft.destination.toLowerCase().startsWith("smb://") ? "smb" : "local"),
  set: (kind) => {
    if (kind === destinationKind.value) return;
    draft.destination = kind === "smb" ? "smb://" : "";
    if (kind === "local") {
      draft.username = "";
      draft.domain = "";
      password.value = "";
    }
  },
});
const intervalError = computed(() => (intervalChoice.value === "custom" && (!Number.isInteger(draft.intervalMinutes) || draft.intervalMinutes < 5 || draft.intervalMinutes > 525600) ? t.archivePanel.schedule.intervalError : undefined));
const retentionError = computed(() => (!Number.isInteger(draft.retentionCount) || draft.retentionCount < 1 || draft.retentionCount > 1000 ? t.archivePanel.schedule.keepError : undefined));
const allowedProfiles = computed(() => props.profiles.filter(profile => profile.enabled && profile.allowBackups));
const profileOptions = computed(() => [
  { value: "", label: t.archivePanel.target.ownTarget },
  ...(draft.storageProfileId && !allowedProfiles.value.some(profile => profile.id === draft.storageProfileId) ? [{ value: draft.storageProfileId, label: t.archivePanel.target.unavailableProfile, disabled: true }] : []),
  ...allowedProfiles.value.map(profile => ({ value: profile.id, label: profile.name })),
]);
const destinationError = computed(() => draft.storageProfileId ? (!draft.enabled || allowedProfiles.value.some(profile => profile.id === draft.storageProfileId) ? undefined : t.archivePanel.target.profileError) : (!draft.destination.trim() || draft.destination.trim().toLowerCase() === "smb://" ? t.archivePanel.target.destinationError : undefined));

// Only the whole installation brings back boards, contacts, comments and notes (GAP2-1); the other two hold pages only.
const modeOptions = (): ChoiceOption[] => {
  const modes = t.archivePanel.modes;
  return [
    { value: "complete", label: modes.complete.label, description: modes.complete.description, icon: "database", badge: modes.complete.badge },
    { value: "full", label: modes.full.label, description: modes.full.description, icon: "history" },
    { value: "latest", label: modes.latest.label, description: modes.latest.description, icon: "file-text" },
  ];
};
const asMode = (value: unknown): BackupMode => (value === "full" || value === "latest" ? value : "complete");
const destinationOptions = (): ChoiceOption[] => [
  { value: "local", label: t.archivePanel.target.local.label, description: t.archivePanel.target.local.description, icon: "database" },
  { value: "smb", label: t.archivePanel.target.smb.label, description: t.archivePanel.target.smb.description, icon: "server" },
];

function applyDraft(settings: BackupSettings) {
  Object.assign(draft, {
    enabled: settings.enabled,
    intervalMinutes: settings.intervalMinutes,
    mode: settings.mode,
    destination: settings.destination,
    username: settings.username,
    domain: settings.domain,
    retentionCount: settings.retentionCount,
    storageProfileId: settings.storageProfileId ?? "",
    folder: settings.folder ?? "",
  });
  intervalChoice.value = knownIntervals.includes(settings.intervalMinutes) ? String(settings.intervalMinutes) : "custom";
  customMinutes.value = String(settings.intervalMinutes);
  retention.value = String(settings.retentionCount);
  password.value = "";
  showErrors.value = false;
  original.value = JSON.stringify(draft);
}
applyDraft(props.state.settings);
// Another administrator's change reaches a clean form; a draft stays as typed.
watch(() => props.state.settings, (settings) => {
  if (!dirty.value && !saving.value) applyDraft(settings);
});
watch(dirty, (value) => emit("dirty", value), { immediate: true });

function chooseInterval(value: string) {
  intervalChoice.value = value;
  if (value !== "custom") draft.intervalMinutes = Number(value);
  else draft.intervalMinutes = Number(customMinutes.value) || draft.intervalMinutes;
}
function setCustomMinutes(value: string) {
  customMinutes.value = value;
  draft.intervalMinutes = /^\d+$/.test(value.trim()) ? Number(value.trim()) : Number.NaN;
}
function setRetention(value: string) {
  retention.value = value;
  draft.retentionCount = /^\d+$/.test(value.trim()) ? Number(value.trim()) : Number.NaN;
}
const payload = () => ({ ...draft, storageProfileId: draft.storageProfileId || null, folder: draft.storageProfileId ? draft.folder : "", password: draft.storageProfileId ? undefined : password.value });

async function save() {
  if (saving.value) return;
  showErrors.value = true;
  if (intervalError.value || retentionError.value || destinationError.value) {
    saveError.value = t.archivePanel.fixFields;
    return;
  }
  saving.value = true;
  saveError.value = "";
  testResult.value = undefined;
  try {
    await destination.send<BackupState>(endpoint, "PUT", payload(), t.archivePanel.changeTitle, t.archivePanel.save, (result) => {
      if (disposed) return;
      applyDraft(result.settings);
      if (result.newBackupKey) newKey.value = result.newBackupKey;
      emit("changed", result);
    });
  } catch (failure) {
    // The typed password stays so the administrator can retry.
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
async function testAccess() {
  if (testing.value || destinationError.value) return;
  testing.value = true;
  testResult.value = undefined;
  try {
    const result = await post<{ message: string }>(`${endpoint}/test`, payload());
    if (!disposed) testResult.value = { ok: true, message: result.message };
  } catch (failure) {
    if (!disposed) testResult.value = { ok: false, message: errorMessage(failure) };
  } finally {
    testing.value = false;
  }
}

/** A complete archive: the server asks for a fresh sign-in (inside the window it passes at once), then the browser downloads it. */
async function onDownload(event: MouseEvent, run: BackupRun) {
  if (run.mode !== "complete") return;
  event.preventDefault();
  try {
    await destination.send<unknown>(downloadAuthorizeUrl(run), "POST", undefined, t.archivePanel.downloadTitle, t.archivePanel.download, () => window.location.assign(downloadUrl(run)), completeDownloadNote());
  } catch (failure) {
    if (!disposed) saveError.value = errorMessage(failure);
  }
}

useDraftGuard(dirty, { save, discard });
onBeforeUnmount(() => {
  disposed = true;
  password.value = "";
  emit("dirty", false);
});

const runs = computed(() => displayedRuns(props.state));
const history = computed(() => runs.value.map(backupActivity));
const runById = computed(() => new Map(runs.value.map((run) => [run.id, run])));
</script>

<template>
  <div class="archive-backup" data-archive-backup>
    <SettingsSection :title="t.archivePanel.schedule.title" icon="calendar" :description="t.archivePanel.schedule.description">
      <SettingsRow v-slot="{ id, describedBy }" :label="t.archivePanel.schedule.enabled" :help="t.archivePanel.schedule.enabledHelp">
        <Switch :id="id" v-model="draft.enabled" :aria-describedby="describedBy" :disabled="saving" data-backup-enabled />
      </SettingsRow>
      <InlineReveal :open="draft.enabled" class="reveal-rows">
        <SettingsRow :label="t.archivePanel.schedule.interval" :label-for="false" :error="showErrors ? intervalError : undefined">
          <OptionPills :model-value="intervalChoice" :options="intervalOptions()" :label="t.archivePanel.schedule.intervalLabel" hide-label data-backup-interval @update:model-value="chooseInterval" />
          <InlineReveal :open="intervalChoice === 'custom'">
            <label class="archive-inline-field">
              <span>{{ t.archivePanel.schedule.customMinutes }}</span>
              <input :value="customMinutes" class="st-input" inputmode="numeric" :aria-invalid="showErrors && !!intervalError" data-backup-custom-minutes @input="setCustomMinutes(($event.target as HTMLInputElement).value)" />
            </label>
          </InlineReveal>
        </SettingsRow>
      </InlineReveal>
      <ChoiceCards :model-value="draft.mode" :options="modeOptions()" :label="t.archivePanel.schedule.content" :columns="3" :disabled="saving" data-backup-mode @update:model-value="draft.mode = asMode($event)" />
      <InlineReveal :open="!!newKey">
        <div v-if="newKey" class="archive-new-key" data-backup-new-key>
          <p class="st-note" role="alert"><AtlasIcon name="key-round" :size="15" />{{ t.archivePanel.newKey.note }}</p>
          <CopyField :label="t.archivePanel.newKey.label" :value="newKey.key" :copy-label="t.archivePanel.newKey.copy" />
          <div class="st-actions"><Button variant="outline" size="sm" type="button" data-backup-new-key-done @click="newKey = undefined">{{ t.archivePanel.newKey.done }}</Button></div>
        </div>
      </InlineReveal>
      <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.archivePanel.schedule.keep" :help="t.archivePanel.schedule.keepHelp" :error="showErrors ? retentionError : undefined">
        <input :id="id" :value="retention" class="st-input archive-number" inputmode="numeric" :aria-label="t.archivePanel.schedule.keepLabel" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-backup-retention @input="setRetention(($event.target as HTMLInputElement).value)" />
      </SettingsRow>
    </SettingsSection>

    <SettingsSection :title="t.archivePanel.target.title" icon="folder" :description="t.archivePanel.target.description">
      <template #actions>
        <Button variant="outline" size="sm" type="button" :disabled="testing || saving || !!destinationError" :aria-busy="testing" data-backup-test @click="testAccess">
          <AtlasIcon :name="testing ? 'loader-circle' : 'plug'" :size="15" :class="{ 'atlas-spinner': testing }" />{{ t.archivePanel.target.test }}
        </Button>
      </template>
      <SettingsRow :label="t.archivePanel.target.profile" :label-for="false" :help="t.archivePanel.target.profileHelp" :error="showErrors && draft.storageProfileId ? destinationError : undefined">
        <OptionPills v-model="draft.storageProfileId" :options="profileOptions" :label="t.archivePanel.target.profile" hide-label :disabled="saving" data-backup-profile />
        <a href="#/settings/general/storage" class="st-link">{{ t.archivePanel.target.manageProfiles }}</a>
        <Transition :css="false" v-bind="disclosureHooks">
          <p v-if="profilesError" class="st-note" role="alert">{{ profilesError }}</p>
        </Transition>
      </SettingsRow>
      <InlineReveal :open="!!draft.storageProfileId" class="reveal-rows">
        <SettingsRow v-slot="{ id, describedBy }" :label="t.archivePanel.target.folder" :help="t.archivePanel.target.folderHelp">
          <input :id="id" v-model="draft.folder" class="st-input st-mono" :aria-describedby="describedBy" :disabled="saving" spellcheck="false" data-backup-folder />
        </SettingsRow>
      </InlineReveal>
      <InlineReveal :open="!draft.storageProfileId" class="reveal-tail">
      <ChoiceCards :model-value="destinationKind" :options="destinationOptions()" :label="t.archivePanel.target.kind" hide-label :columns="2" :disabled="saving" data-backup-destination-kind @update:model-value="destinationKind = $event === 'smb' ? 'smb' : 'local'">
        <template #reveal>
          <SettingsRow v-slot="{ id, describedBy, invalid }" :label="destinationKind === 'smb' ? t.archivePanel.target.smbAddress : t.archivePanel.target.localFolder" :help="destinationKind === 'smb' ? t.archivePanel.target.smbHelp : t.archivePanel.target.localHelp" :error="showErrors ? destinationError : undefined">
            <input :id="id" v-model="draft.destination" class="st-input st-mono" spellcheck="false" :placeholder="destinationKind === 'smb' ? t.archivePanel.target.smbPlaceholder : t.archivePanel.target.localPlaceholder" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-backup-destination />
          </SettingsRow>
          <template v-if="destinationKind === 'smb'">
            <SettingsRow v-slot="{ id }" :label="t.archivePanel.target.username">
              <input :id="id" v-model="draft.username" class="st-input" :aria-label="t.archivePanel.target.usernameLabel" autocomplete="off" :disabled="saving" data-backup-username />
            </SettingsRow>
            <SettingsRow v-slot="{ id }" :label="t.archivePanel.target.domain">
              <input :id="id" v-model="draft.domain" class="st-input" :aria-label="t.archivePanel.target.domainLabel" autocomplete="off" :disabled="saving" data-backup-domain />
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy }" :label="t.archivePanel.target.password" :help="t.archivePanel.target.passwordHelp">
              <input :id="id" v-model="password" class="st-input" type="password" autocomplete="new-password" :placeholder="state.settings.hasPassword ? t.archivePanel.target.passwordSaved : t.archivePanel.target.passwordEmpty" :aria-describedby="describedBy" :disabled="saving" data-backup-password />
            </SettingsRow>
          </template>
        </template>
      </ChoiceCards>
      </InlineReveal>
      <Transition name="st-fade">
        <StatusBadge v-if="testResult" :tone="testResult.ok ? 'success' : 'danger'" :label="testResult.message" role="status" data-backup-test-result />
      </Transition>
    </SettingsSection>

    <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.archivePanel.save" @save="save" @cancel="discard" />
    <ReauthDialog :open="!!destination.pending.value" :title="destination.pending.value?.title ?? ''" :confirm-label="destination.pending.value?.confirmLabel ?? ''" :busy="destination.busy.value" :error="destination.error.value || undefined" @confirm="destination.confirm" @cancel="destination.cancel">
      <p class="st-note" data-backup-destination-reauth><AtlasIcon name="shield-alert" :size="15" />{{ destination.pending.value?.note ?? backupDestinationNote() }}</p>
    </ReauthDialog>

    <SettingsSection :title="t.archivePanel.history.title" icon="history" :count="runs.length" :description="t.archivePanel.history.description(state.nextRunAt ? formatDateTime(state.nextRunAt) : undefined)">
      <ActivityTimeline :items="history" :label="t.archivePanel.history.title" :empty-title="t.archivePanel.history.emptyTitle" :empty-text="t.archivePanel.history.emptyText" data-backup-history>
        <template #item-actions="{ item }">
          <a v-if="runById.get(item.id) && downloadable(runById.get(item.id)!)" class="st-link" :href="downloadUrl(runById.get(item.id)!)" :data-backup-download="item.id" @click="onDownload($event, runById.get(item.id)!)"><AtlasIcon name="download" :size="14" />{{ t.archivePanel.download }}</a>
        </template>
      </ActivityTimeline>
    </SettingsSection>
  </div>
</template>

<style scoped>
/* The sections stack like the body of a settings page. */
.archive-backup { display: grid; gap: 14px; min-width: 0; }
.archive-inline-field { display: grid; gap: 4px; max-width: 260px; padding-top: 8px; }
.archive-inline-field > span { color: var(--muted); font-size: 12px; font-weight: 600; }
.archive-number { max-width: 140px; }
.archive-new-key { display: grid; gap: 8px; }
</style>
