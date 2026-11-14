<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { errorMessage } from "../../../api";
import { logApi as defaultLogApi, type LogApi, type LogEntry } from "../../../logs/logApi";
import { formatAgo, formatDateTime } from "../../../formatDate";
import { isActive, type JobApi, type JobRunDetail } from "../../../jobs/jobApi";
import { jobsTopic, useLiveFeed, type JobRunChange } from "../../../live/liveFeed";
import t from "../../../i18n/settings-jobs";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import { Button } from "../../ui/button";
import ErrorState from "../ErrorState.vue";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import LoadingNote from "../LoadingNote.vue";
import LogList from "../logs/LogList.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import StatusBadge from "../StatusBadge.vue";
import SummaryList from "../SummaryList.vue";
import { useSettingsRoute } from "../useSettingsRoute";
import { durationText, returnFocus, runBadge, summaryEntries, triggerLabel } from "./jobFormat";

/**
 * Správa wiki → Provoz → Úlohy na pozadí → one run (Atlas 3.3 plan §5): its state and times, the numbers the job reported, the error of a
 * failed run (the whole exception folded below its first line) and the server log entries written during the run (LogList, the same list as
 * Systémový log filtered by `jobRunId`; live while the run is queued or running; „Zobrazit log běhu“ on a failed run jumps to it). „Zkusit znovu“ (failed or stopped) and „Zastavit“
 * (queued or running) are here; a retry that the job's queued run takes opens that run. The server tells the page when the run changes
 * (useLiveFeed, jobs feed) and it reads the run again; nothing polls. The
 * state is a live region, and the focus goes to it after an action. `api` is replaced by the gallery.
 */
const props = withDefaults(defineProps<{ api: JobApi; base: string; jobId: string; runId: number; logApi?: LogApi }>(), { logApi: () => defaultLogApi });
const router = useSettingsRoute();

const detail = ref<JobRunDetail>();
const loading = ref(true), error = ref(""), busy = ref("");
const status = ref<HTMLElement>();
const logBox = ref<HTMLElement>();

const run = computed(() => detail.value?.run);
/** Queued or running, also while its job is paused: it can be stopped. */
const going = computed(() => run.value?.state === "queued" || run.value?.state === "running");
const jobPath = computed(() => `${props.base}/${encodeURIComponent(props.jobId)}`);
const back = computed(() => ({ href: router.href(jobPath.value), label: t.run.back }));
// Back to the job, the run that was left is the row the focus returns to. Sync: the route change unmounts this page before a queued watcher would run.
watch(() => router.route.value.path, (path) => { if (path === jobPath.value) returnFocus.id = String(props.runId); }, { flush: "sync" });
const logFilter = computed(() => ({ jobRunId: props.runId }));
const logLive = computed(() => isActive(run.value, detail.value?.paused));
/** The server log of the run; a finished run it has nothing of (from before 3.6, or older than the log keeps, 14 days) shows the log
 * stored with the run instead, in the same list. */
const runLogApi = computed<LogApi>(() => ({
  exportUrl: (filter) => props.logApi.exportUrl(filter),
  async list(filter, cursor, take) {
    const page = await props.logApi.list(filter, cursor, take);
    const stored = detail.value?.log ?? [];
    if (page.entries.length || cursor || logLive.value || !stored.length) return page;
    const entries = stored.map((entry, index) => ({
      id: -1 - index, at: entry.at, level: entry.level.toLowerCase() as LogEntry["level"], category: props.jobId, eventId: null, message: entry.message,
      exceptionType: null, exceptionMessage: null, exception: null, traceId: null, userId: null, jobRunId: props.runId, jobId: props.jobId, jobTitle: null,
    }));
    return { entries: entries.reverse(), more: false, summary: null };
  },
}));
function showLog() {
  logBox.value?.scrollIntoView?.({ block: "start" });
  logBox.value?.focus({ preventScroll: true });
}
const firstLine = (text: string) => text.split("\n", 1)[0]!;
/** A running run that has not sent its heartbeat for 90 s (it sends one every 30 s) may be stuck. */
const silent = computed(() => run.value?.state === "running" && !!run.value.heartbeatAt && Date.now() - new Date(run.value.heartbeatAt).getTime() > 90_000);

