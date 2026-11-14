import { apiLevel, apiLevelInfo } from "../../apiTokenRights";
import { api } from "../../api";
import { roleLabel } from "../../roles";
import { vaultAccessLevelLabels, type VaultAccessLevel } from "../../security";
import { formatDateTime } from "../../formatDate";
import { hubModeLabels, type HubMode } from "../../hub";
import { protectionModeLabels, type ProtectionMode } from "../../protection";
import { vaultFieldLabels, type VaultField } from "../../vaults";
import { buildShellHashEncoded } from "../../shellRoute";
import { locale } from "../../i18n";
import t from "../../i18n/settings-activity";
import type { ActivityDetail, ActivityItem } from "./activity";

/** One row of `GET /api/admin/access-audit` (AccessAuditItem on the server), also the row of the V5 vault audit. Never a value. */
export interface AccessAuditEvent {
  id: string;
  occurredAt: string;
  actorId: string | null;
  actorName: string | null;
  action: string;
  userId: string | null;
  userName: string | null;
  groupId: string | null;
  groupName: string | null;
  spaceId: string | null;
  spaceName: string | null;
  /** A space or page role, or for a vault or secret grant or request its level (discover, read, manage). */
  role: string | null;
  isActive: boolean | null;
  isWikiAdmin: boolean | null;
  canIssueTokens: boolean | null;
  /** V5: the vault and secret of a grant, release, request or protection change, with their names. */
  vaultId?: string | null;
  secretId?: string | null;
  vaultName?: string | null;
  secretTitle?: string | null;
  /** V5 release: released, forbidden, not_found, reauth_required, unsupported, unavailable; shown or copied for a client report. */
  result?: string | null;
  field?: string | null;
  operation?: string | null;
  /** The effective mode of a release and the level it came from. */
  mode?: string | null;
  modeSource?: string | null;
  matchedNetwork?: string | null;
  reauthUsed?: boolean | null;
  clientIp?: string | null;
  sessionId?: string | null;
  requestId?: string | null;
  accessRequestId?: string | null;
  /** The request reason (created) or the decision note (approved, rejected). */
  note?: string | null;
  /** MCP track: the level of an api_permission.changed interval (none, read, write) and, for an api.* or mcp.* action, its channel, operation, outcome, the token name and the page title. Never content. */
  apiLevel?: string | null;
  apiChannel?: string | null;
  apiOperation?: string | null;
  apiOutcome?: string | null;
  apiTokenName?: string | null;
  pageId?: string | null;
  pageTitle?: string | null;
  /** A permanent delete (page.purged, space.purged, object.purged): the type and the title it had; no title for a private object. */
  objectTypeId?: string | null;
  objectTitle?: string | null;
  /** A backup change (backup.destination_changed, backup.downloaded): its kind and the URL, folder or file it names (SEC3-4). */
  backupKind?: string | null;
  backupDestination?: string | null;
  /** V4: the mode of a protection change, the network or proxy of an address change, the configuration version it activated. */
  protectionMode?: string | null;
  trustedAddressName?: string | null;
  trustedAddressRange?: string | null;
  configurationVersion?: string | null;
  hubConnectionId?: string | null;
  externalGroupId?: string | null;
  externalUserId?: string | null;
  hubReason?: string | null;
  hubMode?: string | null;
  hubBindingEnabled?: boolean | null;
  hubGrantsWikiAdmin?: boolean | null;
  hubPreviousState?: string | null;
  hubCurrentState?: string | null;
}
export interface AccessAuditPage {
  items: AccessAuditEvent[];
  hasMore: boolean;
  nextBeforeId: string | null;
  coverageStartedAt: string;
}

/** Narrows the access history: one vault and its secrets, a person (as actor or subject), a group or a space. */
export interface AccessAuditFilter {
  vaultId?: string;
  userId?: string;
  groupId?: string;
  spaceId?: string;
}

/** The access history of the wiki administrator, optionally narrowed (AccessAuditFilter). */
export function loadAccessAudit(options: { limit?: number; beforeId?: string | null } & AccessAuditFilter = {}, signal?: AbortSignal) {
  const query = new URLSearchParams({ limit: String(options.limit ?? 50) });
  if (options.beforeId) query.set("beforeId", options.beforeId);
  for (const key of ["vaultId", "userId", "groupId", "spaceId"] as const) if (options[key]) query.set(key, options[key]!);
  return api<AccessAuditPage>(`/admin/access-audit?${query}`, { signal });
}

