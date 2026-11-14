<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from "vue";
import { api, errorMessage, post, put } from "../../../api";
import { formatDateTime } from "../../../formatDate";
import type { StorageProfile } from "../../../storageProfiles";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmDialog from "../ConfirmDialog.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingNote from "../LoadingNote.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SaveBar from "../SaveBar.vue";
import SearchableTags from "../SearchableTags.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { useSettingsContext } from "../context";
import { useDraftGuard } from "../dirtyGuard";
import t from "../../../i18n/settings-global";

const context = useSettingsContext();
/** One page's automatic PDF in the overview of all exports (the storage administrator's view). */
interface PdfExportRow {
  pageId: string; spaceName: string; pageTitle: string; profileName: string; path: string; enabled: boolean;
  publishedOnly: boolean; keepVersions: boolean; status: "paused" | "pending" | "current" | "error"; lastExportedAt: string | null; lastError: string;
}
const EXPORT_BADGES = {
  current: { label: t.storage.badges.current, tone: "success" },
  pending: { label: t.storage.badges.pending, tone: "info" },
  error: { label: t.storage.badges.error, tone: "danger" },
  paused: { label: t.storage.badges.paused, tone: "neutral" },
} as const;
const pdfExports = ref<PdfExportRow[]>(), exportsError = ref(""), retrying = ref(""), retryError = ref<{ pageId: string; message: string }>();
let exportsGeneration = 0;
const profiles = ref<StorageProfile[]>([]);
const loading = ref(true), loadError = ref(""), saving = ref(false), saveError = ref("");
// undefined: list, null: new profile, a profile: edit its saved version.
const editing = ref<StorageProfile | null>();
const empty = () => ({ name: "", destination: "", username: "", domain: "", enabled: true, allowPageExports: true, allowBackups: true, allSpaces: false, spaceIds: [] as string[] });
const draft = reactive(empty());
const password = ref(""), original = ref(""), showErrors = ref(false);
const testing = ref(""), testResult = ref<{ id: string; ok: boolean; message: string }>();
const removing = ref<StorageProfile>(), removingBusy = ref(false), removeError = ref("");
let disposed = false, generation = 0;
const dirty = computed(() => editing.value !== undefined && (!!password.value || JSON.stringify(draft) !== original.value));
const smb = computed(() => draft.destination.toLowerCase().startsWith("smb://"));
const nameError = computed(() => !draft.name.trim() ? t.storage.nameRequired : undefined);
const destinationError = computed(() => !draft.destination.trim() || draft.destination.toLowerCase().trim() === "smb://" ? t.storage.destinationRequired : undefined);
const spacesError = computed(() => draft.enabled && draft.allowPageExports && !draft.allSpaces && !draft.spaceIds.length ? t.storage.spacesRequired : undefined);
const spaceOptions = computed(() => context.spaces.filter(space => !space.itemAccessOnly).map(space => ({ value: space.id, label: space.name, icon: space.icon })));
const destinationOptions = [
  { value: "local", label: t.storage.local, description: t.storage.localHint, icon: "database" },
  { value: "smb", label: t.storage.smb, description: t.storage.smbHint, icon: "server" },
];

