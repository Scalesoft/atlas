import t from "../../../i18n/settings-backups";
import type { StatusTone } from "../types";

/** Záloha do Gitu: the shapes of `/admin/backups/git` and how its state reads at a glance. */
export interface GitBackupSettings {
  enabled: boolean;
  remoteUrl: string;
  branch: string;
  /** The public half of the deploy key (OpenSSH line); the private key never leaves the server. */
  publicKey: string | null;
  /** Fingerprints of the SSH server keys trusted on first use, e.g. "ssh-ed25519 SHA256:…". */
  hostKeys: string[];
}
export interface GitBackupStatus {
  settings: GitBackupSettings;
  running: boolean;
  progress: { done: number; total: number } | null;
  pendingPages: number;
  lastSyncAt: string | null;
  /** Full sha of the last pushed commit. */
  lastCommit: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  warning: string | null;
}
/** What the SaveBar saves (`PUT`); the key and the host keys have actions of their own. */
export interface GitBackupDraft {
  enabled: boolean;
  remoteUrl: string;
  branch: string;
}

export const gitBackupEndpoint = "/admin/backups/git";
export const defaultBranch = "main";
export const remoteUrlPlaceholder = "ssh://git@git.example.test/Infrastructure/atlas-backup.git";

export type GitBackupState = "ok" | "failed" | "disabled" | "running" | "pending";
/** One word for the state: a run in progress wins, then off, then a failure, then unwritten changes. */
export function gitBackupState(status: GitBackupStatus): GitBackupState {
  if (status.running) return "running";
  if (!status.settings.enabled) return "disabled";
  if (status.lastError) return "failed";
  if (!status.lastSyncAt || status.pendingPages > 0) return "pending";
  return "ok";
}
export const gitStateBadges = (): Record<GitBackupState, { label: string; tone: StatusTone; icon: string }> => ({
  ok: { label: t.gitState.ok, tone: "success", icon: "circle-check" },
  failed: { label: t.gitState.failed, tone: "danger", icon: "circle-x" },
  disabled: { label: t.gitState.disabled, tone: "neutral", icon: "ban" },
  running: { label: t.gitState.running, tone: "info", icon: "refresh" },
  pending: { label: t.gitState.pending, tone: "info", icon: "hourglass" },
});

/** `POST /git/restore/preview`: what the repository holds and what keeps it from being restored. */
export interface GitRestorePreview {
  mode: "full" | "content-only";
  keyId: string | null;
  empty: boolean;
  rows: Record<string, number>;
  problems: string[];
  warnings: string[];
  message: string;
}
export interface GitRestoreResult {
  mode: "full" | "content-only";
  rows: Record<string, number>;
  warnings: string[];
  message: string;
}
/** The word typed to confirm replacing an installation. */
export const restoreConfirmWord = () => t.restore.confirmWord;

export const shortSha =(sha: string | null | undefined) => (sha ? sha.slice(0, 7) : "");

/**
 * The server owns the rules (ssh://, git@server:path and file:// only); the form only asks for an
 * address when the backup is on and points out HTTPS, the most likely mistake, before a round trip.
 */
export function remoteUrlError(value: string, required: boolean): string | undefined {
  const url = value.trim();
  if (!url) return required ? t.gitErrors.urlRequired : undefined;
  if (/^https?:\/\//i.test(url)) return t.gitErrors.https;
  return undefined;
}
export function branchError(value: string): string | undefined {
  const branch = value.trim();
  if (!branch) return t.gitErrors.branchRequired;
  return /\s/.test(branch) ? t.gitErrors.branchSpaces : undefined;
}
