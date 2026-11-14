<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { ApiError, api, errorMessage, post, put } from "../api";
import { formatDateTime } from "../formatDate";
import { FEEDBACK_HOLD_MS } from "../motion/aiMotion";
import { pageTopic, useLiveFeed } from "../live/liveFeed";
import AppDialog from "./AppDialog.vue";
import AtlasIcon from "./AtlasIcon.vue";
import UnsavedChangesDialog from "./UnsavedChangesDialog.vue";
import { Button } from "./ui/button";
import { Switch } from "./ui/switch";
import ConfirmDialog from "./settings/ConfirmDialog.vue";
import ErrorState from "./settings/ErrorState.vue";
import InlineReveal from "./settings/InlineReveal.vue";
import OptionPills from "./settings/OptionPills.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import SaveBar from "./settings/SaveBar.vue";
import SettingsRow from "./settings/SettingsRow.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import StatusBadge from "./settings/StatusBadge.vue";
import LiveUpdateNote from "./settings/LiveUpdateNote.vue";
import PdfCoverDetailsField from "./PdfCoverDetailsField.vue";
import { PDF_COVER_DETAILS, type PdfCoverDetail } from "../contentExport";
// Opened from a page, outside the settings shell: the switch's utilities and the .st-* rows live in the lazy settings
// stylesheets, so without them the main switch of this dialog collapsed to 2 px until a settings screen had loaded.
import "./settings/settings.css";
import "./settings/settings-tailwind.css";

interface ExportSettings {
  enabled: boolean; storageProfileId: string | null; folder: string; fileName: string;
  keepVersions: boolean; publishedOnly: boolean; version: number; coverDetails: PdfCoverDetail[];
}
interface ExportState {
  settings: ExportSettings; profiles: { id: string; name: string; location: string }[];
  status: "paused" | "pending" | "current" | "error";
  lastExportedAt: string | null; lastRevisionId: string | null; lastError: string;
}
interface FolderListing { folder: string; folders: string[]; truncated: boolean }
const props = defineProps<{ pageId: string; title: string }>();
const emit = defineEmits<{ close: []; dirty: [value: boolean]; busy: [value: boolean] }>();
const endpoint = () => "/pages/" + encodeURIComponent(props.pageId) + "/pdf-export";
const state = ref<ExportState>();
const draft = reactive<ExportSettings>({ enabled: false, storageProfileId: null, folder: "", fileName: "document.pdf", keepVersions: false, publishedOnly: false, version: 0, coverDetails: [] });
const original = ref("");
const loading = ref(true), saving = ref(false), running = ref(false);
const loadError = ref(""), saveError = ref(""), runError = ref("");
const showErrors = ref(false), leaving = ref(false);
let disposed = false, generation = 0;
const dirty = computed(() => !!original.value && JSON.stringify(draft) !== original.value);
watch(dirty, value => emit("dirty", value));
watch([saving, running], ([save, run]) => emit("busy", save || run));
// "" stands for no profile yet; a profile that is no longer offered stays visible as a disabled pill so the saved choice is not silently lost.
const profileOptions = computed(() => [
  { value: "", label: t.pdf.chooseProfile },
  ...(draft.storageProfileId && !available.value ? [{ value: draft.storageProfileId, label: t.pdf.unavailableProfile, disabled: true }] : []),
  ...(state.value?.profiles ?? []).map(profile => ({ value: profile.id, label: profile.name })),
]);
const available = computed(() => state.value?.profiles.some(profile => profile.id === draft.storageProfileId));
const profileError = computed(() => draft.enabled && !available.value ? t.pdf.profileError : undefined);
const fileError = computed(() => draft.enabled && (!draft.fileName.trim() || /[/\\]/.test(draft.fileName) || !draft.fileName.toLowerCase().endsWith(".pdf")) ? t.pdf.fileError : undefined);
const coverSummary = computed(() => draft.coverDetails.length
  ? PDF_COVER_DETAILS.filter(detail => draft.coverDetails.includes(detail.value)).map(detail => t.pdfcover.detailLabel(detail.value)).join(", ")
  : t.pdf.noCoverDetails);
/** The whole path the PDF gets, in the form of the profile's root (\\server\share\… or a server path). */
const destination = computed(() => {
  const location = state.value?.profiles.find(profile => profile.id === draft.storageProfileId)?.location;
  if (!location) return "";
  const separator = location.includes("\\") ? "\\" : "/";
  return [location.replace(/[\\/]+$/, ""), ...draft.folder.split("/").filter(Boolean), draft.fileName.trim().replace(/\.pdf$/i, " " + t.pdf.datePattern + ".pdf")].join(separator);
});
const crumbs = computed(() => draft.folder.split("/").filter(Boolean).map((name, index, parts) => ({ name, path: parts.slice(0, index + 1).join("/") })));
const versionSummary = computed(() => (draft.publishedOnly ? t.pdf.onlyPublished : t.pdf.savedVersions) + " · " + (draft.keepVersions ? t.pdf.versionCopies : t.pdf.oneCurrent));
const badge = computed(() => ({
  current: { label: t.pdf.badgeCurrent, tone: "success" as const },
  pending: { label: t.pdf.badgePending, tone: "info" as const },
  error: { label: t.pdf.badgeError, tone: "danger" as const },
  paused: { label: t.pdf.badgePaused, tone: "neutral" as const },
})[state.value?.status ?? "paused"]);

