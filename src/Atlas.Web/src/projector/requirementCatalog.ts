import type { Editor } from "@tiptap/core";
import type { ProjectorCatalog, RequirementJson } from "./projectorApi";
import { storageOf } from "./requirementRuntime";
import type { ProjectorHost } from "./requirementsPlugin";

const FRESH_MS = 60_000;
/** Keys per request for the blocks: the request line stays well under the server's limit with 96-character keys. */
const KEYS_PER_REQUEST = 50;

/**
 * The space's catalog for this editor, without the blocks, fetched once and kept a minute: the panel opens again and a pick
 * resolves without asking the server. A page marked or unmarked as a catalog since (by either menu) is asked again.
 * shortcut: a minute's cache; fetch by the catalog's revision once its endpoint reports one.
 */
export function catalogOf(editor: Editor, host: ProjectorHost): Promise<ProjectorCatalog> {
  const storage = storageOf(editor), spaceId = host.spaceId(), flag = host.isCatalog?.(), cached = storage.catalog;
  if (cached?.spaceId === spaceId && cached.flag === flag && Date.now() - cached.at < FRESH_MS) return cached.value;
  const value = host.api.catalog(spaceId);
  const entry: NonNullable<typeof storage.catalog> = { spaceId, flag, at: Date.now(), value };
  storage.catalog = entry;
  value.then(catalog => {
    entry.size = catalog.definitions.length;
    // shortcut: a host that does not pass the page's own flag (PageView.isCatalog, v-model:catalog on DocumentEditor) learns it
    // here, and a catalog page without keyed blocks reads as none; delete once App.vue passes it (package H3).
    // What this editor set itself (catalogChanged) stays.
    const pageId = host.pageId();
    if (flag === undefined) storage.catalogPage.value ??= !!pageId && catalog.definitions.some(definition => definition.pageId === pageId);
  }, () => { if (storage.catalog === entry) storage.catalog = undefined; });
  return value;
}

/** Whether the page is a requirement catalog: the page's own flag, else what the catalog showed; undefined while unknown. */
export const isCatalogPage = (editor: Editor, host: ProjectorHost) => host.isCatalog?.() ?? storageOf(editor).catalogPage.value;

/** The catalog's titles by key (the summary names a missing prerequisite by it). */
export const catalogNames = (editor: Editor, host: ProjectorHost) =>
  catalogOf(editor, host).then(catalog => new Map(catalog.definitions.map(definition => [definition.key, definition.title])));

/** How many definitions the space's catalog has, once known; the first question fetches it. */
export function catalogSize(editor: Editor, host: ProjectorHost): number | undefined {
  const cached = storageOf(editor).catalog;
  if (cached?.spaceId !== host.spaceId()) catalogOf(editor, host).catch(() => {});
  return cached?.size;
}

/** The blocks of these definitions by key, the first definition of a key counting (as the resolver reads it): fetched to insert them. */
export async function catalogBlocks(host: { spaceId(): string; api: Pick<ProjectorHost["api"], "definitions"> }, keys: readonly string[]): Promise<Map<string, RequirementJson>> {
  const requests: Promise<ProjectorCatalog>[] = [];
  for (let start = 0; start < keys.length; start += KEYS_PER_REQUEST) requests.push(host.api.definitions(host.spaceId(), keys.slice(start, start + KEYS_PER_REQUEST)));
  const blocks = new Map<string, RequirementJson>();
  for (const { definitions } of await Promise.all(requests))
    for (const definition of definitions) if (definition.contentJson && !blocks.has(definition.key)) blocks.set(definition.key, definition.contentJson);
  return blocks;
}

/** After the page was marked or unmarked as a catalog: what it now is, for this editor and the page, and a fresh catalog next time. */
export function catalogChanged(editor: Editor, host: ProjectorHost, isCatalog: boolean) {
  const storage = storageOf(editor);
  storage.catalog = undefined;
  storage.catalogPage.value = isCatalog;
  host.setCatalog?.(isCatalog);
}
