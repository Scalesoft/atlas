import type { InjectionKey } from "vue";
import { api } from "../../api";
import type { ApiTokenRef } from "../../channel";
import { personalLibraryApi, type LibraryList } from "../../personalLibrary";

export type ChangeFilter = "all" | "ai" | "mine";

/** One page of Co se změnilo (RecentChange on the server): its newest change the reader may see and how many more the window holds. */
export interface RecentChange {
  pageId: string;
  title: string;
  spaceId: string | null;
  spaceName: string | null;
  /** The version in the page history (its newest revision), for opening it there. */
  revisionId: string;
  version: number;
  at: string;
  authorId: string;
  authorName: string;
  /** web, api, mcp, assistant, import, system; null on rows from before 2.0. */
  channel: string | null;
  isAi: boolean;
  /** Only for the token's owner and wiki administrators. */
  apiToken: ApiTokenRef | null;
  furtherChanges: number;
  /** The reader may edit the page, so its history opens for them. */
  canOpenHistory: boolean;
}
export interface RecentChangeList { items: RecentChange[]; next: string | null }
/** A person's versions on one Prague day in one space (PersonActivityDay on the server); only days with any. */
export interface PersonActivityDay { day: string; spaceId: string | null; spaceName: string | null; changes: number }

/** The reads of the home dashboard's cards: one object, so a card's data comes from the server or (gallery, tests) from sample data. */
export const homeApi = {
  /** `scope`: one person's changes (their account) and one space (PersonActivity). */
  changes: (filter: ChangeFilter, cursor: string | null, signal?: AbortSignal, limit = 8, scope: { author?: string; space?: string } = {}) =>
    api<RecentChangeList>(`/me/changes?filter=${filter}&limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`
      + `${scope.author ? `&author=${encodeURIComponent(scope.author)}` : ""}${scope.space ? `&space=${encodeURIComponent(scope.space)}` : ""}`, { signal }),
  recent: (signal?: AbortSignal): Promise<LibraryList> => personalLibraryApi.list("recent", 0, signal, 6),
  drafts: (signal?: AbortSignal): Promise<LibraryList> => personalLibraryApi.list("drafts", 0, signal, 3),
};
export type HomeApi = typeof homeApi;
export const homeApiKey: InjectionKey<HomeApi> = Symbol("homeApi");

/** A person's last 365 days by day and space (PersonActivity: their calendar and spaces), read as Co se změnilo decides them. */
export const personActivityDays = (author: string, signal?: AbortSignal) =>
  api<{ days: PersonActivityDay[] }>(`/me/changes/days?author=${encodeURIComponent(author)}`, { signal });
