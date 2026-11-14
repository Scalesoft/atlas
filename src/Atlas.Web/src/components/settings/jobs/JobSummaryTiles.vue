<script setup lang="ts">
import { computed } from "vue";
import { formatNumber } from "../../../i18n";
import t from "../../../i18n/settings-jobs";
import type { JobList } from "../../../jobs/jobApi";
import StatTile from "../StatTile.vue";

/**
 * The top of Úlohy na pozadí (3.6): what runs, what failed, what needs a look and the runs of the last 14 days, as the audit's overview
 * (ActivitySummarySection) composes StatTile. The tiles stand from the first frame with „…“ while the list loads, so nothing below jumps;
 * a tile with something to show is a link that filters the list below (`href` gives the URL of a filter). The page focuses the region
 * (`data-job-summary`) when it opens, Tab goes on through the tiles to the filters. Compact tiles, no heading of its own: the list must stay in
 * view. While a run goes, the Běží tile's icon turns (stopped under reduced motion).
 */
const props = defineProps<{ list?: JobList; href: (query: Record<string, string>) => string }>();
const summary = computed(() => props.list?.summary);
const number = (value: number | undefined) => (value === undefined ? "…" : formatNumber(value));
const overdue = computed(() => props.list?.jobs.filter((job) => job.overdue).length ?? 0);
const stuck = computed(() => props.list?.jobs.filter((job) => job.stuck).length ?? 0);
const attention = computed(() => (props.list ? overdue.value + stuck.value : undefined));
const totals = computed(() => summary.value?.days.reduce((sum, day) => ({ ok: sum.ok + day.succeeded, failed: sum.failed + day.failed }), { ok: 0, failed: 0 }));
// One column per day, its height the day's runs against the busiest day, the failed part at its foot; it only repeats the text.
const bars = computed(() => {
  const days = summary.value?.days ?? [];
  const most = Math.max(1, ...days.map((day) => day.succeeded + day.failed));
  return days.map((day) => {
    const total = day.succeeded + day.failed;
    return { key: day.day, height: total ? Math.max(0.08, total / most) : 0.06, failed: total ? day.failed / total : 0 };
  });
});
</script>

<template>
  <section class="job-summary" tabindex="-1" :aria-label="t.summary.label" data-job-summary>
    <div class="job-summary-tiles">
      <StatTile compact :label="t.summary.running" icon="workflow" :value-icon="summary?.running ? 'loader-circle' : undefined" :value="number(summary?.running)" :data-active="summary?.running ? '' : undefined" :href="summary?.running || summary?.queued ? href({ stav: summary.running ? 'running' : 'queued' }) : undefined" data-stat="running">
        <template v-if="summary">{{ summary.queued ? t.summary.queued(number(summary.queued)) : t.summary.nothingQueued }}</template>
      </StatTile>
      <StatTile compact :label="t.summary.failedDay" icon="circle-x" :tone="summary ? (summary.failedDay ? 'danger' : 'success') : undefined" :value="number(summary?.failedDay)"
        :href="summary?.failedWeek ? href({ stav: 'failed', kdy: summary.failedDay ? '24h' : '7d' }) : undefined" data-stat="failed">
        <template v-if="summary">{{ t.summary.failedWeek(summary.failedWeek) }}</template>
      </StatTile>
      <StatTile compact :label="t.summary.attention" icon="triangle-alert" :tone="list ? (attention ? 'warning' : 'success') : undefined" :value="number(attention)"
        :href="attention ? href({ stav: 'attention' }) : undefined" data-stat="attention">
        <template v-if="list">{{ attention ? t.summary.attentionDetail(overdue, stuck) : t.summary.onTime }}</template>
      </StatTile>
      <StatTile compact :label="t.summary.days" icon="activity" :value="number(totals && totals.ok + totals.failed)" data-stat="days">
        <template v-if="totals">{{ totals.failed ? t.summary.daysFailed(number(totals.failed)) : t.summary.allFine }}</template>
        <template v-if="totals && totals.ok + totals.failed" #chart>
          <span class="day-bars"><i v-for="bar in bars" :key="bar.key" :style="{ transform: `scaleY(${bar.height})` }"><b :style="{ transform: `scaleY(${bar.failed})` }" /></i></span>
        </template>
      </StatTile>
    </div>
  </section>
</template>

<style scoped>
.job-summary { min-width: 0; border-radius: 12px; }
.job-summary:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.job-summary-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
/* Something runs: the value's icon turns, as every Atlas spinner (motion.css .atlas-spinner), and stands still under reduced motion. */
[data-active] :deep(.stat-tile-value > svg) { animation: atlas-spin var(--atlas-motion-spin) linear infinite; }
@media (prefers-reduced-motion: reduce) { [data-active] :deep(.stat-tile-value > svg) { animation: none; } }
:global([data-atlas-reduced-motion] .job-summary [data-active] .stat-tile-value > svg) { animation: none; }
@container settings-shell (max-width: 900px) { .job-summary-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
.day-bars { display: flex; align-items: flex-end; gap: 2px; height: 34px; }
.day-bars > i { display: flex; flex-direction: column; justify-content: flex-end; width: 4px; height: 100%; transform-origin: bottom; border-radius: 3px 3px 1px 1px; overflow: hidden; background: color-mix(in srgb, var(--accent) 55%, var(--surface)); transition: transform var(--atlas-motion-slow) var(--atlas-ease-out); }
.day-bars b { display: block; height: 100%; transform-origin: bottom; background: var(--error); transition: transform var(--atlas-motion-slow) var(--atlas-ease-out); }
</style>
