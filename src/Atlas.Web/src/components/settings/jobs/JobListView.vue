<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from "vue";
import { errorMessage } from "../../../api";
import { rowKeys } from "../../../keyboard/navigation";
import { useSearchKeys } from "../../../lib/searchKeys";
import { formatAgo, formatDateTime } from "../../../formatDate";
import type { Job, JobApi, JobList } from "../../../jobs/jobApi";
import { jobsTopic, useLiveFeed } from "../../../live/liveFeed";
import t from "../../../i18n/settings-jobs";
import LiveUpdateNote from "../LiveUpdateNote.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import FilterMenu from "../FilterMenu.vue";
import InlineReveal from "../InlineReveal.vue";
import SearchField from "../SearchField.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import type { ChoiceOption } from "../types";
import { useSettingsFlash } from "../flash";
import { useSettingsRoute } from "../useSettingsRoute";
import JobRow from "./JobRow.vue";
import JobSummaryTiles from "./JobSummaryTiles.vue";
import { failuresSince, focusReturned, groupJobs, matchesJob, rememberJob, returnFocus, runBadge, serviceBadge, type JobFilters } from "./jobFormat";

/**
 * Správa wiki → Provoz → Úlohy na pozadí (Atlas 3.3 plan §5, 3.6 overview): the summary on top (JobSummaryTiles, focused on arrival), the
 * filters Stav, Druh and Kdy beside the search (kept in the URL: `stav`, `druh`, `kdy`, so a tile is a link), with Stav Selhalo the failed
 * runs of the period with their error, then the recurring jobs and the one-off ones (a migration's conversion, an import, an event's or a
 * hand's run): those going or failed shown, those done or idle folded (unfolded while a filter or the search is on). Each section puts
 * what needs a look first; a row is JobRow. The system services below, read only. The title opens the job (Enter on a focused row does), ↑/↓ walk the rows, the row's gestures
 * (Ctrl+mezerník, Shift+F10…) offer Pozastavit/Obnovit and the history; typing in the filter preselects the first match (`data-first`), which
 * Enter takes (one typed while the list loads waits for it), ↓ goes on to the rows and the first Esc empties the filter (P3, P8). The server tells
 * the page when a run changes (useLiveFeed, jobs feed) and it reads the list again; nothing polls, and a lost feed says so with a manual
 * refresh (LiveUpdateNote). `api` is replaced by the gallery.
 */
const props = defineProps<{ api: JobApi; base: string }>();
const router = useSettingsRoute();

const list = ref<JobList>();
const loading = ref(true), error = ref(""), query = ref(""), busy = ref("");
const flash = useSettingsFlash();
const root = ref<HTMLElement>();

const folded = ref(true);
const say = (text: string, icon?: string) => flash.notify(text, { icon });

async function load() {
  try {
    list.value = await props.api.list();
    list.value.jobs.forEach(rememberJob);
    error.value = "";
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    loading.value = false;
  }
}
// Every run the viewer may see: read again when one changes (and after each reconnect), never on a timer.
const { unavailable: liveOff, refresh } = useLiveFeed(jobsTopic, load);
const row = (id: string) => [...(root.value?.querySelectorAll<HTMLElement>("[data-job-row]") ?? [])].find((item) => item.dataset.jobRow === id);

