<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { errorMessage } from "../../../api";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import SettingsSection from "../SettingsSection.vue";
import StatTile from "../StatTile.vue";
import t from "../../../i18n/settings-global";
import { formatNumber } from "../../../i18n";
import { periodLabels, type ActivityPeriod } from "../activity";
import { loadActivitySummary, type ActivityCount, type ActivityPerson, type ActivitySummary } from "../auditActivity";
import { useOptionalSettingsRoute } from "../useSettingsRoute";

// The audit's overview (wiki administrator): who edits most, how much goes through MCP, the REST API and the assistant, and
// which tools fail. It follows the log's Kdy filter (30 days when none is set) and counts the whole period on the server, not
// just the loaded page of the log. A failed tool call and a tool user lead into the log, filtered. The tiles with their small
// charts stay in view; the rankings unfold under „Podrobnosti“, so the overview does not push the log down.
const router = useOptionalSettingsRoute();
const query = computed(() => router?.route.value.query ?? {});
const period = computed<ActivityPeriod>(() => (query.value.kdy as ActivityPeriod | undefined) && query.value.kdy in periodLabels ? (query.value.kdy as ActivityPeriod) : "30d");
const days: Record<ActivityPeriod, number | undefined> = { "24h": 1, "7d": 7, "30d": 30, all: undefined };
const channel = (key: string) => t.activity.channels[key] ?? key;

const summary = ref<ActivitySummary>();
const error = ref("");
const open = ref(false);
let controller: AbortController | undefined;
async function load(value: ActivityPeriod) {
  controller?.abort();
  const current = (controller = new AbortController());
  error.value = "";
  try {
    summary.value = await loadActivitySummary(days[value], current.signal);
  } catch (failure) {
    if (!current.signal.aborted) error.value = errorMessage(failure);
  }
}
watch(period, load, { immediate: true });
onBeforeUnmount(() => controller?.abort());

const number = (value: number | undefined) => (value === undefined ? "…" : formatNumber(value));
const split = (counts: ActivityCount[]) => counts.map((item) => `${channel(item.key)} ${formatNumber(item.count)}`).join(" · ");
const filtered = (extra: Record<string, string>) => router?.href("security/audit", { ...query.value, ...extra });
const top = computed(() => summary.value?.topEditors[0]);
const width = (value: number, list: { count?: number; calls?: number }[]) => `${Math.max(4, (value / Math.max(1, ...list.map((item) => item.count ?? item.calls ?? 0))) * 100)}%`;
const people = (list: ActivityPerson[]) => list.map((person) => ({ ...person, bar: width(person.count, list) }));
const tone = (index: number) => `ch-${Math.min(index, 3)}`;

// A ring of arcs (pathLength 100): each part is its share of the total, laid one after another from the top.
const ring = (parts: { value: number; tone: string }[], total: number) => {
  let start = 0;
  return parts.filter((part) => part.value > 0 && total > 0).map((part) => {
    const share = (part.value / total) * 100;
    const arc = { tone: part.tone, dash: `${share} ${100 - share}`, offset: -start };
    start += share;
    return arc;
  });
};
const percent = (value: number, total: number) => t.activity.percent(total ? Math.round((value / total) * 100) : 0);
const charts = computed(() => {
  const value = summary.value;
  if (!value) return undefined;
  const editors = value.topEditors.slice(0, 6);
  const most = Math.max(1, ...editors.map((person) => person.count));
  return {
    ai: ring([{ value: value.aiAssistedRevisions, tone: "accent" }], value.revisions),
    aiShare: percent(value.aiAssistedRevisions, value.revisions),
    editors: editors.map((person) => ({ key: person.userId, height: `${Math.max(8, (person.count / most) * 100)}%` })),
    channels: ring(value.toolChannels.map((item, index) => ({ value: item.count, tone: tone(index) })), value.toolCalls),
    failures: ring([{ value: value.toolCalls - value.toolFailures, tone: "success" }, { value: value.toolFailures, tone: "warning" }], value.toolCalls),
    failureShare: percent(value.toolFailures, value.toolCalls),
  };
});
</script>

