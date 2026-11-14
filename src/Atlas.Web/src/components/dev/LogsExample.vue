<script setup lang="ts">
import { onBeforeUnmount, provide } from "vue";
import { createFakeLiveFeed } from "../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../live/liveFeed";
import type { LogApi, LogEntry, LogFilter, LogLevel, LogSummary } from "../../logs/logApi";
import LogsPage from "../settings/logs/LogsPage.vue";

// Local sample data only: an in-memory log the page speaks through; nothing leaves the gallery. 300 entries, newest first, a request id shared by
// a warning, an error with an exception and a critical entry; a new entry is stored every 4 s and told as the server tells it (a fake /hubs/live event, LogsWritten), which „Živě“ follows.
const order: LogLevel[] = ["trace", "debug", "information", "warning", "error", "critical"];
const stack = [
  "System.IO.IOException: Úložiště neodpovídá.",
  "   at System.IO.RandomAccess.WriteAtOffset(SafeFileHandle handle, ReadOnlySpan`1 buffer, Int64 fileOffset)",
  "   at Atlas.Server.Features.Backups.ZipBackupJob.RunAsync(JobContext context, CancellationToken ct) in ZipBackupJob.cs:line 88",
  "   at Atlas.Server.Platform.Jobs.JobRunner.RunAsync(Int64 runId) in JobRunner.cs:line 141",
  "   at System.Threading.Tasks.Task.ExecuteWithThreadLocal(Task& currentTaskSlot, Thread threadPoolThread)",
].join("\n");
const trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
let nextId = 300;
const at = (seconds: number) => new Date(Date.now() - seconds * 1000).toISOString();
const entry = (id: number, level: LogLevel, category: string, message: string, over: Partial<LogEntry> = {}): LogEntry => ({
  id, at: at((300 - id) * 7 + 3), level, category, eventId: null, message, exceptionType: null, exceptionMessage: null, exception: null, traceId: null, userId: null, jobRunId: null, jobId: null, jobTitle: null, ...over,
});
const samples: Array<[LogLevel, string, string, Partial<LogEntry>?]> = [
  ["information", "Atlas.Server.Platform.Jobs.JobRunner", "Úloha ai.index doběhla za 41 s"],
  ["information", "Microsoft.AspNetCore.Hosting.Diagnostics", "Request finished HTTP/1.1 GET /api/pages/tree - 200 in 12 ms"],
  ["debug", "Atlas.Server.Features.Search.SearchIndex", "Přepočítáno 12 vektorů"],
  ["warning", "Atlas.Server.Features.Backups.ZipBackupJob", "Profil úložiště neodpovídá, zkouším znovu", { traceId: trace, jobRunId: 3, jobId: "backups.zip", jobTitle: "Záloha ZIP" }],
  ["information", "Atlas.Server.Platform.Auth.SessionService", "Přihlášen uživatel", { userId: "u-jana" }],
  ["trace", "Microsoft.EntityFrameworkCore.Query", "Executed DbCommand (2ms) SELECT p.id FROM pages p"],
];
const all: LogEntry[] = Array.from({ length: 300 }, (_, index) => {
  const id = 300 - index;
  const [level, category, message, over] = samples[id % samples.length]!;
  return entry(id, level, category, message, over);
});
all.splice(1, 0,
  entry(0, "critical", "Atlas.Server.Platform.Hosting.Watchdog", "Fronta zápisu je plná, záznamy se zahazují", { traceId: trace }),
  entry(0, "error", "Atlas.Server.Features.Backups.ZipBackupJob", "Záloha ZIP selhala po 5 pokusech", { traceId: trace, jobRunId: 3, jobId: "backups.zip", jobTitle: "Záloha ZIP", exceptionType: "System.IO.IOException", exceptionMessage: "Úložiště neodpovídá.", exception: stack }),
);
all.forEach((item, index) => { item.id = 400 - index; item.at = at(index * 7 + 3); });

const matches = (item: LogEntry, filter: LogFilter) =>
  order.indexOf(item.level) >= order.indexOf(filter.level ?? "trace") && (!filter.category || item.category.startsWith(filter.category))
  && (!filter.q || `${item.message} ${item.exception ?? ""}`.toLowerCase().includes(filter.q.toLowerCase())) && (!filter.traceId || item.traceId === filter.traceId)
  && (filter.jobRunId === undefined || item.jobRunId === filter.jobRunId) && (!filter.exception || !!item.exceptionType);
const summary = (): LogSummary => ({
  errorsHour: all.filter((item) => item.level === "error" || item.level === "critical").length, warningsHour: 4, errorsDay: 6, warningsDay: 31, dropped: 128,
});
const feed = createFakeLiveFeed();
provide(liveFeedSourceKey, feed.source);
const storing = setInterval(() => {
  all.unshift(entry(++nextId + 100, "information", "Atlas.Server.Platform.Jobs.JobRunner", `Nový záznam ${nextId}`, { at: new Date().toISOString() }));
  feed.emit("logs", { level: "information" });
}, 4000);
onBeforeUnmount(() => clearInterval(storing));
const api: LogApi = {
  async list(filter, cursor = {}, take = 100) {
    const found = all.filter((item) => matches(item, filter) && (cursor.before === undefined || item.id < cursor.before) && (cursor.after === undefined || item.id > cursor.after));
    const page = found.slice(0, take);
    return { entries: page, more: found.length > take, summary: cursor.before === undefined && cursor.after === undefined ? summary() : null };
  },
  exportUrl: () => "#gallery-log-export",
};
</script>

<template>
  <div class="logs-example"><LogsPage base="layouts/logs" :api="api" /></div>
</template>

<style scoped>
.logs-example { padding: 16px; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); }
</style>
