import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import type { LogApi, LogEntry, LogFilter } from "../../../logs/logApi";
import type { Job, JobApi, JobRun, JobSummary, SystemService } from "../../../jobs/jobApi";
import { mountPage } from "../general/testing";
import type { SettingsFlash } from "../flash";
import { useJobsIssues } from "../global/issues";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import JobsPage from "./JobsPage.vue";
import { failuresSince, groupJobs, humanize, jobHeads, matchesJob, nextText, returnFocus, summaryText, whenText } from "./jobFormat";

let wrapper: VueWrapper | undefined;
/** /hubs/live as the pages hear it: `feed.emit("jobs", …)` is a JobRunChanged, `feed.emit("logs", …)` a LogsWritten. */
let feed = createFakeLiveFeed();
afterEach(() => { feed = createFakeLiveFeed(); logCalls.length = 0; wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); returnFocus.id = ""; jobHeads.clear(); });

const iso = (minutes: number) => new Date(Date.now() + minutes * 60_000).toISOString();
const run = (id: number, jobId: string, state: JobRun["state"], over: Partial<JobRun> = {}): JobRun => ({
  id, jobId, trigger: "schedule", reason: null, state, attempt: 1, spaceId: null, queuedAt: iso(-10), startedAt: iso(-10), heartbeatAt: null, finishedAt: iso(-9),
  cancelRequested: false, summary: null, ...over,
});
const job = (id: string, title: string, over: Partial<Job> = {}): Job => ({
  id, title, description: `Popis: ${title}`, module: "platform", schedule: "Po události nebo ručně", retry: "Bez opakování", known: true, paused: false, nextDueAt: null,
  completedAt: null, lastRun: null, recurring: false, lastSuccessAt: null, typicalSeconds: null, overdue: false, stuck: false, ...over,
});
const quiet: JobSummary = { running: 0, queued: 0, failedDay: 0, failedWeek: 0, days: [], failures: [] };
const failedRun = run(3, "backups.zip", "failed");
const error = "System.IOException: Úložiště neodpovídá.\n   at Atlas.Zip.RunAsync()";
const logLine = (id: number, level: LogEntry["level"], message: string): LogEntry => ({
  id, at: "2026-11-01T10:00:00Z", level, category: "Atlas.Zip", eventId: null, message, exceptionType: null, exceptionMessage: null, exception: null, traceId: null, userId: null,
  jobRunId: 3, jobId: "backups.zip", jobTitle: "Záloha ZIP",
});
/** The server log behind the run view: the lines of run 3, newest first; `logCalls` keep the filter and cursor of every list call. */
const logCalls: Array<{ filter: LogFilter; cursor?: { before?: number; after?: number } }> = [];
const logApi: LogApi = {
  async list(filter, cursor) { logCalls.push({ filter, cursor }); return { entries: cursor?.after !== undefined ? [] : [logLine(2, "error", "Zápis selhal"), logLine(1, "information", "Záloha začala")], more: false, summary: null }; },
  exportUrl: () => "",
};
const services: SystemService[] = [{ id: "ai-probe", title: "Dostupnost AI", description: "d", interval: "Každých 5 s", state: "unresponsive", lastBeatAt: iso(-1), error: null, detail: null }];