/** `GET /api/admin/activity-summary` (ActivitySummary on the server): page edits, page opens and API, MCP and assistant tool calls. */
export interface ActivityCount { key: string; count: number }
export interface ActivityPerson { userId: string; name: string; count: number }
export interface ActivitySummary {
  /** Null for all time. */
  from: string | null;
  revisions: number;
  aiAssistedRevisions: number;
  revisionChannels: ActivityCount[];
  topEditors: ActivityPerson[];
  pageOpens: ActivityCount[];
  toolCalls: number;
  toolFailures: number;
  toolChannels: ActivityCount[];
  topTools: { channel: string; operation: string; calls: number; failures: number }[];
  topToolUsers: ActivityPerson[];
}

/** The wiki administrator's summary over the last `days` days, or over all time. */
export function loadActivitySummary(days: number | undefined, signal?: AbortSignal) {
  return api<ActivitySummary>(`/admin/activity-summary${days ? `?days=${days}` : ""}`, { signal });
}

/** One page of `GET /api/vaults/{id}/audit` or `/api/secrets/{id}/audit` (the server's VaultAuditList record, V5): the access audit's rows. */
export interface VaultAuditPage {
  items: AccessAuditEvent[];
  hasMore: boolean;
  nextBeforeId: string | null;
}
export type VaultAuditTarget = { vaultId: string } | { secretId: string };

/** The V5 audit of a vault (every secret) or of one secret, for Spravuje holders: releases, requests and grants. */
export async function loadVaultAudit(target: VaultAuditTarget, options: { limit?: number; beforeId?: string | null } = {}, signal?: AbortSignal): Promise<AccessAuditPage> {
  const query = new URLSearchParams({ limit: String(options.limit ?? 50) });
  if (options.beforeId) query.set("beforeId", options.beforeId);
  const path = "vaultId" in target ? `/vaults/${encodeURIComponent(target.vaultId)}/audit` : `/secrets/${encodeURIComponent(target.secretId)}/audit`;
  const page = await api<VaultAuditPage>(`${path}?${query}`, { signal });
  // The vault audit has no coverage start of its own: it covers what the vault's audit recorded.
  return { ...page, coverageStartedAt: "" };
}

/**
 * The audit tab of a vault (V5 `GET /api/vaults/{id}/audit`): readable by every Spravuje holder, which includes the space
 * administrators and the wiki administrator, with password releases, requests and grants.
 */
export const vaultAuditSource = {
  available: (caller: { isWikiAdmin: boolean; level: VaultAccessLevel | null | undefined }) => caller.isWikiAdmin || caller.level === "manage",
  load: (vaultId: string, options: { limit?: number; beforeId?: string | null }, signal?: AbortSignal) => loadVaultAudit({ vaultId }, options, signal),
};


