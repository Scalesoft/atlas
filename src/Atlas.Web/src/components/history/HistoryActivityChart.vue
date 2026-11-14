<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { prefersReducedMotion } from "../../motion/aiMotion";
import type { PageRevisionSummary } from "../../history";
import { activityBuckets, isHourly } from "./versionSummary";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import t from "../../i18n/components-history";

// Activity over time: bars of changed lines per day (per hour when the loaded range is at most two days), people and AI
// stacked, publications as markers. Drawn with ECharts like the editor's charts (this component and ECharts load only
// when the panel opens; ECharts is Atlas.Web's own dependency). The editor's renderer is not reusable here: it takes
// a table, has no stacking by series colour and no markers. Data come only from the loaded versions' summaries.
const props = defineProps<{ items: PageRevisionSummary[] }>();
const emit = defineEmits<{ pick: [key: string, hourly: boolean] }>();
const host = ref<HTMLElement>();
const ready = ref(false);
let chart: import("echarts/core").EChartsType | undefined;
let observer: ResizeObserver | undefined;
let disposed = false;
const buckets = computed(() => activityBuckets(props.items));
const hourly = computed(() => isHourly(props.items));
const label = computed(() => hourly.value ? t.chart.byHours(buckets.value.length) : t.chart.byDays(buckets.value.length));
const css = (name: string, fallback: string) => (host.value ? getComputedStyle(host.value).getPropertyValue(name).trim() : "") || fallback;
const reduced = () => prefersReducedMotion(host.value);
const caption = (key: string) => hourly.value ? t.chart.hour(key.slice(8, 10), key.slice(5, 7), key.slice(11)) : t.chart.day(key.slice(8, 10), key.slice(5, 7));

function option() {
  const data = buckets.value, accent = css("--accent", "#3997a9"), ai = css("--ai-ink", "#7a6ad8"), muted = css("--muted", "#667085");
  return {
    animation: !reduced(), animationDuration: 240,
    grid: { left: 4, right: 4, top: 6, bottom: 18 },
    tooltip: { trigger: "axis", axisPointer: { type: "none" }, formatter: (params: { dataIndex: number }[]) => { const b = data[params[0]!.dataIndex]!; return `${caption(b.key)}<br/>${t.chart.peopleLines(b.people)}<br/>${t.chart.aiLines(b.ai)}${b.published ? `<br/>${t.chart.publishedTimes(b.published)}` : ""}`; } },
    xAxis: { type: "category", data: data.map(b => caption(b.key)), axisLabel: { color: muted, fontSize: 10, hideOverlap: true }, axisTick: { show: false }, axisLine: { lineStyle: { color: css("--line", "#ddd") } } },
    yAxis: { type: "value", show: false },
    series: [
      { name: t.chart.people, type: "bar", stack: "size", data: data.map(b => b.people), itemStyle: { color: accent }, barMaxWidth: 18 },
      { name: "AI", type: "bar", stack: "size", data: data.map(b => b.ai), itemStyle: { color: ai }, barMaxWidth: 18 },
      { name: t.chart.publication, type: "scatter", symbol: "diamond", symbolSize: 8, itemStyle: { color: css("--success", "#23794b") }, tooltip: { show: false },
        data: data.map(b => b.published ? Math.max(b.people + b.ai, 1) : "-") },
    ],
  };
}

onMounted(async () => {
  try {
    const [core, charts, components, renderers] = await Promise.all([import("echarts/core"), import("echarts/charts"), import("echarts/components"), import("echarts/renderers")]);
    if (disposed || !host.value) return;
    core.use([charts.BarChart, charts.ScatterChart, components.GridComponent, components.TooltipComponent, renderers.SVGRenderer]);
    chart = core.init(host.value, undefined, { renderer: "svg" });
    chart.setOption(option());
    chart.on("click", (event: { dataIndex?: number }) => { const bucket = buckets.value[event.dataIndex ?? -1]; if (bucket) emit("pick", bucket.key, hourly.value); });
    observer = new ResizeObserver(() => chart?.resize());
    observer.observe(host.value);
    ready.value = true;
  } catch { /* No chart is not an error for the list below it; the skeleton gives way. */ ready.value = true; }
});
watch(buckets, () => chart?.setOption(option(), true));
onBeforeUnmount(() => { disposed = true; observer?.disconnect(); chart?.dispose(); });
</script>

<template>
  <div class="history-activity" data-history-activity>
    <!-- The slot keeps its height from the first frame: skeleton and chart share it, nothing jumps. -->
    <div ref="host" class="history-activity-chart" :class="{ ready }" role="img" :aria-label="label" />
    <LoadingSkeleton v-if="!ready" class="history-activity-skeleton" :rows="1" row-height="56px" :label="t.chart.loading" />
  </div>
</template>

<style scoped>
.history-activity { position: relative; height: 76px; margin: 0 16px 8px; }
.history-activity-chart { position: absolute; inset: 0; opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); cursor: pointer; }
.history-activity-chart.ready { opacity: 1; }
.history-activity-skeleton { position: absolute; inset: 0; }
</style>
