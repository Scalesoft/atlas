import { formatDateTime } from "../../../formatDate";
import { formatBytes, formatNumber } from "../../../i18n";
import t from "../../../i18n/settings-backups";
import type { JobState } from "../../../jobs/jobApi";
import type { ActivityItem } from "../activity";
import type { ChoiceOption } from "../types";

/** Zálohy → Archivy ZIP: the shapes of `/admin/backups`, the state at a glance and how a run reads in the history. */
export const archiveEndpoint = "/admin/backups";
/** "complete" is the whole installation in format v7 (GAP2-1); "full" and "latest" hold the wiki's pages and attachments only. */
export type BackupMode = "complete" | "full" | "latest";
export type BackupStatus = "running" | "succeeded" | "skipped" | "failed";
export interface BackupSettings {
  enabled: boolean;
  intervalMinutes: number;
  mode: BackupMode;
  destination: string;
  username: string;
  domain: string;
  hasPassword: boolean;
  retentionCount: number;
  storageProfileId?: string | null;
  folder?: string;
}
export interface BackupRun {
  id: string;
  startedAt: string;
  finishedAt: string | null;
  status: BackupStatus;
  trigger: "manual" | "scheduled";
  mode: BackupMode;
  destination: string;
  fileName: string | null;
  size: number;
  message: string;
}
/** A run of a backup job (backups.zip): its state, when it ended (else started, else was queued) and its message. `GET /admin/backups/run`
 * answers the last one on its own (204 before the first run); its state reads as on the jobs page (`runBadge`). */
export interface BackupJobRun {
  id: number;
  jobId: string;
  state: JobState;
  at: string;
  message: string | null;
}
export interface BackupState {
  settings: BackupSettings;
  /** The archives; the run state of each check is the job's run (`GET /admin/backups/run`, the rest on the jobs page). */
  runs: BackupRun[];
  nextRunAt: string | null;
  lastSuccessAt: string | null;
  lastBackup?: BackupRun | null;
  warning: string | null;
  /** The backup key the save that switched the complete mode on created: in that one response only. */
  newBackupKey?: { key: string; keyId: string; createdAt: string } | null;
}

export const knownIntervals = [15, 60, 360, 720, 1440, 10080];
export const intervalOptions = (): ChoiceOption[] => [
  { value: "15", label: t.archives.intervals.m15, icon: "clock" },
  { value: "60", label: t.archives.intervals.hourly, icon: "clock" },
  { value: "360", label: t.archives.intervals.h6, icon: "clock" },
  { value: "720", label: t.archives.intervals.h12, icon: "clock" },
  { value: "1440", label: t.archives.intervals.daily, icon: "calendar" },
  { value: "10080", label: t.archives.intervals.weekly, icon: "calendar" },
  { value: "custom", label: t.archives.intervals.custom, icon: "sliders-horizontal" },
];
export function intervalLabel(minutes: number) {
  const known: Record<number, string> = { 15: t.archives.every.m15, 60: t.archives.every.hourly, 360: t.archives.every.h6, 720: t.archives.every.h12, 1440: t.archives.every.daily, 10080: t.archives.every.weekly };
  return known[minutes] ?? t.archives.every.minutes(formatNumber(minutes));
}

const statusBadges = (): Record<BackupStatus, { label: string; tone: "success" | "danger" | "info" | "neutral"; icon: string }> => ({
  running: { label: t.archives.status.running, tone: "info", icon: "loader-circle" },
  succeeded: { label: t.archives.status.succeeded, tone: "success", icon: "circle-check" },
  skipped: { label: t.archives.status.skipped, tone: "neutral", icon: "check" },
  failed: { label: t.archives.status.failed, tone: "danger", icon: "circle-x" },
});

/** The run's detail on the jobs page (Správa wiki → Provoz → Úlohy na pozadí), a settings route. */
export const jobRunPath = (run: BackupJobRun) => `general/jobs/${encodeURIComponent(run.jobId)}/runs/${run.id}`;

