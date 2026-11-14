import { api, post } from "./api";
export interface WikiImportOptions { sourceKey: string; spaceName: string; stripPrefix?: string; sourceBaseUrl?: string; updateExisting: boolean }
/** One source construct that did not arrive unchanged; occurrences are counted, line and excerpt describe the first. */
export interface WikiImportFinding { construct: string; impact: "degraded" | "lost" | "unresolved"; count: number; line: number | null; excerpt: string | null; message: string }
/** `findings` and `tags` are null in reports stored before the per-page report existed. */
export interface WikiImportItem { path: string; kind: "page" | "folder" | "asset" | "unsupported"; action: string; title: string | null; pageId: string | null; expectedVersion: number | null; sha256: string; warnings: string[]; findings?: WikiImportFinding[] | null; tags?: string[] | null }
export interface WikiImportReport { id: string; reviewToken: string; expiresAt: string; spaceId: string | null; spaceName: string; counts: { create: number; update: number; skip: number; conflict: number; unsupported: number; assets: number }; items: WikiImportItem[]; warnings: string[]; canCommit: boolean; completedAt: string | null }
export const contentImportApi = {
  preview(file: File, options: WikiImportOptions, signal?: AbortSignal) {
    const body = new FormData(); body.set("file", file); body.set("options", JSON.stringify(options));
    return api<WikiImportReport>("/admin/imports/wiki-js/preview", { method: "POST", body, signal });
  },
  commit(report: WikiImportReport) {
    return post<WikiImportReport>(`/admin/imports/${encodeURIComponent(report.id)}/commit`, { reviewToken: report.reviewToken, acknowledgeQuarantine: true, acknowledgeWarnings: true });
  },
  get(id: string) { return api<WikiImportReport>(`/admin/imports/${encodeURIComponent(id)}`); },
  sourceUrl(id: string) { return `/api/admin/imports/${encodeURIComponent(id)}/source`; },
  reportUrl(id: string) { return `/api/admin/imports/${encodeURIComponent(id)}/report.json`; },
};
