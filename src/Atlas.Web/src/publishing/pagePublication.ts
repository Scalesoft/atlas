import { api, put } from "../api";
import t from "../i18n/shell";
import { formatShortDate } from "../formatDate";

/** „Publikace“ of a page: Ručně (by hand), Automaticky (30 minutes after the last edit), Živě (readers follow the saved state). */
export type PublicationMode = "Manual" | "Automatic" | "Live";
export interface PagePublication {
  /** null: the page inherits the default of its space. */
  mode: PublicationMode | null;
  spaceMode: PublicationMode;
  effective: PublicationMode;
  publishedVersion: number | null;
  publishedAt: string | null;
  publishedBy: string | null;
  publishedAutomatically: boolean;
  unpublishedChanges: boolean;
  canChange: boolean;
  /** The revision readers read: the writer's „Publikovaná“ view opens it from the history. Null when never published. */
  publishedRevisionId?: string | null;
}
export interface PagePublicationApi {
  get(pageId: string, signal?: AbortSignal): Promise<PagePublication>;
  set(pageId: string, mode: PublicationMode | null): Promise<PagePublication>;
}
export const publicationLabels: Record<PublicationMode, string> = { get Manual() { return t.pub.manual; }, get Automatic() { return t.pub.automatic; }, get Live() { return t.pub.live; } };
/** A writer's draft (koncept) in words: the bar's label and Zveřejnit změny's hint, naming the version readers read meanwhile.
 *  `publishedAt` undefined: published, its date not read yet. */
export function draftWording(published: boolean, publishedAt?: string | null) {
  const when = publishedAt ? formatShortDate(publishedAt) : "";
  return published
    ? { label: t.pub.draftLabel(when), publishHint: t.pub.draftHint(when), publishedLabel: t.pub.publishedLabel(when) }
    : { label: t.pub.draftNotPublished, publishHint: t.pub.draftHintNotPublished, publishedLabel: "" };
}
/** AutoPublish.cs in short: Automaticky publishes a version that closed after 30 minutes without a save. */
export const automaticPublicationHint = () => t.pub.autoHint;
export const pagePublicationApi: PagePublicationApi = {
  get: (pageId, signal) => api<PagePublication>(`/pages/${encodeURIComponent(pageId)}/publication`, { signal }),
  set: (pageId, mode) => put<PagePublication>(`/pages/${encodeURIComponent(pageId)}/publication`, { mode }),
};
