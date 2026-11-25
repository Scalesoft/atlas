<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { errorMessage } from "../../../api";
import { rowKeys } from "../../../keyboard/navigation";
import { formatDateTime } from "../../../formatDate";
import type { JobApi, JobRun, JobRunList } from "../../../jobs/jobApi";
import { jobsTopic, useLiveFeed, type JobRunChange } from "../../../live/liveFeed";
import t from "../../../i18n/settings-jobs";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import SummaryList from "../SummaryList.vue";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import { durationText, focusReturned, jobHeads, nextText, pausedBadge, rememberJob, returnFocus, runBadge, runMoment, summaryText, triggerLabel } from "./jobFormat";
import LoadMoreButton from "../../LoadMoreButton.vue";

/**
 * Správa wiki → Provoz → Úlohy na pozadí → one job (Atlas 3.3 plan §5): what starts it, when it runs next, how it is tried again, whether it is
 * paused (one switch for every trigger, shown at once and put back if the server refuses), and the history of its runs, newest first, each
 * opening its detail. The server tells the page when a run of the job changes (useLiveFeed, jobs feed): nothing polls.
 * „Zpět na úlohy“ gives the focus back to the row the detail was opened from, and coming back from a run to that run's row. `api` is replaced by
 * the gallery.
 */
const props = defineProps<{ api: JobApi; base: string; jobId: string }>();
const router = useSettingsRoute();

const page = ref<JobRunList>();
const runs = ref<JobRun[]>([]);
const loading = ref(true), error = ref(""), busy = ref(""), more = ref(false);
const root = ref<HTMLElement>();
/** The switch's own value: it moves when touched and is put back when the server refuses. */
const paused = ref(false);
const flash = useSettingsFlash();

const job = computed(() => page.value?.job);
/** The heading: the job's own answer, until it comes what the list (or an earlier visit) showed of it. */
const head = computed(() => job.value ?? jobHeads.get(props.jobId));
const back = computed(() => ({ href: router.href(props.base), label: t.detail.back }));
watch(job, (current) => { if (current) paused.value = current.paused; }, { immediate: true });
// Back to the list, this job is the row the focus returns to. Sync: the route change unmounts this page before a queued watcher would run.
watch(() => router.route.value.path, (path) => { if (path === props.base) returnFocus.id = props.jobId; }, { flush: "sync" });
const runPath = (run: JobRun) => `${props.base}/${encodeURIComponent(props.jobId)}/runs/${run.id}`;

/** The first page replaces what it covers; the older pages already fetched stay below it. */
function merge(fresh: JobRunList) {
  const ids = new Set(fresh.runs.map((run) => run.id)), oldest = Math.min(...fresh.runs.map((run) => run.id));
  runs.value = [...fresh.runs, ...runs.value.filter((run) => !ids.has(run.id) && run.id < oldest)];
  page.value = fresh;
  rememberJob(fresh.job);
}
async function load() {
  try {
    merge(await props.api.runs(props.jobId, 1));
    error.value = "";
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    loading.value = false;
  }
}
const { unavailable: liveOff, refresh } = useLiveFeed<JobRunChange>(jobsTopic, load, (change) => change.jobId === props.jobId);
async function older() {
  more.value = true;
  try {
    const next = await props.api.runs(props.jobId, Math.floor(runs.value.length / (page.value?.pageSize || 20)) + 1);
    const ids = new Set(runs.value.map((run) => run.id));
    runs.value = [...runs.value, ...next.runs.filter((run) => !ids.has(run.id))];
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    more.value = false;
  }
}
/** True when the server did it. */
async function act(name: string, work: () => Promise<unknown>, text: string) {
  busy.value = name;
  try {
    await work();
    flash.notify(text);
    await load();
    return true;
  } catch (failure) {
    error.value = errorMessage(failure);
    return false;
  } finally {
    busy.value = "";
  }
}
const runNow = () => act("run", () => props.api.runNow(props.jobId), job.value?.paused ? t.detail.runExplicit : t.detail.runQueued);
async function setPaused(value: boolean) {
  paused.value = value;
  const done = await act("pause", () => props.api.setPaused(props.jobId, value), value ? t.detail.paused : t.detail.resumed);
  if (!done) paused.value = !value;
}

