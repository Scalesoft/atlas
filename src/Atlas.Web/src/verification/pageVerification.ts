import { api, post, put } from "../api";
import t from "../i18n/verification";
import type { StatusTone } from "../components/settings/types";
import { organizationApi } from "../organization";
import { verificationMark } from "./verificationMark";

/**
 * Ověřený obsah (5.2, docs/atlas-5.2-plan.md §2–3 and §7): the state of a page's verification as the server derives it (one function in C#,
 * never computed here), the typed client of the page and of the space's Stav obsahu. The chip, popover, panel facts, search icon and link chip
 * all read this one shape.
 */
export type VerificationState = "off" | "unverified" | "verified" | "expiring" | "expired";
export interface PageVerification {
  state: VerificationState;
  stewardId: string | null;
  stewardName: string | null;
  verifiedAt: string | null;
  verifiedUntil: string | null;
  verifiedVersion: number | null;
  /** „Změněno po ověření“: the published version is newer than the verified one. */
  changedSinceVerification: boolean;
  /** Beyond the §7 block: who verified, and whether it was an agent token („přes AI“). */
  verifiedByName?: string | null;
  verifiedViaAi?: boolean;
  /** The page's own interval: null by the space, 0 never. */
  intervalMonths: number | null;
  /** The space's default interval; null = verification is off in the space. */
  spaceIntervalMonths: number | null;
  /** The panel's Čtenost (90 days) in the answer of GET and PUT …/verification while verification is on; null in the page's bundle. */
  readers?: number | null;
}
/** The intervals a space or a page may verify in, in months (the server's PageVerificationStates.Intervals). */
export const verificationIntervals = [3, 6, 12] as const;
/**
 * What `PUT /pages/{id}/verification` takes (the same as `PUT /api/v1/pages/{id}/verification`): only what is named changes. The server cannot tell
 * null from a missing key, so the owner is cleared with `clearSteward` and „Podle prostoru“ is `inheritInterval`; `intervalMonths` is 0 (never), 3, 6 or 12.
 */
export interface VerificationChange { verify?: true; until?: string; stewardId?: string; clearSteward?: true; intervalMonths?: 0 | (typeof verificationIntervals)[number]; inheritInterval?: true; stewardFromLastAuthor?: true }
export interface PageVerificationApi {
  /** The state read again (a new version was published: „Změněno po ověření“ follows). */
  read(pageId: string): Promise<PageVerification>;
  change(pageId: string, change: VerificationChange): Promise<PageVerification>;
  /** „Nahlásit neaktuální“: tells the owner, changes nothing on the page; `notified` is 0 when nobody could be told. */
  report(pageId: string, note: string): Promise<{ notified: number }>;
}
export const pageVerificationApi: PageVerificationApi = {
  read: (pageId) => api<PageVerification>(`/pages/${encodeURIComponent(pageId)}/verification`),
  change: (pageId, change) => put<PageVerification>(`/pages/${encodeURIComponent(pageId)}/verification`, change),
  report: (pageId, note) => post<{ notified: number }>(`/pages/${encodeURIComponent(pageId)}/verification/report`, note.trim() ? { note: note.trim() } : {}),
};

// Both from the one mapping (verificationMark): unverified is neutral, a plain shield (right after verification is switched on every page is, and a reader must not see a warning or a check on all of them).
export const verificationTone = (state: VerificationState): StatusTone => (verificationMark(state)?.warn ? "warning" : "neutral");
export const verificationIcon = (state: VerificationState) => verificationMark(state)?.icon ?? "shield";
/** Whole days from `now` to `until` (rounded up, never below 0). */
export const daysLeft = (until: string, now = Date.now()) => Math.max(0, Math.ceil((Date.parse(until) - now) / 86_400_000));
/** „Ověřeno“, „Vyprší za 9 dní“, „Ověření vypršelo“, „Neověřeno“. */
export function verificationLabel(value: Pick<PageVerification, "state" | "verifiedUntil">, now = Date.now()) {
  if (value.state === "expiring" && value.verifiedUntil) { const days = daysLeft(value.verifiedUntil, now); return days ? t.expiresIn(days) : t.expiresToday; }
  return t.state[value.state];
}

