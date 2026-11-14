import { api } from "../api";
import type { ContactData } from "../contacts/contactData";

/**
 * The client of the entity catalog (Atlas 3 plan §14, server `Features/Entities`): entities of a kind merged by identity, each with the
 * places it occurs in. The server decides what the reader may see (a reader the published page, a writer the working one), so a list
 * never holds a source the reader could not open.
 */
export type EntityScope = "instance" | "space" | "subtree" | "resource";
export interface EntitySource {
  resourceId: string; resourceType: "atlas.page" | "atlas.contact" | string; title: string; spaceId: string | null; spaceName: string | null;
  anchor: string | null; revisionKind: "working" | "published";
}
/** A listed entity carries its first five sources and `sourceCount`; a query with its `identity` brings them all (PERF2-1). */
/** `contactId`: the person's directory contact, whose places the usage service lists (UsagePanel); without it the places are the snapshots found by identity. */
export interface EntityItem { identityKey: string; kind: string; attributes: ContactData; sources: EntitySource[]; sourceCount: number; contactId?: string | null }
export interface EntityList { items: EntityItem[]; total: number; page: number; pageSize: number }
export interface EntityQuery { scope: EntityScope; id?: string; q?: string; page?: number; identity?: string }

export interface EntityApi {
  list(kind: string, query: EntityQuery, signal?: AbortSignal): Promise<EntityList>;
  /** The address of the same query as a file: CSV or vCard. */
  exportUrl(kind: string, query: EntityQuery, format: "csv" | "vcard"): string;
}

function params(query: EntityQuery, extra: Record<string, string> = {}) {
  const values = new URLSearchParams({ scope: query.scope, ...extra });
  if (query.id) values.set("id", query.id);
  if (query.q?.trim()) values.set("q", query.q.trim());
  if (query.page && query.page > 1) values.set("page", String(query.page));
  if (query.identity) values.set("identity", query.identity);
  return values.toString();
}

export const entityApi: EntityApi = {
  list: (kind, query, signal) => api<EntityList>(`/entities/${encodeURIComponent(kind)}?${params(query)}`, { signal }),
  exportUrl: (kind, query, format) => `/api/entities/${encodeURIComponent(kind)}/export?${params({ ...query, page: 1 }, { format })}`,
};
