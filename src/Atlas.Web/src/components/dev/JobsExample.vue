<script setup lang="ts">
import { computed, onBeforeUnmount, provide } from "vue";
import type { LogApi, LogEntry, LogLevel } from "../../logs/logApi";
import type { Job, JobApi, JobLogEntry, JobRun, JobState, JobSummary, SystemService } from "../../jobs/jobApi";
import JobsPage from "../settings/jobs/JobsPage.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import { createSettingsFlash, provideSettingsFlash } from "../settings/flash";
import { useSettingsRoute } from "../settings/useSettingsRoute";
import { createFakeLiveFeed } from "../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../live/liveFeed";

// Local sample data only: an in-memory job runtime the page speaks through; nothing leaves the gallery. The summary on top is counted from it. A run you start goes queued → running →
// succeeded over five ticks of 1,5 s, each told to the page as the server tells it (a fake /hubs/live event, JobRunChanged), so the state changes
// can be watched; „Propojit kontakty“ is already running when you arrive. A retry goes to the job's queued run when it has one (start
// „Záloha ZIP“ first, then retry its failed run: the page opens the queued one), the way the real runtime merges it; a paused job's waiting
// run waits.
const router = useSettingsRoute();
// The shell is not here, so the example shows the result bar itself, as the shell does.
const flash = createSettingsFlash(router);
provideSettingsFlash(flash);
/** The path below `layouts/jobs`: the list, `{id}`, or `{id}/runs/{runId}`. */
const part = computed(() => router.route.value.segments.slice(2));