/** Stav obsahu of a space (§5.2, §6): a page per row with its state and counts of readers, never names. */
export type ContentHealthFilter = "" | "due" | "verified" | "expiring" | "lapsed" | "no-steward" | "unread";
export type ContentHealthSort = "priority" | "expiry" | "readers" | "published";
export type ContentHealthDays = 30 | 90 | 365;
export interface ContentHealthQuery { state: ContentHealthFilter; mine: boolean; sort: ContentHealthSort; days: ContentHealthDays; q: string; cursor?: string | null }
export interface ContentHealthItem {
  pageId: string;
  title: string;
  /** The titles of the page's parents, from the top of the space. */
  path: string[];
  state: VerificationState;
  stewardId: string | null;
  stewardName: string | null;
  verifiedUntil: string | null;
  /** The reader may edit the page: only then can it verify it or set its owner (a space writer may only read some pages). */
  canEdit: boolean;
  views: number;
  readers: number;
  lastViewedAt: string | null;
  lastPublishedAt: string | null;
}
export interface ContentHealth {
  summary: { verified: number; expiring: number; expired: number; unverified: number; withoutSteward: number };
  items: ContentHealthItem[];
  nextCursor: string | null;
  /** False while the space has verification off (the page then explains it). */
  enabled: boolean;
}
/**
 * `stewardFromLastAuthor`: the owner is the author of each page's last published revision (the first fill after switching verification on);
 * without `pageIds` the server fills the pages of the whole space that have none, at most 500 at a time (`more`).
 */
export type ContentHealthBulk = { action: "verify"; pageIds: string[] } | { action: "steward"; pageIds: string[]; stewardId: string | null } | { action: "stewardFromLastAuthor"; pageIds?: string[] };
/** A skipped page's `reason`: `has_steward` (the fill never replaces an owner), else a refusal (an HTTP status or an error code). */
export interface ContentHealthBulkResult { changed: number; skipped: { pageId: string; reason: string }[]; more?: boolean }
export interface ContentHealthApi {
  list(spaceId: string, query: ContentHealthQuery, signal?: AbortSignal): Promise<ContentHealth>;
  bulk(spaceId: string, request: ContentHealthBulk): Promise<ContentHealthBulkResult>;
  /** „Přesunout do koše“ of one page (the space's page trash, as the tree's); the answer takes it back (the toast's Zpět). */
  trash(spaceId: string, pageId: string): Promise<{ undo(): Promise<unknown> }>;
}
export const contentHealthApi: ContentHealthApi = {
  list(spaceId, query, signal) {
    const params = new URLSearchParams({ sort: query.sort, days: String(query.days) });
    if (query.state) params.set("state", query.state);
    if (query.mine) params.set("mine", "true");
    if (query.q.trim()) params.set("q", query.q.trim());
    if (query.cursor) params.set("cursor", query.cursor);
    return api<ContentHealth>(`/spaces/${encodeURIComponent(spaceId)}/content-health?${params}`, { signal });
  },
  bulk: (spaceId, request) => post<ContentHealthBulkResult>(`/spaces/${encodeURIComponent(spaceId)}/content-health/bulk`, request),
  async trash(spaceId, pageId) {
    // The tree's own command with its versions; a page with subpages is refused (subtree_confirmation_required): the tree asks about those.
    const tree = await organizationApi.tree(spaceId);
    const node = tree.items.find((item) => item.id === pageId);
    const change = await organizationApi.trash(pageId, false, tree.version, node?.version ?? 0);
    return { undo: () => organizationApi.untrash(pageId, node?.parentId ?? null, false, change.version) };
  },
};

