// Eager part of the page source feature (docs/page-source-editor.md in the editor repository): availability,
// the Monaco prefetch and the host endpoint. Tiny on purpose, App.vue imports it; the draft, the panel and
// Monaco live behind DocumentEditor (src/pageSourceDraft.ts) and the editor's own import().
//
// Load the source entry only on intent; its shared chunks need not block the shell.
import { api, ApiError, errorMessage, type Page } from "./api";
import { documentSizeError } from "./savePage";

/** Warms the Monaco chunk on hover/focus of an entry point to the source. Never at page start or idle. */
export function prefetchPageSource() {
  void import("@atlas/markdown/source").then(module => module.prefetchSourceEditor()).catch(() => {
    // Speculative prefetch may fail offline; opening the source editor retries its own import.
  });
}

/** Outcome of the host save, as the editor's `useMarkdownSourceDraft` understands it. */
export type PageSourceSaveResult =
  | { readonly ok: true }
  | { readonly ok: false; readonly reason: "conflict" | "live-edits" | "error"; readonly message?: string };

export interface ReplacePageSourceRequest { contentJson: string; markdown: string; expectedVersion: number }

export const pageSourceApi = {
  /** Replaces the page body like a history restore: new working revision, collaboration reset, audit event.
   *  Content over a stored limit is refused before the upload, with the limit it exceeds. */
  replace(pageId: string, request: ReplacePageSourceRequest, signal?: AbortSignal) {
    const tooLarge = documentSizeError(request.contentJson, request.markdown);
    if (tooLarge) return Promise.reject(new Error(tooLarge));
    return api<Page>(`/pages/${encodeURIComponent(pageId)}/source`, { method: "PUT", body: JSON.stringify(request), signal });
  },
};

/**
 * Maps a failed replace to the draft's outcome: 409 + `collaboration_unsaved` is others' live edits, any other
 * 409 a version conflict. The panel words both itself; only an unexpected error carries the server message.
 */
export function pageSourceFailure(error: unknown): PageSourceSaveResult {
  if (error instanceof ApiError && error.status === 409)
    return { ok: false, reason: error.code === "collaboration_unsaved" ? "live-edits" : "conflict" };
  return { ok: false, reason: "error", message: errorMessage(error) };
}
