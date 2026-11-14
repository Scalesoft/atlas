import { api, post, put } from "./api";
import type { ApiTokenRef } from "./channel";
import type { LibraryView } from "./shellRoute";
import type { TreeNodeType } from "./treeMove";

/** The personal lists the server keeps: Knihovna's tabs (`shellRoute.ts` lists them once) without the overview of spaces and the
 * contact directory. */
export type LibraryTab = Exclude<LibraryView, "spaces" | "contacts" | "personal" | "shared" | "notifications">;
export interface LibraryItem {
  id: string;
  /** "board" for a board among favorites and recent visits (no parent, version 0); a page otherwise. */
  type?: TreeNodeType;
  spaceId: string;
  spaceName: string;
  /** The visible parent page, so equally named pages tell apart. */
  parentTitle?: string | null;
  title: string;
  parentId: string | null;
  version: number;
  isPublished: boolean;
  isFavorite: boolean;
  visitedAt: string | null;
  publishedAt: string | null;
  /** The publisher's note on the latest publication (updates tab); null when none was given. */
  changeSummary?: string | null;
  /** Channel of that publication (updates tab) and its token, the latter only for its owner and wiki administrators. */
  publicationChannel?: string | null;
  publicationApiToken?: ApiTokenRef | null;
}
export interface LibraryList { items: LibraryItem[]; hasMore: boolean; nextOffset: number | null }
export interface PersonalPageState { isFavorite: boolean; visitedAt: string | null }
/** A page's or a board's own marks: the same favorites and visits, at the resource's address. */
const marks = (id: string, type?: TreeNodeType) => `/${type === "board" ? "boards" : "pages"}/${encodeURIComponent(id)}`;
export const personalLibraryApi = {
  list(tab: LibraryTab, offset = 0, signal?: AbortSignal, limit = 20) {
    return api<LibraryList>(`/me/library/${tab}?offset=${offset}&limit=${limit}`, { signal });
  },
  state(id: string, signal?: AbortSignal, type?: TreeNodeType) {
    return api<PersonalPageState>(`${marks(id, type)}/personal-state`, { signal });
  },
  favorite(id: string, isFavorite: boolean, type?: TreeNodeType) {
    return put<PersonalPageState>(`${marks(id, type)}/favorite`, { isFavorite });
  },
  visit(id: string, type?: TreeNodeType) {
    return post<PersonalPageState>(`${marks(id, type)}/visit`);
  },
};
