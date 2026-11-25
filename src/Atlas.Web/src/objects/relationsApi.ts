import { api, ApiError } from "../api";
import { dedupedGet } from "../lib/dedupedGet";
import t from "../i18n/objects";

/**
 * The client of the space's Obsah and of the relations of any object.
 * One module next to objectApi; the server decides what the reader may see, so a node it cannot read arrives as `readable: false`
 * without a title and an edge to something gone as an edge to an id that is no node.
 */
export type RelationKind = "link" | "placement" | "attached" | "mention" | "reference" | "template" | "party" | "catalog";
export const relationKinds: readonly RelationKind[] = ["link", "placement", "attached", "mention", "reference", "template", "party", "catalog"];
/** The flags of the Vztah filter. */
export type ContentRelation = "unused" | "noIncoming" | "broken" | "attached" | "mostUsed";

export interface ContentRow {
  id: string; typeId: string; title: string; path: string | null; scope: "space" | "wiki" | "private" | string; spaceId: string; hostId?: string | null;
  usedCount: number; linksIn: number; linksOut: number; comments: number; updatedAt: string; canEdit: boolean;
}
/** A node of the graph the list has no row for knows only who it is; the facts the server gave are shown when there are any. */
export type DetailRow = Pick<ContentRow, "id" | "typeId" | "title" | "spaceId"> & Partial<ContentRow>;
export interface ContentSummary { types: Record<string, number>; relations: { unused: number; noIncoming: number; broken: number; attached: number } }
export interface ContentPage { items: ContentRow[]; summary: ContentSummary; nextCursor: string | null }
export interface ContentQuery { type?: string; relation?: ContentRelation | ""; q?: string; sort?: string; cursor?: string | null }

export interface RelationNode { id: string; typeId: string; title: string | null; spaceId: string; spaceName: string; readable: boolean }
/** `draft`, `partId`, `excerpt` and `card` come with uses read with drafts and reasons (UsageRow says what they mean). */
export interface RelationEdge { sourceId: string; targetId: string; kind: RelationKind; count: number; anchor?: string | null; draft?: boolean; partId?: string | null; excerpt?: string | null; card?: boolean }
export interface RelationGraph { nodes: RelationNode[]; edges: RelationEdge[] }

export interface RelationsApi {
  contents(spaceId: string, query: ContentQuery, signal?: AbortSignal): Promise<ContentPage>;
  /** Null when the object is unreadable or gone (404): nothing to show, and that is no error. */
  relations(objectId: string, depth: 1 | 2, signal?: AbortSignal): Promise<RelationGraph | null>;
}

/**
 * „Použito na“ (plan 3.1 M5): one place an object is used in, as GET /api/usage/{id} answers. kde: the page or board (`place…`) and
 * its space; jak: `kind`; proč: `partId` (the card on the board, the reference node in the tree, the page's block when it has an id)
 * and `excerpt` (the text around a link or mention, or the person's own card); `draft`: only the page's working draft holds it; `card`:
 * the mention is a contact card (vizitka).
 */