function apply(profile: StorageProfile | null) {
  const values = profile ?? empty();
  Object.assign(draft, {
    name: values.name, destination: values.destination, username: values.username, domain: values.domain,
    enabled: values.enabled, allowPageExports: values.allowPageExports, allowBackups: values.allowBackups,
    allSpaces: values.allSpaces, spaceIds: [...values.spaceIds],
  });
  password.value = ""; original.value = JSON.stringify(draft); showErrors.value = false; saveError.value = "";
}
function edit(profile: StorageProfile | null) {
  editing.value = profile; apply(profile); testResult.value = undefined;
}
function discard() { editing.value = undefined; password.value = ""; saveError.value = ""; }
function chooseDestination(kind: string | string[] | null) {
  if ((kind === "smb") === smb.value) return;
  draft.destination = kind === "smb" ? "smb://" : "";
  draft.username = ""; draft.domain = ""; password.value = "";
}
async function load() {
  const current = ++generation;
  try {
    const result = await api<StorageProfile[]>("/storage-profiles");
    if (disposed || current !== generation) return;
    profiles.value = result; loadError.value = "";
    if (editing.value && !dirty.value && !saving.value) {
      const fresh = result.find(profile => profile.id === editing.value?.id);
      if (fresh) { editing.value = fresh; apply(fresh); }
    }
  } catch (failure) { if (!disposed && current === generation) loadError.value = errorMessage(failure); }
  finally { if (!disposed && current === generation) loading.value = false; }
}
async function save() {
  if (saving.value || editing.value === undefined) return;
  showErrors.value = true;
  if (nameError.value || destinationError.value || spacesError.value) { saveError.value = t.storage.fixFields; return; }
  const savedProfile = editing.value;
  saving.value = true; saveError.value = ""; generation++;
  const payload = {
    ...draft, spaceIds: draft.allSpaces ? [] : [...draft.spaceIds],
    username: smb.value ? draft.username : "", domain: smb.value ? draft.domain : "",
    password: smb.value && password.value ? password.value : undefined, expectedVersion: savedProfile?.version,
  };
  try {
    const result = savedProfile
      ? await put<StorageProfile>("/storage-profiles/" + encodeURIComponent(savedProfile.id), payload)
      : await post<StorageProfile>("/storage-profiles", payload);
    if (disposed) return;
    profiles.value = [...profiles.value.filter(profile => profile.id !== result.id), result];
    editing.value = undefined; password.value = ""; original.value = "";
  } catch (failure) { if (!disposed) saveError.value = errorMessage(failure); }
  finally { saving.value = false; }
}
async function test(profile: StorageProfile) {
  if (testing.value) return;
  testing.value = profile.id; testResult.value = undefined;
  try {
    const result = await post<{ message: string }>("/storage-profiles/" + encodeURIComponent(profile.id) + "/test");
    if (!disposed) testResult.value = { id: profile.id, ok: true, message: result.message };
  } catch (failure) { if (!disposed) testResult.value = { id: profile.id, ok: false, message: errorMessage(failure) }; }
  finally { testing.value = ""; }
}
async function remove() {
  const profile = removing.value;
  if (!profile || removingBusy.value) return;
  removingBusy.value = true; removeError.value = ""; generation++;
  try {
    await api("/storage-profiles/" + encodeURIComponent(profile.id) + "?expectedVersion=" + profile.version, { method: "DELETE" });
    if (disposed) return;
    profiles.value = profiles.value.filter(item => item.id !== profile.id); removing.value = undefined;
  } catch (failure) { if (!disposed) removeError.value = errorMessage(failure); }
  finally { removingBusy.value = false; }
}
async function loadExports() {
  const current = ++exportsGeneration;
  try {
    const rows = await api<PdfExportRow[]>("/storage-profiles/pdf-exports");
    if (!disposed && current === exportsGeneration) { pdfExports.value = rows; exportsError.value = ""; }
  } catch (failure) { if (!disposed && current === exportsGeneration) exportsError.value = errorMessage(failure); }
}
async function retry(row: PdfExportRow) {
  if (retrying.value) return;
  retrying.value = row.pageId; retryError.value = undefined;
  try { await post("/storage-profiles/pdf-exports/" + encodeURIComponent(row.pageId) + "/run"); }
  catch (failure) { if (!disposed) retryError.value = { pageId: row.pageId, message: errorMessage(failure) }; }
  finally { retrying.value = ""; }
  if (!disposed) await loadExports();
}
function focused() { if (!saving.value && !removingBusy.value) { void load(); void loadExports(); } }
useDraftGuard(dirty, { save, discard });
onMounted(() => { void load(); void loadExports(); window.addEventListener("focus", focused); });
onBeforeUnmount(() => { disposed = true; generation++; password.value = ""; window.removeEventListener("focus", focused); });
</script>