async function load() {
  try {
    detail.value = await props.api.run(props.jobId, props.runId);
    error.value = "";
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    loading.value = false;
  }
}
const { unavailable: liveOff, refresh } = useLiveFeed<JobRunChange>(jobsTopic, load, (change) => change.runId === props.runId);
/** `work` answers the run it acted on; another one than this (a retry the job's queued run took) is opened instead. */
async function act(name: string, work: () => Promise<{ runId: number }>) {
  busy.value = name;
  try {
    const { runId } = await work();
    if (runId !== props.runId) {
      router.navigate(`${jobPath.value}/runs/${runId}`);
      return;
    }
    await load();
    await nextTick();
    status.value?.focus();
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}
const retry = () => act("retry", () => props.api.retry(props.jobId, props.runId));
const cancel = () => act("cancel", () => props.api.cancel(props.jobId, props.runId));

const items = computed(() => {
  const current = run.value;
  if (!current) return [];
  const rows = [
    { key: "state", icon: "activity", label: t.run.state, value: runBadge(current.state).label },
    { key: "trigger", icon: "refresh", label: t.run.trigger, value: triggerLabel(current.trigger) },
    ...(current.reason ? [{ key: "reason", icon: "info", label: t.run.reason, value: current.reason }] : []),
    { key: "queued", icon: "clock", label: t.run.queued, value: formatDateTime(current.queuedAt) },
    ...(current.startedAt ? [{ key: "started", icon: "clock", label: t.run.started, value: formatDateTime(current.startedAt) }] : []),
    ...(current.finishedAt ? [{ key: "finished", icon: "circle-check", label: t.run.finished, value: formatDateTime(current.finishedAt) }] : []),
    ...(current.startedAt ? [{ key: "duration", icon: "hourglass", label: current.finishedAt ? t.run.took : t.run.runningFor, value: durationText(current) }] : []),
    { key: "attempt", icon: "refresh", label: t.run.attempt, value: String(current.attempt || 1) },
  ];
  if (current.state === "running" && current.heartbeatAt) {
    rows.push({ key: "heartbeat", icon: "activity", label: silent.value ? t.run.silentSince : t.run.lastHeard, value: silent.value ? formatDateTime(current.heartbeatAt) : formatAgo(current.heartbeatAt) });
  }
  return rows;
});

onMounted(() => void load());
</script>

<template>
  <SettingsPage :title="t.run.title(runId)" icon="clock" :description="run ? (run.reason ?? triggerLabel(run.trigger)) : undefined" :back="back">
    <!-- The line the description takes holds its room while the run loads. -->
    <template v-if="!run && !error" #meta><LoadingNote :label="t.run.loading" /></template>
    <template v-if="run && (going || run.state === 'cancelled')" #actions>
      <Button v-if="going" variant="outline" type="button" :disabled="busy === 'cancel' || run.cancelRequested" :aria-busy="busy === 'cancel'" data-run-cancel @click="cancel"><AtlasIcon name="close" :size="16" />{{ run.cancelRequested ? t.run.stopping : t.run.stop }}</Button>
      <Button v-else variant="outline" type="button" :disabled="busy === 'retry'" :aria-busy="busy === 'retry'" data-run-retry @click="retry"><AtlasIcon name="refresh" :size="16" />{{ t.retry }}</Button>
    </template>
    <ErrorState v-if="error" :title="run ? t.run.actionFailed : t.run.loadFailed" :text="error" @retry="load" />
    <LiveUpdateNote v-if="liveOff" @refresh="refresh" />
    <LoadingRegion :loading="loading" :ready="!!run || !!error" :rows="3" row-height="56px" :label="t.run.loading">
      <div v-if="run" class="job-stack">
        <SummaryList :items="items" :label="t.run.label" data-run-summary>
          <template #value-state>
            <span ref="status" class="run-status" role="status" tabindex="-1" data-run-status><Transition name="st-fade" mode="out-in"><StatusBadge :key="run.state" :label="runBadge(run.state).label" :tone="runBadge(run.state).tone" :icon="runBadge(run.state).icon" size="sm" /></Transition></span>
            <StatusBadge v-if="silent" :label="t.run.silent" tone="warning" size="sm" />
          </template>
        </SummaryList>
        <div v-if="summaryEntries(run).length" class="run-tiles" data-run-tiles>
          <StatTile v-for="entry in summaryEntries(run)" :key="entry.key" :label="entry.label" :value="entry.value" />
        </div>
        <ErrorState v-if="run.state === 'failed'" :title="t.run.failedTitle" :text="detail!.error ? firstLine(detail!.error) : t.run.noError" :retry-label="t.retry" :busy="busy === 'retry'" data-run-error @retry="retry">
          <details v-if="detail!.error && detail!.error.includes('\n')" class="run-stack"><summary>{{ t.run.fullError }}</summary><pre>{{ detail!.error }}</pre></details>
          <button type="button" class="st-link run-log-link" data-run-log-link @click="showLog">{{ t.run.showLog }}</button>
        </ErrorState>
        <p v-else-if="detail!.error" class="st-note" data-run-error>{{ t.run.lastAttemptFailed(firstLine(detail!.error)) }}</p>
        <div ref="logBox" class="run-log-box" tabindex="-1" data-run-log>
          <SettingsSection :title="t.run.log" icon="file-text"><LogList :api="runLogApi" :filter="logFilter" :live="logLive" /></SettingsSection>
        </div>
      </div>
    </LoadingRegion>
  </SettingsPage>
</template>

<style scoped>
.job-stack { display: grid; gap: 18px; min-width: 0; }
.run-log-box { min-width: 0; }
.run-log-box:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.run-log-link { background: none; cursor: pointer; font-size: 13px; }
.run-status { display: inline-flex; border-radius: 7px; }
.run-status:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.run-tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 10px; animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out); }
.run-stack summary { cursor: pointer; font-size: 13px; }
.run-stack pre { margin: 8px 0 0; max-height: 320px; overflow: auto; font-size: 12px; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