export interface UsageRow {
  placeId: string; placeTypeId: string; placeTitle: string; spaceId: string | null; spaceName: string | null; kind: RelationKind; count: number;
  anchor?: string | null; draft: boolean; partId?: string | null; excerpt?: string | null; card?: boolean;
}
/** `total` counts the uses the reader may see; those in places they may not see are neither listed nor counted. */
export interface UsagePage { objectId: string; items: UsageRow[]; total: number; nextCursor: string | null }
/** `hidden`: only whether some use is in a place the reader may not see (never how many), for the last-usage warning. */
export interface UsageCount { objectId: string; total: number; hidden: boolean }
export interface UsageApi {
  /** Null when the object is unreadable or gone (404). `part`: the uses of one part of it (a catalog requirement's key on its page). */
  usage(objectId: string, cursor?: string | null, signal?: AbortSignal, part?: string): Promise<UsagePage | null>;
  /** The cheap count of badges and of the last-usage warning; null as above. */
  count(objectId: string, signal?: AbortSignal, part?: string, fresh?: boolean): Promise<UsageCount | null>;
}
const usageQuery = (cursor?: string | null, part?: string, fresh?: boolean) => {
  const query = new URLSearchParams({ ...(cursor ? { cursor } : {}), ...(part ? { part } : {}), ...(fresh ? { fresh: "true" } : {}) }).toString();
  return query ? `?${query}` : "";
};
const missingAsNull = (failure: unknown) => { if (failure instanceof ApiError && failure.status === 404) return null; throw failure; };
// A kind a newer server (or a module) adds before the web knows it is left out: every reader looks its label up in kindInfo.
const known = (item: { kind: string }) => Object.hasOwn(kindInfo, item.kind);
export const usageApi: UsageApi = {
  // Many rows ask for the same object at once (a list of panels, a trash dialog beside it): one request each.
  usage: (objectId, cursor, signal, part) => dedupedGet(`usage:${objectId.toLowerCase()}:${cursor ?? ""}:${part ?? ""}`, shared => api<UsagePage>(`/usage/${encodeURIComponent(objectId)}${usageQuery(cursor, part)}`, { signal: shared })
    .then(page => ({ ...page, items: page.items.filter(known) }), missingAsNull), signal),
  // `fresh`: the last-usage warning of a removal or delete, which also derives the mentions a save left behind.
  count: (objectId, signal, part, fresh) => dedupedGet(`usage-count:${objectId.toLowerCase()}:${part ?? ""}:${fresh ? "fresh" : ""}`, shared => api<UsageCount>(`/usage/${encodeURIComponent(objectId)}/count${usageQuery(null, part, fresh)}`, { signal: shared }).catch(missingAsNull), signal),
};
/** „na 1 místě“, „na 3 místech“: the locative after „Použito na“. */
export const placesText = (count: number) => t.places(count);

function queryOf(query: ContentQuery) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) if (value) params.set(key, value);
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const relationsApi: RelationsApi = {
  contents: (spaceId, query, signal) => api<ContentPage>(`/spaces/${encodeURIComponent(spaceId)}/contents${queryOf(query)}`, { signal }),
  // A page shows its relations twice (the footer's graph and „Odtud dál“ in the dock) and both ask at the same moment: one request.
  relations: (objectId, depth, signal) => dedupedGet(`relations:${objectId.toLowerCase()}:${depth}`, shared =>
    api<RelationGraph>(`/objects/${encodeURIComponent(objectId)}/relations?depth=${depth}`, { signal: shared })
      .then(graph => ({ ...graph, edges: graph.edges.filter(known) }), missingAsNull), signal),
};

/** How an object type is named and drawn; the types of modules we do not know get their last segment. */
const typeInfos: Record<string, { label: string; plural: string; icon: string }> = {
  "atlas.page": { get label() { return t.types.page.label; }, get plural() { return t.types.page.plural; }, icon: "page" },
  "atlas.board": { get label() { return t.types.board.label; }, get plural() { return t.types.board.plural; }, icon: "layout-dashboard" },
  "atlas.contact": { get label() { return t.types.contact.label; }, get plural() { return t.types.contact.plural; }, icon: "user" },
  "atlas.note": { get label() { return t.types.note.label; }, get plural() { return t.types.note.plural; }, icon: "file-text" },
  // The graph's stand-in for a link that leads nowhere (relationGraph.ts).
  unavailable: { get label() { return t.unavailable; }, get plural() { return t.unavailable; }, icon: "unlink" },
};
export function typeInfo(typeId: string) {
  const known = typeInfos[typeId];
  if (known) return known;
  const name = typeId.split(".").at(-1) ?? typeId;
  const label = name.charAt(0).toUpperCase() + name.slice(1);
  return { label, plural: label, icon: "box" };
}

/**
 * The plain-text projection of a title (every text of an object is inline Markdown): what a list, a tooltip, a sort and a graph
 * card show as its words. Marks, links and escapes are gone, block prefixes too.
 */
