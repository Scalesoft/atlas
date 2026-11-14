import { api, type PageSummary } from "./api";

export type PageTemplateSummary = PageSummary;

export const pageTemplateApi = {
  list(spaceId: string, signal?: AbortSignal) {
    return api<PageTemplateSummary[]>(`/spaces/${encodeURIComponent(spaceId)}/page-templates`, { signal });
  },
};
