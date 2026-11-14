import { api } from "../api";
import type { BlockJson } from "./requirementModel";
import type { RequirementDirection } from "./requirementTypes";

// The projector module's endpoints the page editor uses (server Features/Projector/ProjectorEndpoints.cs). Thin on purpose: the
// rules live on the server; the editor writes the blocks itself through the live document and resolves a catalog pick on the
// catalog it holds (requirementResolver.ts).

export type RequirementJson = BlockJson & { type: "requirement" };

/** A predefined requirement of a catalog page; `contentJson`, the block node to insert, only from `definitions`. */
export interface CatalogDefinition {
  key: string;
  title: string;
  titleMarkdown: string;
  direction: RequirementDirection;
  requires: string[];
  contentJson?: RequirementJson | null;
  pageId: string;
  /** The heading above it on the catalog page, "" before the first. */
  bundle: string;
  /** The product's stable key (P1): the module's heading anchor, kept when the heading is renamed; "" before the first heading. */
  moduleKey?: string;
  /** The heading one level above the module's (plan 3.4 §5), „Jádro“ without one. */
  category?: string;
  /** The heading one level above the category's (plan 3.5 C: # Portál pacienta › ## Rezervace › ### Webové objednávání), "" without one. */
  product?: string;
  /** What the module is: the paragraphs under its heading before its first block (plan 3.5 C, O7), "" without them. */
  summary?: string;
}

export interface ProjectorCatalog { definitions: CatalogDefinition[]; warnings: string[] }

/** A deployment preset (M4a): a page template of the space holding requirement blocks; what it means (its first paragraph), its
 * modules and products, and the titles of its blocks whose text differs from the catalog's (plan 3.5 C, O1, O3). */
export interface ProjectorPreset { pageId: string; title: string; requirements: number; summary?: string; products?: string[] | null; modules?: number; differs?: string[] | null }

/** One block to insert: `requiredBy` null for the chosen one, `reason` for the user ("Vybráno", "Potřebuje ho: Objednávky"). */
export interface ResolvedRequirement { definition: CatalogDefinition; requiredBy: string | null; reason: string }

/** Who changed a block last and when (server RequirementChangeInfo): read in the working revision for an editor ("save"), in the
 * publication otherwise ("publish"). */
export interface RequirementChangeInfo {
  kind: "save" | "publish"; changedById: string; changedBy: string; changedAt: string; title: string; state: string;
}

/** A block of another page (`key@pageId`) as the reader may read it; `readable` false tells nothing else (unknown). */
export interface RequirementPrerequisite {
  reference: string; pageId: string; key: string; readable: boolean;
  pageTitle?: string | null; blockId?: string | null; title?: string | null; state?: string | null; direction?: string | null;
}

/** One block of a space-wide read: the page it is on with its block (server RequirementRow). */
export interface RequirementRowJson { pageId: string; pageTitle: string; requirement: { blockId: string; key: string; title: string; state: string } }

const catalogPath = (spaceId: string) => `/projector/spaces/${encodeURIComponent(spaceId)}/catalog`;

