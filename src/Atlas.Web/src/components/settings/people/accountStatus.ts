import type { DirectoryUser, Invitation } from "../../../administration";
import { formatDateTime } from "../../../formatDate";
import t from "../../../i18n/settings-people";
import type { StatusTone } from "../types";

export type AccountStatus = "active" | "pending" | "deactivated";

// The labels are read when used (getters), so the language of the page applies.
export const accountStatusLabels: Record<AccountStatus, { label: string; tone: StatusTone; icon: string }> = {
  active: { get label() { return t.status.active; }, tone: "success", icon: "circle-check" },
  pending: { get label() { return t.status.pending; }, tone: "warning", icon: "hourglass" },
  deactivated: { get label() { return t.status.deactivated; }, tone: "neutral", icon: "ban" },
};

/** An account that has never been activated must never read as active (settings 2.5). */
export function accountStatus(account: Pick<DirectoryUser, "isActive" | "credentialState">): AccountStatus {
  if (!account.isActive) return "deactivated";
  return account.credentialState === "pending" ? "pending" : "active";
}

export const accountSourceLabels: Record<DirectoryUser["source"], { label: string; icon: string }> = {
  builtin: { get label() { return t.source.builtin; }, icon: "key-round" },
  external: { get label() { return t.source.external; }, icon: "fingerprint" },
};

/** How long a one-time code lasts, as the server issued it: an invitation of an account that has never set a password 7 days, a
 * password reset 30 minutes; with the absolute end. */
export function codeValidity(code: Pick<Invitation, "purpose" | "expiresAt">): string {
  const until = formatDateTime(code.expiresAt);
  return t.codeValidity(code.purpose === "reset", until);
}