export type ArchiveState = "ok" | "running" | "unconfigured" | "manual" | "failed" | "attention" | "spare";
/**
 * One word for the archives on the Zálohy overview: a run in progress wins, then a missing
 * destination, a failed last run, scheduling that is off and any other warning of their own (the
 * server's rules, BackupPolicy.cs), which the caller separates from the git backup's.
 */
/** `gitOn`: the Git backup is on, so a ZIP archive not set up or switched off is a spare, not a warning (owner, 29. 10. 2026).
 * `last`: the state of the job's last run, read on its own. */
export function archiveState(state: BackupState, warning: string | null, gitOn = false, last?: JobState): ArchiveState {
  if (last === "running") return "running";
  if (!state.settings.storageProfileId && !state.settings.destination.trim()) return gitOn ? "spare" : "unconfigured";
  if (last === "failed") return "failed";
  if (!state.settings.enabled) return gitOn ? "spare" : "manual";
  return warning ? "attention" : "ok";
}
export const archiveStateBadges = (): Record<ArchiveState, { label: string; tone: "success" | "warning" | "danger" | "info"; icon: string }> => ({
  ok: { label: t.archives.state.ok, tone: "success", icon: "circle-check" },
  running: { label: t.archives.state.running, tone: "info", icon: "loader-circle" },
  unconfigured: { label: t.archives.state.unconfigured, tone: "warning", icon: "triangle-alert" },
  manual: { label: t.archives.state.manual, tone: "warning", icon: "triangle-alert" },
  failed: { label: t.archives.state.failed, tone: "danger", icon: "circle-x" },
  attention: { label: t.archives.state.attention, tone: "warning", icon: "triangle-alert" },
  spare: { label: t.archives.state.spare, tone: "info", icon: "clock" },
});
export const destinationLabel = (destination: string) =>
  !destination.trim() ? t.archives.destination.none : destination.trim().toLowerCase().startsWith("smb://") ? t.archives.destination.smb : t.archives.destination.local;


/** The newest successful archive stays downloadable after it falls out of the recent history. */
export function displayedRuns(state: BackupState | undefined) {
  const runs = state?.runs ?? [];
  const last = state?.lastBackup;
  return last && !runs.some((run) => run.id === last.id) ? [...runs, last] : runs;
}
export const downloadUrl = (run: BackupRun) => `/api/admin/backups/runs/${encodeURIComponent(run.id)}/download`;
export const downloadable = (run: BackupRun) => run.status === "succeeded" && !!run.fileName;
/** A complete archive holds every private note: its download asks for a fresh sign-in first and is audited (SEC3-1). */
export const downloadAuthorizeUrl = (run: BackupRun) => `${archiveEndpoint}/runs/${encodeURIComponent(run.id)}/download/authorize`;
export const completeDownloadNote = () => t.archives.completeDownloadNote;

/** A run as one history sentence: "Automatická záloha vytvořila archiv 1,5 kB", with its message. */
export function backupActivity(run: BackupRun): ActivityItem {
  return {
    id: run.id,
    at: run.startedAt,
    actorId: run.trigger,
    actor: run.trigger === "manual" ? t.archives.manual : t.archives.automatic,
    action: t.archives.actions[run.status],
    target: run.status === "succeeded" && run.size ? formatBytes(run.size) : undefined,
    context: run.status === "skipped" ? t.archives.skipped : run.message || undefined,
    category: "backup",
    outcome: run.status === "failed" ? "denied" : run.status === "succeeded" ? "allowed" : "changed",
    icon: run.trigger === "manual" ? "download" : "calendar",
    badge: statusBadges()[run.status],
    details: [
      { label: t.archives.detail.target, value: run.destination },
      { label: t.archives.detail.content, value: t.archives.detail[run.mode] },
      ...(run.fileName ? [{ label: t.archives.detail.file, value: run.fileName }] : []),
      { label: t.archives.detail.size, value: formatBytes(run.size) },
      ...(run.finishedAt ? [{ label: t.archives.detail.finished, value: formatDateTime(run.finishedAt) }] : []),
    ],
  };
}
