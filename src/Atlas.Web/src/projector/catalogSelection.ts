import t from "../i18n/projector";
import type { CatalogDefinition, RequirementJson, ResolvedRequirement } from "./projectorApi";
import { requirementSection } from "./requirementCommands";
import { resetRequirementJson } from "./requirementModel";
import { resolveRequirements, MAX_DEPTH, MAX_ITEMS, type Resolution } from "./requirementResolver";

// What a pick of several catalog rows brings (the catalog panel and „Nasazení z katalogu“ share it): the blocks in dependency
// order, grouped by the catalog's modules, and what a new deployment page made of them holds.

/** A module (bundle) of the catalog: one run of definitions under one heading of a catalog page. */
export interface CatalogModule {
  id: string;
  /** The heading, "" before a page's first one. */
  title: string;
  pageId: string;
  keys: string[];
  /** The product category over the module (the heading one level above it, „Jádro“ without one), "" when the catalog says none. */
  category: string;
  /** The product over the category (the heading above that), "" when the catalog says none. */
  product: string;
  /** The keys' common dotted prefix („portal.nia“), which tells two modules of one name apart. */
  prefix: string;
  /** What the module is, its catalog paragraph (O7); "" without one. */
  summary: string;
}

function commonPrefix(keys: readonly string[]) {
  let parts = keys[0]!.split(".");
  for (const key of keys.slice(1)) {
    const other = key.split(".");
    let same = 0;
    while (same < parts.length && parts[same] === other[same]) same++;
    parts = parts.slice(0, same);
  }
  return parts.join(".");
}

/** The catalog's modules in catalog order: a heading repeated further down (two „Core“) is a module of its own. */
export function catalogModules(definitions: readonly CatalogDefinition[]): CatalogModule[] {
  const modules: CatalogModule[] = [];
  let last: CatalogModule | undefined;
  for (const definition of definitions) {
    if (!last || last.pageId !== definition.pageId || last.title !== definition.bundle || last.category !== (definition.category ?? "") || last.product !== (definition.product ?? "")) {
      last = { id: `${definition.pageId}#${modules.length}`, title: definition.bundle, pageId: definition.pageId, keys: [], category: definition.category ?? "",
        product: definition.product ?? "", prefix: "", summary: definition.summary ?? "" };
      modules.push(last);
    }
    last.keys.push(definition.key);
  }
  for (const module of modules) module.prefix = commonPrefix(module.keys);
  return modules;
}

export interface SelectionPlan {
  /** What to insert, dependencies first. */
  items: ResolvedRequirement[];
  /** The same by module, a module ordered before the modules whose blocks need it (by where its last block falls). */
  sections: { module: CatalogModule; items: ResolvedRequirement[] }[];
  /** Brought along without being chosen. */
  added: ResolvedRequirement[];
  /** Inputs from the customer among them. */
  inbound: number;
  warnings: string[];
}

/** What inserting the chosen keys brings, with the page's own keys skipped (resolveRequirements). */
export function planSelection(definitions: readonly CatalogDefinition[], modules: readonly CatalogModule[], keys: readonly string[],
  present: readonly string[] = [], dismissed: readonly string[] = [], source?: "page"): SelectionPlan | Extract<Resolution, { refused: unknown }> {
  const resolution = resolveRequirements(definitions, keys, present, dismissed, source);
  if ("refused" in resolution) return resolution;
  const moduleOf = new Map(modules.flatMap(module => module.keys.map(key => [key, module] as const)));
  const sections = new Map<CatalogModule, { module: CatalogModule; items: ResolvedRequirement[]; last: number }>();
  resolution.items.forEach((item, index) => {
    const module = moduleOf.get(item.definition.key)!;
    const section = sections.get(module) ?? sections.set(module, { module, items: [], last: 0 }).get(module)!;
    section.items.push(item);
    section.last = index;
  });
  const chosen = new Set(keys);
  return {
    items: resolution.items,
    sections: [...sections.values()].sort((a, b) => a.last - b.last).map(({ module, items }) => ({ module, items })),
    added: resolution.items.filter(item => !chosen.has(item.definition.key)),
    inbound: resolution.items.filter(item => item.definition.direction === "inbound").length,
    warnings: resolution.warnings,
  };
}

/** Why a selection cannot be inserted, kindly. */
export function refusalText(refused: { code: string; path?: string[] }) {
  return refused.code === "cyclic_dependency" ? t.selection.cycle(refused.path!.join(" → ")) : t.selection.tooBig(MAX_ITEMS, MAX_DEPTH);
}

/** „Vloží se 23 požadavků ve 4 modulech, z toho 19 vstupů od zákazníka.“ */
export function planSummary(plan: SelectionPlan) {
  if (!plan.items.length) return t.selection.nothing;
  return t.selection.summary(plan.items.length, plan.sections.length, plan.inbound);
}

/** The new page's own id in the links of its dependency lines: the page has none yet, the server writes its real one (CreatePageHandler). */
export const NEW_PAGE_ID = "ffffffff-ffff-ffff-ffff-ffffffffffff";

/**
 * A new deployment page of the plan (spec §1.4–1.5): the summary, one heading per module with its requirements as headings under it
 * (requirementSection: reset for the page, planned, no facts line), and an empty line to write on. `ids` are the requirement
 * headings' ids in document order.
 */
export function deploymentDocument(plan: SelectionPlan, blocks: ReadonlyMap<string, RequirementJson>, moduleTitle = (module: CatalogModule) => module.title || t.selection.defaultModule) {
  const ids: string[] = [];
  const content: unknown[] = [{ type: "overview", attrs: { id: globalThis.crypto.randomUUID(), kind: "requirements" } }];
  const titles = new Map(plan.items.map(item => [item.definition.key, item.definition.title] as const));
  for (const { module, items } of plan.sections) {
    content.push({ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: moduleTitle(module) }] });
    for (const item of items) {
      const section = requirementSection(resetRequirementJson(blocks.get(item.definition.key)!), 3, NEW_PAGE_ID, titles);
      for (const node of section) if (node.type === "heading" && node.attrs?.state != null && node.attrs.headingId) ids.push(String(node.attrs.headingId));
      content.push(...section);
    }
  }
  content.push({ type: "paragraph" });
  return { value: { format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 4 }, { id: "heading-anchors", version: 2 }, { id: "pages", version: 1 }], document: { type: "doc", content } }, ids };
}