const vaultLevels = new Set(["discover", "read", "manage"]);
// Icon and category of each event; the verb phrase is read where it is shown (t.audit.actions).
const sentences: Record<string, { icon: string; category: string }> = {
  "hub.connection.changed": { icon: "settings", category: "hub" },
  "hub.binding.changed": { icon: "users", category: "hub" },
  "hub.identity.changed": { icon: "shield-check", category: "hub" },
  // SEC-A-1: the OIDC providers decide who signs in, and who becomes a wiki administrator.
  "oidc.configuration_changed": { icon: "fingerprint", category: "signin" },
  "account.created": { icon: "account", category: "account" },
  // SEC1-2, SEC3-1: a backup holds the private notes too, so a new repository, deploy key, folder or the complete mode is audited,
  // and so is taking a complete archive out.
  "backup.destination_changed": { icon: "download", category: "backup" },
  "backup.downloaded": { icon: "download", category: "backup" },
  "account.updated": { icon: "account", category: "account" },
  "ai.group_policy.changed": { icon: "users", category: "group" },
  "ai.user_policy.changed": { icon: "account", category: "account" },
  "group.created": { icon: "users", category: "group" },
  "group.renamed": { icon: "users", category: "group" },
  "membership.added": { icon: "users", category: "group" },
  "membership.removed": { icon: "users", category: "group" },
  "grant.added": { icon: "book-open", category: "space" },
  "grant.removed": { icon: "book-open", category: "space" },
  "api_permission.enabled": { icon: "key-round", category: "account" },
  "api_permission.disabled": { icon: "key-round", category: "account" },
  "api_permission.changed": { icon: "key-round", category: "account" },
  "api.write": { icon: "code-xml", category: "api" },
  "mcp.write": { icon: "plug", category: "api" },
  "mcp.publish": { icon: "plug", category: "api" },
  "assistant.write": { icon: "user-check", category: "assistant" },
  "assistant.publish": { icon: "user-check", category: "assistant" },
  "mcp.enabled": { icon: "plug", category: "api" },
  "mcp.disabled": { icon: "plug", category: "api" },
  "credential.reset_requested": { icon: "key-round", category: "account" },
  "credential.password_changed": { icon: "key-round", category: "account" },
  "credential.activated": { icon: "key-round", category: "account" },
  "credential.recovered": { icon: "key-round", category: "account" },
  // V5: releases and requests.
  "secret.released": { icon: "key-round", category: "release" },
  "secret.release_denied": { icon: "shield-alert", category: "release" },
  "secret.client_report": { icon: "eye", category: "release" },
  "access_request.created": { icon: "user-check", category: "request" },
  "access_request.approved": { icon: "circle-check", category: "request" },
  "access_request.rejected": { icon: "circle-x", category: "request" },
  "access_request.cancelled": { icon: "circle-x", category: "request" },
  // V4: protection, networks, proxies and fresh sign-ins.
  "protection.set": { icon: "fingerprint", category: "protection" },
  "protection.cleared": { icon: "arrow-right-left", category: "protection" },
  "trusted_network.created": { icon: "network", category: "network" },
  "trusted_network.updated": { icon: "network", category: "network" },
  "trusted_network.removed": { icon: "trash", category: "network" },
  "trusted_proxy.created": { icon: "server", category: "network" },
  "trusted_proxy.updated": { icon: "server", category: "network" },
  "trusted_proxy.removed": { icon: "trash", category: "network" },
  "security.proxy_hops_changed": { icon: "server", category: "network" },
  "reauth.consumed": { icon: "fingerprint", category: "reauth" },
  "reauth.window_used": { icon: "fingerprint", category: "reauth" },
  // Unified trash: every permanent delete (SEC1-4).
  "page.purged": { icon: "trash-2", category: "trash" },
  "space.purged": { icon: "trash-2", category: "trash" },
  "object.purged": { icon: "trash-2", category: "trash" },
};
type Known = { icon: string; category: string };

const categoryIcons: Record<string, string> = {
  hub: "users", signin: "fingerprint", backup: "download", release: "key-round", request: "user-check", vault: "lock", protection: "fingerprint", network: "network",
  reauth: "shield-check", account: "account", api: "plug", assistant: "user-check", group: "users", space: "book-open", page: "page", trash: "trash-2",
};
export const auditCategoryLabels: Record<string, { label: string; icon: string }> = Object.fromEntries(
  Object.entries(categoryIcons).map(([key, icon]) => [key, { get label() { return t.audit.categories[key]!; }, icon }]),
);

/**
 * What an API, MCP or assistant result code means to an administrator. The assistant's proposal states (pending, applied,
 * declined, expired, superseded) are recorded as outcomes too: none of them is a refusal.
 */