<template>
  <section class="st-group activity-summary" aria-labelledby="activity-summary-title" data-activity-summary>
    <div class="summary-head">
      <h3 id="activity-summary-title" class="st-group-label">{{ t.activity.heading(periodLabels[period]) }}</h3>
      <button v-if="summary && !error" type="button" class="summary-toggle" :aria-expanded="open" aria-controls="activity-summary-detail" data-summary-toggle @click="open = !open">
        {{ open ? t.activity.hide : t.activity.show }}<AtlasIcon name="down" :size="16" class="summary-chevron" />
      </button>
    </div>
    <ErrorState v-if="error" :title="t.activity.loadFailed(error)" @retry="load(period)" />
    <template v-else>
      <div class="summary-tiles">
        <StatTile :label="t.activity.edits" icon="pencil" :value="number(summary?.revisions)" data-stat="revisions">
          <template v-if="summary">{{ summary.aiAssistedRevisions ? t.activity.withAi(number(summary.aiAssistedRevisions)) : t.activity.noAi }}</template>
          <template v-if="charts && summary?.revisions" #chart>
            <svg class="mini-ring" viewBox="0 0 36 36" width="46" height="46">
              <g transform="rotate(-90 18 18)">
                <circle class="ring-track" cx="18" cy="18" r="15" pathLength="100" />
                <circle v-for="(arc, index) in charts.ai" :key="index" class="ring-arc" :data-tone="arc.tone" cx="18" cy="18" r="15" pathLength="100" :stroke-dasharray="arc.dash" :stroke-dashoffset="arc.offset" />
              </g>
              <text x="18" y="18" class="ring-label">{{ charts.aiShare }}</text>
            </svg>
          </template>
        </StatTile>
        <StatTile :label="t.activity.mostActive" icon="account" :value="summary ? (top?.name ?? t.activity.nobody) : '…'" data-stat="top-editor">
          <template v-if="top">{{ t.counts.edits(top.count) }}</template>
          <template v-if="charts && charts.editors.length > 1" #chart>
            <span class="mini-bars"><i v-for="bar in charts.editors" :key="bar.key" :style="{ height: bar.height }" /></span>
          </template>
        </StatTile>
        <StatTile :label="t.activity.calls" icon="plug" :value="number(summary?.toolCalls)" data-stat="tool-calls">
          <template v-if="summary">
            <span v-if="summary.toolChannels.length" class="mini-legend">
              <template v-for="(item, index) in summary.toolChannels" :key="item.key">{{ index ? " · " : "" }}<span><i :data-tone="tone(index)" />{{ channel(item.key) }} {{ formatNumber(item.count) }}</span></template>
            </span>
            <template v-else>{{ t.activity.noCalls }}</template>
          </template>
          <template v-if="charts?.channels.length" #chart>
            <svg class="mini-ring" viewBox="0 0 36 36" width="46" height="46">
              <g transform="rotate(-90 18 18)">
                <circle class="ring-track" cx="18" cy="18" r="15" pathLength="100" />
                <circle v-for="(arc, index) in charts.channels" :key="index" class="ring-arc" :data-tone="arc.tone" cx="18" cy="18" r="15" pathLength="100" :stroke-dasharray="arc.dash" :stroke-dashoffset="arc.offset" />
              </g>
            </svg>
          </template>
        </StatTile>
        <StatTile
          :label="t.activity.failedCalls"
          icon="triangle-alert"
          :tone="summary ? (summary.toolFailures ? 'warning' : 'success') : undefined"
          :value="number(summary?.toolFailures)"
          :href="summary?.toolFailures ? filtered({ vysledek: 'failed' }) : undefined"
          data-stat="tool-failures"
        >
          <template v-if="summary">{{ summary.toolFailures ? t.activity.showInAudit : t.activity.allPassed }}</template>
          <template v-if="charts?.failures.length" #chart>
            <svg class="mini-ring" viewBox="0 0 36 36" width="46" height="46">
              <g transform="rotate(-90 18 18)">
                <circle class="ring-track" cx="18" cy="18" r="15" pathLength="100" />
                <circle v-for="(arc, index) in charts.failures" :key="index" class="ring-arc" :data-tone="arc.tone" cx="18" cy="18" r="15" pathLength="100" :stroke-dasharray="arc.dash" :stroke-dashoffset="arc.offset" />
              </g>
              <text x="18" y="18" class="ring-label">{{ charts.failureShare }}</text>
            </svg>
          </template>
        </StatTile>
      </div>
      <InlineReveal v-if="summary" id="activity-summary-detail" v-model:open="open">
        <div class="summary-lists">
          <SettingsSection :title="t.activity.topEditors" icon="account" data-summary-list="editors">
            <ol v-if="summary.topEditors.length" class="rank-list">
              <li v-for="person in people(summary.topEditors)" :key="person.userId"><span class="rank-name">{{ person.name }}</span><span class="rank-bar" :style="{ width: person.bar }" /><span class="rank-count">{{ t.counts.edits(person.count) }}</span></li>
            </ol>
            <p v-else class="rank-empty">{{ t.activity.nobodyEdited }}</p>
            <p v-if="summary.revisionChannels.length" class="rank-note">{{ split(summary.revisionChannels) }}</p>
          </SettingsSection>
          <SettingsSection :title="t.activity.toolUsers" icon="users" data-summary-list="tool-users">
            <ol v-if="summary.topToolUsers.length" class="rank-list">
              <li v-for="person in people(summary.topToolUsers)" :key="person.userId">
                <a class="rank-name" :href="filtered({ kdo: person.userId })" :title="t.activity.showCalls(person.name)">{{ person.name }}</a><span class="rank-bar" :style="{ width: person.bar }" /><span class="rank-count">{{ t.counts.calls(person.count) }}</span>
              </li>
            </ol>
            <p v-else class="rank-empty">{{ t.activity.nobodyCalled }}</p>
          </SettingsSection>
          <SettingsSection :title="t.activity.topTools" icon="plug" data-summary-list="tools">
            <ol v-if="summary.topTools.length" class="rank-list">
              <li v-for="tool in summary.topTools" :key="`${tool.channel}:${tool.operation}`">
                <span class="rank-name"><code class="st-mono">{{ tool.operation }}</code><small>{{ channel(tool.channel) }}</small></span>
                <span class="rank-bar" :style="{ width: width(tool.calls, summary.topTools) }" />
                <span class="rank-count">{{ t.counts.calls(tool.calls) }}<small v-if="tool.failures" class="rank-failed">{{ t.counts.failed(tool.failures) }}</small></span>
              </li>
            </ol>
            <p v-else class="rank-empty">{{ t.activity.noToolCalls }}</p>
          </SettingsSection>
          <SettingsSection :title="t.activity.opens" icon="eye" data-summary-list="opens">
            <ol v-if="summary.pageOpens.length" class="rank-list">
              <li v-for="item in summary.pageOpens" :key="item.key"><span class="rank-name">{{ channel(item.key) }}</span><span class="rank-bar" :style="{ width: width(item.count, summary.pageOpens) }" /><span class="rank-count">{{ t.counts.opens(item.count) }}</span></li>
            </ol>
            <p v-else class="rank-empty">{{ t.activity.nobodyOpened }}</p>
          </SettingsSection>
        </div>
      </InlineReveal>
    </template>
  </section>
