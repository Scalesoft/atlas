import { api, post, put } from "../api";
import t from "../i18n/verification";
import type { StatusTone } from "../components/settings/types";

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
}
/**
 * What `PUT /pages/{id}/verification` takes (the same as `PUT /api/v1/pages/{id}/verification`): only what is named changes. The server cannot tell
 * null from a missing key, so the owner is cleared with `clearSteward` and „Podle prostoru“ is `inheritInterval`; `intervalMonths` is 0 (never), 3, 6 or 12.
 */
export interface VerificationChange { verify?: true; until?: string; stewardId?: string; clearSteward?: true; intervalMonths?: 0 | 3 | 6 | 12; inheritInterval?: true; stewardFromLastAuthor?: true }
export interface PageVerificationApi {
  change(pageId: string, change: VerificationChange): Promise<PageVerification>;
  /** „Nahlásit neaktuální“: tells the owner, changes nothing on the page. */
  report(pageId: string, note: string): Promise<unknown>;
}
export const pageVerificationApi: PageVerificationApi = {
  change: (pageId, change) => put<PageVerification>(`/pages/${encodeURIComponent(pageId)}/verification`, change),
  report: (pageId, note) => post<{ notified: number }>(`/pages/${encodeURIComponent(pageId)}/verification/report`, note.trim() ? { note: note.trim() } : {}),
};

/** The chip shows only a switched-on verification. */
export const verificationShown = (value: Pick<PageVerification, "state"> | null | undefined): value is PageVerification => !!value && value.state !== "off";
// Unverified is neutral: right after verification is switched on every page is, and a reader must not see a warning on all of them.
export const verificationTone = (state: VerificationState): StatusTone => (state === "expired" ? "warning" : "neutral");
export const verificationIcon = (state: VerificationState) => (state === "expired" ? "shield-alert" : "shield-check");
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
  isPublished: boolean;
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
/** `fromLastAuthor`: the owner is the author of each page's last published revision (the first fill after switching verification on). */
export type ContentHealthBulk = { action: "verify"; pageIds: string[] } | { action: "steward"; pageIds: string[]; stewardId: string | null } | { action: "stewardFromLastAuthor"; pageIds: string[] };
export interface ContentHealthBulkResult { changed: number; skipped: { pageId: string; reason: string }[] }
export interface ContentHealthApi {
  list(spaceId: string, query: ContentHealthQuery, signal?: AbortSignal): Promise<ContentHealth>;
  bulk(spaceId: string, request: ContentHealthBulk): Promise<ContentHealthBulkResult>;
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
};

