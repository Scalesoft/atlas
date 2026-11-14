import { api, post, put } from "../api";

/** A comment as its reader sees it (server CommentDto); a deleted one keeps its author and time with an empty body. */
export interface ObjectComment {
  id: string;
  authorId: string;
  authorName: string;
  /** Atlas Markdown. */
  body: string;
  /** A heading id of a page or an item id of a board; null for the object as a whole. */
  anchor: string | null;
  createdAt: string;
  editedAt: string | null;
  deleted: boolean;
  canEdit: boolean;
  canDelete: boolean;
}
/** Newest first, at most 20; the next page is asked for before the last item's id. */
export interface CommentPage { items: ObjectComment[]; hasMore: boolean; count: number; canPost: boolean }

/** The comments of an object (`/api/objects/{id}/comments`, comments spec). */
export interface CommentApi {
  page(objectId: string, before?: string, signal?: AbortSignal): Promise<CommentPage>;
  post(objectId: string, body: string, anchor?: string | null): Promise<ObjectComment>;
  edit(objectId: string, commentId: string, body: string): Promise<ObjectComment>;
  remove(objectId: string, commentId: string): Promise<void>;
  /** The live counts of the objects the reader may read and that have any; at most 100 ids. */
  counts(objectIds: readonly string[], signal?: AbortSignal): Promise<Record<string, number>>;
}

const base = (objectId: string) => `/objects/${encodeURIComponent(objectId)}/comments`;
export const commentApi: CommentApi = {
  page: (objectId, before, signal) => api<CommentPage>(`${base(objectId)}${before ? `?before=${encodeURIComponent(before)}` : ""}`, { signal }),
  post: (objectId, body, anchor) => post<ObjectComment>(base(objectId), { body, anchor: anchor ?? null }),
  edit: (objectId, commentId, body) => put<ObjectComment>(`${base(objectId)}/${encodeURIComponent(commentId)}`, { body }),
  remove: (objectId, commentId) => api<void>(`${base(objectId)}/${encodeURIComponent(commentId)}`, { method: "DELETE" }),
  counts: async (objectIds, signal) => objectIds.length
    ? (await api<{ counts: Record<string, number> }>(`/objects/comments/counts?ids=${objectIds.map(encodeURIComponent).join(",")}`, { signal })).counts
    : {},
};
