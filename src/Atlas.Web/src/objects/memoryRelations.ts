import { foldText } from "../lib/foldText";
import { kindInfo, type ContentPage, type ContentQuery, type ContentRow, type RelationEdge, type RelationGraph, type RelationNode, type RelationsApi, type UsageApi, type UsagePage, type UsageRow } from "./relationsApi";

/**
 * The server's Obsah and relations, answered from objects held in memory: the gallery and the tests run the real list and graph
 * on it with no backend. It follows the contract: the tiles count the
 * whole space, `relation` filters by the flags of §3.2, the cursor is an offset, a node the reader may not read has no title, and
 * an edge to something that is no node is a broken one.
 */
export interface MemoryObject extends RelationNode {
  path?: string | null; scope?: ContentRow["scope"]; hostId?: string | null; comments?: number; updatedAt?: string; canEdit?: boolean;
}

const collator = new Intl.Collator("cs", { numeric: true });

export function memoryRelations(objects: readonly MemoryObject[], edges: readonly RelationEdge[], options: { pageSize?: number } = {}): RelationsApi {
  const pageSize = options.pageSize ?? 25;
  const ids = new Set(objects.map(object => object.id));
  const sum = (list: readonly RelationEdge[]) => list.reduce((total, edge) => total + edge.count, 0);
  const rowOf = (object: MemoryObject): ContentRow & { flags: Record<string, boolean> } => {
    const incoming = edges.filter(edge => edge.targetId === object.id), outgoing = edges.filter(edge => edge.sourceId === object.id);
    const usedCount = sum(incoming.filter(edge => edge.kind === "placement" || edge.kind === "attached" || edge.kind === "mention"));
    const linksIn = sum(incoming.filter(edge => edge.kind === "link")), linksOut = sum(outgoing.filter(edge => edge.kind === "link"));
    const placed = object.typeId === "atlas.page" || object.typeId === "atlas.board";
    return {
      id: object.id, typeId: object.typeId, title: object.title ?? "", path: object.path ?? null, scope: object.scope ?? "space", spaceId: object.spaceId, hostId: object.hostId ?? null,
      usedCount, linksIn, linksOut, comments: object.comments ?? 0, updatedAt: object.updatedAt ?? "2026-10-20T10:00:00Z", canEdit: object.canEdit ?? true,
      flags: {
        unused: !placed && usedCount === 0 && !object.hostId,
        noIncoming: placed && !incoming.some(edge => edge.kind === "link" || edge.kind === "placement"),
        broken: outgoing.some(edge => !ids.has(edge.targetId)),
        attached: !!object.hostId || outgoing.some(edge => edge.kind === "attached"),
        mostUsed: true,
      },
    };
  };
  const readable = objects.filter(object => object.readable);
  return {
    async contents(_spaceId, query: ContentQuery): Promise<ContentPage> {
      const all = readable.map(rowOf);
      const text = foldText((query.q ?? "").trim());
      let rows = all.filter(row => (!query.type || row.typeId === query.type) && (!text || foldText(row.title).includes(text))
        && (!query.relation || row.flags[query.relation]));
      rows = rows.sort(query.relation === "mostUsed" ? (a, b) => b.usedCount - a.usedCount || collator.compare(a.title, b.title) : (a, b) => collator.compare(a.title, b.title));
      const start = Number(query.cursor ?? 0) || 0;
      const types: Record<string, number> = {};
      for (const row of all) types[row.typeId] = (types[row.typeId] ?? 0) + 1;
      const count = (flag: string) => all.filter(row => row.flags[flag]).length;
      return {
        items: rows.slice(start, start + pageSize).map(({ flags: _flags, ...row }) => row),
        summary: { types, relations: { unused: count("unused"), noIncoming: count("noIncoming"), broken: count("broken"), attached: count("attached") } },
        nextCursor: start + pageSize < rows.length ? String(start + pageSize) : null,
      };
    },
    async relations(objectId, depth): Promise<RelationGraph | null> {
      const start = objects.find(object => object.id.toLowerCase() === objectId.toLowerCase());
      if (!start?.readable) return null;
      const seen = new Set([start.id]);
      let frontier = [start.id];
      for (let step = 0; step < depth; step++) {
        const next: string[] = [];
        for (const edge of edges) {
          for (const [near, far] of [[edge.sourceId, edge.targetId], [edge.targetId, edge.sourceId]] as const) {
            if (frontier.includes(near) && !seen.has(far) && ids.has(far)) { seen.add(far); next.push(far); }
          }
        }
        frontier = next;
      }
      return {
        nodes: objects.filter(object => seen.has(object.id)).map(({ id, typeId, title, spaceId, spaceName, readable: ok }) => ({ id, typeId, title: ok ? title : null, spaceId, spaceName, readable: ok })),
        edges: edges.filter(edge => seen.has(edge.sourceId) && (seen.has(edge.targetId) || !ids.has(edge.targetId))),
      };
    },
  };
}

/**
 * „Použito na“ over the same objects and edges (GET /api/usage/{id}): an edge uses the end kindInfo.used names, from the place at its
 * other end; a place the reader may not read is neither listed nor counted, the count only says there is one. An edge's `draft`, `partId` and `excerpt` pass through, the
 * rows are sorted by space, place and kind as the server sorts them, the cursor is an offset.
 */
export function memoryUsage(objects: readonly MemoryObject[], edges: readonly RelationEdge[], options: { pageSize?: number } = {}): UsageApi {
  const pageSize = options.pageSize ?? 50;
  const byId = new Map(objects.map(object => [object.id.toLowerCase(), object]));
  async function read(objectId: string, cursor?: string | null): Promise<{ page: UsagePage; hidden: boolean } | null> {
    const self = objectId.toLowerCase();
    if (!byId.get(self)?.readable) return null;
    const rows: UsageRow[] = [];
    let hidden = false;
    for (const edge of edges) {
      const out = kindInfo[edge.kind].used === "out";
      if ((out ? edge.sourceId : edge.targetId).toLowerCase() !== self) continue;
      const place = byId.get((out ? edge.targetId : edge.sourceId).toLowerCase());
      if (!place?.readable) { hidden = true; continue; }
      rows.push({ placeId: place.id, placeTypeId: place.typeId, placeTitle: place.title ?? "", spaceId: place.spaceId, spaceName: place.spaceName, kind: edge.kind, count: edge.count,
        anchor: edge.anchor ?? null, draft: !!edge.draft, partId: edge.partId ?? null, excerpt: edge.excerpt ?? null, card: !!edge.card });
    }
    rows.sort((a, b) => collator.compare(a.spaceName ?? "", b.spaceName ?? "") || collator.compare(a.placeTitle, b.placeTitle) || a.kind.localeCompare(b.kind));
    const start = Number(cursor ?? 0) || 0;
    return { page: { objectId, items: rows.slice(start, start + pageSize), total: rows.length, nextCursor: start + pageSize < rows.length ? String(start + pageSize) : null }, hidden };
  }
  return {
    usage: async (objectId, cursor) => (await read(objectId, cursor))?.page ?? null,
    async count(objectId) {
      const found = await read(objectId, null);
      return found && { objectId, total: found.page.total, hidden: found.hidden };
    },
  };
}