/** An in-memory API; `calls` says what the page asked for, in order. */
function fakeApi(jobs: Job[], runs: JobRun[] = [], options: { list?: () => Job[]; paused?: boolean; retryTo?: number; fail?: boolean; gate?: Promise<void>; summary?: JobSummary } = {}) {
  const calls: string[] = [];
  const api: JobApi = {
    async list() { calls.push("list"); await options.gate; if (options.fail) throw new Error("Server neodpovídá."); return { jobs: options.list?.() ?? jobs, systemServices: services, summary: options.summary ?? quiet }; },
    async runs(jobId) { calls.push(`runs ${jobId}`); if (options.fail) throw new Error("Server neodpovídá."); const own = runs.filter((item) => item.jobId === jobId); return { job: jobs.find((item) => item.id === jobId)!, runs: own, total: own.length, page: 1, pageSize: 20, lastSuccessAt: null }; },
    async run(jobId, runId) { calls.push(`run ${jobId} ${runId}`); if (options.fail) throw new Error("Server neodpovídá."); return { run: runs.find((item) => item.id === runId)!, error, log: [], paused: options.paused ?? false }; },
    async runNow(jobId) { calls.push(`runNow ${jobId}`); return { runId: 99 }; },
    async retry(jobId, runId) { calls.push(`retry ${runId}`); return { runId: options.retryTo ?? runId }; },
    async cancel(jobId, runId) { calls.push(`cancel ${runId}`); return { runId }; },
    async setPaused(jobId, paused) { calls.push(`paused ${jobId} ${paused}`); },
  };
  return { api, calls };
}
let flashOf: () => SettingsFlash;
const mount = (api: JobApi, part: string[] = [], query = "") => {
  const mounted = mountPage(JobsPage, { api, part, logApi }, { hash: `#/settings/general/jobs${query}`, feed: feed.source });
  wrapper = mounted.wrapper as unknown as VueWrapper;
  flashOf = mounted.flash;
  return mounted.wrapper;
};

