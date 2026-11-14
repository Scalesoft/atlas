import { api, post, put } from "../api";

/**
 * Background jobs (Atlas 3.3 plan §4.7, §5): the cookie API of Správa wiki → Provoz → Úlohy na pozadí; /api/v1/jobs and the MCP tools answer
 * the same. JSON camelCase; a refusal is an ApiError (`not_found`, `run_finished`, `run_not_retryable`, `job_unknown`).
 */
export type JobState = "queued" | "running" | "succeeded" | "failed" | "cancelled";
export type JobTrigger = "startup" | "event" | "schedule" | "manual" | "retry";

/** `summary` is what the job reported for the UI: a flat object whose keys name the numbers. */
export interface JobRun {
  id: number; jobId: string; trigger: JobTrigger; reason: string | null; state: JobState; attempt: number; spaceId: string | null;
  queuedAt: string; startedAt: string | null; heartbeatAt: string | null; finishedAt: string | null; cancelRequested: boolean;
  summary: Record<string, unknown> | null;
}
/**
 * `known: false` is a row whose definition is gone („Neznámá úloha“); `lastRun` is the newest run the viewer may see. `recurring`: it runs by
 * itself (a plan, the start, the feature's data), else once, by an event or by hand. `lastSuccessAt`, `typicalSeconds` (the median success),
 * `overdue` (a missed due time, no run going) and `stuck` (going far longer than typical) are the list's (3.6).
 */
export interface Job {
  id: string; title: string; description: string; module: string; schedule: string; retry: string; known: boolean; paused: boolean;
  nextDueAt: string | null; completedAt: string | null; lastRun: JobRun | null; recurring: boolean; lastSuccessAt: string | null;
  typicalSeconds: number | null; overdue: boolean; stuck: boolean;
}
export type SystemServiceState = "ok" | "failed" | "unresponsive" | "waiting";
export interface SystemService {
  id: string; title: string; description: string; interval: string | null; state: SystemServiceState; lastBeatAt: string | null; error: string | null;
  detail: string | null;
}
export interface JobDayCount { day: string; succeeded: number; failed: number }
/** A failed run of the last 7 days with the first line of its error. */
export interface JobFailure { runId: number; jobId: string; jobTitle: string; finishedAt: string; error: string | null }
/** The top of the list, counted by the server over the runs the viewer may see; `days` are the last 14 (UTC), oldest first. */
export interface JobSummary { running: number; queued: number; failedDay: number; failedWeek: number; days: JobDayCount[]; failures: JobFailure[] }
export interface JobList { jobs: Job[]; systemServices: SystemService[]; summary: JobSummary }
export interface JobRunList { job: Job; runs: JobRun[]; total: number; page: number; pageSize: number; lastSuccessAt: string | null }
export interface JobLogEntry { at: string; level: string; message: string }
/** `paused`: the job is paused, so a queued run of it waits for the resume (see `isActive`). */
export interface JobRunDetail { run: JobRun; error: string | null; log: JobLogEntry[]; paused: boolean }

/** What the pages need of the server; the gallery replaces it with an in-memory one. */
export interface JobApi {
  list(): Promise<JobList>;
  runs(jobId: string, page?: number): Promise<JobRunList>;
  run(jobId: string, runId: number): Promise<JobRunDetail>;
  runNow(jobId: string): Promise<{ runId: number }>;
  retry(jobId: string, runId: number): Promise<{ runId: number }>;
  cancel(jobId: string, runId: number): Promise<{ runId: number }>;
  setPaused(jobId: string, paused: boolean): Promise<void>;
}

const job = (jobId: string) => `/admin/jobs/${encodeURIComponent(jobId)}`;

export const jobApi: JobApi = {
  list: () => api<JobList>("/admin/jobs"),
  runs: (jobId, page = 1) => api<JobRunList>(`${job(jobId)}/runs?page=${page}`),
  run: (jobId, runId) => api<JobRunDetail>(`${job(jobId)}/runs/${runId}`),
  runNow: (jobId) => post(`${job(jobId)}/runs`),
  retry: (jobId, runId) => post(`${job(jobId)}/runs/${runId}/retry`),
  cancel: (jobId, runId) => post(`${job(jobId)}/runs/${runId}/cancel`),
  setPaused: (jobId, paused) => put<void>(`${job(jobId)}/paused`, { paused }),
};

/** A run that is still going: the pages poll while one is. A queued run of a paused job waits for the resume, so it is not (a run an administrator started is polled for a while by the pages themselves). */
export const isActive = (run: JobRun | null | undefined, paused = false) => run?.state === "running" || (run?.state === "queued" && !paused);