const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();
const ahead = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
let nextRun = 10;
const run = (id: number, jobId: string, state: JobState, over: Partial<JobRun> = {}): JobRun => ({
  id, jobId, trigger: "schedule", reason: null, state, attempt: 1, spaceId: null, queuedAt: ago(30), startedAt: ago(30), heartbeatAt: null, finishedAt: ago(29),
  cancelRequested: false, summary: null, ...over,
});
const job = (id: string, title: string, description: string, schedule: string, over: Partial<Job> = {}): Job => ({
  id, title, description, module: "platform", schedule, retry: "Až 5 pokusů, první odklad 30 s, každý další dvojnásobný", known: true, paused: false, nextDueAt: null,
  completedAt: null, lastRun: null, recurring: false, lastSuccessAt: null, typicalSeconds: null, overdue: false, stuck: false, ...over,
});
// Recurring: a failed backup, an overdue one (its due time passed 3 h ago), an index stuck for 2 h where 4 min is usual, a paused one. One-off: a
// running link run, a migration's conversion and an import that are done (folded under „Hotové a nečinné“), a job this version no longer knows.
const jobs: Job[] = [
  job("backups.zip", "Záloha ZIP", "Zapíše zálohu wiki do archivu ZIP v nastavený čas.", "Podle nastavení funkce", { recurring: true, nextDueAt: ahead(60 * 14), lastSuccessAt: ago(2040), lastRun: run(3, "backups.zip", "failed", { queuedAt: ago(600), startedAt: ago(600), finishedAt: ago(598) }) }),
  job("backups.git", "Záloha do Gitu", "Zapíše změny wiki do repozitáře zálohy.", "Podle nastavení funkce", { recurring: true, nextDueAt: ago(180), overdue: true, lastSuccessAt: ago(60 * 27), lastRun: run(11, "backups.git", "succeeded", { queuedAt: ago(60 * 27), startedAt: ago(60 * 27), finishedAt: ago(60 * 27 - 2) }) }),
  job("ai.index", "Indexace pro AI", "Přepočítá vektory změněných stránek pro hledání a asistenta.", "Každých 5 min", { recurring: true, typicalSeconds: 240, stuck: true, lastSuccessAt: ago(130), lastRun: run(12, "ai.index", "running", { queuedAt: ago(120), startedAt: ago(120), finishedAt: null, heartbeatAt: ago(1) }) }),
  job("retention", "Úklid provozních logů", "Maže provozní logy podle stáří a počtu.", "Každou hodinu", { recurring: true, nextDueAt: ahead(40), lastSuccessAt: ago(29), lastRun: run(5, "retention", "succeeded", { summary: { removed: 1240 } }) }),
  job("youtrack.refresh", "Obnova karet YouTracku", "Obnoví karty a nástěnky s issues.", "Každých 15 min", { recurring: true, paused: true, lastSuccessAt: ago(299), lastRun: run(8, "youtrack.refresh", "succeeded", { queuedAt: ago(300), startedAt: ago(300), finishedAt: ago(299) }) }),
  job("contacts.link", "Propojit kontakty ve stránkách", "Doplní do stránek odkazy na kontakty v adresáři.", "Po události nebo ručně",
    { lastRun: run(7, "contacts.link", "running", { trigger: "manual", reason: "Spustil(a) Jana Nováková", finishedAt: null, heartbeatAt: ago(0), summary: { pagesDone: 120, pages: 400 } }) }),
  job("pages.requirements-overview", "Převod přehledů požadavků", "Po aktualizaci převede staré přehledy požadavků na nový blok.", "Jednou při startu, dokud neuspěje", { completedAt: ago(60 * 24 * 2), lastRun: run(13, "pages.requirements-overview", "succeeded", { trigger: "startup", summary: { pagesDone: 34 } }) }),
  job("wikijs.import", "Import z Wiki.js", "Načte stránky a přílohy ze staré wiki.", "Po události nebo ručně", { lastRun: run(14, "wikijs.import", "cancelled", { trigger: "manual", reason: "Spustil(a) Jana Nováková" }) }),
  job("old.migration", "Neznámá úloha", "Úloha old.migration, kterou tato verze Atlasu už nezná; zůstává jen její historie.", "—", { known: false, completedAt: ago(60 * 24 * 3), lastRun: run(9, "old.migration", "succeeded") }),
];
const history = new Map<string, JobRun[]>(jobs.map((item) => [item.id, item.lastRun ? [item.lastRun, ...(item.id === "backups.zip" ? [run(2, item.id, "succeeded", { queuedAt: ago(2040) }), run(1, item.id, "succeeded", { queuedAt: ago(3480) })] : [])] : []]));
const logs = new Map<number, JobLogEntry[]>([
  [3, [{ at: ago(600), level: "Information", message: "Záloha začala, cíl: profil „Sdílená složka“" }, { at: ago(599), level: "Warning", message: "Profil úložiště neodpovídá, zkouším znovu" }, { at: ago(598), level: "Error", message: "Zápis do úložiště selhal po 5 pokusech" }]],
  [7, [{ at: ago(2), level: "Information", message: "Propojeno 120 stránek ze 400" }]],
]);
const errors = new Map<number, string>([[3, "System.IO.IOException: Úložiště neodpovídá.\n   at Atlas.Server.Features.Backups.ZipBackupJob.RunAsync(JobContext context, CancellationToken ct)\n   at Atlas.Server.Platform.Jobs.JobRunner.RunAsync(Int64 runId)"]]);
/** The server log of a run as the log viewer reads it: the lines the run wrote (ids grow with time), the failure with its stack. */
const levelOf: Record<string, LogLevel> = { Information: "information", Warning: "warning", Error: "error", Critical: "critical" };
const logLines = (runId: number): LogEntry[] => {
  const owner = [...history.values()].flat().find((item) => item.id === runId);
  return (logs.get(runId) ?? []).map((line, index): LogEntry => {
    const failed = line.level === "Error";
    return {
      id: runId * 1000 + index, at: line.at, level: levelOf[line.level] ?? "information", category: "Atlas.Server.Features.Backups.ZipBackupJob", eventId: null, message: line.message,
      exceptionType: failed ? "System.IO.IOException" : null, exceptionMessage: failed ? "Úložiště neodpovídá." : null, exception: failed ? errors.get(runId) ?? null : null,
      traceId: null, userId: null, jobRunId: runId, jobId: owner?.jobId ?? null, jobTitle: jobs.find((item) => item.id === owner?.jobId)?.title ?? null,
    };
  }).reverse();
};
const logApi: LogApi = {
  async list(filter, cursor = {}, take = 100) {
    const found = logLines(filter.jobRunId ?? -1).filter((item) => (cursor.before === undefined || item.id < cursor.before) && (cursor.after === undefined || item.id > cursor.after));
    return { entries: found.slice(0, take), more: found.length > take, summary: null };
  },
  exportUrl: () => "#gallery-job-log-export",
};
const services: SystemService[] = [
  { id: "live-refresh", title: "Živé odběry", description: "Každých 5 s znovu ověří práva živých odběrů otevřených stránek.", interval: "Každých 5 s", state: "ok", lastBeatAt: ago(0), error: null, detail: null },
  { id: "outbox", title: "Doručování událostí", description: "Doručuje události z outboxu jejich konzumentům hned po uložení.", interval: null, state: "ok", lastBeatAt: null, error: null, detail: "Čeká 0, mrtvé 0" },
  { id: "ai-probe", title: "Dostupnost AI", description: "Každých 5 s ověří, že modely AI odpovídají.", interval: "Každých 5 s", state: "unresponsive", lastBeatAt: ago(3), error: null, detail: null },
  { id: "search-misses", title: "Neúspěšná hledání", description: "Každých 10 s zapíše do databáze hledání, která nic nenašla.", interval: "Každých 10 s", state: "failed", lastBeatAt: ago(0), error: "Zápis do databáze selhal", detail: null },
];

