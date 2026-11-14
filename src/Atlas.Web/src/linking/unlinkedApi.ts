import { api } from "../api";

/** A candidate of an unlinked mention, the most likely first: a contact, a page or a YouTrack issue. */
export interface UnlinkedCandidate { id: string; label: string; hint?: string | null }
/**
 * A mention of a space's page that is not linked yet (GET /api/spaces/{id}/unlinked, P15 „viditelné mezery“): `text` as written, the
 * `ordinal`-th such text of the page, `context` the block's text around it (≤ 160 characters, always holding `text`), `anchor` the id of
 * the heading above it; `unresolved` is an unresolved chip waiting for the pick, `suggested`
 * a known name in plain text. `version` is the page's version the row was read at.
 */
export interface UnlinkedMention {
  pageId: string; pageTitle: string; version: number; ordinal: number; anchor: string | null; context: string; text: string;
  kind: "person" | "page" | "issue"; state: "unresolved" | "suggested"; candidates: UnlinkedCandidate[];
}
export interface UnlinkedPage { items: UnlinkedMention[]; nextCursor: string | null }
/** One confirmation: link with a candidate, or turn an unresolved chip into its text. Nothing is remembered outside the page. */
export type UnlinkedChoice = { targetId: string } | "text";

export interface UnlinkedApi {
  list(spaceId: string, query?: { cursor?: string | null; pageId?: string; limit?: number }): Promise<UnlinkedPage>;
  /** 200: the page's new version; 409 (ApiError): the page changed, the row is out of date. */
  resolve(mention: UnlinkedMention, choice: UnlinkedChoice): Promise<{ version: number }>;
  /** Takes a resolved row back; the overview shows „Zpět“ only when the api has it. */
  undo?(mention: UnlinkedMention, choice: UnlinkedChoice, version: number): Promise<void>;
}

export const unlinkedApi: UnlinkedApi = {
  list(spaceId, { cursor, pageId, limit = 50 } = {}) {
    const query = new URLSearchParams({ limit: String(limit) });
    if (cursor) query.set("cursor", cursor);
    if (pageId) query.set("pageId", pageId);
    return api<UnlinkedPage>(`/spaces/${encodeURIComponent(spaceId)}/unlinked?${query}`);
  },
  resolve(mention, choice) {
    return api<{ version: number }>(`/pages/${encodeURIComponent(mention.pageId)}/references`, {
      method: "POST", body: JSON.stringify({ expectedVersion: mention.version, ordinal: mention.ordinal, text: mention.text, choice }),
    });
  },
};
