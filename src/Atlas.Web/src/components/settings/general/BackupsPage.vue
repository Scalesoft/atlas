<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { api, errorMessage, post } from "../../../api";
import type { StorageProfile } from "../../../storageProfiles";
import { formatDateTime } from "../../../formatDate";
import { formatNumber } from "../../../i18n";
import t from "../../../i18n/settings-backups";
import { jobsTopic, useLiveFeed, type JobRunChange } from "../../../live/liveFeed";
import ErrorState from "../ErrorState.vue";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsTabs from "../SettingsTabs.vue";
import StatusBadge from "../StatusBadge.vue";
import TileCard from "../TileCard.vue";
import { useSettingsContext } from "../context";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import type { SettingsTab } from "../types";
import ArchiveBackupPanel from "./ArchiveBackupPanel.vue";
import BackupImportSection from "./BackupImportSection.vue";
import { disclosureHooks } from "../disclosure";
import GitBackupPanel from "./GitBackupPanel.vue";
import InstallationRestoreSection from "./InstallationRestoreSection.vue";
import { runBadge } from "../jobs/jobFormat";
import { archiveEndpoint, archiveState, archiveStateBadges, destinationLabel, intervalLabel, jobRunPath, type BackupJobRun, type BackupState } from "./backups";
import { gitBackupEndpoint, gitBackupState, gitStateBadges, shortSha, type GitBackupStatus } from "./gitBackup";

/**
 * Zálohy (`general/backups[/git|/restore]`): every way the wiki is backed up on one page. On top the
 * two ways side by side, Archivy ZIP (full snapshots to restore from) and Git (a commit after every
 * change), each with its state and its manual run; below, as SettingsTabs, the settings of each and
 * the restore from an archive. The page owns the server state of both: loaded together, read again
 * when the window regains focus and after its own action. The page paints at once: the cards and tabs stand,
 * sized skeletons hold their facts until the states arrive. The ZIP job's last run is read on its own (it
 * never holds the first paint) and polled only while it is queued or running; once it ends the archive
 * state is read again. Nothing else is polled (owner decision). Each panel
 * owns its draft and SaveBar; leaving a tab with unsaved changes asks like leaving any settings page.
 * The shell's badge gets the archive warning first, then the git one, as `GET /admin/backups` does.
 */