const items = computed(() => {
  const current = job.value;
  if (!current) return [];
  return [
    { key: "schedule", icon: "clock", label: t.detail.triggers, value: current.schedule },
    { key: "next", icon: "calendar", label: t.detail.nextRun, value: nextText(current, undefined, true) },
    { key: "retry", icon: "refresh", label: t.detail.retries, value: current.retry },
    { key: "success", icon: "circle-check", label: t.detail.lastSuccess, value: page.value?.lastSuccessAt ? formatDateTime(page.value.lastSuccessAt) : t.detail.noSuccess },
  ];
});

onMounted(async () => {
  await load();
  await nextTick();
  focusReturned(root.value, (id) => `[data-job-run="${id}"]`);
});
</script>

<template>
  <SettingsPage :title="head?.title ?? t.detail.fallbackTitle" icon="clock" :description="head?.description" :back="back">
    <!-- Opened from the list the heading stands at once; opened by its address, the line the description takes and the button hold their room. -->
    <template v-if="!head && !error" #meta><LoadingNote :label="t.detail.loading" /></template>
    <template v-if="!head || head.known" #actions>
      <Button variant="outline" type="button" :disabled="!job || busy === 'run'" :aria-busy="busy === 'run'" data-job-run-now @click="runNow"><AtlasIcon name="refresh" :size="16" />{{ t.runNow }}</Button>
    </template>
    <ErrorState v-if="error" :title="job ? t.detail.actionFailed : t.detail.loadFailed" :text="error" @retry="load" />
    <LiveUpdateNote v-if="liveOff" @refresh="refresh" />
    <LoadingRegion :loading="loading" :ready="!!job || !!error" :rows="3" row-height="56px" :label="t.detail.loading">
      <div v-if="job" ref="root" class="job-stack">
        <SummaryList :items="items" :label="t.detail.label" data-job-summary>
          <template #value-next><span>{{ items[1]!.value }}</span><StatusBadge v-if="job.paused" v-bind="pausedBadge()" size="sm" /></template>
        </SummaryList>
        <SettingsSection v-if="job.known" :title="t.detail.pauseSection" icon="ban">
          <SettingsRow v-slot="{ id, describedBy }" :label="t.detail.pauseJob" :help="t.detail.pauseHelp">
            <Switch :id="id" :model-value="paused" :aria-describedby="describedBy" :disabled="busy === 'pause'" data-job-paused @update:model-value="setPaused($event)" />
          </SettingsRow>
        </SettingsSection>
        <SettingsSection :title="t.detail.history" icon="history" :count="page?.total">
          <SettingsList :label="t.detail.runsLabel" @keydown="rowKeys($event, 'li')">
            <SettingsListRow v-for="run in runs" :key="run.id" :title="triggerLabel(run.trigger)" :data-job-run="run.id">
              <template #title><a class="st-link" :href="router.href(runPath(run))">{{ triggerLabel(run.trigger) }}{{ run.reason ? ` · ${run.reason}` : "" }}</a></template>
              <template #description>
                {{ runMoment(run) }}<template v-if="durationText(run)"> · {{ t.detail.tookFor(durationText(run)) }}</template><template v-if="run.attempt > 1"> · {{ t.detail.attempt(run.attempt) }}</template>
                <span v-if="summaryText(run)" class="job-run-summary">{{ summaryText(run) }}</span>
              </template>
              <template #meta><Transition name="st-fade" mode="out-in"><StatusBadge :key="run.state" :label="runBadge(run.state).label" :tone="runBadge(run.state).tone" :icon="runBadge(run.state).icon" size="sm" /></Transition></template>
            </SettingsListRow>
          </SettingsList>
          <p v-if="!runs.length" class="st-note">{{ t.detail.neverRan }}<template v-if="job.known">{{ " " + t.detail.runNowHint }}</template></p>
          <div v-if="page && runs.length < page.total" class="job-older"><LoadMoreButton older :busy="more" data-job-older @click="older" /></div>
        </SettingsSection>
      </div>
    </LoadingRegion>
  </SettingsPage>
</template>

<style scoped>
.job-stack { display: grid; gap: 18px; min-width: 0; }
.job-run-summary { display: block; }
.job-older { margin-top: 10px; }
</style>
