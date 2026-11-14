<script setup lang="ts">
import { computed } from "vue";
import type { LogEntry } from "../../../logs/logApi";
import t from "../../../i18n/settings-logs";
import { useSettingsRoute } from "../useSettingsRoute";
import { Tooltip, TooltipContent, TooltipTrigger } from "../../ui/tooltip";
import { exceptionLines, levelLabel, logTime, shortCategory } from "./logFormat";

/**
 * One line of the server log as a log reads: time, the level as a label, the category, the message; a long line wraps under the message, and
 * the exception follows line by line (a wrapped frame stays indented). The level colours the line, nothing else does. The request id (short)
 * filters by the request; a line of a job run names the job and opens the run, unless that run is the page being read.
 */
const props = defineProps<{ entry: LogEntry; tabbable?: boolean }>();
const emit = defineEmits<{ trace: [traceId: string] }>();
const router = useSettingsRoute();
const runPath = computed(() => {
  const { jobId, jobRunId } = props.entry;
  const path = jobId && jobRunId ? `general/jobs/${encodeURIComponent(jobId)}/runs/${jobRunId}` : "";
  return path && router.route.value.path !== path ? path : "";
});
const exception = computed(() => (props.entry.exception ? exceptionLines(props.entry.exception)
  : props.entry.exceptionType ? [{ text: `${props.entry.exceptionType}: ${props.entry.exceptionMessage ?? ""}`, frame: false }] : []));
</script>

<template>
  <li class="log-line" :data-level="entry.level" :data-log-row="entry.id" :tabindex="tabbable ? 0 : -1">
    <time class="log-time" :datetime="entry.at" :title="entry.at">{{ logTime(entry.at) }}</time>
    <span class="log-level" data-log-level>{{ levelLabel(entry.level) }}</span>
    <div class="log-text">
      <span class="log-category" :title="entry.category">{{ shortCategory(entry.category) }}</span>
      <Tooltip v-if="runPath">
        <TooltipTrigger as-child><a class="log-job" :href="router.href(runPath)" :tabindex="tabbable ? 0 : -1" data-log-job>{{ entry.jobTitle ?? entry.jobId }}</a></TooltipTrigger>
        <TooltipContent>{{ t.row.openRun(entry.jobRunId!) }}</TooltipContent>
      </Tooltip>
      <span class="log-message">{{ entry.message }}</span>
      <Tooltip v-if="entry.traceId">
        <TooltipTrigger as-child><button type="button" class="log-trace" :tabindex="tabbable ? 0 : -1" :aria-label="t.row.onlyRequest(entry.traceId)" data-log-trace @click="emit('trace', entry.traceId)">{{ entry.traceId.slice(0, 8) }}</button></TooltipTrigger>
        <TooltipContent>{{ t.row.onlyRequest(entry.traceId) }}</TooltipContent>
      </Tooltip>
      <span v-if="exception.length" class="log-exception" data-log-exception>
        <span v-for="(line, index) in exception" :key="index" class="log-exception-line" :class="{ 'is-frame': line.frame }">{{ line.text }}</span>
      </span>
    </div>
  </li>
</template>

<style scoped>
.log-line { display: grid; grid-template-columns: auto 5ch minmax(0, 1fr); column-gap: 1ch; padding: 1px 10px; font-family: var(--font-mono, ui-monospace, monospace); font-size: 12.5px; line-height: 1.55; }
@media (hover: hover) and (pointer: fine) { .log-line:hover { background: var(--hover); } }
.log-time { color: var(--muted); white-space: nowrap; }
.log-level { align-self: start; margin-top: 2px; padding: 0 3px; border-radius: 3px; font-size: 10.5px; font-weight: 700; line-height: 1.6; text-align: center; color: var(--muted); }
.log-text { min-width: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.log-category { color: var(--muted); }
.log-category::after { content: " "; }
.log-job { margin-right: 1ch; color: var(--accent); }
.log-trace { margin-left: 1ch; padding: 0; border: 0; background: none; color: var(--muted); font: inherit; font-size: 11.5px; text-decoration: underline dotted; cursor: pointer; }
.log-job:focus-visible, .log-trace:focus-visible { outline: var(--state-focus-ring); outline-offset: 1px; }
.log-exception { display: block; }
.log-exception-line { display: block; padding-left: 2ch; text-indent: -2ch; }
.log-exception-line.is-frame { padding-left: 6ch; text-indent: -2ch; opacity: .85; }
/* Narrow: time and level on the first line, the text under them in the whole width. */
@container log-list (max-width: 560px) {
  .log-line { padding-block: 3px; border-top: 1px solid var(--line); }
  .log-line:first-child { border-top: 0; }
  .log-text { grid-column: 1 / -1; }
  .log-exception-line.is-frame { padding-left: 4ch; }
}

.log-line[data-level="information"] .log-level { background: var(--hover); color: var(--text); }
.log-line[data-level="warning"] .log-level { background: var(--warning-bg); color: var(--warning); }
.log-line[data-level="warning"] .log-message { color: var(--warning); }
.log-line[data-level="error"] .log-level, .log-line[data-level="critical"] .log-level { background: var(--error); color: var(--on-accent); }
.log-line[data-level="error"] :is(.log-message, .log-exception), .log-line[data-level="critical"] :is(.log-message, .log-exception) { color: var(--error); }
.log-line[data-level="debug"], .log-line[data-level="trace"] { color: var(--muted); }
</style>