/**
 * „Aktuální PDF“ right after an export in progress or a failed one is a completed action: the badge settles once and
 * returns to its quiet look. A state that was already current when the dialog opened (or is read again) stays quiet.
 */
const exportDone = ref(false);
let doneTimer: ReturnType<typeof setTimeout> | undefined;
watch(() => state.value?.status, (now, was) => {
  clearTimeout(doneTimer);
  exportDone.value = now === "current" && (was === "pending" || was === "error");
  if (exportDone.value) doneTimer = setTimeout(() => { exportDone.value = false; }, FEEDBACK_HOLD_MS);
});

function apply(settings: ExportSettings) {
  Object.assign(draft, settings, { coverDetails: [...settings.coverDetails] });
  original.value = JSON.stringify(draft);
  showErrors.value = false;
}
async function load() {
  if (saving.value || running.value) return;
  const current = ++generation;
  try {
    const result = await api<ExportState>(endpoint());
    if (disposed || current !== generation) return;
    const clean = !dirty.value;
    state.value = result;
    if (clean) apply(result.settings);
    loadError.value = "";
  } catch (failure) {
    if (!disposed && current === generation) loadError.value = errorMessage(failure);
  } finally { if (!disposed && current === generation) loading.value = false; }
}
// The export's state is read again when the server says it stored a PDF or failed (useLiveFeed on the page's channel; nothing polls).
const { unavailable: liveOff, refresh: refreshLive } = useLiveFeed(pageTopic(props.pageId, "PdfExportChanged"), load);
/**
 * Browsing is choosing: the opened folder is the target folder, the root included. Listing never creates a folder; the
 * server keeps every path inside the profile's root.
 */
const listing = ref<FolderListing>();
const foldersLoading = ref(false), foldersError = ref(""), newFolder = ref(""), creating = ref(false), createError = ref("");
let folderGeneration = 0;
async function loadFolders() {
  const profileId = draft.storageProfileId, current = ++folderGeneration;
  if (!draft.enabled || !profileId || !available.value) { listing.value = undefined; foldersError.value = ""; return; }
  foldersLoading.value = true;
  try {
    const result = await api<FolderListing>(endpoint() + "/folders?profileId=" + encodeURIComponent(profileId) + "&folder=" + encodeURIComponent(draft.folder));
    if (!disposed && current === folderGeneration) { listing.value = result; foldersError.value = ""; }
  } catch (failure) {
    if (!disposed && current === folderGeneration) { listing.value = undefined; foldersError.value = errorMessage(failure); }
  } finally { if (!disposed && current === folderGeneration) foldersLoading.value = false; }
}
watch(() => [draft.enabled, draft.storageProfileId, draft.folder, available.value], () => { createError.value = ""; void loadFolders(); });
function open(folder: string) { if (!saving.value && !running.value) draft.folder = folder; }
async function createFolder() {
  const name = newFolder.value.trim();
  if (!name || creating.value || !draft.storageProfileId) return;
  creating.value = true; createError.value = "";
  try {
    const result = await post<FolderListing>(endpoint() + "/folders", { storageProfileId: draft.storageProfileId, parent: draft.folder, name });
    if (disposed) return;
    newFolder.value = ""; draft.folder = result.folder;
  } catch (failure) { if (!disposed) createError.value = errorMessage(failure); }
  finally { creating.value = false; }
}
/** The server's question when the target file exists and this export did not write it: the save waits for an explicit takeover. */
const takeover = ref("");
async function save(confirmTakeover = false) {
  if (saving.value || running.value) return;
  showErrors.value = true;
  if (profileError.value || fileError.value) { saveError.value = t.pdf.fixFields; return; }
  saving.value = true; saveError.value = ""; generation++;
  try {
    const { version, ...values } = draft;
    const result = await put<ExportState>(endpoint(), { ...values, expectedVersion: version, confirmTakeover });
    if (disposed) return;
    state.value = result; apply(result.settings); takeover.value = "";
  } catch (failure) {
    if (disposed) return;
    if (failure instanceof ApiError && failure.code === "pdf_target_exists" && !confirmTakeover) takeover.value = failure.message;
    else { saveError.value = errorMessage(failure); takeover.value = ""; }
  } finally { saving.value = false; }
}
function discard() {
  if (state.value) apply(state.value.settings);
  saveError.value = ""; runError.value = "";
}
async function run() {
  if (dirty.value || saving.value || running.value || !state.value?.settings.enabled) return;
  running.value = true; runError.value = ""; generation++;
  try { await post(endpoint() + "/run"); }
  catch (failure) { if (!disposed) runError.value = errorMessage(failure); }
  finally { running.value = false; }
  if (!disposed) await load();
}
function close() {
  if (saving.value || running.value) return;
  if (dirty.value) leaving.value = true;
  else emit("close");
}
async function saveAndClose() {
  await save();
  if (!dirty.value && !saveError.value) emit("close");
  leaving.value = false;
}
function focused() { void load(); }
onMounted(() => {
  void load(); window.addEventListener("focus", focused);
});
onBeforeUnmount(() => { emit("dirty", false); emit("busy", false); disposed = true; generation++; clearTimeout(doneTimer); window.removeEventListener("focus", focused); });
</script>

