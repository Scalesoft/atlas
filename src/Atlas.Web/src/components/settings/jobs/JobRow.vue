<script setup lang="ts">
import { computed } from "vue";
import { formatAgo, formatDateTime } from "../../../formatDate";
import type { Job, JobFailure } from "../../../jobs/jobApi";
import t from "../../../i18n/settings-jobs";
import AtlasIcon from "../../AtlasIcon.vue";
import RowActionsMenu from "../../RowActionsMenu.vue";
import { Button } from "../../ui/button";
import SettingsListRow from "../SettingsListRow.vue";
import StatusBadge from "../StatusBadge.vue";
import { useSettingsRoute } from "../useSettingsRoute";
import { attentionBadges, jobBadge, moduleIcon, nextText, summaryText } from "./jobFormat";

/**
 * One job of Úlohy na pozadí: the title opens it, the state, what needs a look (Po termínu, Zaseknutá) and the next run beside it, „Spustit
 * teď“ and the row menu (Pozastavit/Obnovit, Historie běhů). A failed newest run stands out with the first line of its error (`failure`, from
 * the summary) and a link to the run. The description is the detail's (the list stays two lines a job); a running row turns its badge's icon and
 * sweeps a thin line along its foot, both stopped under reduced motion. Private to JobListView, which lists it in three places.
 */
const props = defineProps<{ job: Job; base: string; busy: boolean; first: boolean; failure?: JobFailure }>();
const emit = defineEmits<{ run: []; paused: [paused: boolean] }>();
const router = useSettingsRoute();
const detailPath = computed(() => `${props.base}/${encodeURIComponent(props.job.id)}`);
const failed = computed(() => props.job.lastRun?.state === "failed" ? props.job.lastRun : undefined);
const badge = computed(() => jobBadge(props.job));
const menu = computed(() => [
  { id: "toggle", label: props.job.paused ? t.row.resume : t.row.pause, icon: props.job.paused ? "refresh" : "ban", disabled: !props.job.known || props.busy },
  { id: "history", label: t.row.history, icon: "history" },
]);
const chosen = (id: string) => (id === "toggle" ? emit("paused", !props.job.paused) : router.navigate(detailPath.value));
</script>

<template>
  <SettingsListRow :title="job.title" :icon="moduleIcon(job.module)" :data-job-row="job.id" :data-first="first || undefined" :data-failed="failed ? '' : undefined"
    :data-running="job.lastRun?.state === 'running' ? '' : undefined">
    <template #title><a class="st-link" :href="router.href(detailPath)" :title="job.description">{{ job.title }}</a></template>
    <template #description>
      <span v-if="failed" class="job-error" data-job-error>
        <AtlasIcon name="circle-x" :size="13" />{{ failure?.error ?? t.errorInDetail }}
        <a class="st-link" :href="router.href(`${detailPath}/runs/${failed.id}`)" :aria-label="t.row.failedDetails(job.title)">{{ t.row.details }}</a>
      </span>
      <span v-if="job.lastRun" class="job-last" :title="formatDateTime(job.lastRun.finishedAt ?? job.lastRun.startedAt ?? job.lastRun.queuedAt)">
        {{ t.row.lastRun(formatAgo(job.lastRun.finishedAt ?? job.lastRun.startedAt ?? job.lastRun.queuedAt)) }}<template v-if="summaryText(job.lastRun)"> · {{ summaryText(job.lastRun) }}</template>
        <template v-if="job.recurring && job.lastRun.state !== 'succeeded'"> · {{ job.lastSuccessAt ? t.row.succeededAgo(formatAgo(job.lastSuccessAt)) : t.row.neverSucceeded }}</template>
      </span>
    </template>
    <template #meta>
      <span class="job-status" role="status" tabindex="-1" data-job-status>
        <Transition name="st-fade" mode="out-in"><StatusBadge :key="badge.label" :label="badge.label" :tone="badge.tone" :icon="badge.icon" size="sm" /></Transition>
      </span>
      <StatusBadge v-for="item in attentionBadges(job)" :key="item.label" :label="item.label" :tone="item.tone" :icon="item.icon" size="sm" data-job-attention />
      <small class="job-next">{{ nextText(job) }}</small>
    </template>
    <template #actions>
      <Button v-if="job.known" variant="outline" size="sm" type="button" :disabled="busy" :aria-busy="busy" :aria-label="t.row.runNowLabel(job.title)" data-job-run-now @click="emit('run')"><AtlasIcon name="refresh" :size="15" />{{ t.runNow }}</Button>
    </template>
    <RowActionsMenu :items="menu" :label="t.row.actions(job.title)" @select="chosen" />
  </SettingsListRow>
</template>

<style scoped>
.job-last, .job-error { display: block; }
.job-error { color: var(--error); font-weight: 550; overflow-wrap: anywhere; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.job-error > svg { vertical-align: -2px; margin-right: 4px; }
.job-error .st-link { margin-left: 6px; font-weight: 600; }
.job-next { color: var(--muted); font-size: 12.5px; }
.job-status { display: inline-flex; border-radius: 7px; }
.job-status:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
[data-failed] { box-shadow: inset 3px 0 0 var(--error); }
[data-running] { position: relative; }
/* The run goes: a soft accent sweep along the row's foot (LoadingRegion's refresh line) and the turning icon of Probíhá. */
[data-running]::after {
  content: ""; position: absolute; inset: auto 0 0; height: 2px; pointer-events: none;
  background: linear-gradient(90deg, transparent, var(--accent), transparent); background-size: 200% 100%;
  animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out), atlas-progress var(--atlas-motion-loop) var(--atlas-ease-inout) infinite;
}
[data-running] .job-status :deep(svg) { animation: atlas-spin var(--atlas-motion-spin) linear infinite; }
@media (prefers-reduced-motion: reduce) { [data-running]::after, [data-running] .job-status :deep(svg) { animation: none; } }
:global([data-atlas-reduced-motion] [data-job-row][data-running]::after), :global([data-atlas-reduced-motion] [data-job-row][data-running] .job-status svg) { animation: none; }
</style>
