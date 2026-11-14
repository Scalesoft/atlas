import { api, post, put } from "./api";
import type { ApiLevel } from "./apiTokenRights";
import { securityChange } from "./protection";

export interface DirectoryUser {
  id: string;
  username: string;
  displayName: string;
  isActive: boolean;
  isWikiAdmin: boolean;
  version: string;
  hasBuiltinCredential: boolean;
  source: "builtin" | "external";
  credentialState: "active" | "pending" | "none";
}
export interface Group {
  id: string;
  name: string;
  version: string;
  source: "local" | "hub";
  hubUrl?: string | null;
}
export interface Member {
  id: string;
  username: string;
  displayName: string;
  isActive: boolean;
  version: string;
}
export interface Grant {
  id: string;
  userId: string | null;
  groupId: string | null;
  name: string;
  role: SpaceRole;
  version: string;
  startAt?: string | null;
  expiresAt?: string | null;
}
export type SpaceRole = "Reader" | "Writer" | "Admin";
/** GET /api/spaces/{id}/access: the space's grants only. People and groups for a new grant come from /api/access/candidates. */
export interface Access {
  grants: Grant[];
}
/** A one-time code for setting the local password. purpose: invitation (an account that has never set a password; 7 days) or reset
 * (30 minutes). */
export interface Invitation {
  token: string;
  expiresAt: string;
  purpose: "invitation" | "reset";
  user?: DirectoryUser;
}
/** The separate permission to issue personal API tokens and its ceiling (`/admin/users/{id}/api-access`): `level` is none, read,
 * draft or publish; `canIssueTokens` stays for the 1.5 clients (level is not none). */
export interface ApiAccess {
  canIssueTokens: boolean;
  version: string | null;
  level?: ApiLevel;
}
export interface AiQuotaOverrides {
  searchAnswerDailyLimit: number | null;
  chatAnswerDailyLimit: number | null;
  pageSummaryDailyLimit: number | null;
  agentDailyLimit: number | null;
}
export interface AiLimitRow {
  task: "search.answer" | "chat.answer" | "page.summary" | "assistant.agent";
  effective: number | null;
  used: number;
  remaining: number | null;
  resetAt: string;
  source: "default" | "custom" | "unlimited";
  origin: "global" | "group" | "user";
  groupId: string | null;
  groupName: string | null;
}
/** `toolsEnabled`: the resolved "AI s nástroji" right (D17); missing means false. */
export interface AiPersonalUsage { enabled: boolean; toolsEnabled?: boolean; limits: AiLimitRow[] }
export interface AiUserPolicy extends AiQuotaOverrides {
  enabled: boolean | null;
  /** Personal "AI s nástroji" exception; null inherits from the groups. */
  toolsEnabled: boolean | null;
  version: string | null;
  resolved: AiPersonalUsage;
}
export interface AiAccessGroup extends AiQuotaOverrides { groupId: string; groupName: string; toolsEnabled: boolean }
export interface AiAccessView { groups: AiAccessGroup[]; version: string | null }
export interface AuthMethods {
  builtinEnabled: boolean;
  providers: { id: string; displayName: string; loginUrl: string }[];
  /** Present only on a local Development server, for a loopback request, with Authentication:DeveloperSignIn:Enabled. */
  developerSignIn?: true;
}
/** GET /admin/users/summary: the directory counted by the server (the list stops at `adminPageLimit` rows). */
export interface UserDirectorySummary { total: number; pending: number; deactivated: number; admins: number }
/** The server's page size of the account and member lists: a page of exactly this many rows may have another behind it. */
export const adminPageLimit = 1000;
const query = (values: Record<string, string | number | undefined>) => {
  const text = new URLSearchParams(Object.entries(values).filter(([, value]) => value).map(([key, value]) => [key, String(value)])).toString();
  return text ? `?${text}` : "";
};
export const adminApi = {
  /** One page from `offset`; `q` = the server's search over name and username, `id` = just that account. */
  users: (params: { q?: string; offset?: number; id?: string } = {}) => api<DirectoryUser[]>(`/admin/users${query(params)}`),
  userSummary: () => api<UserDirectorySummary>("/admin/users/summary"),
  /** A new wiki administrator is a security change: `proof` is a verified fresh sign-in, or null inside its window. */
  createUser: (
    body: {
      username: string;
      displayName: string;
      isWikiAdmin: boolean;
    },
    proof?: string | null,
  ) => securityChange<Invitation>("/admin/users", "POST", body, proof),
  /** Turning Správce wiki on or off is a security change: `proof` is a verified fresh sign-in, or null inside its window. */
  updateUser: (
    id: string,
    body: {
      displayName: string;
      isActive: boolean;
      isWikiAdmin: boolean;
      expectedVersion: string;
    },
    proof?: string | null,
  ) => securityChange<DirectoryUser>(`/admin/users/${id}`, "PUT", body, proof),
  resetPassword: (user: DirectoryUser, proof?: string | null) =>
    securityChange<Invitation>(`/admin/users/${user.id}/reset-password`, "POST", {
      expectedVersion: user.version,
    }, proof),
  apiAccess: (id: string) => api<ApiAccess>(`/admin/users/${id}/api-access`),
  setApiAccess: (id: string, level: ApiLevel, expectedVersion: string | null) =>
    put<ApiAccess>(`/admin/users/${id}/api-access`, { level, expectedVersion }),
  aiPolicy: (id: string) => api<AiUserPolicy>(`/admin/users/${id}/ai-policy`),
  setAiPolicy: (id: string, body: AiQuotaOverrides & { enabled: boolean | null; toolsEnabled: boolean | null; expectedVersion: string | null }) =>
    put<AiUserPolicy>(`/admin/users/${id}/ai-policy`, body),
  aiAccess: () => api<AiAccessView>("/admin/ai/access"),
  setAiAccess: (groups: Omit<AiAccessGroup, "groupName">[], expectedVersion: string | null) =>
    put<AiAccessView>("/admin/ai/access", { groups, expectedVersion }),
  groups: () => api<Group[]>("/admin/groups"),
  createGroup: (name: string) => post<Group>("/admin/groups", { name }),
  updateGroup: (group: Group, name: string) =>
    put<Group>(`/admin/groups/${group.id}`, {
      name,
      expectedVersion: group.version,
    }),
  members: (id: string, params: { q?: string; offset?: number } = {}) => api<Member[]>(`/admin/groups/${id}/members${query(params)}`),
  setMember: (
    group: Group,
    userId: string,
    member: Member | undefined,
    isMember: boolean,
    proof?: string | null,
  ) =>
    securityChange(`/admin/groups/${group.id}/members/${userId}`, "PUT", {
      isMember,
      expectedVersion: member?.version ?? null,
    }, proof),
  // Space grants go through the access layer (accessApi.ts, kind space); the old endpoint stays for other clients.
};
/** Weakest first; the words come from roles.ts. */
export const spaceRoles = ["Reader", "Writer", "Admin"] as const satisfies readonly SpaceRole[];
