import { api, post, put } from "./api";
import type { ApiTokenRef } from "./channel";

/** Why a person or group reaches a page: global (WikiAdmin), spaceAdmin (always, D4), space (space default, only without own
 * access), page (a direct page grant). groupId/name: the group it comes through. version: CAS token of a page grant. */
export interface AccessSource {
  kind: "global" | "hubGlobal" | "spaceAdmin" | "space" | "page";
  name: string;
  role: string;
  groupId?: string | null;
  grantId?: string | null;
  startAt?: string | null;
  expiresAt?: string | null;
  version?: string | null;
  effective?: boolean;
}
export interface AccessPerson {
  userId: string;
  displayName: string;
  username: string;
  role: string | null;
  isWikiAdmin: boolean;
  canReadPublished: boolean;
  canReadDraft: boolean;
  canEdit: boolean;
  lastViewedAt?: string | null;
  lastEditedAt?: string | null;
  sources: AccessSource[];
}
export interface AccessGroup {
  groupId: string;
  name: string;
  memberCount: number;
  role: string | null;
  sources: AccessSource[];
}
export interface PageAccessCounts {
  manage: number;
  edit: number;
  read: number;
  /** Space members hidden by own access (they gain the page on return to the space defaults). */
  hidden: number;
  loseOnReset: number;
  downgradeOnReset: number;
  defaultGrants: number;
  losing: string[];
}
export interface PageAccess {
  spaceId: string;
  spaceName: string;
  isPublished: boolean;
  canManage: boolean;
  /** The caller's own access row (not the page access mode). */
  myAccess: AccessPerson;
  items: AccessPerson[];
  hasMore: boolean;
  pageTitle?: string;
  /** Management detail, only with canManage. */
  mode?: "inherit" | "custom" | null;
  modeVersion?: string | null;
  counts?: PageAccessCounts | null;
  groups?: AccessGroup[] | null;
  shareLinks?: { active: number; nextExpiry: string | null } | null;
}
export interface PageAccessExplanation {
  userId: string;
  displayName: string;
  canView: boolean;
  canEdit: boolean;
  role: string | null;
  sources: AccessSource[];
  denial: "inactive" | "privateDraft" | "ownAccess" | "none" | null;
}
export interface AccessCandidate { id: string; kind: "user" | "group"; name: string; detail: string | null }
export interface ShareLink {
  id: string;
  pageId: string;
  pageTitle: string;
  name: string;
  revisionVersion: number;
  createdByName: string;
  createdAt: string;
  expiresAt: string;
  revokedAt: string | null;
  lastOpenedAt: string | null;
  /** Follows a live page (Publikace Živě): the lists say Živě, not a revision. */
  live?: boolean;
  status: "active" | "expired" | "revoked" | "unavailable";
}
export interface ShareLinkPage { items: ShareLink[]; hasMore: boolean }
export interface PageAccessEvent {
  id: string;
  action: "page.opened" | "share.opened" | "share.created" | "share.revoked" | "page.source-edited";
  occurredAt: string;
  actorId: string | null;
  actorName: string | null;
  shareLinkId: string | null;
  shareLinkName: string | null;
  revisionVersion: number;
  /** Channel of the opening or edit (web, api, mcp, ...); null on legacy rows. The token only for its owner and wiki administrators. */
  channel?: string | null;
  apiToken?: ApiTokenRef | null;
}
/** A mark or unmark of the page as its space's requirement catalog (module Projector); channel web, api or mcp. */
export interface PageCatalogMark {
  id: string; isCatalog: boolean; occurredAt: string; actorId: string; actorName: string | null; channel: string; apiToken?: ApiTokenRef | null;
}
/** `catalogMarks`: newest first, at most 50, on the first page only and only when there is one. */
export interface PageAccessHistory { items: PageAccessEvent[]; nextBeforeId: string | null; catalogMarks?: PageCatalogMark[] }
export interface SharedPage {
  title: string;
  contentJson: string;
  revisionVersion: number;
  /** When `live`, the time of the save shown. */
  publishedAt: string;
  expiresAt: string;
  /** The link follows a live page (Publikace Živě): the revision is its latest save, not a publication. */
  live?: boolean;
}
export interface SharingPolicy { enabled: boolean; version: string | null }
/** The longest validity of a new anonymous link, in days: PageShareLink.MaxValidityDays on the server (sharing.test.ts keeps them equal). */
export const shareLinkMaxDays = 30;
export type PublicShareState =
  | { kind: "loading" }
  | { kind: "ready"; value: SharedPage }
  /** `missing`: the server has no such link (expired, revoked or never was): permanent, nothing to try again. */
  | { kind: "error"; missing: boolean };

export function parseSharedToken(pathname: string) {
  const match = pathname.match(/^\/s\/([^/]+)\/?$/);
  if (!match) return undefined;
  try { return decodeURIComponent(match[1]!); } catch { return undefined; }
}

export const sharingApi = {
  pageAccess: (pageId: string, q = "", offset = 0) =>
    api<PageAccess>(`/pages/${pageId}/access?offset=${offset}&limit=50${q ? `&q=${encodeURIComponent(q)}` : ""}`),
  setAccessMode: (pageId: string, body: { ownAccess: boolean; expectedVersion: string | null }) =>
    put<{ mode: "inherit" | "custom"; version: string }>(`/pages/${pageId}/access/mode`, body),
  explainAccess: (pageId: string, userId: string) =>
    api<PageAccessExplanation>(`/pages/${pageId}/access/explain?userId=${encodeURIComponent(userId)}`),
  accessCandidates: (pageId: string, kind: "user" | "group", q = "") =>
    api<AccessCandidate[]>(`/pages/${pageId}/access/candidates?kind=${kind}${q ? `&q=${encodeURIComponent(q)}` : ""}`),
  pageLinks: (pageId: string, offset = 0) =>
    api<ShareLinkPage>(`/pages/${pageId}/share-links?offset=${offset}&limit=50`),
  accessHistory: (pageId: string, beforeId?: string) =>
    api<PageAccessHistory>(`/pages/${pageId}/access-history?limit=50${beforeId ? `&beforeId=${encodeURIComponent(beforeId)}` : ""}`),
  spaceLinks: (spaceId: string, offset = 0) =>
    api<ShareLinkPage>(`/spaces/${spaceId}/share-links?offset=${offset}&limit=50`),
  createLink: (pageId: string, body: { name: string; expiresAt: string }) =>
    post<{ link: ShareLink; url: string }>(`/pages/${pageId}/share-links`, body),
  revokeLink: (pageId: string, linkId: string) =>
    api<void>(`/pages/${pageId}/share-links/${linkId}`, { method: "DELETE" }),
  policy: (spaceId: string) => api<SharingPolicy>(`/spaces/${spaceId}/external-sharing`),
  setPolicy: (spaceId: string, body: { enabled: boolean; expectedVersion: string | null }) =>
    put<SharingPolicy>(`/spaces/${spaceId}/external-sharing`, body),
  sharedPage: (token: string) => api<SharedPage>(`/shared/${encodeURIComponent(token)}`),
};