describe("the list", () => {
  const jobs = [
    job("backups.zip", "Záloha ZIP", { lastRun: failedRun, nextDueAt: iso(120), recurring: true }),
    job("retention", "Úklid logů", { lastRun: run(4, "retention", "succeeded", { summary: { removed: 1240 } }), recurring: true }),
    job("youtrack", "Obnova YouTracku", { paused: true, recurring: true }),
    job("old", "Neznámá úloha", { known: false }),
    job("pages.index", "Index stránek", { module: "pages" }),
  ];

  it("parts the recurring jobs from the one-off ones with their state, offers „Spustit teď“ for the ones this version knows and shows the system services", async () => {
    const { api, calls } = fakeApi(jobs);
    const view = mount(api);
    await flushPromises();

    expect(view.findAll("[data-job-section]").map((section) => section.attributes("data-job-section"))).toEqual(["recurring", "oneoff"]);
    expect(view.get('[data-job-row="backups.zip"]').text()).toContain("Selhalo");
    expect(view.get('[data-job-row="backups.zip"] a').attributes("href")).toBe("#/settings/general/jobs/backups.zip");
    expect(view.get('[data-job-row="backups.zip"]').text()).toContain("Příště:");
    expect(view.get('[data-job-row="retention"]').text()).toContain("Dokončeno");
    expect(view.get('[data-job-row="retention"]').text()).toContain("smazané řádky");
    expect(view.get('[data-job-row="youtrack"]').text()).toContain("Pozastavená");
    // The one-off jobs that never ran are folded; unfolded, the one this version no longer knows offers no run.
    expect(view.find('[data-job-row="old"]').exists()).toBe(false);
    await view.get("[data-job-fold]").trigger("click");
    expect(view.find('[data-job-row="old"]').exists()).toBe(true);
    expect(view.find('[data-job-row="old"] [data-job-run-now]').exists()).toBe(false);
    expect(view.get("[data-job-services]").text()).toContain("Neodpovídá");

    await view.get('[data-job-row="youtrack"] [data-job-run-now]').trigger("click");
    await flushPromises();
    expect(calls).toContain("runNow youtrack");
    // A paused job says that this run is the administrator's explicit will.
    expect(flashOf().message.value?.text).toContain("Úloha „Obnova YouTracku“ je pozastavená, ale spustili jste ji výslovně");
  });

  it("reads the list again when the server tells a run changed and after a reconnect, never on a timer", async () => {
    vi.useFakeTimers();
    let state: JobRun["state"] = "running";
    const { api, calls } = fakeApi(jobs, [], { list: () => [job("backups.zip", "Záloha ZIP", { recurring: true, lastRun: run(5, "backups.zip", state, { finishedAt: null }) })] });
    const view = mount(api);
    await flushPromises();
    const lists = () => calls.filter((call) => call === "list").length;
    // The first read and the one the subscription asks for (it covers what came between them).
    expect(lists()).toBe(2);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(lists()).toBe(2);

    state = "succeeded";
    feed.emit("jobs", { runId: 5, jobId: "backups.zip", state, spaceId: null });
    await flushPromises();
    expect(lists()).toBe(3);
    expect(view.get('[data-job-row="backups.zip"]').text()).toContain("Dokončeno");
    feed.resubscribe("jobs");
    await flushPromises();
    expect(lists()).toBe(4);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(lists()).toBe(4);
  });

  it("says quietly when the live feed is lost and offers a manual refresh, without polling behind it", async () => {
    vi.useFakeTimers();
    const { api, calls } = fakeApi(jobs);
    const view = mount(api);
    await flushPromises();
    expect(view.find("[data-live-update-note]").exists()).toBe(false);
    feed.fail("jobs");
    await flushPromises();
    expect(view.get("[data-live-update-note]").text()).toContain("Živá aktualizace nedostupná");
    const before = calls.length;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(calls).toHaveLength(before);
    await view.get("[data-live-update-refresh]").trigger("click");
    await flushPromises();
    expect(calls.slice(before)).toEqual(["list"]);
    // Back online: the note goes and the state is read once more.
    feed.resubscribe("jobs");
    await flushPromises();
    expect(view.find("[data-live-update-note]").exists()).toBe(false);
    expect(calls.slice(before)).toEqual(["list", "list"]);
  });

  it("preselects the first match of the filter, opens it with Enter and empties the filter with the first Esc", async () => {
    const { api } = fakeApi(jobs);
    const view = mount(api);
    await flushPromises();
    await view.get("[data-job-filter]").setValue("zip");
    expect(view.findAll("[data-job-row]").map((row) => row.attributes("data-job-row"))).toEqual(["backups.zip"]);
    expect(view.get("[data-job-row]").attributes("data-first")).toBeDefined();
    await view.get("[data-job-filter]").trigger("keydown", { key: "Escape" });
    expect((view.get("[data-job-filter]").element as HTMLInputElement).value).toBe("");
    expect(view.find("[data-first]").exists()).toBe(false);
    await view.get("[data-job-filter]").setValue("zip");
    await view.get("[data-job-filter]").trigger("keydown", { key: "Enter" });
    expect(location.hash).toBe("#/settings/general/jobs/backups.zip");
  });

  it("takes the first match once the list has arrived when Enter was pressed before", async () => {
    let release!: () => void;
    const { api } = fakeApi(jobs, [], { gate: new Promise<void>((resolve) => { release = resolve; }) });
    const view = mount(api);
    await view.get("[data-job-filter]").setValue("index");
    await view.get("[data-job-filter]").trigger("keydown", { key: "Enter" });
    expect(location.hash).toBe("#/settings/general/jobs");
    release();
    await flushPromises();
    expect(location.hash).toBe("#/settings/general/jobs/pages.index");
  });

  it("gives the focus back to the row a detail was opened from, but not while the keyboard is in the filter", async () => {
    const { api } = fakeApi(jobs);
    returnFocus.id = "retention";
    const view = mount(api);
    await flushPromises();
    expect(document.activeElement).toBe(view.get('[data-job-row="retention"] a').element);
    view.unmount();
    wrapper = undefined;

    let release!: () => void;
    returnFocus.id = "retention";
    const slow = fakeApi(jobs, [], { gate: new Promise<void>((resolve) => { release = resolve; }) });
    const typing = mount(slow.api);
    (typing.get("[data-job-filter]").element as HTMLInputElement).focus();
    release();
    await flushPromises();
    expect(document.activeElement).toBe(typing.get("[data-job-filter]").element);
  });

  it("shows the error and no skeleton when the first load fails", async () => {
    const { api } = fakeApi(jobs, [], { fail: true });
    const view = mount(api);
    await flushPromises();
    expect(view.text()).toContain("Server neodpovídá.");
    expect(view.find(".loading-skeleton").exists()).toBe(false);
  });
});

