<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import { errorMessage } from "../../../../api";
import { aiReportsClient, aiTaskLabel, feedbackReason, interactionOutcome, interactionKindLabel, type QualityReport, type AiReportsClient, type UsageReport } from "../../../../ai/reports";
import { formatDateTime, formatDay } from "../../../../formatDate";
import { riseIn } from "../../../../motion/aiMotion";
import { Button } from "../../../ui/button";
import { Table, TableBody, TableCaption, TableCell, TableHead, TableHeader, TableRow } from "../../../ui/table";
import EmptyState from "../../EmptyState.vue";
import ErrorState from "../../ErrorState.vue";
import LoadingNote from "../../LoadingNote.vue";
import SettingsSection from "../../SettingsSection.vue";
import StatTile from "../../StatTile.vue";
import { useOptionalSettingsRoute } from "../../useSettingsRoute";
import { locale } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";

const props = withDefaults(defineProps<{ kind: "usage" | "quality"; compact?: boolean; client?: AiReportsClient; exportHref?: string | null }>(), {
  compact: false, client: () => aiReportsClient, exportHref: "/api/admin/ai/usage.csv",
});
const router = useOptionalSettingsRoute();
const fullReportHref = router?.href("general/modules/ai/usage") ?? "#/settings/general/modules/ai/usage";
const usage = ref<UsageReport>();
const quality = ref<QualityReport>();
const loading = ref(true);
const error = ref("");
const root = ref<HTMLElement>();
let request: AbortController | undefined;
const number = (value: number | null) => value === null ? t.reports.unknown : value.toLocaleString(locale, { maximumFractionDigits: 2 });
const providers = computed(() => usage.value?.providers ?? []);
const recorded = computed(() => providers.value.reduce((n, x) => n + x.calls, 0));
const cloudCalls = computed(() => providers.value.filter(x => x.location === "cloud").reduce((n, x) => n + x.calls, 0));
const companyCalls = computed(() => providers.value.filter(x => x.location === "company").reduce((n, x) => n + x.calls, 0));
const costKnownCalls = computed(() => providers.value.reduce((n, x) => n + x.costKnownCalls, 0));
const knownCost = computed(() => costKnownCalls.value
  ? providers.value.reduce((n, x) => n + (x.estimatedCost ?? 0), 0) : null);
const latestCall = computed(() => providers.value.map(x => x.lastCallAt).sort().at(-1));
const total = computed(() => props.kind === "usage"
  ? (providers.value.length ? recorded.value : usage.value?.items.reduce((n, x) => n + x.calls, 0) ?? 0)
  : quality.value?.items.reduce((n, x) => n + x.count, 0) ?? 0);
const positive = computed(() => quality.value?.items.filter(x => x.rating === "up").reduce((n, x) => n + x.count, 0) ?? 0);
const costText = (value: number | null) => value === null ? t.reports.unknown : value > 0 && value < 0.000001
  ? t.reports.tinyCost : value.toLocaleString(locale, { minimumFractionDigits: 2, maximumFractionDigits: 6 }) + " USD";
