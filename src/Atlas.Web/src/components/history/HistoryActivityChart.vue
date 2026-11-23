<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import type { ChartData, ChartLook } from "@atlas/markdown/plugins/charts";
import type { PageRevisionSummary } from "../../history";
import { userColor } from "../../live/objectPresence";
import { activityBuckets, activityPeople, bucketKey, isHourly } from "./versionSummary";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import t from "../../i18n/components-history";

// Activity over time: bars of changed lines per day (per hour when the loaded range is at most two days). Each person's lines
// in their own colour (the colour of their cursor), AI in the AI ink, publications as diamonds; the version under the pointer
// or selected in the list (`focus`, its activeFrom) stays bright, the other bars soften. Drawn by the editor's chart renderer
// (loaded with ECharts only when the panel opens). Data come only from the loaded versions' summaries.
const props = defineProps<{ items: PageRevisionSummary[]; focus?: string }>();
const emit = defineEmits<{ pick: [key: string, hourly: boolean] }>();
const host = ref<HTMLElement>();
const ready = ref(false);
let renderer: { update(data: ChartData, look?: ChartLook): void; destroy(): void } | undefined;
let disposed = false;
const buckets = computed(() => activityBuckets(props.items));
const hourly = computed(() => isHourly(props.items));
const people = computed(() => activityPeople(props.items, buckets.value));
const label = computed(() => hourly.value ? t.chart.byHours(buckets.value.length) : t.chart.byDays(buckets.value.length));
const css = (name: string, fallback: string) => (host.value ? getComputedStyle(host.value).getPropertyValue(name).trim() : "") || fallback;
const caption = (key: string) => hourly.value ? t.chart.hour(key.slice(8, 10), key.slice(5, 7), key.slice(11)) : t.chart.day(key.slice(8, 10), key.slice(5, 7));
const others = (byPerson: Record<string, number>) => Object.entries(byPerson).reduce((sum, [id, n]) => people.value.shown.some(p => p.id === id) ? sum : sum + n, 0);

function chart(): [ChartData, ChartLook] {
  const shown = people.value.shown, rest = people.value.others, list = buckets.value;
  const header = ["", ...shown.map(p => p.name), ...(rest ? [t.chart.others] : []), "AI", t.chart.publication];
  const grid = [header, ...list.map(b => [
    hourly.value ? `${b.key.slice(11)}:00` : caption(b.key),
    ...shown.map(p => String(b.byPerson[p.id] ?? 0)), ...(rest ? [String(others(b.byPerson))] : []), String(b.ai),
    b.published ? String(Math.max(b.people + b.ai, 1)) : "",
  ])];
  const focusKey = props.focus && bucketKey(props.focus, hourly.value);
  return [{ version: 1, title: label.value, kind: "bar", grid, source: null, sourceStatus: null }, {
    stacked: true, compact: true, marks: true, focus: list.findIndex(b => b.key === focusKey),
    colors: [...shown.map(p => userColor(p.id)), ...(rest ? [css("--muted", "#667085")] : []), css("--ai-ink", "#7a6ad8"), css("--success", "#23794b")],
    tooltip: index => {
      const b = list[index]!;
      return [caption(b.key), ...shown.filter(p => b.byPerson[p.id]).map(p => t.chart.lines(p.name, b.byPerson[p.id]!)),
        ...(rest && others(b.byPerson) ? [t.chart.lines(t.chart.others, others(b.byPerson))] : []), ...(b.ai ? [t.chart.lines("AI", b.ai)] : []),
        ...(b.published ? [t.chart.publishedTimes(b.published)] : [])].join("\n");
    },
    pick: index => { const bucket = list[index]; if (bucket) emit("pick", bucket.key, hourly.value); },
  }];
}

onMounted(async () => {
  try {
    const { loadChartRenderer } = await import("@atlas/markdown/plugins/charts");
    const { createChartRenderer } = await loadChartRenderer();
    if (disposed || !host.value) return;
    renderer = createChartRenderer(host.value, ...chart());
  } catch { /* No chart is not an error for the list below it; the skeleton gives way. */ }
  ready.value = true;
});
watch(() => [buckets.value, props.focus], () => renderer?.update(...chart()));
onBeforeUnmount(() => { disposed = true; renderer?.destroy(); });
</script>

<template>
  <div class="history-activity" data-history-activity>
    <!-- The slot keeps its height from the first frame: skeleton and chart share it, nothing jumps. -->
    <div ref="host" class="history-activity-chart" :class="{ ready }" role="img" :aria-label="label" />
    <LoadingSkeleton v-if="!ready" class="history-activity-skeleton" :rows="1" row-height="88px" :label="t.chart.loading" />
  </div>
</template>

<style scoped>
.history-activity { position: relative; height: 108px; margin: 4px 16px 8px; }
.history-activity-chart { position: absolute; inset: 0; opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); cursor: pointer; }
.history-activity-chart.ready { opacity: 1; }
.history-activity-skeleton { position: absolute; inset: 0; }
</style>