<template>
  <SettingsPage :title="t.storage.title" :description="t.storage.description">
    <template v-if="editing === undefined" #actions><Button data-add-profile @click="edit(null)"><AtlasIcon name="folder-plus" :size="16" />{{ t.storage.add }}</Button></template>
    <LoadingSkeleton v-if="loading" :label="t.storage.loading" />
    <template v-else-if="editing !== undefined">
      <SettingsSection :title="editing ? t.storage.editTitle : t.storage.addTitle" icon="folder">
        <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.storage.name" :help="t.storage.nameHelp" :error="showErrors ? nameError : undefined">
          <input :id="id" v-model="draft.name" class="st-input" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" data-profile-name />
        </SettingsRow>
        <ChoiceCards :model-value="smb ? 'smb' : 'local'" :options="destinationOptions" :label="t.storage.type" :columns="2" :disabled="saving" @update:model-value="chooseDestination">
          <template #reveal>
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="smb ? t.storage.smbAddress : t.storage.localFolder" :help="smb ? t.storage.smbHelp : t.storage.localHelp" :error="showErrors ? destinationError : undefined">
              <input :id="id" v-model="draft.destination" class="st-input st-mono" :aria-describedby="describedBy" :aria-invalid="invalid" :disabled="saving" spellcheck="false" data-profile-destination />
            </SettingsRow>
            <InlineReveal :open="smb">
              <SettingsRow v-slot="{ id }" :label="t.storage.username"><input :id="id" v-model="draft.username" class="st-input" autocomplete="off" :disabled="saving" data-profile-username /></SettingsRow>
              <SettingsRow v-slot="{ id }" :label="t.storage.domain"><input :id="id" v-model="draft.domain" class="st-input" autocomplete="off" :disabled="saving" data-profile-domain /></SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" :label="t.storage.password" :help="t.storage.passwordHelp">
                <input :id="id" v-model="password" type="password" class="st-input" :placeholder="editing?.hasPassword ? t.storage.passwordSaved : t.storage.passwordPlaceholder" autocomplete="new-password" :aria-describedby="describedBy" :disabled="saving" data-profile-password />
              </SettingsRow>
            </InlineReveal>
          </template>
        </ChoiceCards>
        <SettingsRow v-slot="{ id, describedBy }" :label="t.storage.enabled" :help="t.storage.enabledHelp">
          <Switch :id="id" v-model="draft.enabled" :aria-describedby="describedBy" :disabled="saving" data-profile-enabled />
        </SettingsRow>
      </SettingsSection>
      <SettingsSection :title="t.storage.allowedUse" icon="shield-check">
        <SettingsRow v-slot="{ id }" :label="t.storage.allowBackups"><Switch :id="id" v-model="draft.allowBackups" :disabled="saving" data-profile-backups /></SettingsRow>
        <SettingsRow v-slot="{ id }" :label="t.storage.allowPdf"><Switch :id="id" v-model="draft.allowPageExports" :disabled="saving" data-profile-page-exports /></SettingsRow>
        <InlineReveal :open="draft.allowPageExports" class="reveal-rows">
          <SettingsRow v-slot="{ id }" :label="t.storage.allowAllSpaces"><Switch :id="id" v-model="draft.allSpaces" :disabled="saving" data-profile-all-spaces /></SettingsRow>
          <InlineReveal :open="!draft.allSpaces">
            <SettingsRow :label="t.storage.allowedSpaces" :label-for="false" :error="showErrors ? spacesError : undefined">
              <fieldset :disabled="saving" class="profile-spaces"><SearchableTags v-model="draft.spaceIds" :options="spaceOptions" :label="t.storage.spacesLabel" :empty-text="t.storage.noOtherSpace" /></fieldset>
            </SettingsRow>
          </InlineReveal>
        </InlineReveal>
      </SettingsSection>
      <p class="st-note">{{ t.storage.saveNote }}</p>
      <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.storage.saveProfile" @save="save" @cancel="discard" />
      <Button v-if="!dirty" variant="outline" @click="discard">{{ t.storage.backToProfiles }}</Button>
    </template>
    <template v-else>
      <ErrorState v-if="loadError" :title="t.storage.loadFailed" :text="loadError" @retry="load" />
      <SettingsSection v-if="profiles.length" :title="t.storage.saved" :count="profiles.length">
        <SettingsList :label="t.storage.listLabel">
          <SettingsListRow v-for="profile in profiles" :key="profile.id" :title="profile.name" icon="folder" :data-profile="profile.id">
            <template #description><code class="st-mono">{{ profile.destination }}</code><p v-if="testResult?.id === profile.id" :class="{ 'profile-error': !testResult.ok }" role="status">{{ testResult.message }}</p></template>
            <template #meta>
              <StatusBadge :label="profile.enabled ? t.storage.active : t.storage.off" :tone="profile.enabled ? 'success' : 'neutral'" size="sm" />
              <StatusBadge v-if="profile.allowBackups" label="ZIP" size="sm" />
              <StatusBadge v-if="profile.allowPageExports" :label="profile.allSpaces ? t.storage.pdfAllSpaces : t.storage.pdfSelectedSpaces" size="sm" />
            </template>
            <template #actions>
              <Button variant="ghost" size="sm" :data-edit-profile="profile.id" @click="edit(profile)">{{ t.storage.edit }}</Button>
              <Button variant="outline" size="sm" :disabled="!!testing" :aria-busy="testing === profile.id" :data-test-profile="profile.id" @click="test(profile)">{{ t.storage.test }}</Button>
              <Button variant="ghost" size="icon-sm" :aria-label="t.storage.removeAria(profile.name)" :data-remove-profile="profile.id" @click="removing = profile; removeError = ''"><AtlasIcon name="trash" :size="15" /></Button>
            </template>
          </SettingsListRow>
        </SettingsList>
      </SettingsSection>
      <EmptyState v-else-if="!loadError" icon="folder" :title="t.storage.emptyTitle" :text="t.storage.emptyText" />
      <SettingsSection :title="t.storage.exportsTitle" icon="file-text" :count="pdfExports?.length || undefined" :description="t.storage.exportsDescription">
        <ErrorState v-if="exportsError" :title="t.storage.exportsFailed" :text="exportsError" @retry="loadExports" />
        <LoadingNote v-else-if="!pdfExports" :label="t.storage.exportsLoading" />
        <SettingsList v-else-if="pdfExports.length" :label="t.storage.exportsLabel">
          <SettingsListRow v-for="row in pdfExports" :key="row.pageId" :title="row.pageTitle || t.storage.untitled" icon="file-text" :data-pdf-export-row="row.pageId">
            <template #description>
              <code class="st-mono">{{ row.path }}</code>
              <p>{{ t.storage.exportDetail(row.spaceName, row.publishedOnly, row.keepVersions, row.lastExportedAt ? formatDateTime(row.lastExportedAt) : null) }}</p>
              <p v-if="row.lastError" class="profile-error">{{ row.lastError }}</p>
              <p v-if="retryError?.pageId === row.pageId" class="profile-error" role="alert">{{ retryError.message }}</p>
            </template>
            <template #meta><StatusBadge :label="EXPORT_BADGES[row.status].label" :tone="EXPORT_BADGES[row.status].tone" size="sm" /></template>
            <template #actions>
              <Button variant="ghost" size="sm" :data-open-pdf-export="row.pageId" @click="context.openPage(row.pageId, true)">{{ t.storage.openSettings }}</Button>
              <Button variant="outline" size="sm" :disabled="!row.enabled || !!retrying" :aria-busy="retrying === row.pageId" :data-retry-pdf-export="row.pageId" @click="retry(row)">{{ t.storage.retryExport }}</Button>
            </template>
          </SettingsListRow>
        </SettingsList>
        <p v-else class="st-note">{{ t.storage.noExports }}</p>
      </SettingsSection>
    </template>
    <ConfirmDialog :open="!!removing" :title="t.storage.removeTitle" :confirm-label="t.storage.removeLabel" :busy="removingBusy" :error="removeError" @confirm="remove" @cancel="removing = undefined">
      <p>{{ t.storage.removeBefore }} <strong>{{ removing?.name }}</strong> {{ t.storage.removeAfter }}</p>
      <p>{{ t.storage.removeInUse }}</p>
    </ConfirmDialog>
  </SettingsPage>
</template>

<style scoped>
.profile-spaces { padding: 0; margin: 0; border: 0; min-width: 0; }
.profile-error { color: var(--error); }
code { overflow-wrap: anywhere; }
</style>