describe("the overview", () => {
  const day = (offset: number, succeeded: number, failed: number) => ({ day: new Date(Date.now() - offset * 86_400_000).toISOString().slice(0, 10), succeeded, failed });
  const summary: JobSummary = {
    running: 1, queued: 2, failedDay: 1, failedWeek: 3, days: [day(1, 20, 2), day(0, 10, 1)],
    failures: [
      { runId: 31, jobId: "convert", jobTitle: "Převod přehledů", finishedAt: iso(-60), error: "Stránka 7 má neplatný blok" },
      { runId: 30, jobId: "backups.zip", jobTitle: "Záloha ZIP", finishedAt: iso(-60 * 24 * 3), error: "Úložiště neodpovídá." },
    ],
  };
  const jobs = [
    job("backups.zip", "Záloha ZIP", { recurring: true, lastRun: run(30, "backups.zip", "failed", { finishedAt: iso(-60 * 24 * 3) }), lastSuccessAt: iso(-60 * 24 * 4) }),
    job("retention", "Úklid logů", { recurring: true, lastRun: run(4, "retention", "succeeded"), lastSuccessAt: iso(-9) }),
    job("backups.git", "Záloha do Gitu", { recurring: true, overdue: true, lastRun: run(5, "backups.git", "succeeded", { finishedAt: iso(-60 * 30) }) }),
    job("ai.index", "Indexace", { recurring: true, stuck: true, lastRun: run(6, "ai.index", "running", { finishedAt: null }) }),
    job("convert", "Převod přehledů", { lastRun: run(31, "convert", "failed", { finishedAt: iso(-60) }) }),
    job("import", "Import", { lastRun: run(32, "import", "running", { finishedAt: null }) }),
    job("links", "Propojit kontakty", { lastRun: run(33, "links", "succeeded"), completedAt: iso(-60) }),
  ];

  it("stands with „…“ while the list loads and takes the focus, then counts and links each tile to its filter", async () => {
    let release!: () => void;
    const { api } = fakeApi(jobs, [], { summary, gate: new Promise<void>((resolve) => { release = resolve; }) });
    const view = mount(api);
    expect(view.get('[data-stat="running"]').text()).toContain("…");
    expect(document.activeElement).toBe(view.get("[data-job-summary]").element);
    release();
    await flushPromises();

    expect(view.get('[data-stat="running"]').text()).toContain("ve frontě 2");
    expect(view.get('[data-stat="running"]').attributes("href")).toBe("#/settings/general/jobs?stav=running");
    expect(view.get('[data-stat="failed"]').text()).toContain("za 7 dní 3 běhy");
    expect(view.get('[data-stat="failed"]').attributes("href")).toBe("#/settings/general/jobs?stav=failed&kdy=24h");
    expect(view.get('[data-stat="attention"]').text()).toContain("po termínu 1 · zaseknuté 1");
    expect(view.get('[data-stat="attention"]').attributes("href")).toBe("#/settings/general/jobs?stav=attention");
    expect(view.get('[data-stat="days"]').text()).toContain("33");
    expect(view.get('[data-stat="days"]').text()).toContain("z toho 3 selhalo");
  });

  it("puts what needs a look first, shows a failure's error inline with its run one click away and folds the done one-off jobs", async () => {
    const { api } = fakeApi(jobs, [], { summary });
    const view = mount(api);
    await flushPromises();
    const ids = (section: string) => view.findAll(`[data-job-section="${section}"] [data-job-row]`).map((row) => row.attributes("data-job-row"));

    expect(ids("recurring")).toEqual(["ai.index", "backups.git", "backups.zip", "retention"]);
    expect(view.get('[data-job-row="ai.index"]').text()).toContain("Zaseknutá");
    expect(view.get('[data-job-row="backups.git"]').text()).toContain("Po termínu");
    const failed = view.get('[data-job-row="backups.zip"]');
    expect(failed.attributes("data-failed")).toBeDefined();
    expect(failed.get("[data-job-error]").text()).toContain("Úložiště neodpovídá.");
    expect(failed.get("[data-job-error] a").attributes("href")).toBe("#/settings/general/jobs/backups.zip/runs/30");
    expect(failed.text()).toContain("úspěšně");
    expect(ids("oneoff")).toEqual(["convert", "import"]);
    expect(view.get("[data-job-fold]").attributes("aria-expanded")).toBe("false");
    await view.get("[data-job-fold]").trigger("click");
    expect(ids("oneoff")).toEqual(["convert", "import", "links"]);
  });

  it("filters by the URL's state, kind and time, and with Selhalo lists the failed runs of the period with their error", async () => {
    const { api } = fakeApi(jobs, [], { summary });
    const view = mount(api, [], "?stav=failed");
    await flushPromises();
    expect(view.findAll("[data-failure]").map((row) => row.attributes("data-failure"))).toEqual(["31", "30"]);
    expect(view.get('[data-failure="31"]').text()).toContain("Stránka 7 má neplatný blok");
    expect(view.get('[data-failure="31"] a').attributes("href")).toBe("#/settings/general/jobs/convert/runs/31");
    expect(view.findAll("[data-job-row]").map((row) => row.attributes("data-job-row"))).toEqual(["backups.zip", "convert"]);
    expect(view.find("[data-job-services]").exists()).toBe(false);

    history.replaceState(null, "", "#/settings/general/jobs?stav=failed&kdy=24h");
    window.dispatchEvent(new PopStateEvent("popstate"));
    await flushPromises();
    expect(view.findAll("[data-failure]").map((row) => row.attributes("data-failure"))).toEqual(["31"]);
    expect(view.findAll("[data-job-row]").map((row) => row.attributes("data-job-row"))).toEqual(["convert"]);

    history.replaceState(null, "", "#/settings/general/jobs?druh=oneoff");
    window.dispatchEvent(new PopStateEvent("popstate"));
    await flushPromises();
    expect(view.findAll("[data-job-section]").map((section) => section.attributes("data-job-section"))).toEqual(["oneoff"]);
    // A filter unfolds the done ones: a match must not hide in the fold.
    expect(view.findAll("[data-job-row]").map((row) => row.attributes("data-job-row"))).toEqual(["convert", "import", "links"]);
    await view.get("[data-job-clear]").trigger("click");
    await flushPromises();
    expect(location.hash).toBe("#/settings/general/jobs");
    expect(view.findAll("[data-job-section]")).toHaveLength(2);
  });

  it("matches, groups and dates the failures in plain functions", () => {
    const now = Date.now();
    const none = { state: "", kind: "", when: "", text: "" };
    expect(jobs.filter((item) => matchesJob(item, { ...none, state: "attention" }, now)).map((item) => item.id)).toEqual(["backups.git", "ai.index"]);
    expect(jobs.filter((item) => matchesJob(item, { ...none, kind: "oneoff", text: "prevod" }, now)).map((item) => item.id)).toEqual(["convert"]);
    expect(jobs.filter((item) => matchesJob(item, { ...none, when: "24h" }, now)).map((item) => item.id)).not.toContain("backups.git");
    const groups = groupJobs(jobs);
    expect([groups.recurring.length, groups.open.map((item) => item.id), groups.settled.map((item) => item.id)]).toEqual([4, ["convert", "import"], ["links"]]);
    expect(failuresSince(summary.failures, "24h", now).map((failure) => failure.runId)).toEqual([31]);
    expect(failuresSince(summary.failures, "", now)).toHaveLength(2);
  });
});

