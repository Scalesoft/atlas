import { formatDateTime, formatShortDate, formatTime } from "../../../formatDate";
import { isActive, type Job, type JobFailure, type JobRun, type JobState, type JobTrigger, type SystemServiceState } from "../../../jobs/jobApi";
import { formatNumber, locale } from "../../../i18n";
import t from "../../../i18n/settings-jobs";
import { foldText } from "../../../lib/foldText";
import type { StatusTone } from "../types";

/** The words of the pages: one place, so the list, the detail, the run, the Přehled and the Zálohy say the same (the run states are the archive history's: Probíhá, Dokončeno, Selhalo). */
export interface Badge { label: string; tone: StatusTone; icon: string }

const runBadges: Record<JobState, Omit<Badge, "label">> = {
  queued: { tone: "info", icon: "clock" },
  running: { tone: "accent", icon: "loader-circle" },
  succeeded: { tone: "success", icon: "circle-check" },
  failed: { tone: "danger", icon: "circle-x" },
  // Not the icon of a paused job (below), whose word is one letter away.
  cancelled: { tone: "neutral", icon: "octagon-x" },
};
export const runBadge = (state: JobState): Badge => ({ label: t.runStates[state], ...runBadges[state] });

/** The badge of a paused job: „Pozastavená“ says it of the job, a stopped run is „Zastaveno“. */
export const pausedBadge = (): Badge => ({ label: t.paused, tone: "neutral", icon: "ban" });

/** A paused job says so before anything else; one that never ran says that. */
export function jobBadge(job: Job): Badge {
  if (job.paused) return pausedBadge();
  return job.lastRun ? runBadge(job.lastRun.state) : { label: t.neverRan, tone: "neutral", icon: "clock" };
}

const serviceBadges: Record<SystemServiceState, Omit<Badge, "label">> = {
  ok: { tone: "success", icon: "circle-check" },
  failed: { tone: "danger", icon: "circle-x" },
  unresponsive: { tone: "warning", icon: "triangle-alert" },
  waiting: { tone: "neutral", icon: "clock" },
};
export const serviceBadge = (state: SystemServiceState): Badge => ({ label: t.services[state], ...serviceBadges[state] });

export const triggerLabel = (trigger: JobTrigger) => t.triggers[trigger] ?? trigger;

export const moduleTitle = (module: string) => t.modules[module] ?? module;
const moduleIcons: Record<string, string> = { platform: "server", pages: "file-text", boards: "layout-dashboard", projector: "workflow" };
export const moduleIcon = (module: string) => moduleIcons[module] ?? "blocks";

/** „dnes ve 2:00“, „zítra ve 2:00“, „9. 10. 2:00“; a time that has come is „hned“. */
export function whenText(iso: string, now = new Date()): string {
  const at = new Date(iso);
  if (at.getTime() <= now.getTime()) return t.when.now;
  const days = Math.round((new Date(at).setHours(0, 0, 0, 0) - new Date(now).setHours(0, 0, 0, 0)) / 86_400_000);
  return days === 0 ? t.when.today(formatTime(at)) : days === 1 ? t.when.tomorrow(formatTime(at)) : t.when.later(formatShortDate(at, now), formatTime(at));
}

/** The „Příště“ of the list: a job that runs once says when it did, one with a due time when, any other how it is started; `bare` leaves out the „Příště:“ of a due time. */
export function nextText(job: Job, now = new Date(), bare = false): string {
  if (job.completedAt) return t.next.once(formatShortDate(job.completedAt, now));
  if (job.paused) return t.next.awaitingResume;
  return job.nextDueAt ? (bare ? whenText(job.nextDueAt, now) : t.next.due(whenText(job.nextDueAt, now))) : job.schedule;
}

/** The keys the jobs report (JobContext.Summarize) and what they count; a key not listed here (a job of a later version) is humanized. */
const camel = /([a-zá-ž])([A-ZÁ-Ž])/g;
/** The key of a summary as a label: „pagesDone“ → „Hotové stránky“; a key nobody knows as words: „someCount“ → „Some count“. */
export const humanize = (key: string) => t.summaryLabels[key] ?? key.replace(camel, "$1 $2").toLocaleLowerCase(locale).replace(/^./, (first) => first.toLocaleUpperCase(locale));

const scalar = (value: unknown): value is string | number | boolean => ["string", "number", "boolean"].includes(typeof value);
const shown = (value: string | number | boolean) => (typeof value === "number" ? formatNumber(value) : typeof value === "boolean" ? (value ? t.yes : t.no) : value);
/** The numbers a run reported, as tiles. */
export const summaryEntries = (run: JobRun) => Object.entries(run.summary ?? {}).filter((entry): entry is [string, string | number | boolean] => scalar(entry[1]))
  .map(([key, value]) => ({ key, label: humanize(key), value: shown(value) }));