export function plainTitle(markdown: string | null | undefined): string {
  return (markdown ?? "").split("\n").map(line => line.replace(/^\s*(?:#{1,6}\s+|>\s?|[-+*]\s+|\d+[.)]\s+)/, ""))
    .join(" ")
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/(\*\*|__|~~)(?=\S)(.+?)(?<=\S)\1/g, "$2")
    .replace(/(?<![\w\\*])([*_])(?=\S)(.+?)(?<=\S)\1(?![\w*])/g, "$2")
    .replace(/`([^`]+)`/g, "$1")
    .replace(/\\([\\`*_{}[\]()#+.!|<>~-])/g, "$1")
    .replace(/\s+/g, " ").trim();
}

/** An edge that touches an object, seen from it: `out` leads away from it, `in` leads to it; `node` is the other end (an id that is no node is a broken one). */
export interface Neighbour { key: string; edge: RelationEdge; direction: "out" | "in"; node: RelationNode | undefined; readable: boolean }

/** The places an object is connected with in an answer of the relations endpoint, one per edge that starts or ends at it. */
export function neighboursOf(objectId: string, graph: RelationGraph | null | undefined): Neighbour[] {
  const self = objectId.toLowerCase();
  const nodes = new Map((graph?.nodes ?? []).map(node => [node.id.toLowerCase(), node]));
  return (graph?.edges ?? []).flatMap(edge => {
    const out = edge.sourceId.toLowerCase() === self, into = edge.targetId.toLowerCase() === self;
    if (!out && !into) return [];
    const node = nodes.get((out ? edge.targetId : edge.sourceId).toLowerCase());
    return [{ key: `${edge.kind}:${edge.sourceId}>${edge.targetId}`, edge, direction: out ? "out" as const : "in" as const, node, readable: !!node?.readable }];
  });
}

/** What each kind of relation says, from the source to the target, and read from the target's side. `used`: the end that is used
 * somewhere (Kde je použito): a link, a mention and a tree reference lead to it (`in`), a card and an attached board lead from it
 * to its board or page (`out`). */
export const kindInfo: Record<RelationKind, { label: string; incoming: string; use: string; icon: string; colour: "blue" | "green" | "purple" | "orange" | "gray"; used: "in" | "out" }> = {
  link: { get label() { return t.kinds.link.label; }, get incoming() { return t.kinds.link.incoming; }, get use() { return t.kinds.link.use; }, icon: "link", colour: "blue", used: "in" },
  placement: { get label() { return t.kinds.placement.label; }, get incoming() { return t.kinds.placement.incoming; }, get use() { return t.kinds.placement.use; }, icon: "layout-dashboard", colour: "green", used: "out" },
  attached: { get label() { return t.kinds.attached.label; }, get incoming() { return t.kinds.attached.incoming; }, get use() { return t.kinds.attached.use; }, icon: "workflow", colour: "purple", used: "out" },
  mention: { get label() { return t.kinds.mention.label; }, get incoming() { return t.kinds.mention.incoming; }, get use() { return t.kinds.mention.use; }, icon: "user", colour: "orange", used: "in" },
  reference: { get label() { return t.kinds.reference.label; }, get incoming() { return t.kinds.reference.incoming; }, get use() { return t.kinds.reference.use; }, icon: "git-branch", colour: "gray", used: "in" },
  template: { get label() { return t.kinds.template.label; }, get incoming() { return t.kinds.template.incoming; }, get use() { return t.kinds.template.use; }, icon: "layout-template", colour: "purple", used: "in" },
  party: { get label() { return t.kinds.party.label; }, get incoming() { return t.kinds.party.incoming; }, get use() { return t.kinds.party.use; }, icon: "user", colour: "orange", used: "in" },
  catalog: { get label() { return t.kinds.catalog.label; }, get incoming() { return t.kinds.catalog.incoming; }, get use() { return t.kinds.catalog.use; }, icon: "list-checks", colour: "purple", used: "in" },
};
