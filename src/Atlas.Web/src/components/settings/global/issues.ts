import { ref } from "vue";
import { api, post } from "../../../api";
import type { StatusTone } from "../types";
import { spaceSettingsHash } from "../../../settingsRoutes";
import { vaultStatusLabels, type Vault, type VaultLocation } from "../../../vaults";
import type { FreshAuthState } from "../../../protection";
import t from "../../../i18n/settings-global";
import { locale } from "../../../i18n";
import { jobApi, type Job, type JobApi } from "../../../jobs/jobApi";
import { formatAgo } from "../../../formatDate";
import { logApi, type LogApi } from "../../../logs/logApi";
import { globalSettingsBase } from "../../../settingsRoutes";
import { buildSettingsHash } from "../useSettingsRoute";

/** The security side of K řešení: pending access requests, "mimo sítě" without a network, fresh sign-in not proven. */
export interface SecurityIssueFacts {
  pendingRequests: number;
  freshAuth: FreshAuthState | null;
  outsideWithoutNetworks: boolean;
  /** The token-rights upgrade's levels wait for a wiki administrator's confirmation. */
  levelsReview?: boolean;
}

/** One thing on "K řešení": what is wrong, where, and where it is fixed. */
export interface SettingsIssue {
  id: string;
  icon: string;
  tone: StatusTone;
  title: string;
  detail: string;
  /** A path of global settings (`general/backups`) or a full hash of another surface. */
  target: { path: string } | { hash: string };
  action: string;
  /** Done in place (a retry) instead of following the target. */
  run?: () => Promise<void>;
  /** What Přehled confirms once `run` succeeded; the row may be gone by then. */
  done?: string;
}

/** What needs attention in global settings, from the vault states, the locations, the backups and the security facts. */
export function settingsIssues(vaults: Vault[], locations: VaultLocation[], backupWarning: string | null, loaded: boolean, security?: SecurityIssueFacts): SettingsIssue[] {
  const issues: SettingsIssue[] = [];
  if (security?.pendingRequests)
    issues.push({
      id: "requests",
      icon: "user-check",
      tone: "warning",
      title: t.issues.requests.title(security.pendingRequests),
      detail: t.issues.requests.detail,
      target: { path: "security/requests" },
      action: t.issues.requests.action,
    });
  if (security?.outsideWithoutNetworks)
    issues.push({
      id: "no-networks",
      icon: "network",
      tone: "warning",
      title: t.issues.noNetworks.title,
      detail: t.issues.noNetworks.detail,
      target: { path: "security/networks/new" },
      action: t.issues.noNetworks.action,
    });
  if (security?.levelsReview)
    issues.push({
      id: "api_levels_review",
      icon: "key-round",
      tone: "info",
      title: t.issues.levelsReview.title,
      detail: t.issues.levelsReview.detail,
      target: { path: "people/tokens" },
      action: t.issues.levelsReview.action,
    });
  if (security?.freshAuth === "unsupported" || security?.freshAuth === "unknown")
    issues.push({
      id: "fresh-auth",
      icon: "shield-alert",
      tone: security.freshAuth === "unsupported" ? "danger" : "info",
      title: security.freshAuth === "unsupported" ? t.issues.freshAuth.unsupportedTitle : t.issues.freshAuth.unknownTitle,
      detail: security.freshAuth === "unsupported" ? t.issues.freshAuth.unsupportedDetail : t.issues.freshAuth.unknownDetail,
      target: { path: "security/overview" },
      action: t.issues.freshAuth.action,
    });
  if (backupWarning)
    issues.push({ id: "backups", icon: "download", tone: "warning", title: t.issues.backups.title, detail: backupWarning, target: { path: "general/backups" }, action: t.issues.backups.action });
  for (const vault of vaults.filter((item) => item.status !== "unlocked"))
    issues.push({
      id: `vault-${vault.id}`,
      icon: vault.status === "needsPassword" ? "key-round" : "triangle-alert",
      tone: vault.status === "needsPassword" ? "warning" : "danger",
      title: t.issues.vault.title(vault.name, vaultStatusLabels[vault.status].toLocaleLowerCase(locale)),
      detail: t.issues.vault.detail(vault.spaceName, vault.lastFailure),
      target: { hash: spaceSettingsHash(vault.spaceId, `vaults/${vault.id}/status`) },
      action: vault.status === "needsPassword" ? t.issues.vault.enterPassword : t.issues.vault.check,
    });
  if (loaded && !locations.length)
    issues.push({ id: "locations", icon: "folder", tone: "info", title: t.issues.locations.title, detail: t.issues.locations.detail, target: { path: "general/files/new" }, action: t.issues.locations.action });
  return issues;
}