async function runNow(job: Job) {
  busy.value = job.id;
  try {
    await props.api.runNow(job.id);
    say(job.paused ? t.list.runExplicit(job.title) : t.list.runQueued(job.title));
    await load();
    // The state of the run is what the focus goes to (§5).
    await nextTick();
    row(job.id)?.querySelector<HTMLElement>("[data-job-status]")?.focus();
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}
async function setPaused(job: Job, paused: boolean) {
  busy.value = job.id;
  try {
    await props.api.setPaused(job.id, paused);
    say(paused ? t.list.paused(job.title) : t.list.resumed(job.title));
    await load();
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = "";
  }
}

/** A filter of the URL's query: setting it keeps the others. */
const filter = (key: string) => computed({
  get: () => router.route.value.query[key] ?? "",
  set: (value: string) => router.setQuery({ ...router.route.value.query, [key]: value || undefined }),
});
const state = filter("stav"), kind = filter("druh"), when = filter("kdy");
const filters = computed<JobFilters>(() => ({ state: state.value, kind: kind.value, when: when.value, text: query.value }));
const filtering = computed(() => !!(state.value || kind.value || when.value || query.value.trim()));
const filterHref = (filterQuery: Record<string, string>) => router.href(props.base, filterQuery);
const clearFilters = () => router.setQuery({});
const stateOptions: ChoiceOption[] = [
  { value: "", label: t.list.anyState, icon: "filter" },
  ...(["running", "queued", "failed", "succeeded", "cancelled"] as const).map((value) => ({ value, label: runBadge(value).label, icon: runBadge(value).icon, tone: runBadge(value).tone })),
  { value: "attention", label: t.list.attention, icon: "triangle-alert", tone: "warning" },
];
const kindOptions: ChoiceOption[] = [
  { value: "", label: t.list.allKinds, icon: "layers" }, { value: "recurring", label: t.list.recurring, icon: "rotate-cw" }, { value: "oneoff", label: t.list.oneOff, icon: "activity" },
];
const whenOptions: ChoiceOption[] = [
  { value: "", label: t.list.anyTime, icon: "history" }, { value: "24h", label: t.list.h24, icon: "clock" }, { value: "7d", label: t.list.d7, icon: "calendar" }, { value: "30d", label: t.list.d30, icon: "calendar" },
];

const groups = computed(() => groupJobs((list.value?.jobs ?? []).filter((job) => matchesJob(job, filters.value))));
/** The one-off jobs shown open: while a filter is on, the done and idle ones too (a match must not hide in the fold). */
const openOneOff = computed(() => (filtering.value ? [...groups.value.open, ...groups.value.settled] : groups.value.open));
const foldedOneOff = computed(() => (filtering.value ? [] : groups.value.settled));
const shown = computed(() => groups.value.recurring.length + openOneOff.value.length + foldedOneOff.value.length);
const failures = computed(() => (state.value === "failed" ? failuresSince(list.value?.summary.failures ?? [], when.value) : []));
const failureOf = (job: Job) => list.value?.summary.failures.find((failure) => failure.runId === job.lastRun?.id);
const first = computed(() => (query.value.trim() ? (groups.value.recurring[0] ?? openOneOff.value[0]) : undefined));
function openFirst() {
  if (!first.value) return false;
  router.navigate(`${props.base}/${encodeURIComponent(first.value.id)}`);
}
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => loading.value, tab: false, take: openFirst });
/** ↓ in the filter goes to the first job shown; the rest is every search's. */
function filterKeys(event: KeyboardEvent) {
  if (keys.onKeydown(event) || event.key !== "ArrowDown") return;
  const link = root.value?.querySelector<HTMLElement>("[data-job-row] a");
  if (!link) return;
  event.preventDefault();
  link.focus();
}

onMounted(async () => {
  // Arriving, the keyboard starts at the summary; back from a job, at its row (below).
  if (!returnFocus.id) root.value?.querySelector<HTMLElement>("[data-job-summary]")?.focus({ preventScroll: true });
  await load();
  // „Zpět na úlohy“ returns the focus to the row the detail was opened from, unless the user has begun to type in the filter meanwhile;
  // a done one-off job's row is in the fold, which then opens (before the first render of the list, so it opens without a motion).
  if (groups.value.settled.some((job) => job.id === returnFocus.id)) folded.value = false;
  await nextTick();
  focusReturned(root.value, (id) => `[data-job-row="${id}"]`);
});
</script>