interface ApiResult { outcome: ActivityItem["outcome"]; text: string; badge?: ActivityItem["badge"] }
type ApiBadgeTone = "danger" | "info" | "warning" | "neutral";
type ApiMeta = { text: keyof typeof t.audit.api; outcome: ActivityItem["outcome"]; badge?: { key: keyof typeof t.audit.apiBadges; tone: ApiBadgeTone; icon: string } };
const neutralBadge = (key: keyof typeof t.audit.apiBadges, icon: string): ApiMeta["badge"] => ({ key, tone: "neutral", icon });
const problemBadge = (key: keyof typeof t.audit.apiBadges): ApiMeta["badge"] => ({ key, tone: "warning", icon: "triangle-alert" });
const noPermission: ApiMeta["badge"] = { key: "noPermission", tone: "danger", icon: "ban" };
const serverError: ApiMeta["badge"] = { key: "error", tone: "danger", icon: "triangle-alert" };
const apiResults: Record<string, ApiMeta> = {
  ok: { outcome: "changed", text: "ok" },
  allowed: { outcome: "changed", text: "ok" },
  applied: { outcome: "changed", text: "applied" },
  pending: { outcome: "changed", text: "pending", badge: { key: "pending", tone: "info", icon: "clock" } },
  declined: { outcome: "changed", text: "declined", badge: neutralBadge("declined", "circle-x") },
  expired: { outcome: "changed", text: "expired", badge: neutralBadge("expired", "clock") },
  superseded: { outcome: "changed", text: "superseded", badge: neutralBadge("superseded", "clock") },
  cancelled: { outcome: "failed", text: "cancelled", badge: problemBadge("cancelled") },
  scope_missing: { outcome: "denied", text: "scopeMissing", badge: noPermission },
  forbidden: { outcome: "denied", text: "forbidden", badge: noPermission },
  unauthenticated: { outcome: "denied", text: "unauthenticated", badge: { key: "unauthenticated", tone: "danger", icon: "ban" } },
  version_conflict: { outcome: "failed", text: "versionConflict", badge: problemBadge("conflict") },
  collaboration_unsaved: { outcome: "failed", text: "editing", badge: problemBadge("editing") },
  live_edits: { outcome: "failed", text: "editing", badge: problemBadge("editing") },
  locked: { outcome: "failed", text: "locked", badge: problemBadge("locked") },
  rate_limited: { outcome: "failed", text: "rateLimited", badge: problemBadge("rateLimited") },
  unavailable: { outcome: "failed", text: "unavailable", badge: problemBadge("unavailable") },
  error: { outcome: "failed", text: "error", badge: serverError },
  internal_error: { outcome: "failed", text: "error", badge: serverError },
};
/** The families of the many specific codes (heading_not_found, tree_conflict, invalid_argument, …). */
function apiMeta(code: string): ApiMeta {
  if (apiResults[code]) return apiResults[code];
  if (code === "gone" || code.endsWith("not_found")) return { outcome: "failed", text: "gone", badge: problemBadge("notFound") };
  if (code.endsWith("conflict") || code.endsWith("_changed")) return { outcome: "failed", text: "changed", badge: problemBadge("conflict") };
  if (code.startsWith("invalid") || code === "too_large" || code === "unsupported_media") return { outcome: "failed", text: "invalid", badge: problemBadge("invalid") };
  if (code.endsWith("unavailable")) return apiResults.unavailable!;
  return { outcome: "failed", text: "failed", badge: problemBadge("failed") };
}
function apiResult(code: string): ApiResult {
  const meta = apiMeta(code);
  return { outcome: meta.outcome, text: t.audit.api[meta.text], badge: meta.badge && { label: t.audit.apiBadges[meta.badge.key], tone: meta.badge.tone, icon: meta.badge.icon } };
}

/** REST operations and MCP tool names as a verb phrase; the channel follows the target ("… stránky Nabídka přes MCP"). */
const operationKeys: Record<string, keyof typeof t.audit.operations> = {
  "page.create": "create", atlas_create_draft: "createDraft",
  "page.save": "save", atlas_replace_page: "replace",
  atlas_update_section: "updateSection", atlas_insert_section: "insertSection",
  "page.rename": "renamePage", atlas_rename_page: "renamePage",
  "page.move": "movePage", atlas_move_page: "movePage",
  "page.move_space": "moveSpace", atlas_move_page_to_space: "moveSpace",
  atlas_arrange_pages: "arrange",
  "tree.sort": "sortTree", atlas_sort_pages: "sortTree",
  "page.trash": "trashPage", atlas_trash_page: "trashPage",
  "page.untrash": "untrashPage", "page.restore": "restoreVersion", "page.version_name": "versionName", atlas_restore_page: "restorePage",
  "page.publish": "publish", atlas_publish: "publish",
  "page.tags_set": "tags", "page.tags_change": "tags", atlas_set_tags: "tags",
  atlas_apply_labels: "applyLabels",
  "page.decoration_set": "decoration", atlas_set_decoration: "decoration",
  "page.embed_update": "embed", atlas_update_embed: "embed",
  "attachment.upload": "upload", atlas_upload_attachment: "upload",
  "board.create": "boardCreate", atlas_create_board: "boardCreate",
  "board.edit": "boardEdit", atlas_edit_board: "boardEdit",
  "board.move": "boardMove", atlas_move_board: "boardMove",
  "board.delete": "boardDelete", atlas_delete_board: "boardDelete",
  "board.untrash": "boardUntrash", atlas_untrash_board: "boardUntrash",
  atlas_create_note: "noteCreate", atlas_edit_note: "noteEdit", atlas_trash_note: "noteTrash",
  atlas_upsert_contact: "contactSave", atlas_merge_contacts: "contactsMerge",
  "object.create": "objectCreate", "object.change": "objectChange", "object.trash": "objectTrash",
  "object.restore": "objectRestore", atlas_restore_object: "objectRestore",
  "tree_reference.add": "refAdd", atlas_add_tree_reference: "refAdd",
  "tree_reference.remove": "refRemove", atlas_remove_tree_reference: "refRemove",
  "tree_reference.move": "refMove", atlas_move_tree_reference: "refMove",
  "notifications.read": "notificationsRead", atlas_mark_notifications_read: "notificationsRead",
  "job.run": "jobRun", atlas_run_job: "jobRun", "job.retry": "jobRetry",
  "job.cancel": "jobCancel", atlas_cancel_job_run: "jobCancel", "job.pause": "jobPause",
  "page.favorite": "favorite", "page.visit": "visit",
  "assistant.proposal": "proposal",
};
/** The phrase of an operation; Projector's many tools share one, the assistant's decision reads by its result. */
export function operationVerb(operation: string | null | undefined, outcome?: string | null): string | undefined {
  if (!operation) return undefined;
  const verbs = t.audit.operations;
  if (operation === "assistant.decision") return outcome === "applied" ? verbs.decisionApplied : outcome === "declined" ? verbs.decisionDeclined : verbs.decision;
  if (operation.startsWith("projector") || operation.startsWith("requirement")) return verbs.projector;
  const key = operationKeys[operation];
  return key && verbs[key];
}
const viaChannel: Record<string, string> = { "api.write": "api", "mcp.write": "mcp", "mcp.publish": "mcp", "assistant.write": "assistant", "assistant.publish": "assistant" };