describe("a job and its run", () => {
  const history = [run(3, "backups.zip", "failed"), run(2, "backups.zip", "succeeded", { summary: { pages: 12 }, trigger: "manual", reason: "Spustil(a) Jana" })];
  const jobs = [job("backups.zip", "Záloha ZIP", { lastRun: history[0], retry: "Až 5 pokusů" })];

  it("lists the runs newest first, opens each one and pauses with the one switch", async () => {
    const { api, calls } = fakeApi(jobs, history);
    const view = mount(api, ["backups.zip"]);
    await flushPromises();

    expect(view.get("[data-job-summary]").text()).toContain("Až 5 pokusů");
    expect(view.findAll("[data-job-run]").map((row) => row.attributes("data-job-run"))).toEqual(["3", "2"]);
    expect(view.get('[data-job-run="2"] a').attributes("href")).toBe("#/settings/general/jobs/backups.zip/runs/2");
    expect(view.get('[data-job-run="2"]').text()).toContain("stránky 12");
    expect(view.get("a.st-link").attributes("href")).toBe("#/settings/general/jobs");

    await view.get("[data-job-paused]").trigger("click");
    await flushPromises();
    expect(calls).toContain("paused backups.zip true");
  });

  it("shows a failed run with its first error line, the whole error folded, its server log, and retries it", async () => {
    const { api, calls } = fakeApi(jobs, history);
    const view = mount(api, ["backups.zip", "runs", "3"]);
    await flushPromises();

    expect(view.get("[data-run-error]").text()).toContain("System.IOException: Úložiště neodpovídá.");
    expect(view.get("[data-run-error] pre").text()).toContain("at Atlas.Zip.RunAsync()");
    expect(view.findAll("[data-run-log] [data-log-row]").map((row) => row.text())).toEqual([expect.stringContaining("Zápis selhal"), expect.stringContaining("Záloha začala")]);
    expect(logCalls[0]!.filter).toEqual({ jobRunId: 3 });
    expect(view.find("[data-run-log-link]").exists()).toBe(true);
    expect(view.get("[data-run-status]").text()).toBe("Selhalo");
    await view.get("[data-run-error] > button").trigger("click");
    await flushPromises();
    expect(calls).toContain("retry 3");
  });

  it("shows the log stored with a finished run the server log has nothing of (before 3.6, or aged out)", async () => {
    const { api } = fakeApi(jobs, history);
    const original = api.run;
    api.run = async (jobId, runId) => ({ ...(await original(jobId, runId)), log: [
      { at: "2026-11-01T10:00:00Z", level: "Information", message: "Záloha začala" }, { at: "2026-11-01T10:00:05Z", level: "Error", message: "Uložení selhalo" }] });
    const list = logApi.list;
    logApi.list = async () => ({ entries: [], more: false, summary: null });
    try {
      const view = mount(api, ["backups.zip", "runs", "3"]);
      await flushPromises();
      const rows = view.findAll("[data-run-log] [data-log-row]");
      expect(rows.map((row) => row.text())).toEqual([expect.stringContaining("Uložení selhalo"), expect.stringContaining("Záloha začala")]);
      expect(rows[0]!.attributes("data-level")).toBe("error");
    } finally {
      logApi.list = list;
    }
  });

  it("follows a running run as the server tells it and offers to stop it; a stopped one can be tried again", async () => {
    vi.useFakeTimers();
    let state: JobRun["state"] = "running";
    const runs = [run(5, "backups.zip", "running", { finishedAt: null, heartbeatAt: iso(0), summary: { linked: 120 } })];
    const { api, calls } = fakeApi(jobs, runs);
    const original = api.run;
    api.run = async (jobId, runId) => { const detail = await original(jobId, runId); return { ...detail, run: { ...detail.run, state } }; };
    const view = mount(api, ["backups.zip", "runs", "5"]);
    await flushPromises();
    expect(view.get("[data-run-tiles]").text()).toContain("120");
    await view.get("[data-run-cancel]").trigger("click");
    await flushPromises();
    expect(calls).toContain("cancel 5");

    state = "cancelled";
    const reads = () => calls.filter((call) => call.startsWith("run ")).length;
    const before = reads();
    // Another run's change is not this page's.
    feed.emit("jobs", { runId: 6, jobId: "backups.zip", state: "running", spaceId: null });
    await flushPromises();
    expect(reads()).toBe(before);
    expect(view.find("[data-run-cancel]").exists()).toBe(true);
    feed.emit("jobs", { runId: 5, jobId: "backups.zip", state, spaceId: null });
    await flushPromises();
    expect(view.find("[data-run-cancel]").exists()).toBe(false);
    expect(view.find("[data-run-retry]").exists()).toBe(true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(reads()).toBe(before + 1);
  });

  it("tails the run's log when the server stores lines, only while the run is active", async () => {
    vi.useFakeTimers();
    let state: JobRun["state"] = "running";
    const runs = [run(3, "backups.zip", "running", { finishedAt: null })];
    const { api } = fakeApi(jobs, runs);
    const original = api.run;
    api.run = async (jobId, runId) => { const detail = await original(jobId, runId); return { ...detail, run: { ...detail.run, state } }; };
    mount(api, ["backups.zip", "runs", "3"]);
    await flushPromises();
    expect(feed.count("logs")).toBe(1);
    logCalls.length = 0;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(logCalls).toHaveLength(0);
    feed.emit("logs", { level: "information" });
    await flushPromises();
    expect(logCalls).toEqual([{ filter: { jobRunId: 3 }, cursor: { after: 2 } }]);

    state = "succeeded";
    feed.emit("jobs", { runId: 3, jobId: "backups.zip", state, spaceId: null });
    await flushPromises();
    expect(feed.count("logs")).toBe(0);
    logCalls.length = 0;
    feed.emit("logs", { level: "error" });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(logCalls).toHaveLength(0);
  });

  it("moves the switch at once and puts it back when the server refuses", async () => {
    const { api } = fakeApi(jobs, history);
    let refuse!: () => void;
    api.setPaused = () => new Promise<void>((_, reject) => { refuse = () => reject(new Error("Nelze pozastavit.")); });
    const view = mount(api, ["backups.zip"]);
    await flushPromises();
    expect(view.get("[data-job-paused]").attributes("aria-checked")).toBe("false");
    await view.get("[data-job-paused]").trigger("click");
    expect(view.get("[data-job-paused]").attributes("aria-checked")).toBe("true");
    refuse();
    await flushPromises();
    expect(view.get("[data-job-paused]").attributes("aria-checked")).toBe("false");
    expect(view.text()).toContain("Nelze pozastavit.");
  });

  it("reads a queued run of a paused job once, on the job or on the run, and asks nothing more", async () => {
    vi.useFakeTimers();
    const waiting = run(6, "backups.zip", "queued", { trigger: "event", startedAt: null, finishedAt: null });
    const paused = [job("backups.zip", "Záloha ZIP", { paused: true, lastRun: waiting })];
    const onJob = fakeApi(paused, [waiting]);
    mount(onJob.api, ["backups.zip"]);
    await flushPromises();
    await vi.advanceTimersByTimeAsync(10_000);
    // The first read and the one the subscription asks for; no timer after them.
    expect(onJob.calls.filter((call) => call.startsWith("runs "))).toHaveLength(2);
    wrapper?.unmount();
    wrapper = undefined;

    const onRun = fakeApi(paused, [waiting], { paused: true });
    const view = mount(onRun.api, ["backups.zip", "runs", "6"]);
    await flushPromises();
    await vi.advanceTimersByTimeAsync(10_000);
    expect(onRun.calls.filter((call) => call.startsWith("run "))).toHaveLength(2);
    // It can still be stopped.
    expect(view.find("[data-run-cancel]").exists()).toBe(true);
  });

  it("opens the run a retry went to when the job's queued run took it", async () => {
    const { api, calls } = fakeApi(jobs, history, { retryTo: 7 });
    const view = mount(api, ["backups.zip", "runs", "3"]);
    await flushPromises();
    await view.get("[data-run-error] > button").trigger("click");
    await flushPromises();
    expect(calls).toContain("retry 3");
    expect(location.hash).toBe("#/settings/general/jobs/backups.zip/runs/7");
  });

  it("gives the focus to the run's row on the way back from it, and a job's row on the way back from the job", async () => {
    const { api } = fakeApi(jobs, history);
    returnFocus.id = "2";
    const view = mount(api, ["backups.zip"]);
    await flushPromises();
    expect(document.activeElement).toBe(view.get('[data-job-run="2"] a').element);
    view.unmount();
    wrapper = undefined;

    const run3 = mount(api, ["backups.zip", "runs", "3"]);
    await flushPromises();
    location.hash = "#/settings/general/jobs/backups.zip";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await flushPromises();
    expect(returnFocus.id).toBe("3");
    run3.unmount();
    wrapper = undefined;

    const detail = mount(api, ["backups.zip"]);
    await flushPromises();
    location.hash = "#/settings/general/jobs/backups.zip";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await flushPromises();
    returnFocus.id = "";
    location.hash = "#/settings/general/jobs";
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await flushPromises();
    expect(returnFocus.id).toBe("backups.zip");
    detail.unmount();
    wrapper = undefined;
  });

  it("stands the job's heading at once when it is opened from the list, and holds the room for it when it is not", async () => {
    const { api } = fakeApi(jobs, history);
    const list = mount(api);
    await flushPromises();
    list.unmount();
    wrapper = undefined;

    const waiting: JobApi = { ...api, runs: () => new Promise(() => undefined) };
    const opened = mount(waiting, ["backups.zip"]);
    await flushPromises();
    expect(opened.get("h2").text()).toBe("Záloha ZIP");
    expect(opened.text()).toContain("Popis: Záloha ZIP");
    expect(opened.get("[data-job-run-now]").attributes("disabled")).toBeDefined();
    opened.unmount();
    wrapper = undefined;

    jobHeads.clear();
    const direct = mount(waiting, ["backups.zip"]);
    await flushPromises();
    expect(direct.get("h2").text()).toBe("Úloha na pozadí");
    expect(direct.text()).toContain("Načítání úlohy");
    expect(direct.get("[data-job-run-now]").attributes("disabled")).toBeDefined();
  });

  it("shows the error and no skeleton when the first load fails", async () => {
    const { api } = fakeApi(jobs, history, { fail: true });
    const view = mount(api, ["backups.zip", "runs", "3"]);
    await flushPromises();
    expect(view.text()).toContain("Server neodpovídá.");
    expect(view.find(".loading-skeleton").exists()).toBe(false);
  });
});

describe("the failed jobs of Přehled", () => {
  it("names each failed job with a retry in place and counts them for the rail", async () => {
    const jobs = [job("backups.zip", "Záloha ZIP", { lastRun: failedRun }), job("retention", "Úklid logů", { lastRun: run(4, "retention", "succeeded") })];
    const { api, calls } = fakeApi(jobs);
    const found = useJobsIssues(api);
    expect(found.issues()).toEqual([]);
    await found.load();

    const [issue] = found.issues();
    expect(found.failed.value).toHaveLength(1);
    expect(issue!.title).toBe("Úloha „Záloha ZIP“ selhala");
    expect(issue!.target).toEqual({ path: "general/jobs/backups.zip/runs/3" });
    await issue!.run!();
    expect(calls).toEqual(["list", "retry 3", "list"]);
  });

  it("shows nothing when the jobs cannot be read", async () => {
    const found = useJobsIssues({ ...fakeApi([]).api, list: async () => { throw new Error("403"); } });
    await found.load();
    expect(found.issues()).toEqual([]);
  });
});

describe("the words", () => {
  it("says when a job runs next and what a run reported", () => {
    const now = new Date(2026, 10, 1, 12, 0);
    const at = (days: number, hour: number) => new Date(2026, 10, 1 + days, hour, 0).toISOString();
    expect(whenText(at(0, 18), now)).toMatch(/^dnes ve 18:00/);
    expect(whenText(at(1, 2), now)).toMatch(/^zítra ve 2:00/);
    expect(whenText(at(5, 2), now)).toMatch(/^6\. 11\. 2:00/);
    expect(whenText(at(0, 9), now)).toBe("hned");
    expect(nextText(job("a", "A", { completedAt: at(-2, 3) }), now)).toMatch(/^Jednorázová, hotovo 30\. 10\./);
    expect(nextText(job("a", "A", { schedule: "Po události nebo ručně" }), now)).toBe("Po události nebo ručně");
    expect(humanize("pagesDone")).toBe("Hotové stránky");
    expect(humanize("someCount")).toBe("Some count");
    expect(summaryText(run(1, "a", "succeeded", { summary: { linked: 120, purgedGenerations: 2, ignored: { nested: 1 } } }))).toBe("propojeno 120, smazané generace 2");
  });
});