<template>
  <SettingsPage :title="t.list.title" icon="clock" :description="t.list.description">
    <div ref="root" class="jobs">
      <ErrorState v-if="error" :title="t.list.loadFailed" :text="error" @retry="load" />
      <LiveUpdateNote v-if="liveOff" @refresh="refresh" />
      <JobSummaryTiles :list="list" :href="filterHref" />
      <div class="job-filters" role="search" :aria-label="t.list.filterLabel">
        <SearchField v-model="query" :label="t.list.search" class="job-search" data-job-filter @keydown="filterKeys" />
        <FilterMenu v-model="state" :options="stateOptions" :label="t.list.state" icon="filter" data-filter="state" />
        <FilterMenu v-model="kind" :options="kindOptions" :label="t.list.kind" icon="layers" data-filter="kind" />
        <FilterMenu v-model="when" :options="whenOptions" :label="t.list.when" icon="calendar" data-filter="when" />
        <button v-if="state || kind || when" type="button" class="st-link job-clear" data-job-clear @click="clearFilters"><AtlasIcon name="close" :size="13" />{{ t.list.clear }}</button>
      </div>
      <LoadingRegion :loading="loading" :ready="!!list || !!error" :rows="4" row-height="64px" :label="t.list.loading">
        <div v-if="list" class="job-stack">
          <SettingsSection v-if="state === 'failed'" :title="t.list.failedRuns" icon="circle-x" :count="failures.length" count-tone="danger" data-job-failures>
            <SettingsList v-if="failures.length" :label="t.list.failedRuns" @keydown="rowKeys($event, 'li')">
              <SettingsListRow v-for="failure in failures" :key="failure.runId" :title="failure.jobTitle" icon="circle-x" :data-failure="failure.runId">
                <template #title><a class="st-link" :href="router.href(`${base}/${encodeURIComponent(failure.jobId)}/runs/${failure.runId}`)">{{ failure.jobTitle }}</a></template>
                <template #description><span class="job-failure-error">{{ failure.error ?? t.errorInDetail }}</span></template>
                <template #meta><small class="job-when" :title="formatDateTime(failure.finishedAt)">{{ formatAgo(failure.finishedAt) }}</small></template>
              </SettingsListRow>
            </SettingsList>
            <p v-else class="job-none">{{ t.list.noFailures }}</p>
          </SettingsSection>
          <SettingsSection v-if="groups.recurring.length" :title="t.list.recurringTitle" icon="rotate-cw" :count="groups.recurring.length" data-job-section="recurring">
            <SettingsList :label="t.list.recurringTitle" @keydown="rowKeys($event, 'li')">
              <JobRow v-for="job in groups.recurring" :key="job.id" :job="job" :base="base" :busy="busy === job.id" :first="job === first" :failure="failureOf(job)" @run="runNow(job)" @paused="setPaused(job, $event)" />
            </SettingsList>
          </SettingsSection>
          <SettingsSection v-if="openOneOff.length || foldedOneOff.length" :title="t.list.oneOff" icon="activity" :count="openOneOff.length + foldedOneOff.length" data-job-section="oneoff">
            <SettingsList v-if="openOneOff.length" :label="t.list.oneOffOpenLabel" @keydown="rowKeys($event, 'li')">
              <JobRow v-for="job in openOneOff" :key="job.id" :job="job" :base="base" :busy="busy === job.id" :first="job === first" :failure="failureOf(job)" @run="runNow(job)" @paused="setPaused(job, $event)" />
            </SettingsList>
            <template v-if="foldedOneOff.length">
              <button type="button" class="job-fold" :aria-expanded="!folded" aria-controls="job-settled" data-job-fold @click="folded = !folded">
                <AtlasIcon name="down" :size="16" class="job-fold-chevron" />{{ t.list.settled }} <span class="st-count">{{ foldedOneOff.length }}</span>
              </button>
              <InlineReveal id="job-settled" :open="!folded" @update:open="folded = !$event">
                <SettingsList :label="t.list.settledLabel" @keydown="rowKeys($event, 'li')">
                  <JobRow v-for="job in foldedOneOff" :key="job.id" :job="job" :base="base" :busy="busy === job.id" :first="false" @run="runNow(job)" @paused="setPaused(job, $event)" />
                </SettingsList>
              </InlineReveal>
            </template>
          </SettingsSection>
          <EmptyState v-if="!shown && state !== 'failed'" icon="clock" :title="filtering ? t.list.noneMatch : t.list.noneYet" :text="filtering ? t.list.tryOther : t.list.noneHint" />
          <SettingsSection v-if="list.systemServices.length && !filtering" :title="t.list.servicesTitle" icon="server" :count="list.systemServices.length" :description="t.list.servicesHelp" data-job-services>
            <SettingsList :label="t.list.servicesTitle">
              <SettingsListRow v-for="service in list.systemServices" :key="service.id" :title="service.title" :description="service.description" icon="activity" :data-service="service.id">
                <template #meta>
                  <StatusBadge :label="serviceBadge(service.state).label" :tone="serviceBadge(service.state).tone" :icon="serviceBadge(service.state).icon" size="sm" />
                  <small class="job-next">{{ service.error ?? service.detail ?? (service.lastBeatAt ? t.list.lastBeat(formatAgo(service.lastBeatAt)) : t.list.noBeat) }}</small>
                </template>
              </SettingsListRow>
            </SettingsList>
          </SettingsSection>
        </div>
      </LoadingRegion>
    </div>
  </SettingsPage>
</template>

<style scoped>
.jobs, .job-stack { display: grid; gap: 14px; min-width: 0; }
.job-next, .job-when { color: var(--muted); font-size: 12.5px; }
.job-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.job-search { flex: 1 1 240px; min-width: 200px; max-width: 360px; }
.job-clear { display: inline-flex; align-items: center; gap: 4px; font-size: 12.5px; background: none; cursor: pointer; }
.job-failure-error { color: var(--error); font-weight: 550; overflow-wrap: anywhere; }
.job-none { margin: 0; padding: 12px var(--st-pad-x); color: var(--muted); font-size: 13px; }
.job-fold { display: flex; align-items: center; gap: 8px; width: 100%; padding: 10px var(--st-pad-x); border: 0; border-top: 1px solid var(--line); background: transparent; color: var(--muted); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; transition: background-color var(--atlas-motion-fast); }
.job-fold:focus-visible { outline: var(--state-focus-ring); outline-offset: -2px; }
@media (hover: hover) and (pointer: fine) { .job-fold:hover { background: var(--hover); } }
.job-fold-chevron { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.job-fold[aria-expanded="true"] .job-fold-chevron { rotate: 0deg; }
/* The job Enter takes, while the filter has the focus (P8). */
.jobs:has(input[data-job-filter]:focus) [data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
</style>