const releaseResults: Record<string, { label: keyof typeof t.audit.release; outcome: ActivityItem["outcome"]; badge?: { label: keyof typeof t.audit.release; tone: "info" | "danger"; icon: string } }> = {
  released: { label: "released", outcome: "allowed" },
  forbidden: { label: "forbidden", outcome: "denied" },
  not_found: { label: "notFound", outcome: "denied" },
  reauth_required: { label: "reauthRequired", outcome: "denied", badge: { label: "badgeReauth", tone: "info", icon: "fingerprint" } },
  unsupported: { label: "unsupported", outcome: "denied", badge: { label: "badgeUnsupported", tone: "danger", icon: "shield-alert" } },
  unavailable: { label: "unavailable", outcome: "failed" },
};
const releaseLabel = (code: string) => (releaseResults[code] ? t.audit.release[releaseResults[code].label] : undefined);
const modeLabel = (mode: string | null | undefined) => (mode ? (protectionModeLabels[mode as ProtectionMode] ?? mode) : undefined);
const fieldLabel = (field: string | null | undefined) => (field ? (vaultFieldLabels[field as VaultField] ?? field) : undefined);

/** Where a vault event happened: "heslo Router · trezor Servery a síť". */
function secretTarget(event: AccessAuditEvent) {
  const vault = event.vaultName ? t.audit.vault(event.vaultName) : event.vaultId ? t.audit.vaultBare : undefined;
  const secret = event.secretTitle ? t.audit.secret(event.secretTitle) : event.secretId ? t.audit.secretBare : undefined;
  return [secret, vault].filter(Boolean).join(" · ") || undefined;
}