<template>
  <AppDialog :title="t.pdf.title" :open="true" size="wide" @close="close">
    <div class="pdf-export-body settings-ui">
      <LoadingSkeleton v-if="loading" :rows="6" :label="t.pdf.loading" />
      <ErrorState v-else-if="!state" :title="t.pdf.loadFailed" :text="loadError" @retry="load" />
      <div v-else class="pdf-export-content">
        <p class="st-note">{{ t.pdf.intro }}</p>
        <LiveUpdateNote v-if="liveOff" @refresh="refreshLive" />
        <SettingsSection :title="t.pdf.sectionTitle" icon="file-text">
          <SettingsRow v-slot="{ id, describedBy }" :label="t.pdf.autoSave" :help="t.pdf.autoSaveHelp">
            <Switch :id="id" v-model="draft.enabled" :disabled="saving || running" :aria-describedby="describedBy" data-pdf-enabled />
          </SettingsRow>
          <InlineReveal :open="draft.enabled">
            <SettingsRow :label="t.pdf.storageProfile" :label-for="false" :help="t.pdf.storageProfileHelp" :error="showErrors ? profileError : undefined">
              <OptionPills :model-value="draft.storageProfileId ?? ''" :options="profileOptions" :label="t.pdf.storageProfile" hide-label :disabled="saving || running" data-pdf-profile @update:model-value="draft.storageProfileId = $event || null" />
            </SettingsRow>
            <p v-if="!state.profiles.length" class="st-note">{{ t.pdf.noProfile }}</p>
            <SettingsRow :label="t.pdf.targetFolder" :label-for="false" :help="t.pdf.targetFolderHelp">
              <div class="pdf-folders" data-pdf-folder-browser>
                <nav :aria-label="t.pdf.folderPath" class="pdf-folders-path">
                  <Button variant="ghost" size="sm" :disabled="saving || running || !crumbs.length" :aria-current="crumbs.length ? undefined : 'location'" data-pdf-folder-root @click="open('')"><AtlasIcon name="folder" :size="15" />{{ t.pdf.storageRoot }}</Button>
                  <template v-for="(crumb, index) in crumbs" :key="crumb.path">
                    <span aria-hidden="true">/</span>
                    <Button variant="ghost" size="sm" :disabled="saving || running || index === crumbs.length - 1" :aria-current="index === crumbs.length - 1 ? 'location' : undefined" @click="open(crumb.path)">{{ crumb.name }}</Button>
                  </template>
                </nav>
                <LoadingNote v-if="foldersLoading && !listing" :label="t.pdf.loadingFolders" />
                <p v-else-if="foldersError" class="pdf-export-error" role="alert">{{ foldersError }} <Button variant="link" size="sm" data-pdf-folders-retry @click="loadFolders">{{ t.pdf.retry }}</Button></p>
                <ul v-else-if="listing?.folders.length" class="pdf-folders-list" :aria-label="t.pdf.subfolders">
                  <li v-for="name in listing.folders" :key="name"><Button variant="ghost" size="sm" :disabled="saving || running" :data-pdf-subfolder="name" @click="open(draft.folder ? draft.folder + '/' + name : name)"><AtlasIcon name="folder" :size="15" />{{ name }}</Button></li>
                </ul>
                <p v-else-if="listing" class="st-note">{{ t.pdf.noSubfolders }}</p>
                <p v-if="listing?.truncated" class="st-note">{{ t.pdf.truncated }}</p>
                <form class="pdf-folders-new" @submit.prevent="createFolder">
                  <input v-model="newFolder" class="st-input" maxlength="255" :placeholder="t.pdf.newFolder" :aria-label="t.pdf.newFolder" :disabled="creating || saving || running || !listing" spellcheck="false" data-pdf-new-folder />
                  <Button type="submit" variant="outline" size="sm" :disabled="!newFolder.trim() || creating || saving || running || !listing" :aria-busy="creating" data-pdf-create-folder><AtlasIcon name="folder-plus" :size="15" />{{ creating ? t.pdf.creating : t.pdf.createFolder }}</Button>
                </form>
                <p v-if="createError" class="pdf-export-error" role="alert">{{ createError }}</p>
              </div>
            </SettingsRow>
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.pdf.pdfName" :help="t.pdf.pdfNameHelp" :error="showErrors ? fileError : undefined">
              <input :id="id" v-model="draft.fileName" class="st-input st-mono" maxlength="120" :disabled="saving || running" :aria-describedby="describedBy" :aria-invalid="invalid" spellcheck="false" data-pdf-filename />
            </SettingsRow>
            <p class="st-note" data-pdf-destination>{{ t.pdf.resultPath }} <code class="st-mono">{{ destination || t.pdf.chooseStorageFirst }}</code></p>
            <SettingsSection :title="t.pdf.coverPage" advanced :summary="coverSummary">
              <PdfCoverDetailsField v-model="draft.coverDetails" :title="title" :disabled="saving || running" />
            </SettingsSection>
            <SettingsSection :title="t.pdf.versionsAndPublishing" advanced :summary="versionSummary">
              <SettingsRow v-slot="{ id }" :label="t.pdf.publishedOnly">
                <Switch :id="id" v-model="draft.publishedOnly" :disabled="saving || running" data-pdf-published-only />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" :label="t.pdf.keepVersions" :help="t.pdf.keepVersionsHelp">
                <Switch :id="id" v-model="draft.keepVersions" :disabled="saving || running" :aria-describedby="describedBy" data-pdf-keep-versions />
              </SettingsRow>
            </SettingsSection>
          </InlineReveal>
        </SettingsSection>
        <SaveBar :dirty="dirty" :saving="saving" :error="saveError" :save-label="t.pdf.saveExport" @save="save()" @cancel="discard" />
        <SettingsSection :title="t.pdf.exportStatus" icon="activity">
          <template #actions><Button variant="outline" size="sm" :disabled="dirty || saving || running || !state.settings.enabled" :aria-busy="running" data-pdf-run @click="run">{{ running ? t.pdf.scheduling : state.status === "error" ? t.pdf.retry : t.pdf.exportNow }}</Button></template>
          <StatusBadge :tone="badge.tone" :label="badge.label" :confirmed="exportDone" role="status" />
          <p class="st-note">{{ state.lastExportedAt ? t.pdf.lastExport + formatDateTime(state.lastExportedAt) : t.pdf.noExportYet }}</p>
          <InlineReveal :open="!!(state.lastError || runError || loadError || dirty)">
            <p v-if="state.lastError" class="pdf-export-error" role="alert">{{ state.lastError }}</p>
            <p v-if="runError || loadError" class="pdf-export-error" role="alert">{{ runError || loadError }}</p>
            <p v-if="dirty" class="st-note">{{ t.pdf.saveFirst }}</p>
          </InlineReveal>
        </SettingsSection>
      </div>
    </div>
  </AppDialog>
  <ConfirmDialog :open="!!takeover" :title="t.pdf.takeoverTitle" :confirm-label="t.pdf.takeoverConfirm" :destructive="false" :busy="saving" :error="saveError" @confirm="save(true)" @cancel="takeover = ''">
    <p>{{ takeover }}</p>
    <p>{{ t.pdf.takeoverNote }}</p>
  </ConfirmDialog>
  <UnsavedChangesDialog :open="leaving" :saving="saving" @stay="leaving = false" @discard="discard(); emit('close')" @save="saveAndClose" />
</template>

<style scoped>
.pdf-export-body, .pdf-export-content { display: grid; gap: 14px; min-width: 0; }
/* The loaded settings arrive in place of the loading note: they fade in instead of replacing it at once. */
.pdf-export-content { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.pdf-export-error { color: var(--error); font-size: 13px; }
.pdf-folders { display: grid; gap: 8px; min-width: 0; }
.pdf-folders-path, .pdf-folders-list { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 4px; min-width: 0; }
.pdf-folders-list { margin: 0; padding: 0; list-style: none; }
.pdf-folders-new { display: flex; flex-wrap: wrap; gap: 8px; }
.pdf-folders-new .st-input { flex: 1 1 14rem; min-width: 0; }
[data-pdf-destination] code { overflow-wrap: anywhere; }
/* The state is a badge, not a bar: in the section body (grid or flex column) it stretched across the whole dialog. */
.pdf-export-content :deep(.status-badge) { justify-self: start; align-self: flex-start; }
</style>