type BackupTab = "archives" | "git" | "restore";
const props = defineProps<{ part?: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const flash = useSettingsFlash();

const tab = computed<BackupTab>(() => (props.part === "git" || props.part === "restore" ? props.part : "archives"));
const tabs = computed<SettingsTab[]>(() => [
  { id: "archives", label: t.page.tabs.archives, icon: "folder", href: router.href("general/backups") },
  { id: "git", label: t.page.tabs.git, icon: "git-branch", href: router.href("general/backups/git") },
  { id: "restore", label: t.page.tabs.restore, icon: "history", href: router.href("general/backups/restore") },
]);

const profiles = ref<StorageProfile[]>([]), profilesError = ref("");
const archive = ref<BackupState>();
const git = ref<GitBackupStatus>();
const archiveError = ref("");
const gitError = ref("");
const loading = ref(true);
const refreshError = ref("");
const busy = ref<"" | "run" | "sync">("");
const runError = ref("");
const syncError = ref("");
const archiveDirty = ref(false);
const gitDirty = ref(false);
let version = 0;
let disposed = false;

async function read<T>(path: string): Promise<{ value?: T; error?: string }> {
  try {
    return { value: await api<T>(path) };
  } catch (failure) {
    return { error: errorMessage(failure) };
  }
}
/** Both states at once. A part shown before keeps its state when it cannot be read again, and says so. */
async function readBoth() {
  const current = ++version;
  const [archiveResult, gitResult, profileResult] = await Promise.all([read<BackupState>(archiveEndpoint), read<GitBackupStatus>(gitBackupEndpoint), read<StorageProfile[]>("/storage-profiles")]);
  if (disposed || current !== version) return;
  if (Array.isArray(profileResult.value)) profiles.value = profileResult.value;
  profilesError.value = profileResult.error ?? "";
  if (archiveResult.value) archive.value = archiveResult.value;
  if (gitResult.value) git.value = gitResult.value;
  archiveError.value = archive.value ? "" : (archiveResult.error ?? "");
  gitError.value = git.value ? "" : (gitResult.error ?? "");
  refreshError.value = (archive.value && archiveResult.error) || (git.value && gitResult.error) || "";
  report();
}
/** Opened on Git or Obnovení by its address: the tabs come to the top, not two screens of summary cards before them. */
const tabsAnchor = ref<HTMLElement>();
let landed = false;
async function load() {
  loading.value = true;
  await readBoth();
  if (disposed) return;
  loading.value = false;
  if (!landed && props.part) {
    landed = true;
    await nextTick();
    tabsAnchor.value?.scrollIntoView({ block: "start" });
  }
}
function refresh() {
  if (!loading.value && !busy.value) void readBoth();
  void readLastRun();
}
/** The archive state again, after an own action: its warning also carries the git backup's (see `report`). */
async function readArchive() {
  const current = ++version;
  const result = await read<BackupState>(archiveEndpoint);
  if (disposed || current !== version) return;
  if (result.value) archive.value = result.value;
  refreshError.value = result.error && archive.value ? result.error : "";
  report();
}

/*
 * `GET /admin/backups` answers the archives' own warning, else the git backup's. The overview tells
 * them apart by the git state read with it; the shell's badge gets the same order.
 */
const archiveWarning = computed(() => {
  const warning = archive.value?.warning ?? null;
  return warning && warning !== git.value?.warning ? warning : null;
});
function report() {
  if (archive.value) context.warning(archiveWarning.value ?? git.value?.warning ?? null);
}
/** A panel saved, generated a key or read the state: take it over and drop older reads still on their way. */
function archiveChanged(result: BackupState) {
  version++;
  archive.value = result;
  report();
}
function gitChanged(result: GitBackupStatus) {
  version++;
  git.value = result;
  report();
  // The archive warning read before may still carry the git backup's former one.
  void readArchive();
}
async function readGit() {
  const current = ++version;
  const result = await read<GitBackupStatus>(gitBackupEndpoint);
  if (disposed || current !== version) return;
  if (result.value) gitChanged(result.value);
  else if (result.error) refreshError.value = result.error;
}

// The ZIP job's last run (undefined until read, null before the first run); its log and history are on the jobs page.
const lastRun = ref<BackupJobRun | null>();
const lastRunFailed = ref(false);
const live = (run: BackupJobRun | null | undefined) => run?.state === "running" || run?.state === "queued";
/** Read on its own and again whenever the server says a run of the ZIP job changed (useLiveFeed; nothing polls); a run that ends brings the
 * new archive and warning. A failed read keeps what is shown. */
async function readLastRun() {
  const before = lastRun.value;
  try {
    const next = (await api<BackupJobRun | undefined>(`${archiveEndpoint}/run`)) ?? null;
    if (disposed) return;
    lastRun.value = next;
    lastRunFailed.value = false;
    if (live(before) && !live(next)) void readArchive();
  } catch {
    if (disposed) return;
    lastRunFailed.value = true;
  }
}
const { unavailable: liveOff, refresh: refreshLive } = useLiveFeed<JobRunChange>(jobsTopic, readLastRun, (change) => change.jobId === "backups.zip");

// Archivy ZIP at a glance.
const archiveDisplayState = computed(() => (archive.value ? archiveState(archive.value, archiveWarning.value, git.value?.settings.enabled === true, lastRun.value?.state) : undefined));
const archiveBadge = computed(() => (archiveDisplayState.value ? archiveStateBadges()[archiveDisplayState.value] : undefined));
const savedDestination = computed(() => archive.value?.settings.destination.trim() ?? "");
const runBlocked = computed(() => {
  if (!archive.value) return t.page.loading;
  if (archiveDirty.value) return t.page.archive.runBlockedDirty;
  if (!archive.value.settings.storageProfileId && !savedDestination.value) return t.page.archive.runBlockedNoDestination;
  return undefined;
});
async function runArchive() {
  if (busy.value || runBlocked.value) return;
  busy.value = "run";
  runError.value = "";
  try {
    await post<{ runId: number }>(`${archiveEndpoint}/run`);
    await Promise.all([readArchive(), readLastRun()]);
    if (!disposed) flash.notify(t.page.archive.started, { icon: "download" });
  } catch (failure) {
    if (!disposed) runError.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}

// Git at a glance. Our own sync is running while its call is open, even though the last state said otherwise.
const gitDisplayState = computed(() => (busy.value === "sync" ? "running" : git.value ? gitBackupState(git.value) : "disabled"));
const gitBadge = computed(() => gitStateBadges()[gitDisplayState.value]);
const gitStatusLine = computed(() => {
  const current = git.value;
  if (!current) return "";
  switch (gitDisplayState.value) {
    case "running":
      return current.progress && busy.value !== "sync" ? t.page.git.progress(formatNumber(current.progress.done), formatNumber(current.progress.total)) : t.page.git.writing;
    case "disabled":
      return current.settings.remoteUrl ? t.page.git.turnOn : t.page.git.setUpAndTurnOn;
    case "pending":
      return current.lastSyncAt ? t.page.git.publishWritten : t.page.git.firstSync;
    case "ok":
      return t.page.git.allWritten;
    default:
      return "";
  }
});
const syncBlocked = computed(() => {
  const saved = git.value?.settings;
  if (!saved) return t.page.loading;
  if (gitDirty.value) return t.page.git.syncBlockedDirty;
  if (!saved.publicKey) return t.page.git.syncBlockedNoKey;
  if (!saved.enabled || !saved.remoteUrl) return t.page.git.syncBlockedNotSet;
  if (git.value?.running) return t.page.git.syncBlockedRunning;
  return undefined;
});
async function syncGit() {
  if (busy.value || syncBlocked.value) return;
  busy.value = "sync";
  syncError.value = "";
  version++;
  try {
    const result = await post<GitBackupStatus>(`${gitBackupEndpoint}/sync`);
    if (disposed) return;
    gitChanged(result);
    if (!result.lastError) flash.notify(result.lastCommit ? t.page.git.syncDoneCommit(shortSha(result.lastCommit)) : t.page.git.syncDone, { icon: "circle-check" });
  } catch (failure) {
    if (!disposed) syncError.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}

function focused() {
  refresh();
}
onMounted(() => {
  void load();
  void readLastRun();
  window.addEventListener("focus", focused);
});
onBeforeUnmount(() => {
  disposed = true;
  version++;
  window.removeEventListener("focus", focused);
});
</script>

<template>
  <SettingsPage :title="t.page.title" :description="t.page.description">
    <ErrorState v-if="!loading && !archive && !git" :title="t.page.loadError" :text="archiveError || gitError" @retry="load" />
    <template v-else>
      <section class="backup-methods" :aria-label="t.page.methodsLabel">
        <TileCard
          :title="t.page.archive.title"
          :description="t.page.archive.description"
          icon="folder"
          class="backup-method"
          data-backup-method="archives"
          :data-state="archiveDisplayState"
        >
          <template #badge><StatusBadge v-if="archiveBadge" :tone="archiveBadge.tone" :icon="archiveBadge.icon" :label="archiveBadge.label" size="sm" data-backup-state /></template>
          <p v-if="!archive && archiveError" class="backup-method-alert is-error" role="alert" data-backup-load-error><AtlasIcon name="circle-x" :size="15" />{{ t.page.archive.loadError(archiveError) }}</p>
          <LoadingRegion v-else :loading="loading" :ready="!!archive" :rows="2" row-height="52px" :label="t.page.archive.loading">
          <template v-if="archive">
            <dl class="backup-facts">
              <div data-stat="last">
                <dt>{{ t.page.archive.lastBackup }}</dt>
                <dd>{{ archive.lastSuccessAt ? formatDateTime(archive.lastSuccessAt) : t.page.none }}</dd>
              </div>
              <div data-stat="run">
                <dt>{{ t.page.archive.lastRun }}</dt>
                <dd>
                  <LoadingNote v-if="lastRun === undefined && !lastRunFailed" inline :label="t.page.archive.lastRunLoading" />
                  <!-- A new state cross-fades in place; the row keeps its room while it loads. -->
                  <Transition v-else name="st-fade" mode="out-in">
                    <span v-if="lastRun" :key="`${lastRun.id}:${lastRun.state}`" class="backup-run" :data-run-state="lastRun.state" data-backup-last-run>
                      <StatusBadge v-bind="runBadge(lastRun.state)" size="sm" />
                      <span>{{ formatDateTime(lastRun.at) }}<template v-if="lastRun.message"> · {{ lastRun.message }}</template></span>
                      <a class="st-link" :href="router.href(jobRunPath(lastRun))" data-backup-run-detail>{{ t.page.archive.lastRunDetail }}</a>
                    </span>
                    <span v-else :key="lastRunFailed ? 'failed' : 'none'">{{ lastRunFailed ? t.page.archive.lastRunFailed : t.page.archive.lastRunNone }}</span>
                  </Transition>
                  <LiveUpdateNote v-if="liveOff && live(lastRun)" @refresh="refreshLive" />
                </dd>
              </div>
              <div data-stat="next">
                <dt>{{ t.page.archive.nextBackup }}</dt>
                <dd>{{ archive.nextRunAt ? `${formatDateTime(archive.nextRunAt)} · ${intervalLabel(archive.settings.intervalMinutes)}` : archive.settings.enabled ? t.page.archive.willBeScheduled : t.page.archive.manualOnly }}</dd>
              </div>
              <div data-stat="destination">
                <dt>{{ t.page.archive.destination }}</dt>
                <dd>{{ archive.settings.storageProfileId ? profiles.find(profile => profile.id === archive!.settings.storageProfileId)?.name ?? t.page.archive.storageProfile : destinationLabel(savedDestination) }}<code v-if="savedDestination && !archive.settings.storageProfileId" class="st-mono backup-fact-code" :title="savedDestination">{{ savedDestination }}</code></dd>
              </div>
            </dl>
            <Transition :css="false" v-bind="disclosureHooks">
              <p v-if="archiveWarning" class="backup-method-alert" role="alert" data-backup-warning><AtlasIcon name="triangle-alert" :size="15" />{{ archiveWarning }}</p>
            </Transition>
            <Transition :css="false" v-bind="disclosureHooks">
              <p v-if="runError" class="backup-method-alert is-error" role="alert" data-backup-run-error><AtlasIcon name="circle-x" :size="15" />{{ runError }}</p>
            </Transition>
          </template>
          </LoadingRegion>
          <template #footer>
            <Button type="button" size="sm" :disabled="!!busy || !!runBlocked" :title="runBlocked" :aria-busy="busy === 'run'" data-backup-run @click="runArchive">
              <AtlasIcon :name="busy === 'run' ? 'loader-circle' : 'download'" :size="15" :class="{ 'atlas-spinner': busy === 'run' }" />{{ t.page.archive.runNow }}
            </Button>
          </template>
        </TileCard>

        <TileCard
          :title="t.page.git.title"
          :description="t.page.git.description"
          icon="git-branch"
          class="backup-method"
          data-backup-method="git"
          :data-state="git ? gitDisplayState : undefined"
          data-git-status
        >
          <template #badge><StatusBadge v-if="git" :tone="gitBadge.tone" :icon="gitBadge.icon" :label="gitBadge.label" size="sm" data-backup-state /></template>
          <p v-if="!git && gitError" class="backup-method-alert is-error" role="alert" data-git-load-error><AtlasIcon name="circle-x" :size="15" />{{ t.page.git.loadError(gitError) }}</p>
          <LoadingRegion v-else :loading="loading" :ready="!!git" :rows="2" row-height="52px" :label="t.page.git.loading">
          <template v-if="git">
            <dl class="backup-facts">
              <div data-stat="last-sync">
                <dt>{{ t.page.git.lastSync }}</dt>
                <dd>
                  {{ git.lastSyncAt ? formatDateTime(git.lastSyncAt) : t.page.none }}
                  <template v-if="git.lastCommit"><code class="st-mono backup-fact-code" :title="git.lastCommit" data-git-last-commit>{{ shortSha(git.lastCommit) }}</code></template>
                </dd>
              </div>
              <div data-stat="pending">
                <dt>{{ t.page.git.pending }}</dt>
                <dd>{{ t.page.git.pages(git.pendingPages) }}</dd>
              </div>
              <div data-stat="repository">
                <dt>{{ t.page.git.repository }}</dt>
                <dd><code v-if="git.settings.remoteUrl" class="st-mono backup-fact-code" :title="git.settings.remoteUrl">{{ git.settings.remoteUrl }} · {{ git.settings.branch }}</code><template v-else>{{ t.page.notSet }}</template></dd>
              </div>
            </dl>
            <p v-if="gitDisplayState === 'failed'" class="backup-method-alert is-error" data-git-failure>
              <AtlasIcon name="circle-x" :size="15" /><span><span role="alert" data-git-error>{{ git.lastError }}</span> {{ t.page.git.nextAttempt }}</span>
            </p>
            <p v-else-if="git.warning" class="backup-method-alert" role="alert" data-git-warning><AtlasIcon name="triangle-alert" :size="15" />{{ git.warning }}</p>
            <p v-else class="backup-method-note" data-git-note>{{ gitStatusLine }}</p>
            <Transition :css="false" v-bind="disclosureHooks">
              <p v-if="syncError" class="backup-method-alert is-error" role="alert" data-git-action-error><AtlasIcon name="circle-x" :size="15" />{{ syncError }}</p>
            </Transition>
          </template>
          </LoadingRegion>
          <template #footer>
            <Button type="button" size="sm" variant="outline" :disabled="!!busy || !!syncBlocked" :title="syncBlocked" :aria-busy="busy === 'sync'" data-git-sync @click="syncGit">
              <AtlasIcon :name="busy === 'sync' ? 'loader-circle' : 'refresh'" :size="15" :class="{ 'atlas-spinner': busy === 'sync' }" />{{ t.page.git.syncNow }}
            </Button>
          </template>
        </TileCard>
      </section>
      <p v-if="refreshError" class="st-note" role="status"><AtlasIcon name="triangle-alert" :size="15" />{{ t.page.refreshError(refreshError) }}</p>

      <div ref="tabsAnchor" class="backup-tabs-anchor"><SettingsTabs :options="tabs" :model-value="tab" :label="t.page.tabsLabel" /></div>

      <!-- The part leaves and the next rises in, like the tabs of the AI module and of Přihlašování. -->
      <Transition name="st-step" mode="out-in">
        <div :key="tab" class="backup-part">
          <template v-if="tab === 'archives'">
            <ArchiveBackupPanel v-if="archive" :state="archive" :profiles="profiles" :profiles-error="profilesError" @changed="archiveChanged" @dirty="archiveDirty = $event" />
            <ErrorState v-else-if="archiveError" :title="t.page.archive.settingsLoadError" :text="archiveError" @retry="load" />
            <LoadingSkeleton v-else :label="t.page.archive.settingsLoading" />
          </template>
          <template v-else-if="tab === 'git'">
            <GitBackupPanel v-if="git" :state="git" @changed="gitChanged" @dirty="gitDirty = $event" @refresh="readGit" />
            <ErrorState v-else-if="gitError" :title="t.page.git.settingsLoadError" :text="gitError" @retry="load" />
            <LoadingSkeleton v-else :label="t.page.git.settingsLoading" />
          </template>
          <div v-else class="backup-restore">
            <InstallationRestoreSection source="zip" />
            <InstallationRestoreSection source="git" :configured="!!git?.settings.remoteUrl && !!git.settings.publicKey" />
            <BackupImportSection @imported="context.imported" />
          </div>
        </div>
      </Transition>
    </template>
  </SettingsPage>
</template>

<style scoped>
.backup-part { min-width: 0; }
.backup-tabs-anchor { scroll-margin-top: 12px; }
.backup-restore { display: grid; gap: 18px; }
.backup-methods { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 320px), 1fr)); gap: 12px; }
.backup-method { gap: 12px; }
/* Facts as label and value rows; the value wraps, a long path or address stays on its own line. */
.backup-facts { display: grid; gap: 8px; margin: 0; container: backup-facts / inline-size; }
.backup-facts > div { display: grid; grid-template-columns: 138px minmax(0, 1fr); gap: 2px 12px; align-items: baseline; }
.backup-facts dt { color: var(--muted); font-size: 12px; font-weight: 600; }
.backup-facts dd { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; margin: 0; min-width: 0; color: var(--text); font-size: 13px; }
/* The last run: badge, time and message wrap as one value; a long message breaks instead of widening the card. */
.backup-run { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-width: 0; overflow-wrap: anywhere; }
.backup-fact-code { max-width: 100%; overflow: hidden; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.backup-method-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.backup-method-alert { display: flex; align-items: flex-start; gap: 6px; margin: 0; color: var(--text); font-size: 12.5px; line-height: 1.5; }
.backup-method-alert > svg { flex-shrink: 0; margin-top: 2px; color: var(--warning); }
.backup-method-alert.is-error { color: var(--error); }
.backup-method-alert.is-error > svg { color: var(--error); }
/* The run buttons sit at the bottom, so they line up across both cards. */
.backup-method :deep(.tile-card-footer) { justify-content: flex-start; padding-top: 4px; }
/* The card body is a column of flex items, not a grid: the warning and error notes unfold and give the gap above them back (settings/disclosure.ts). */
.backup-method :deep(.tile-card-body), .backup-method :deep(.loading-region-content) { display: flex; flex-direction: column; }
/* The LoadingRegion around the facts is the card body's only child: its content is the column that keeps the body's 6 px gap between facts and notes. */
.backup-method :deep(.loading-region-content) { gap: 6px; }
/* The label stacks over its value when the card is narrow (two cards squeezed side by side, a phone): the card's own width decides, not the screen's. */
@container backup-facts (max-width: 360px) { .backup-facts > div { grid-template-columns: 1fr; } }
</style>