/** The same in one sentence for a row of the history. */
export const summaryText = (run: JobRun) => summaryEntries(run).map((entry) => `${entry.label.toLocaleLowerCase(locale)} ${entry.value}`).join(", ");

/** How long a run took or has been going; empty before it started. */
export function durationText(run: JobRun, now = Date.now()): string {
  if (!run.startedAt) return "";
  const seconds = Math.max(0, Math.round(((run.finishedAt ? new Date(run.finishedAt).getTime() : now) - new Date(run.startedAt).getTime()) / 1000));
  if (seconds < 60) return t.duration.seconds(seconds);
  const minutes = Math.floor(seconds / 60);
  return minutes < 60 ? t.duration.minutes(minutes, seconds % 60) : t.duration.hours(Math.floor(minutes / 60), minutes % 60);
}

/** The moment a run is placed at in the history: when it finished, else started, else was queued. */
export const runMoment = (run: JobRun) => formatDateTime(run.finishedAt ?? run.startedAt ?? run.queuedAt);

/** What the pages already know of a job's heading, so the detail's header stands at once when it is opened from the list (or back from a run) instead of jumping when its own answer arrives. */
export const jobHeads = new Map<string, { title: string; description: string; known: boolean }>();
export const rememberJob = (job: Job) => jobHeads.set(job.id, { title: job.title, description: job.description, known: job.known });

/** The row the page that is left was opened from: a job's (back to the list) or a run's (back to the job). The page that leaves sets it, the one that arrives takes it once and gives that row the focus (§5). */
export const returnFocus = { id: "" };
/** The arriving page's part of it: the link of the row `rowSelector` names gets the focus, unless the keyboard is already inside `page` (the filter the user began to type in). */
export function focusReturned(page: HTMLElement | undefined, rowSelector: (id: string) => string) {
  const id = returnFocus.id;
  returnFocus.id = "";
  if (id && page && !page.contains(document.activeElement)) page.querySelector<HTMLElement>(`${rowSelector(CSS.escape(id))} a`)?.focus();
}

/** The badges a job needs beside its state: a missed due time, a run going far longer than typical (the server decides both). */
export const attentionBadges = (job: Job): Badge[] => [
  ...(job.overdue ? [{ label: t.overdue, tone: "warning" as const, icon: "hourglass" }] : []),
  ...(job.stuck ? [{ label: t.stuck, tone: "warning" as const, icon: "triangle-alert" }] : []),
];

/**
 * The filters of the list, as the URL keeps them: `state` a run state of the newest run or `attention` (overdue or stuck), `kind`
 * `recurring` or `oneoff`, `when` the newest run within `24h`, `7d` or `30d`; empty is no filter. `text` is the search.
 */
export interface JobFilters { state: string; kind: string; when: string; text: string }
const periodHours: Record<string, number> = { "24h": 24, "7d": 24 * 7, "30d": 24 * 30 };
/** Where a period starts, in ms; undefined for none. */
export const since = (when: string, now = Date.now()) => (periodHours[when] ? now - periodHours[when] * 3_600_000 : undefined);
const moment = (run: JobRun) => new Date(run.finishedAt ?? run.startedAt ?? run.queuedAt).getTime();

export function matchesJob(job: Job, filters: JobFilters, now = Date.now()): boolean {
  const last = job.lastRun;
  if (filters.state === "attention" ? !job.overdue && !job.stuck : filters.state && last?.state !== filters.state) return false;
  if (filters.kind && (filters.kind === "recurring") !== job.recurring) return false;
  const from = since(filters.when, now);
  if (from !== undefined && (!last || moment(last) < from)) return false;
  const needle = foldText(filters.text.trim());
  return !needle || foldText(`${job.title} ${job.description} ${job.id} ${moduleTitle(job.module)}`).includes(needle);
}

/** What needs a look comes first: a failure, a stuck or overdue job, then what is going, then the rest by title. */
const rank = (job: Job) => (job.stuck || job.overdue || job.lastRun?.state === "failed" ? 0 : isActive(job.lastRun, job.paused) ? 1 : 2);
/**
 * The list's sections: the recurring jobs, then the one-off ones (a migration's conversion, an import, an event's or a hand's run) that are going
 * or failed, and the one-off ones that are done or idle, which the page folds away.
 */
export function groupJobs(jobs: Job[]) {
  const sorted = [...jobs].sort((a, b) => rank(a) - rank(b) || a.title.localeCompare(b.title, locale));
  return {
    recurring: sorted.filter((job) => job.recurring),
    open: sorted.filter((job) => !job.recurring && rank(job) < 2),
    settled: sorted.filter((job) => !job.recurring && rank(job) === 2),
  };
}

/** The failures the page names: those of the period (7 days without one, the server's reach). */
export const failuresSince = (failures: JobFailure[], when: string, now = Date.now()) => {
  const from = since(when || "7d", now) ?? 0;
  return failures.filter((failure) => new Date(failure.finishedAt).getTime() >= from);
};