/** The top of the page as the server counts it, from the runs above; the 14 days are a fixed sample plus what failed here. */
const sample = [26, 25, 27, 24, 26, 25, 28, 26, 24, 27, 25, 26, 23, 12];
function summary(): JobSummary {
  const all = [...history.values()].flat();
  const within = (item: JobRun, hours: number) => !!item.finishedAt && Date.now() - Date.parse(item.finishedAt) < hours * 3_600_000;
  const failed = all.filter((item) => item.state === "failed" && within(item, 24 * 7)).sort((a, b) => Date.parse(b.finishedAt!) - Date.parse(a.finishedAt!));
  return {
    running: all.filter((item) => item.state === "running").length, queued: all.filter((item) => item.state === "queued").length,
    failedDay: failed.filter((item) => within(item, 24)).length, failedWeek: failed.length,
    days: sample.map((succeeded, index) => ({ day: new Date(Date.now() - (13 - index) * 86_400_000).toISOString().slice(0, 10), succeeded, failed: index === 13 ? failed.filter((item) => within(item, 24)).length : [0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0][index]! })),
    failures: failed.map((item) => ({ runId: item.id, jobId: item.jobId, jobTitle: jobs.find((owner) => owner.id === item.jobId)!.title, finishedAt: item.finishedAt!, error: errors.get(item.id)?.split("\n")[0] ?? null })),
  };
}

/** The runtime: every tick moves a queued run to running and a running one nearer its end (the runs this example started only), and tells it
 * as the server does. */
const polls = new Map<number, number>();
const feed = createFakeLiveFeed();
provide(liveFeedSourceKey, feed.source);
function tick() {
  for (const [jobId, runs] of history) {
    for (const current of runs) {
      const seen = polls.get(current.id);
      if (seen === undefined || !["queued", "running"].includes(current.state)) continue;
      if (current.state === "queued" && jobs.find((item) => item.id === jobId)?.paused && current.trigger !== "manual") continue;
      polls.set(current.id, seen + 1);
      const entries = logs.get(current.id) ?? [];
      if (seen === 0) Object.assign(current, { state: "running", startedAt: new Date().toISOString(), heartbeatAt: new Date().toISOString() });
      if (seen === 4) Object.assign(current, { state: "succeeded", finishedAt: new Date().toISOString(), summary: { tasks: 5 } });
      logs.set(current.id, [...entries, { at: new Date().toISOString(), level: "Information", message: seen === 4 ? "Hotovo" : "Zpracováno " + (seen + 1) }]);
      const owner = jobs.find((item) => item.id === jobId)!;
      if (owner.lastRun?.id === current.id) owner.lastRun = { ...current };
      feed.emit("jobs", { runId: current.id, jobId, state: current.state, spaceId: current.spaceId });
      feed.emit("logs", { level: "information" });
    }
  }
}
const ticking = setInterval(tick, 1500);
onBeforeUnmount(() => clearInterval(ticking));
const start = (jobId: string, over: Partial<JobRun> = {}) => {
  const created = run(nextRun++, jobId, "queued", { trigger: "manual", reason: "Spustil(a) Jana Nováková", queuedAt: new Date().toISOString(), startedAt: null, finishedAt: null, ...over });
  history.set(jobId, [created, ...(history.get(jobId) ?? [])]);
  jobs.find((item) => item.id === jobId)!.lastRun = created;
  polls.set(created.id, 0);
  return { runId: created.id };
};
const find = (jobId: string, runId: number) => history.get(jobId)?.find((item) => item.id === runId)!;
const api: JobApi = {
  async list() { return { jobs: jobs.map((item) => ({ ...item })), systemServices: services, summary: summary() }; },
  async runs(jobId) { const runs = history.get(jobId) ?? []; return { job: { ...jobs.find((item) => item.id === jobId)! }, runs: runs.map((item) => ({ ...item })), total: runs.length, page: 1, pageSize: 20, lastSuccessAt: runs.find((item) => item.state === "succeeded")?.finishedAt ?? null }; },
  async run(jobId, runId) { const current = find(jobId, runId); return { run: { ...current }, error: errors.get(runId) ?? null, log: logs.get(runId) ?? [], paused: jobs.find((item) => item.id === jobId)!.paused }; },
  async runNow(jobId) { return start(jobId); },
  async retry(jobId, runId) {
    const queued = history.get(jobId)?.find((item) => item.state === "queued");
    if (queued) return { runId: queued.id };
    const current = find(jobId, runId);
    Object.assign(current, { state: "queued", finishedAt: null, startedAt: null });
    polls.set(runId, 0);
    errors.delete(runId);
    return { runId };
  },
  async cancel(jobId, runId) { Object.assign(find(jobId, runId), { state: "cancelled", finishedAt: new Date().toISOString() }); return { runId }; },
  async setPaused(jobId, paused) { jobs.find((item) => item.id === jobId)!.paused = paused; },
};
polls.set(7, 1);
</script>

<template>
  <div class="jobs-example"><ResultBanner v-if="flash.message.value" :key="flash.message.value.key" :message="flash.message.value.text" :icon="flash.message.value.icon" @dismiss="flash.dismiss()" /><JobsPage :key="part.join('/')" :part="part" base="layouts/jobs" :api="api" :log-api="logApi" /></div>
</template>

<style scoped>
.jobs-example { padding: 16px; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); }
</style>