/** The technical details of a release, request or security change. Never a value. */
function securityDetails(event: AccessAuditEvent): ActivityDetail[] {
  const d = t.audit.details;
  const details: ActivityDetail[] = [];
  const add = (label: string, value: string | null | undefined, hint?: string) => {
    if (value) details.push({ label, value, ...(hint ? { hint } : {}) });
  };
  const release = event.action.startsWith("secret.");
  if (release || event.action.startsWith("access_request.") || event.action.startsWith("protection.")) add(d.serverTime, formatDateTime(event.occurredAt));
  if (release) {
    add(d.clientAddress, event.clientIp);
    add(d.networkRule, event.matchedNetwork ? `${event.matchedNetwork}${event.configurationVersion ? d.version(event.configurationVersion) : ""}` : d.noRule);
    add(d.effectiveMode, event.mode ? `${modeLabel(event.mode)}${event.modeSource ? ` · ${t.audit.sources[event.modeSource] ?? event.modeSource}` : ""}` : undefined);
    add(d.networkException, event.mode ? (event.mode === "outsideTrustedNetworks" && event.matchedNetwork ? d.used(event.matchedNetwork) : d.notUsed) : undefined);
    add(d.field, fieldLabel(event.field));
    add(d.operation, event.operation === "copy" ? d.copy : event.operation === "reveal" ? d.reveal : event.operation);
    if (event.action !== "secret.client_report") add(d.result, event.result ? (releaseLabel(event.result) ?? event.result) : undefined);
    if (typeof event.reauthUsed === "boolean") add(d.reauth, event.reauthUsed ? d.reauthYes : d.reauthNo);
    if (event.action === "secret.client_report")
      add(d.clientReports, event.result === "copied" ? d.toClipboard : event.result === "shown" ? d.onScreen : (event.result ?? d.shown), d.clientHint);
    add(d.session, event.sessionId ? `…${event.sessionId.slice(-6)}` : undefined);
    add(d.requestId, event.requestId);
  }
  if (event.action.startsWith("access_request.")) {
    add(d.level, event.role && vaultLevels.has(event.role) ? vaultAccessLevelLabels[event.role as VaultAccessLevel] : undefined);
    add(event.action === "access_request.created" ? d.reason : d.note, event.note);
  }
  if (event.action.startsWith("protection.")) {
    add(d.mode, modeLabel(event.protectionMode));
    add(d.level, event.secretId ? d.levelSecret : event.vaultId ? d.levelVault : d.levelInstance);
    add(d.configVersion, event.configurationVersion);
  }
  if (event.action.startsWith("trusted_") || event.action === "security.proxy_hops_changed") {
    add(d.range, event.trustedAddressRange);
    add(d.configVersion, event.configurationVersion);
  }
  return details;
}

const securityCategories = new Set(["release", "request", "protection", "network", "reauth"]);

/** A V4/V5 event (release, request, protection, network, fresh sign-in) as "kdo co kdy". */
function securityActivity(event: AccessAuditEvent, known: Known): ActivityItem {
  const a = t.audit;
  const space = event.spaceName ? a.space(event.spaceName) : undefined;
  const level = event.role && vaultLevels.has(event.role) ? a.level(vaultAccessLevelLabels[event.role as VaultAccessLevel]) : undefined;
  let action = a.actions[event.action] ?? event.action;
  let target: string | undefined;
  let context: string | undefined;
  let outcome: ActivityItem["outcome"] = "changed";
  let badge: ActivityItem["badge"];
  let icon = known.icon;
  if (known.category === "release") {
    if (event.action === "secret.released") {
      action = event.operation === "copy" ? a.copied : a.shown;
      icon = event.operation === "copy" ? "copy" : "eye";
    }
    const field = fieldLabel(event.field)?.toLocaleLowerCase(locale) ?? a.secretBare;
    const where = [event.secretTitle ?? (event.secretId ? a.secretBare : undefined), event.vaultName ? a.vault(event.vaultName) : undefined].filter(Boolean).join(" · ");
    target = [field, where].filter(Boolean).join(" · ");
    context = [space, modeLabel(event.mode)].filter(Boolean).join(" · ") || undefined;
    const result = event.result ? releaseResults[event.result] : undefined;
    outcome = event.action === "secret.release_denied" ? (result?.outcome ?? "denied") : "allowed";
    if (event.action === "secret.release_denied") badge = result?.badge && { label: a.release[result.badge.label], tone: result.badge.tone, icon: result.badge.icon };
    else if (event.action === "secret.client_report") {
      action = event.result === "copied" || event.operation === "copy" ? a.reportedCopy : a.reportedShow;
      badge = { label: a.clientReport, tone: "neutral", icon: "eye" };
    }
  } else if (known.category === "request") {
    // "požádal(a) o přístup k heslu Router · trezor Servery", "… k trezoru Servery".
    const where = event.secretId ? a.requestSecret(event.secretTitle ?? "") : a.requestVault(event.vaultName ?? "");
    target = [event.secretId && event.vaultName ? a.requestSecretIn(where, event.vaultName) : where, event.action === "access_request.created" ? undefined : event.userName].filter(Boolean).join(" · ");
    context = [space, level].filter(Boolean).join(" · ") || undefined;
    outcome = event.action === "access_request.approved" ? "allowed" : event.action === "access_request.rejected" ? "denied" : "changed";
  } else if (known.category === "protection") {
    target = secretTarget(event) ?? a.defaultProtection;
    context = [space, event.action === "protection.set" ? modeLabel(event.protectionMode) : undefined].filter(Boolean).join(" · ") || undefined;
  } else if (known.category === "network") {
    target = event.trustedAddressName ?? undefined;
    context = event.trustedAddressRange ?? (event.configurationVersion ? a.configVersion(event.configurationVersion) : undefined);
  } else {
    outcome = "allowed";
    badge = { label: a.verified, tone: "success", icon: "fingerprint" };
    target = secretTarget(event) ? a.forTarget(secretTarget(event)!) : undefined;
  }
  const details = securityDetails(event);
  return {
    id: event.id,
    at: event.occurredAt,
    actorId: event.actorId ?? "system",
    actor: event.actorName ?? a.system,
    action,
    target,
    context,
    category: known.category,
    outcome,
    icon,
    details: details.length ? details : undefined,
    spaceId: event.spaceId ?? undefined,
    vaultId: event.vaultId ?? undefined,
    badge,
  };
}