export const projectorApi = {
  /** The space's catalog to list and resolve: every definition without its block. */
  catalog: (spaceId: string, signal?: AbortSignal) => api<ProjectorCatalog>(`${catalogPath(spaceId)}?bodies=false`, { signal }),
  /** These keys' definitions with their blocks, to insert them. */
  definitions: (spaceId: string, keys: readonly string[], signal?: AbortSignal) =>
    api<ProjectorCatalog>(`${catalogPath(spaceId)}?${keys.map(key => `keys=${encodeURIComponent(key)}`).join("&")}`, { signal }),
  /** „Na co čekáme“ of the space: the requirements waiting for input, by party (the party field offers those names). */
  waiting: (spaceId: string, signal?: AbortSignal) => api<{ parties: { party: string }[] }>(`/projector/spaces/${encodeURIComponent(spaceId)}/waiting`, { signal }),
  setCatalogPage: (pageId: string, isCatalog: boolean) =>
    api<{ pageId: string; isCatalog: boolean }>(`/projector/pages/${encodeURIComponent(pageId)}/catalog`, { method: "PUT", body: JSON.stringify({ isCatalog }) }),
  /**
   * A new page with this page's text and blocks, their progress reset (spec §1.5); answers the new page, with `warnings` naming
   * the access of the page the copy could not take (a manager grant skipped, a grant capped at the actor's own rights).
   */
  /** The space's deployment presets: its page templates with requirement blocks; `details` adds what each holds (summary, products, modules, differences). */
  presets: (spaceId: string, signal?: AbortSignal, details = false) =>
    api<ProjectorPreset[]>(`/projector/spaces/${encodeURIComponent(spaceId)}/presets${details ? "?details=true" : ""}`, { signal }),
  /** The deployment pages of the space the account may read (those with keyed blocks), as the overview lists them. */
  deployments: async (spaceId: string, signal?: AbortSignal) =>
    (await api<{ pages: { pageId: string; title: string }[] }>(`/projector/spaces/${encodeURIComponent(spaceId)}/overview?filter=all`, { signal })).pages
      .map(({ pageId, title }) => ({ pageId, title })),
  /** A page's top-level requirement blocks as definitions with their blocks (the catalog's rule on any page or preset), to insert them. */
  pageDefinitions: (pageId: string, signal?: AbortSignal) => api<ProjectorCatalog>(`/projector/pages/${encodeURIComponent(pageId)}/definitions`, { signal }),
  /** A page's blocks as the server reads them for this reader, with who changed each last (`changed`). */
  pageRequirements: (pageId: string, signal?: AbortSignal) =>
    api<{ blocks: { blockId: string; changed?: RequirementChangeInfo | null }[] }>(`/projector/pages/${encodeURIComponent(pageId)}/requirements`, { signal }),
  /** The blocks of other pages these references name, as this reader may read them. */
  prerequisites: (refs: readonly string[], signal?: AbortSignal) =>
    api<RequirementPrerequisite[]>(`/projector/prerequisites?${refs.map(ref => `refs=${encodeURIComponent(ref)}`).join("&")}`, { signal }),
  /** „Co to blokuje“: the blocks of other pages that require this one. */
  dependents: (pageId: string, blockId: string, signal?: AbortSignal) =>
    api<{ key: string; rows: RequirementRowJson[] }>(`/projector/pages/${encodeURIComponent(pageId)}/requirements/${encodeURIComponent(blockId)}/dependents`, { signal }),
  /** The cycle the block `key` would close through other pages by requiring `requires`; null path when none. */
  cycle: (pageId: string, key: string, requires: readonly string[], signal?: AbortSignal) =>
    api<{ path: string[] | null }>(`/projector/pages/${encodeURIComponent(pageId)}/requirements/cycle?key=${encodeURIComponent(key)}${requires.map(ref => `&requires=${encodeURIComponent(ref)}`).join("")}`, { signal }),
  /** At most 50 keyed blocks of the space's deployment pages the account may read whose title, key or page title holds every word of
   * `q`, to pick a dependency on another page; `excludePageId` (the picker's own page) is left out before that limit. */
  searchRequirements: (spaceId: string, q: string, signal?: AbortSignal, excludePageId?: string) =>
    api<RequirementRowJson[]>(`/projector/spaces/${encodeURIComponent(spaceId)}/requirements/search?q=${encodeURIComponent(q)}`
      + (excludePageId ? `&excludePageId=${encodeURIComponent(excludePageId)}` : ""), { signal }),
  createDeployment: (sourcePageId: string, request: { spaceId: string; parentId?: string | null; title: string }) =>
    api<{ id: string; title: string; warnings?: string[] }>(`/projector/pages/${encodeURIComponent(sourcePageId)}/deployments`, { method: "POST", body: JSON.stringify(request) }),
};