/** GET /api/admin/outbox: undelivered background messages (architecture §5.1); dead ones wait for a retry or their retention. */
export interface OutboxHealth { pending: number; dead: number; oldestPendingAt: string | null; lastFailedAt: string | null; lastError: string | null }

/** The outbox's dead messages as one issue of Přehled with a retry in place (ARCH1-13); none while nothing is dead or unknown. */
export function useOutboxIssue() {
  const health = ref<OutboxHealth | null>(null);
  async function load() {
    try {
      health.value = await api<OutboxHealth>("/admin/outbox");
    } catch {
      health.value = null;
    }
  }
  async function retry() {
    await post("/admin/outbox/retry");
    await load();
  }
  function issue(): SettingsIssue | null {
    const dead = health.value?.dead ?? 0;
    if (!dead) return null;
    return {
      id: "outbox",
      icon: "triangle-alert",
      tone: "warning",
      title: t.issues.outbox.title(dead),
      detail: t.issues.outbox.detail(health.value?.lastError ?? undefined),
      target: { path: "security/overview" },
      action: t.issues.outbox.action,
      run: retry,
      done: t.issues.outbox.done,
    };
  }
  return { health, load, issue };
}

/** The jobs whose newest run failed, as issues of Přehled with a retry in place (Atlas 3.3 plan §5); none while all is well or the list cannot be read. */
export function useJobsIssues(api: JobApi = jobApi) {
  const failed = ref<Job[]>([]);
  async function load() {
    try {
      failed.value = (await api.list()).jobs.filter((job) => job.lastRun?.state === "failed");
    } catch {
      failed.value = [];
    }
  }
  async function retry(job: Job) {
    await api.retry(job.id, job.lastRun!.id);
    await load();
  }
  function issues(): SettingsIssue[] {
    return failed.value.map((job) => ({
      id: `job-${job.id}`,
      icon: "triangle-alert",
      tone: "warning",
      title: t.issues.job.title(job.title),
      detail: t.issues.job.detail(formatAgo(job.lastRun!.finishedAt ?? job.lastRun!.queuedAt)),
      target: { path: `general/jobs/${encodeURIComponent(job.id)}/runs/${job.lastRun!.id}` },
      action: t.issues.job.action,
      run: () => retry(job),
      done: t.issues.job.done,
    }));
  }
  return { failed, load, issues };
}

/** Errors the server wrote to its own log in the last hour (Systémový log): the rail count and an issue of Přehled that opens the log filtered to errors. */
export function useLogsIssue(api: LogApi = logApi) {
  const errorsHour = ref(0);
  async function load() {
    try {
      errorsHour.value = (await api.list({ level: "error" }, undefined, 1)).summary?.errorsHour ?? 0;
    } catch {
      errorsHour.value = 0;
    }
  }
  function issue(): SettingsIssue | null {
    if (!errorsHour.value) return null;
    return {
      id: "logs-errors",
      icon: "triangle-alert",
      tone: "warning",
      title: t.issues.logs.title(errorsHour.value),
      detail: t.issues.logs.detail,
      target: { hash: buildSettingsHash(globalSettingsBase, "general/logs", { uroven: "error" }) },
      action: t.issues.logs.action,
    };
  }
  return { errorsHour, load, issue };
}
