<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import type { AiLimitRow, AiPersonalUsage } from "../../administration";
import { onAiUsageChanged, useAiUsageLoader } from "../../ai/quotaUsage";
import aiQuota from "../../i18n/ai-quota";
import { formatDateTime } from "../../formatDate";
import LoadingSkeleton from "../LoadingSkeleton.vue";
const props = defineProps<{ task?: AiLimitRow["task"]; value?: AiPersonalUsage; compact?: boolean; contextKey?: string }>();
const load = useAiUsageLoader();
const loaded = ref<AiPersonalUsage>(), failed = ref(false), fetching = ref(false);
const current = computed(() => props.value ?? loaded.value);
const rows = computed(() => current.value?.limits.filter(row => !props.task || row.task === props.task) ?? []);
const labels = (task: AiLimitRow["task"]) => aiQuota.quota.tasks[task] ?? task;
let request: AbortController | undefined;
let resetTimer: ReturnType<typeof setTimeout> | undefined;
function clearResetTimer() { clearTimeout(resetTimer); resetTimer = undefined; }
function scheduleReset(value: AiPersonalUsage) {
  clearResetTimer();
  const now = Date.now();
  // A stale/past server date must not create an immediate refresh loop.
  const next = Math.min(...value.limits.map(row => Date.parse(row.resetAt)).filter(at => Number.isFinite(at) && at > now));
  const delay = next - now;
  if (Number.isFinite(delay) && delay <= 2_147_483_647) resetTimer = setTimeout(() => { resetTimer = undefined; void refresh(); }, delay);
}
async function refresh() {
  request?.abort();
  clearResetTimer();
  if (props.value) { failed.value = false; return; }
  const pending = request = new AbortController();
  fetching.value = true;
  try { const value = await load(pending.signal); if (!pending.signal.aborted) { loaded.value = value; failed.value = false; scheduleReset(value); } }
  catch { if (!pending.signal.aborted) { loaded.value = undefined; failed.value = true; } }
  finally { if (request === pending) fetching.value = false; }
}
function origin(row: AiLimitRow) { return row.origin === "user" ? aiQuota.quota.user : row.origin === "group" ? aiQuota.quota.group(row.groupName ?? "") : aiQuota.quota.wikiDefault; }
const finiteRows = computed(() => current.value?.enabled ? rows.value.filter(row => row.remaining !== null && row.effective !== null && row.effective > 0) : []);
const compactLabel = computed(() => finiteRows.value.map(row => aiQuota.quota.remaining(labels(row.task), row.remaining, row.effective)).join('; '));
function remainingPercent(row: AiLimitRow) { return Math.max(0, Math.min(100, 100 * (row.remaining ?? 0) / (row.effective || 1))); }
const unsubscribe = onAiUsageChanged(() => { void refresh(); });
watch(() => [props.contextKey, props.task, props.value], refresh);
onMounted(() => { void refresh(); window.addEventListener("focus", refresh); });
onBeforeUnmount(() => { request?.abort(); clearResetTimer(); unsubscribe(); window.removeEventListener("focus", refresh); });
</script>
<template>
  <details v-if="compact && finiteRows.length" class="ai-quota-compact">
    <summary :aria-label="compactLabel" :title="compactLabel">
      <svg v-for="row in finiteRows" :key="row.task" viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
        <circle cx="12" cy="12" r="9" class="quota-track" />
        <circle cx="12" cy="12" r="9" pathLength="100" data-remaining :stroke-dasharray="`${remainingPercent(row)} 100`" transform="rotate(-90 12 12)" />
      </svg>
    </summary>
    <div class="quota-detail">
      <strong>{{ aiQuota.quota.today }}</strong>
      <p v-for="row in finiteRows" :key="row.task">{{ aiQuota.quota.remaining(labels(row.task), row.remaining, row.effective) }}<small>{{ aiQuota.quota.resets(formatDateTime(row.resetAt)) }}</small></p>
    </div>
  </details>
  <div v-else-if="!compact" class="ai-quota-status" data-ai-quota-status aria-live="polite">
    <LoadingSkeleton v-if="fetching && !current && !failed" :rows="1" row-height="22px" />
    <p v-if="failed" class="ai-quota-note">{{ aiQuota.quota.failed }}</p>
    <p v-else-if="current && !current.enabled" class="ai-quota-note">{{ aiQuota.quota.disabled }}</p>
    <template v-if="current?.enabled">
      <p v-for="row in rows" :key="row.task" class="ai-quota-note" :data-ai-quota-task="row.task">
        <strong v-if="!task">{{ labels(row.task) }}: </strong>
        <span>{{ row.remaining === null ? aiQuota.quota.noLimit : aiQuota.quota.remainingShort(row.remaining, row.effective) }}</span>
        <span> · {{ aiQuota.quota.usedToday(row.used) }} · {{ origin(row) }} · {{ aiQuota.quota.resets(formatDateTime(row.resetAt)) }}</span>
      </p>
    </template>
  </div>
</template>
<style scoped>
.ai-quota-compact { position: relative; flex: none; }
.ai-quota-compact summary { display: flex; gap: 2px; cursor: pointer; list-style: none; border-radius: 8px; padding: 4px; }
.ai-quota-compact summary::-webkit-details-marker { display: none; }
.ai-quota-compact summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.ai-quota-compact circle { fill: none; stroke: var(--accent); stroke-width: 2; stroke-linecap: round; }
.ai-quota-compact .quota-track { stroke: var(--line); }
.quota-detail { position: absolute; bottom: calc(100% + 10px); right: 0; width: min(260px, calc(var(--atlas-sidebar-width, 292px) - 32px), calc(100vw - 32px)); box-sizing: border-box; overflow-wrap: anywhere; padding: 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); color: var(--text); box-shadow: var(--shadow-menu); z-index: 30; font-size: 12px; }
/* The card appeared at once (only the faint 140 ms fade of every open <details>): it grows out of the rings it hangs from, the way a menu
   grows from its trigger (the tokens and the keyframes of motion.css), and under reduced motion it only fades. */
.ai-quota-compact[open] .quota-detail { transform-origin: 100% 100%; animation: atlas-menu-in var(--atlas-motion-surface) var(--atlas-ease-out); }
@media (prefers-reduced-motion: reduce) { .ai-quota-compact[open] .quota-detail { animation-name: atlas-backdrop-in; } }
:where([data-atlas-reduced-motion]) .ai-quota-compact[open] .quota-detail { animation-name: atlas-backdrop-in; }
.quota-detail p { margin: 10px 0 0; }
.quota-detail small { display: block; color: var(--muted); margin-top: 3px; }
.ai-quota-status { min-width: 0; }
.ai-quota-note { margin: 6px 0; color: var(--muted); font-size: 12px; line-height: 1.5; overflow-wrap: anywhere; }
</style>
