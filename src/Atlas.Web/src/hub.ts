import { api, post } from "./api";
import shellMore from "./i18n/shell-more";
import { securityChange } from "./protection";

export type HubMode = "Disabled" | "Preview" | "Enforced";
export interface HubBinding {
  groupId: string; name: string; externalGroupId: string; enabled: boolean; grantsWikiAdmin: boolean; version: string; hubUrl: string;
}
export interface HubIdentity {
  userId: string; displayName: string; externalIdentityId: string; state: string;
  verifiedUntil: string | null; lastSuccessAt: string | null; lastAttemptAt: string | null; lastErrorCode: string | null;
}
export interface HubSettings {
  connectionId: string | null; version: string | null; issuer: string; baseUrl: string; hasToken: boolean; mode: HubMode;
  providers: { issuer: string; displayName: string }[]; bindings: HubBinding[]; identities: HubIdentity[];
  pollSeconds: number; maxValiditySeconds: number;
  health?: { connectionId: string; state: "Disabled" | "Unverified" | "Healthy" | "Error" | "Stale"; known: number; pending: number; expired: number; errors: number; lastSuccessAt: string | null } | null;
}
export interface HubDraft { issuer: string; baseUrl: string; token?: string; mode: HubMode; expectedVersion: string | null }
export interface HubBindingDraft { enabled: boolean; grantsWikiAdmin: boolean; expectedVersion: string }
export interface HubGroups { groups: { id: string; name: string }[]; hasMore: boolean }
export interface ProfileHubStatus {
  mode: HubMode | null;
  identity: Pick<HubIdentity, "state" | "verifiedUntil" | "lastSuccessAt" | "lastAttemptAt" | "lastErrorCode"> | null;
}
export const profileHubApi = {
  status: () => api<ProfileHubStatus>("/profile/hub"),
  verify: () => post<void>("/profile/hub/verify"),
};
export interface HubMigrationGrant {
  id: string; spaceId: string | null; pageId: string | null; userId: string | null; groupId: string | null; role: number;
  startAt: string | null; expiresAt: string | null; activeFrom: string; activeTo: string | null;
}
export interface HubMigrationPage { pageId: string; grants: { groupId: string; role: "Reader" | "Writer" | "Admin" }[] }
export interface HubMigrationDraft {
  expectedVersion: string; groups: { localGroupId: string; externalGroupId: string }[];
  removeSpaceGrantIds: string[]; removePageGrantIds: string[]; removeWikiAdminUserIds: string[]; pageBoundaries: HubMigrationPage[];
}
export interface HubMigrationInventory {
  users: { id: string; displayName: string; isActive: boolean; isWikiAdmin: boolean; oidcAdminIssuer: string | null; hasPassword: boolean }[];
  groups: { id: string; name: string }[];
  members: { id: string; groupId: string; userId: string; activeFrom: string; activeTo: string | null }[];
  effectiveMembers: HubMigrationInventory["members"];
  grants: HubMigrationGrant[];
  spaces: { id: string; name: string }[];
  pages: { id: string; spaceId: string; title: string; ownAccess: boolean; isTrashed: boolean; isTemplate: boolean }[];
  identities: { id: string; userId: string; issuer: string; subject: string; activeTo: string | null }[];
  bindings: { groupId: string; externalGroupId: string; enabled: boolean; grantsWikiAdmin: boolean }[];
  effectiveWikiAdmins: string[];
}
export interface HubMigrationRoute { userId: string; resourceKind: string; resourceId: string | null; role: number; source: string; grantId: string | null; groupId: string | null }
export interface HubMigrationPreview {
  ticket: string; expiresAt: string; configurationVersion: string; inventory: HubMigrationInventory;
  groupDiffs: { localGroupId: string; externalGroupId: string; convertsLocalGroup: boolean; beforeUserIds: string[]; afterUserIds: string[] }[];
  removedGrants: HubMigrationGrant[]; removedWikiAdminUserIds: string[]; pageBoundaries: HubMigrationPage[];
  copiedGrantsRemoved: { pageId: string; sourceSpaceGrantId: string; userId: string | null; groupId: string | null; role: number }[];
  beforeRoutes: HubMigrationRoute[]; afterRoutes: HubMigrationRoute[]; warnings: string[];
  observedIdentities: { identityId: string; userId: string; state: "Active" | "Disabled" | "Missing" }[];
}
export interface HubMigrationResult { convertedGroups: number; removedGrants: number; removedWikiAdmins: number; isolatedPages: number }
export const hubMigrationApi = {
  inventory: () => api<HubMigrationInventory>("/admin/modules/hub/migration/inventory"),
  preview: (draft: HubMigrationDraft) => post<HubMigrationPreview>("/admin/modules/hub/migration/preview", draft),
  apply: (ticket: string, proof?: string | null) => securityChange<HubMigrationResult>("/admin/modules/hub/migration/apply", "POST", { ticket }, proof),
};
const endpoint = "/admin/modules/hub";
export const hubModeLabels: Record<HubMode, string> = { get Disabled() { return shellMore.hub.disabled; }, get Preview() { return shellMore.hub.preview; }, get Enforced() { return shellMore.hub.enforced; } };
export function hubErrorMessage(code: string): string {
  return ({
    timeout: shellMore.hub.timeout, unavailable: shellMore.hub.unavailable,
    unauthorized: shellMore.hub.unauthorized,
    forbidden: shellMore.hub.forbidden,
    "insufficient-permissions": shellMore.hub.forbidden,
    "rate-limited": shellMore.hub.rateLimited,
    "not-visible": shellMore.hub.notVisible,
    "identity-mismatch": shellMore.hub.identityMismatch,
    "invalid-response": shellMore.hub.invalidResponse,
    "response-too-large": shellMore.hub.responseTooLarge,
  } as Record<string, string>)[code] ?? shellMore.hub.verificationFailed;
}
export const hubApi = {
  settings: () => api<HubSettings>(endpoint),
  save: (draft: HubDraft, proof?: string | null) => securityChange<HubSettings>(endpoint, "PUT", draft, proof),
  test: (draft: HubDraft) => post<{ ok: boolean; message: string }>(`${endpoint}/test`, draft),
  search: (query: string) => api<HubGroups>(`${endpoint}/groups/search?${new URLSearchParams({ query })}`),
  attach: (externalGroupId: string, expectedVersion: string | null) => post<HubSettings>(`${endpoint}/groups`, { externalGroupId, expectedVersion }),
  binding: (groupId: string, draft: HubBindingDraft, proof?: string | null) => securityChange<HubSettings>(`${endpoint}/groups/${encodeURIComponent(groupId)}`, "PUT", draft, proof),
  verify: (userId?: string) => post<{ verified: number; failed: number; deferred: number }>(`${endpoint}/verify`, { userId }),
};