const providerLabel = (value: string) => ({
  anthropic: "Anthropic", "openai-compatible": t.reports.openaiCompatible, gateway: "Gateway",
} as Record<string, string>)[value] ?? value;
const locationLabel = (value: string) => value === "cloud" ? t.reports.cloud : value === "company" ? t.reports.companyServer : value;
async function load() {
  request?.abort();
  const current = request = new AbortController();
  loading.value = true; error.value = "";
  try {
    if (props.kind === "usage") {
      const result = await props.client.usage(current.signal);
      if (!current.signal.aborted) usage.value = result;
    } else {
      const result = await props.client.quality(current.signal);
      if (!current.signal.aborted) quality.value = result;
    }
    if (!current.signal.aborted) {
      await nextTick();
      if (root.value) riseIn([...root.value.querySelectorAll<HTMLElement>("[data-report-stat]")]);
    }
  } catch (failure) { if (!current.signal.aborted) error.value = errorMessage(failure); }
  finally { if (request === current) loading.value = false; }
}
watch(() => props.kind, load, { immediate: true });
onBeforeUnmount(() => request?.abort());
</script>
<template>
  <div ref="root" class="ai-reports" :data-ai-report="kind" :aria-busy="loading">
    <SettingsSection :title="kind === 'usage' ? (compact ? t.reports.callsAndCosts : t.reports.recordedCalls) : t.reports.qualityTitle"
      :icon="kind === 'usage' ? 'activity' : 'circle-check'"
      :description="kind === 'usage' ? t.reports.usageDescription : t.reports.qualityDescription">
      <template #actions><Button type="button" variant="outline" size="sm" :disabled="loading" @click="load">{{ t.reports.refresh }}</Button></template>
      <ErrorState v-if="error" :title="t.reports.loadFailed" :text="error" :busy="loading" @retry="load" />
      <LoadingNote v-else-if="loading && !usage && !quality" :label="t.reports.loading" />
      <template v-else-if="kind === 'usage' && usage">
        <div class="report-stats">
          <StatTile :label="t.reports.recordedCallsTile" :value="number(total)" icon="activity" data-report-stat />
          <StatTile :label="t.reports.cloud" :value="number(cloudCalls)" icon="external" data-report-stat />
          <StatTile :label="t.reports.companyServer" :value="number(companyCalls)" icon="database" data-report-stat />
          <StatTile :label="t.reports.costEstimate" :value="costText(knownCost)" icon="line-chart" data-report-stat />
        </div>
        <p class="st-muted">{{ t.reports.costCovers(number(costKnownCalls), number(recorded)) }}</p>
        <p class="st-muted">{{ t.reports.period(formatDateTime(usage.since), formatDateTime(usage.until), latestCall ? formatDateTime(latestCall) : t.reports.none) }}</p>
        <template v-if="compact">
          <div class="st-actions"><Button as-child variant="outline" size="sm"><a :href="fullReportHref" data-ai-full-report>{{ t.reports.fullReport }}</a></Button></div>
        </template>
        <template v-else>
          <div v-if="exportHref" class="st-actions"><Button as-child variant="outline" size="sm"><a :href="exportHref" download data-ai-export>{{ t.reports.downloadCsv }}</a></Button></div>
          <template v-if="providers.length">
            <h3>{{ t.reports.providersTitle }}</h3>
            <Table>
              <TableCaption>{{ t.reports.providersCaption }}</TableCaption>
              <TableHeader><TableRow><TableHead>{{ t.reports.providerModel }}</TableHead><TableHead>{{ t.reports.callsErrorsCanceled }}</TableHead><TableHead>{{ t.reports.tokensInOut }}</TableHead><TableHead>{{ t.reports.costEstimate }}</TableHead><TableHead>{{ t.reports.lastCall }}</TableHead></TableRow></TableHeader>
              <TableBody><TableRow v-for="row in providers" :key="[row.provider,row.location,row.model].join(':')" data-ai-provider-row>
                <TableCell><strong>{{ providerLabel(row.provider) }} · {{ locationLabel(row.location) }}</strong><small>{{ row.model }}</small></TableCell>
                <TableCell>{{ number(row.calls) }} / {{ number(row.errors) }} / {{ number(row.canceled) }}</TableCell>
                <TableCell>{{ number(row.inputTokens) }} / {{ number(row.outputTokens) }}<small>{{ t.reports.knownTokens(number(row.inputKnownCalls), number(row.outputKnownCalls), number(row.calls)) }}</small></TableCell>
                <TableCell>{{ costText(row.estimatedCost) }}<small>{{ t.reports.knownCost(number(row.costKnownCalls), number(row.calls)) }}</small></TableCell>
                <TableCell>{{ formatDateTime(row.lastCallAt) }}</TableCell>
              </TableRow></TableBody>
            </Table>
            <h3>{{ t.reports.byDay }}</h3>
            <Table>
              <TableCaption>{{ t.reports.byDayCaption }}</TableCaption>
              <TableHeader><TableRow><TableHead>{{ t.reports.day }}</TableHead><TableHead>{{ t.reports.callsErrorsCanceled }}</TableHead><TableHead>{{ t.reports.tokensInOut }}</TableHead><TableHead>{{ t.reports.costEstimate }}</TableHead></TableRow></TableHeader>
              <TableBody><TableRow v-for="row in usage.days ?? []" :key="row.day" data-ai-day-row>
                <TableCell>{{ formatDay(row.day.slice(0, 10)) }}</TableCell>
                <TableCell>{{ number(row.calls) }} / {{ number(row.errors) }} / {{ number(row.canceled) }}</TableCell>
                <TableCell>{{ number(row.inputTokens) }} / {{ number(row.outputTokens) }}<small>{{ t.reports.knownTokens(number(row.inputKnownCalls), number(row.outputKnownCalls), number(row.calls)) }}</small></TableCell>
                <TableCell>{{ costText(row.estimatedCost) }}<small>{{ t.reports.knownCost(number(row.costKnownCalls), number(row.calls)) }}</small></TableCell>
              </TableRow></TableBody>
            </Table>
          </template>
          <EmptyState v-else compact :title="t.reports.noCallsTitle" :text="t.reports.noCallsText" icon="activity" />
          <template v-if="usage.items.length">
            <h3>{{ t.reports.tasks }}</h3>
            <Table>
              <TableCaption>{{ t.reports.tasksCaption }}</TableCaption>
              <TableHeader><TableRow><TableHead>{{ t.reports.task }}</TableHead><TableHead>{{ t.reports.callsErrors }}</TableHead><TableHead>{{ t.reports.latency }}</TableHead><TableHead>{{ t.reports.tokensInOut }}</TableHead><TableHead>{{ t.reports.costEstimate }}</TableHead></TableRow></TableHeader>
              <TableBody><TableRow v-for="row in usage.items" :key="row.task + row.kind + row.operation" data-ai-usage-row>
                <TableCell><strong>{{ aiTaskLabel(row.task) }}</strong><small>{{ row.operation === 'embedding' ? t.kinds.embeddings : t.kinds.chat }} · {{ row.kind === 'quick' ? t.kinds.quick : row.kind }}</small></TableCell>
                <TableCell>{{ number(row.calls) }} / {{ number(row.errors) }}</TableCell>
                <TableCell>{{ number(row.p50Ms) }} / {{ number(row.p95Ms) }} ms</TableCell>
                <TableCell>{{ number(row.inputTokens) }} / {{ number(row.outputTokens) }}<small v-if="row.inputKnownCalls !== row.calls || row.outputKnownCalls !== row.calls">{{ t.reports.knownTokens(row.inputKnownCalls, row.outputKnownCalls, row.calls) }}</small></TableCell>
                <TableCell>{{ costText(row.estimatedCost) }}<small>{{ t.reports.knownCost(number(row.costKnownCalls ?? 0), number(row.calls)) }}</small></TableCell>
              </TableRow></TableBody>
            </Table>
          </template>
        </template>
      </template>
      <template v-else-if="quality">
        <div class="report-stats"><StatTile :label="t.reports.ratings" :value="number(total)" icon="quote" data-report-stat /><StatTile :label="t.reports.helpfulAnswers" :value="total ? Math.round(positive / total * 100) + ' %' : t.reports.noRatings" icon="circle-check" data-report-stat /></div>
        <p class="st-muted">{{ t.reports.ratingsNote }}</p>
        <Table v-if="quality.items.length"><TableCaption>{{ t.reports.ratingsCaption }}</TableCaption>
          <TableHeader><TableRow><TableHead>{{ t.reports.task }}</TableHead><TableHead>{{ t.reports.rating }}</TableHead><TableHead>{{ t.reports.reason }}</TableHead><TableHead>{{ t.reports.count }}</TableHead></TableRow></TableHeader>
          <TableBody><TableRow v-for="row in quality.items" :key="[row.task, row.promptVersion, row.rating, row.reason].join(':')">
            <TableCell>{{ aiTaskLabel(row.task) }}<small>{{ row.promptVersion ?? t.reports.noPromptVersion }}</small></TableCell><TableCell>{{ row.rating === 'up' ? t.reports.helpful : t.reports.notHelpful }}</TableCell><TableCell>{{ row.rating === 'up' ? '—' : feedbackReason(row.reason) }}</TableCell><TableCell>{{ number(row.count) }}</TableCell>
          </TableRow></TableBody>
        </Table>
        <EmptyState v-else compact :title="t.reports.noRatingsTitle" :text="t.reports.noRatingsText" icon="quote" />
        <template v-if="quality.interactions?.length">
          <h3>{{ t.reports.acceptance }}</h3>
          <p class="st-muted">{{ t.reports.acceptanceNote }}</p>
          <Table>
            <TableHeader><TableRow><TableHead>{{ t.reports.taskType }}</TableHead><TableHead>{{ t.reports.action }}</TableHead><TableHead>{{ t.reports.count }}</TableHead></TableRow></TableHeader>
            <TableBody><TableRow v-for="row in quality.interactions" :key="[row.task,row.kind,row.outcome].join(':')">
              <TableCell>{{ aiTaskLabel(row.task) }}<small>{{ interactionKindLabel(row.kind) }}</small></TableCell><TableCell>{{ interactionOutcome(row.outcome) }}</TableCell><TableCell>{{ number(row.count) }}</TableCell>
            </TableRow></TableBody>
          </Table>
        </template>
        <template v-if="quality.noSources?.length">
          <h3>{{ t.reports.noSources }}</h3>
          <p class="st-muted">{{ t.reports.noSourcesNote }}</p>
          <Table><TableHeader><TableRow><TableHead>{{ t.reports.space }}</TableHead><TableHead>{{ t.reports.count }}</TableHead></TableRow></TableHeader>
            <TableBody><TableRow v-for="row in quality.noSources" :key="row.spaceId ?? 'wiki'"><TableCell>{{ row.spaceName }}</TableCell><TableCell>{{ number(row.count) }}</TableCell></TableRow></TableBody>
          </Table>
        </template>
      </template>
    </SettingsSection>
  </div>
</template>
<style scoped>
.ai-reports { display: grid; min-width: 0; gap: 16px; }
/* The section body is a column with its own gap: the browser's paragraph margins came on top of it (44 px between the
   tiles and each note). Only the tiles keep a little extra room, and a heading a little more above than below. */
.report-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(190px, 100%), 1fr)); gap: 12px; margin-bottom: 4px; }
p { margin: 0; }
h3 { margin: 8px 0 0; font-size: 1rem; font-weight: 600; }
small { display: block; color: var(--muted); margin-top: 4px; font-size: 12px; }
</style>
