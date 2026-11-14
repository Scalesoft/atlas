import { api, post, put } from "./api";
import shellMore from "./i18n/shell-more";

export interface TrashCleanupCounts { trashed: number; trashedDue: number; unusedDue: number }
/** `automatic/days/version` are the space's own rule; `effective*` is the rule in force (the wiki's when `decidedByWiki`). */
export interface SpaceTrashCleanup {
  automatic: boolean;
  days: number;
  version: string | null;
  decidedByWiki: boolean;
  effectiveAutomatic: boolean;
  effectiveDays: number;
  counts: TrashCleanupCounts;
}
export interface TrashCleanupRun { purged: number; trashed: number; skipped: number }
export type TrashCleanupScope = "space" | "wiki";
export interface WikiTrashCleanup { scope: TrashCleanupScope; automatic: boolean; days: number; version: string | null }
export const minCleanupDays = 7, maxCleanupDays = 3650;

const spacePath = (spaceId: string) => `/spaces/${encodeURIComponent(spaceId)}/trash-cleanup`;
export const spaceTrashCleanupApi = {
  load: (spaceId: string, signal?: AbortSignal) => api<SpaceTrashCleanup>(spacePath(spaceId), { signal }),
  save: (spaceId: string, rule: { automatic: boolean; days: number }, expectedVersion: string | null) =>
    put<SpaceTrashCleanup>(spacePath(spaceId), { ...rule, expectedVersion }),
  run: (spaceId: string) => post<TrashCleanupRun>(`${spacePath(spaceId)}/run`),
};
export type SpaceTrashCleanupApi = typeof spaceTrashCleanupApi;

export const wikiTrashCleanupApi = {
  load: (signal?: AbortSignal) => api<WikiTrashCleanup>("/admin/trash-cleanup", { signal }),
  save: (rule: { scope: TrashCleanupScope; automatic: boolean; days: number }, expectedVersion: string | null) =>
    put<WikiTrashCleanup>("/admin/trash-cleanup", { ...rule, expectedVersion }),
};
export type WikiTrashCleanupApi = typeof wikiTrashCleanupApi;

/** Text for a days field that is out of range, undefined otherwise. */
export const daysProblem = (days: number) =>
  Number.isInteger(days) && days >= minCleanupDays && days <= maxCleanupDays ? undefined : shellMore.trash.daysProblem(minCleanupDays, maxCleanupDays);
