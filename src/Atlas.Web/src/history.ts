import { api, type Page } from "./api";
import type { ApiTokenRef } from "./channel";

export interface PageRevisionSummary {
  id: string;
  version: number;
  title: string;
  authorId: string;
  authorName: string;
  activeFrom: string;
  activeTo: string | null;
  isCurrent: boolean;
  isPublished: boolean;
  lastPublishedAt: string | null;
  /** Optional note given at publication; null for revisions never published or published without one. */
  changeSummary?: string | null;
  /** Channel of the change (web, api, mcp, import, system); null on legacy rows. The token only for its owner and wiki administrators. */
  channel?: string | null;
  apiToken?: ApiTokenRef | null;
  /** The same for the last publication of this revision. */
  publicationChannel?: string | null;
  publicationApiToken?: ApiTokenRef | null;
  // 3.6-C: the list returns versions (groups of autosave checkpoints). Optional because a version chosen in the article dock
  // or a revision fetched by id may not carry them; the panel reads them defensively.
  groupId?: string;
  /** The revision this version's diff starts from: no search through the list, no second list call. */
  previousRevisionId?: string | null;
  startedAt?: string;
  checkpoints?: number;
  /** The description given by „Uložit verzi“ (a named version). */
  label?: string | null;
  isExplicit?: boolean;
  /** A change with AI; its source is `channel` + `apiToken`. The author stays the human. */
  isAi?: boolean;
  /** Null on revisions from before 3.6. */
  changes?: RevisionChanges | null;
}
export interface RevisionChanges {
  added: number; removed: number;
  blocks: number;
  sections: string[];
  states: { title: string; from: string; to: string }[];
  requirementsAdded: number; requirementsRemoved: number;
  contactsAdded: string[]; contactsRemoved: string[];
  attachmentsAdded: number; attachmentsRemoved: number;
  titleBefore: string | null;
  contributors: { id: string; name: string; checkpoints: number }[];
}
export interface PageHistory {
  pageId: string;
  currentVersion: number;
  items: PageRevisionSummary[];
  nextBeforeVersion: number | null;
}
export interface PageRevision extends PageRevisionSummary {
  pageId: string;
  contentJson: string;
  markdown: string;
}
// Revisions are immutable, so the panel keeps the last ones (LRU) and shares in-flight requests. The cache is private to
// the open panel and dropped with it (`forget`), so a lost permission never serves stale previews.
const CACHE_SIZE = 60;
const revisions = new Map<string, PageRevision>();
const pending = new Map<string, Promise<PageRevision>>();
const pages = new Map<string, Promise<PageHistory>>();
const key = (pageId: string, id: string) => `${pageId}/${id}`;
let epoch = 0;
function fetchRevision(pageId: string, revisionId: string) {
  const k = key(pageId, revisionId), existing = pending.get(k);
  if (existing) return existing;
  const generation = epoch;
  const request = api<PageRevision>(`/pages/${encodeURIComponent(pageId)}/history/${encodeURIComponent(revisionId)}`).then(value => {
    if (generation === epoch) {
      revisions.set(k, value);
      if (revisions.size > CACHE_SIZE) revisions.delete(revisions.keys().next().value!);
    }
    return value;
  }).finally(() => { if (pending.get(k) === request) pending.delete(k); });
  pending.set(k, request);
  return request;
}
function fetchList(pageId: string, beforeVersion: number | null, group?: string) {
  const query = new URLSearchParams({ limit: group ? "50" : "20" }); // 50 is the server's page limit
  if (group) query.set("group", group);
  if (beforeVersion !== null) query.set("beforeVersion", String(beforeVersion));
  return api<PageHistory>(`/pages/${encodeURIComponent(pageId)}/history?${query}`);
}
export const historyApi = {
  /** The newest page (beforeVersion null) is always fetched; older pages are shared between "load more" and the diff of the oldest loaded item. */
  async list(pageId: string, beforeVersion: number | null, signal: AbortSignal) {
    if (beforeVersion === null) return api<PageHistory>(`/pages/${encodeURIComponent(pageId)}/history?limit=20`, { signal });
    const k = `${pageId}/${beforeVersion}`;
    let request = pages.get(k);
    if (!request) { request = fetchList(pageId, beforeVersion); pages.set(k, request); request.catch(() => pages.delete(k)); }
    return request;
  },
  /** The individual revisions (steps) of one version, newest first; shared and dropped with the rest by `forget`. */
  async steps(pageId: string, groupId: string, beforeVersion: number | null) {
    const k = `${pageId}/group/${groupId}/${beforeVersion ?? ""}`;
    let request = pages.get(k);
    if (!request) { request = fetchList(pageId, beforeVersion, groupId); pages.set(k, request); request.catch(() => pages.delete(k)); }
    return request;
  },
  /** „Uložit verzi“: names the current state; the toolbar button is a later wave. */
  nameVersion(pageId: string, expectedVersion: number, label: string | null) {
    return api<{ revisionId: string; version: number }>(`/pages/${encodeURIComponent(pageId)}/versions`, { method: "POST", body: JSON.stringify({ expectedVersion, label }) });
  },
  /** Cached; a request shared with a prefetch is not cancelled by `signal`, the caller only stops waiting. */
  async revision(pageId: string, revisionId: string, signal: AbortSignal) {
    const k = key(pageId, revisionId), cached = revisions.get(k);
    if (cached) { revisions.delete(k); revisions.set(k, cached); return cached; }
    const value = await fetchRevision(pageId, revisionId);
    if (signal.aborted) throw new DOMException("Aborted", "AbortError");
    return value;
  },
  /** Warms the cache for a neighbour; failures are ignored, the real selection reports errors. */
  prefetch(pageId: string, revisionId: string) {
    if (!revisions.has(key(pageId, revisionId))) void fetchRevision(pageId, revisionId).catch(() => undefined);
  },
  forget() { epoch++; revisions.clear(); pending.clear(); pages.clear(); },
  restore(pageId: string, revisionId: string, expectedVersion: number) {
    return api<Page>(`/pages/${encodeURIComponent(pageId)}/restore`, {
      method: "POST",
      body: JSON.stringify({ revisionId, expectedVersion }),
    });
  },
};
