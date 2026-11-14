import { api, ApiError } from "./api";
import t from "./i18n/shell";
import { dedupedGet } from "./lib/dedupedGet";
import { PagePrefetchMap } from "./lib/pagePrefetchMap";
import { kindInfo, neighboursOf, relationsApi, type RelationGraph, type RelationKind } from "./objects/relationsApi";

/** A page that links to the current one, as the server lists it (only what the reader may open). */
export interface LinkingPage {
  id: string;
  spaceId: string;
  spaceName: string;
  title: string;
}

/**
 * GET /api/pages/{id}/connections: R2 "Odkazuje sem" (`total` counts every readable page, `items` only the first `limit`) and
 * R1 "Související". `related` is missing while the capability is off (no AI, task off, embeddings outage); its items come most
 * similar first, already filtered by the reader's rights, and carry no count. Missing and empty both mean no column.
 */
export interface PageConnections {
  linksHere: { items: LinkingPage[]; total: number };
  /** `people`: the contacts the related pages name, the editor's people ranking only (assist/relatedPeople.ts), never shown. */
  related?: { items: LinkingPage[]; people?: string[] };
}

export interface PageConnectionsApi {
  /** Null when the page is unreadable or gone (404): there is nothing to show, and that is not an error. `fresh`: never a kept answer. */
  get(pageId: string, limit: number, signal?: AbortSignal, fresh?: boolean): Promise<PageConnections | null>;
  /** What else the page leads to or is part of (GET /api/objects/{id}/relations): the boards showing it, the contacts it mentions, its deployment board. */
  relations(pageId: string, signal?: AbortSignal, fresh?: boolean): Promise<RelationGraph | null>;
}

/** How many linking pages one read asks for. */
export const LINKS_HERE_MORE = 50;

async function read(pageId: string, limit: number, signal: AbortSignal) {
  try {
    return await api<PageConnections>(`/pages/${encodeURIComponent(pageId)}/connections?limit=${limit}`, { signal });
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 404) return null;
    throw failure;
  }
}

/**
 * An open page shows its connections twice: the graph under the article and „Odtud dál“ in the dock. Asking at the same moment they share
 * one request (dedupedGet); the dock, loaded on its own, mounts a moment later and takes the graph's answer while it is this young. A
 * refresh (`fresh`) always asks the server.
 */
const KEEP_MS = 10_000;
const kept = new PagePrefetchMap<{ at: number; answer: unknown }>(KEEP_MS, 4);
async function keptAnswer<T>(key: string, fresh: boolean | undefined, ask: () => Promise<T>): Promise<T> {
  const recent = kept.get(key);
  if (!fresh && recent && Date.now() - recent.at < KEEP_MS) return recent.answer as T;
  const answer = await ask();
  kept.set(key, { at: Date.now(), answer });
  return answer;
}
/** Access changed or another account signed in: no kept answer stands for the new one. */
export function forgetPageConnections() { kept.clear(); }

export const pageConnectionsApi: PageConnectionsApi = {
  relations: (pageId, signal, fresh) => keptAnswer(`relations:${pageId}`, fresh, () => relationsApi.relations(pageId, 1, signal)),
  get: (pageId, limit, signal, fresh) => keptAnswer(`connections:${pageId}:${limit}`, fresh,
    () => dedupedGet(`connections:${pageId}:${limit}`, shared => read(pageId, limit, shared), signal)),
};

/** A place of a page beyond its links: a board that shows it, a contact it mentions, the board of its deployment. `relation` and `icon` say it as `kindInfo` does everywhere. */
export interface PagePlace { id: string; typeId: string; title: string; spaceId: string; spaceName: string; kind: RelationKind; relation: string; icon: string }

/** The places in the page's relations, readable ones only (an unreadable one has no name to show); links are the connections' own list. */
export function placesOf(pageId: string, graph: RelationGraph | null): PagePlace[] {
  const places = new Map<string, PagePlace>();
  for (const { edge, direction, node } of neighboursOf(pageId, graph)) {
    const wanted = direction === "out" ? edge.kind === "placement" || edge.kind === "mention" : edge.kind === "attached";
    if (!wanted || !node?.readable || !node.title) continue;
    const info = kindInfo[edge.kind];
    places.set(`${edge.kind}:${node.id.toLowerCase()}`, { id: node.id, typeId: node.typeId, title: node.title, spaceId: node.spaceId, spaceName: node.spaceName, kind: edge.kind, relation: direction === "in" ? info.incoming : edge.kind === "mention" ? t.misc.mentionedHere : info.label, icon: info.icon });
  }
  return [...places.values()];
}