/** An audit row as "kdo co kdy": actor, verb phrase, target, and the level, result or mode as context. */
export function auditActivity(event: AccessAuditEvent): ActivityItem {
  const a = t.audit;
  const d = a.details;
  const known = sentences[event.action];
  const knownAction = a.actions[event.action];
  const actor = event.actorName ?? a.system;
  if (known?.category === "hub") {
    const details: ActivityDetail[] = [];
    const add = (label: string, value: string | null | undefined) => { if (value) details.push({ label, value }); };
    add(d.hubConnection, event.hubConnectionId);
    add(d.hubGroupId, event.externalGroupId);
    add(d.hubUserId, event.externalUserId);
    add(d.reason, event.hubReason ? a.hubReasons[event.hubReason] ?? event.hubReason : undefined);
    add(d.hubMode, event.hubMode ? hubModeLabels[event.hubMode as HubMode] ?? event.hubMode : undefined);
    if (event.hubBindingEnabled != null) add(d.hubMembership, event.hubBindingEnabled ? d.on : d.off);
    if (event.hubGrantsWikiAdmin != null) add(d.hubWikiAdmin, event.hubGrantsWikiAdmin ? d.on : d.off);
    if (event.hubCurrentState) add(d.stateChange, [event.hubPreviousState ? a.hubStates[event.hubPreviousState] ?? event.hubPreviousState : undefined, a.hubStates[event.hubCurrentState] ?? event.hubCurrentState].filter(Boolean).join(" → "));
    return { id: event.id, at: event.occurredAt, actorId: event.actorId ?? "system", actor, action: knownAction!,
      target: event.groupName ?? event.userName ?? undefined, category: "hub", icon: known.icon, outcome: "changed", details };
  }
  if (known && securityCategories.has(known.category)) return securityActivity(event, known);
  if (known?.category === "backup") {
    return { id: event.id, at: event.occurredAt, actorId: event.actorId ?? "system", actor,
      action: (event.action === "backup.destination_changed" && event.backupKind && a.backupChanges[event.backupKind]) || knownAction!,
      target: event.backupDestination ?? undefined, category: "backup", outcome: "changed", icon: known.icon };
  }
  if (known?.category === "trash") {
    const space = event.action === "space.purged";
    return { id: event.id, at: event.occurredAt, actorId: event.actorId ?? "system", actor,
      action: (event.action === "object.purged" && event.objectTypeId && a.purgedObjects[event.objectTypeId]) || knownAction!,
      target: (space ? (event.spaceName ?? event.objectTitle) : event.objectTitle) ?? undefined, context: !space && event.spaceName ? a.space(event.spaceName) : undefined,
      category: "trash", outcome: "changed", icon: known.icon, spaceId: event.spaceId ?? undefined };
  }
  const resource = event.action === "resource_grant.added" || event.action === "resource_grant.removed";
  const vault = resource && !!event.role && vaultLevels.has(event.role);
  const added = event.action.endsWith(".added");
  const secret = vault && !!(event.secretId || event.secretTitle);
  const action = resource ? a.grantAccess(added ? a.grantAdded : a.grantRemoved, vault ? (secret ? a.forSecret : a.forVault) : a.forPage) : (knownAction ?? event.action);
  // One staircase everywhere: "úroveň Čte" for a vault, a page and a space alike.
  const levelName = event.role ? (vault ? vaultAccessLevelLabels[event.role as VaultAccessLevel] : roleLabel(event.role)) : undefined;
  const level = levelName ? a.level(levelName) : undefined;
  // The sentence reads "kdo — co udělal — s čím"; whom it concerns goes to the context line, so a
  // name never lands where Czech expects the object ("přidal(a) do skupiny Provoz · účet Jana").
  const account = event.userName ?? (event.userId ? a.unknownAccount : undefined);
  const subject = account ? a.account(account) : event.groupName ? a.group(event.groupName) : undefined;
  const membership = event.action.startsWith("membership.");
  const spaceRole = event.action === "grant.added" || event.action === "grant.removed";
  // A tool call reads by what it did to which page ("upravil(a) sekci stránky Nabídka přes MCP"); the token goes to the context.
  const tool = known?.category === "api" || known?.category === "assistant" ? a.via[viaChannel[event.action]!] : undefined;
  const result = tool && event.apiOutcome ? apiResult(event.apiOutcome) : undefined;
  const page = tool ? (event.pageTitle ?? (event.pageId ? a.unpublishedPage : undefined)) : undefined;
  // The event's own action ("upravil(a) koncept přes MCP") already names the channel; an operation's verb does not.
  const verb = tool ? operationVerb(event.apiOperation, event.apiOutcome) : undefined;
  const target = tool ? page : resource
    ? (vault ? (secret ? (event.secretTitle ?? undefined) : (event.vaultName ?? undefined)) : undefined)
    : membership ? (event.groupName ?? undefined)
    : spaceRole ? (event.spaceName ?? undefined)
    : event.action.startsWith("group.") ? (event.groupName ?? undefined)
    : [account, event.groupName ? a.group(event.groupName) : undefined].filter(Boolean).join(" · ") || undefined;
  const whom = resource || membership || spaceRole ? subject : undefined;
  // The account flags an event recorded (created, updated, API permission) belong to its details.
  const details: ActivityDetail[] = [
    ...(result ? [{ label: d.whatHappened, value: result.text }] : []),
    ...(levelName ? [{ label: d.level, value: levelName }] : []),
    ...(vault && secretTarget(event) ? [{ label: d.target, value: secretTarget(event)! }] : []),
    ...(whom ? [{ label: d.concerns, value: whom }] : []),
    ...(typeof event.isActive === "boolean" ? [{ label: d.accountState, value: event.isActive ? d.active : d.deactivated }] : []),
    ...(typeof event.isWikiAdmin === "boolean" ? [{ label: d.wikiRole, value: event.isWikiAdmin ? d.wikiAdmin : d.regularAccount }] : []),
    ...(event.apiLevel ? [{ label: d.personalApiLevel, value: apiLevelInfo[apiLevel(event.apiLevel)].label }] : []),
    ...(typeof event.canIssueTokens === "boolean" && !event.apiLevel ? [{ label: d.personalApiTokens, value: event.canIssueTokens ? d.allowed : d.forbidden }] : []),
    ...(event.apiChannel ? [{ label: d.channel, value: a.channels[event.apiChannel] ?? event.apiChannel }] : []),
    ...(event.apiOperation ? [{ label: d.operation, value: event.apiOperation }] : []),
    ...(event.apiOutcome ? [{ label: d.resultCode, value: event.apiOutcome }] : []),
    ...(event.apiTokenName ? [{ label: d.token, value: event.apiTokenName }] : []),
  ];
  // The level an account's API was set to reads on the context line: "účet Jana · úroveň API Zápis".
  const apiContext = event.apiLevel ? a.apiLevel(apiLevelInfo[apiLevel(event.apiLevel)].label) : undefined;
  return {
    id: event.id,
    at: event.occurredAt,
    actorId: event.actorId ?? "system",
    actor,
    // The proposal and the decision speak of the assistant themselves; every other tool says its channel after the target.
    action: verb ?? action,
    target,
    href: tool && event.pageId ? buildShellHashEncoded({ view: "page", pageId: event.pageId }) : undefined,
    via: verb && !event.apiOperation?.startsWith("assistant.") ? tool : undefined,
    badge: result?.badge,
    context: [whom, !tool && event.pageTitle ? a.page(event.pageTitle) : undefined, tool && event.apiTokenName ? a.token(event.apiTokenName) : undefined, secret && event.vaultName ? a.vault(event.vaultName) : undefined, event.spaceName && !spaceRole ? a.space(event.spaceName) : undefined, level, apiContext].filter(Boolean).join(" · ") || undefined,
    category: resource ? (vault ? "vault" : "page") : (known?.category ?? "account"),
    outcome: result?.outcome ?? "changed",
    icon: resource ? (vault ? (added ? "key-round" : "lock") : "page") : (known?.icon ?? "history"),
    details: details.length ? details : undefined,
    spaceId: event.spaceId ?? undefined,
    vaultId: event.vaultId ?? undefined,
  };
}