</template>

<style scoped>
.activity-summary { display: grid; gap: 12px; min-width: 0; }
.summary-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-height: 28px; }
.summary-head .st-group-label { margin: 0; }
.summary-toggle { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border: 0; border-radius: 8px; background: transparent; color: var(--accent); font: inherit; font-size: 13px; font-weight: 600; cursor: pointer; transition: background-color var(--atlas-motion-fast); }
.summary-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
@media (hover: hover) and (pointer: fine) { .summary-toggle:hover { background: var(--hover); } }
.summary-chevron { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
[aria-expanded="true"] > .summary-chevron { rotate: 0deg; transition-duration: var(--atlas-motion-slow); }
@media (prefers-reduced-motion: reduce) { .summary-chevron { transition: none; } }
.summary-tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; }
.summary-lists { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; padding-top: 2px; }
@container settings-shell (max-width: 900px) { .summary-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); } .summary-lists { grid-template-columns: minmax(0, 1fr); } }

/* Small charts beside the figures (StatTile #chart): they only repeat what the text says. */
[data-tone="accent"], [data-tone="ch-0"] { --tone: var(--accent); }
[data-tone="ch-1"] { --tone: color-mix(in srgb, var(--accent) 58%, var(--surface)); }
[data-tone="ch-2"] { --tone: color-mix(in srgb, var(--accent) 32%, var(--surface)); }
[data-tone="ch-3"] { --tone: color-mix(in srgb, var(--muted) 45%, var(--surface)); }
[data-tone="success"] { --tone: var(--success); }
[data-tone="warning"] { --tone: var(--warning); }
.mini-ring { display: block; overflow: visible; }
.ring-track, .ring-arc { fill: none; stroke-width: 4.5; }
.ring-track { stroke: color-mix(in srgb, var(--muted) 18%, transparent); }
.ring-arc { stroke: var(--tone); transition: stroke-dasharray var(--atlas-motion-slow) var(--atlas-ease-out), stroke-dashoffset var(--atlas-motion-slow) var(--atlas-ease-out); }
.ring-label { fill: var(--text); font-size: 8.5px; font-weight: 650; text-anchor: middle; dominant-baseline: central; font-variant-numeric: tabular-nums; }
.mini-legend > span { white-space: nowrap; }
.mini-legend i { display: inline-block; width: 8px; height: 8px; margin-right: 5px; border-radius: 50%; background: var(--tone); }
.mini-bars { display: flex; align-items: flex-end; gap: 3px; height: 40px; }
.mini-bars > i { width: 6px; border-radius: 3px 3px 1px 1px; background: color-mix(in srgb, var(--accent) 40%, var(--surface)); transition: height var(--atlas-motion-slow) var(--atlas-ease-out); }
.mini-bars > i:first-child { background: var(--accent); }

.rank-list { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }
.rank-list > li {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  grid-template-areas: "name count" "bar bar";
  gap: 3px 12px;
  font-size: 13.5px;
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
}
.rank-name { grid-area: name; display: flex; align-items: baseline; gap: 8px; min-width: 0; overflow-wrap: anywhere; }
.rank-name small { color: var(--muted); font-size: 12px; }
a.rank-name { color: var(--accent); font-weight: 600; text-decoration: none; }
a.rank-name:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; border-radius: 3px; }
@media (hover: hover) and (pointer: fine) {
  a.rank-name:hover { text-decoration: underline; }
}
.rank-count { grid-area: count; display: flex; gap: 8px; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; white-space: nowrap; }
.rank-failed { color: var(--warning); font-weight: 600; }
.rank-bar { grid-area: bar; height: 4px; border-radius: 999px; background: var(--accent); opacity: .55; transition: width var(--atlas-motion-slow) var(--atlas-ease-out); }
.rank-empty, .rank-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.rank-note { margin-top: 10px; }
</style>
