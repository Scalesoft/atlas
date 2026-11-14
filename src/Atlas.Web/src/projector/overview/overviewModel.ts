import t from "../../i18n/projector";
import { buildShellHash } from "../../shellRoute";
import { keyed, type PageRequirement } from "../requirementModel";
import { sectionName } from "../requirementOverview";
import { requirementReadiness } from "../requirementReadiness";
import { requirementStateLabel } from "../requirementSummary";
import { requirementDefaults, type RequirementDirection, type RequirementState } from "../requirementTypes";
import type { OverviewFilter, OverviewPage, RequirementBlock, RequirementOverview, WaitingList } from "./overviewApi";

// Pure layout of the server's answers for the two space views; the filtering itself is the server's.

/** `detail`: the key, when another column of its group has the same title; empty otherwise. */
export interface MatrixColumn { key: string; title: string; group: string; detail: string }
export interface MatrixGroup { label: string; span: number }
/** A page's next steps, by the page's one readiness rule (requirementReadiness): what can be deployed or handed over, what blocks. */
export interface RowReadiness { deployable: number; handover: number; blocking: number }
export interface MatrixCell {
  blockId: string; state: RequirementState; direction: RequirementDirection; title: string;
  /** Inside the chosen filter (the server returned it); the others are dimmed. */
  matches: boolean;
  /** "10. 9." or, while waiting, "15 d". */
  text: string;
  label: string; href: string;
}
export interface MatrixRow { pageId: string; title: string; href: string; percent: number; readiness: RowReadiness; cells: (MatrixCell | null)[] }
/** `hidden`: the pages the filter leaves out, having nothing left in it. */
export interface Matrix { rows: MatrixRow[]; columns: MatrixColumn[]; groups: MatrixGroup[]; hidden: number }

/** The page opened at one of its blocks: the page route's anchor (`?heading=`) also finds a requirement block (DocumentEditor). */
export const blockHref = (pageId: string, blockId: string) => buildShellHash({ view: "page", pageId, heading: blockId });

const dayPattern = /^(\d{4})-(\d{2})-(\d{2})$/;
function dayNumber(date: string): number | null {
  const match = dayPattern.exec(date);
  if (!match) return null;
  const [year, month, day] = match.slice(1).map(Number) as [number, number, number];
  const time = Date.UTC(year, month - 1, day);
  return new Date(time).getUTCDate() === day ? time / 86_400_000 : null;
}

/** Whole days from `date` to `today` (both YYYY-MM-DD); null for no date or an invalid one. */
export function daysSince(date: string, today: string): number | null {
  const from = dayNumber(date), to = dayNumber(today);
  return from === null || to === null ? null : to - from;
}

function shortDate(date: string, today: string) {
  const [year, month, day] = date.split("-");
  return `${Number(day)}. ${Number(month)}.` + (year === today.slice(0, 4) ? "" : ` ${year}`);
}

/** How long we wait, from the block's date: „190 dní“; a date still ahead (a deadline typed there) is „za 16 dní“, never a minus (PR2-3). */
export function waitedFor(days: number | null): string {
  if (days === null) return t.waited.undated;
  if (days === 0) return t.waited.today;
  if (days === -1) return t.waited.tomorrow;
  return days < 0 ? t.waited.inDays(-days) : t.waited.days(days);
}

export function cellText(block: Pick<RequirementBlock, "state" | "date">, today: string): string {
  if (block.state === "waiting_for_input") {
    const days = daysSince(block.date, today);
    return days === null ? "" : days < 0 ? `za ${-days} d` : `${days} d`;
  }
  return dayNumber(block.date) === null ? "" : shortDate(block.date, today);
}

const prefixOf = (key: string) => key.includes(".") ? key.slice(0, key.indexOf(".")) : "";
const finished = (state: RequirementState) => state === "done" || state === "not_applicable";

/** The page's keyed blocks read as the page's views read blocks, and counted by what can happen next (U4-9). */
export function rowReadiness(cells: readonly RequirementBlock[]): RowReadiness {
  const items: PageRequirement[] = cells.map(cell => ({ id: cell.blockId, title: cell.title, ancestors: [],
    attrs: { ...requirementDefaults, key: cell.key, direction: cell.direction, state: cell.state, date: cell.date, party: cell.party, where: cell.where, requires: cell.requires.join(",") } }));
  const byKey = keyed(items), memo = new Map<PageRequirement, PageRequirement[]>(), counts: RowReadiness = { deployable: 0, handover: 0, blocking: 0 };
  for (const item of items) {
    if (item.attrs.state === "blocked") counts.blocking++;
    if (item.attrs.direction !== "inbound" && requirementReadiness(item, byKey, memo).state === "ready") counts[item.attrs.direction === "internal" ? "deployable" : "handover"]++;
  }
  return counts;
}

/** Which blocks a filter keeps, as the server's RequirementOverviewBuilder: every one, the unfinished ones, the unfinished inputs. */
export const keeps: Record<OverviewFilter, (block: RequirementBlock) => boolean> = {
  all: () => true,
  missing: block => !finished(block.state),
  "inbound-missing": block => block.direction === "inbound" && !finished(block.state),
};

const readiness = new WeakMap<OverviewPage, RowReadiness>();
const readinessOf = (page: OverviewPage) => readiness.get(page) ?? readiness.set(page, rowReadiness(page.cells)).get(page)!;

function rowCells(page: OverviewPage, columns: readonly MatrixColumn[], matching: ReadonlySet<string>, today: string): (MatrixCell | null)[] {
  const byKey = new Map<string, RequirementBlock>();
  for (const cell of page.cells) if (!byKey.has(cell.key)) byKey.set(cell.key, cell);
  return columns.map(({ key }) => {
    const block = byKey.get(key);
    if (!block) return null;
    const text = cellText(block, today);
    return {
      blockId: block.blockId, state: block.state, direction: block.direction, title: block.title, matches: matching.has(block.blockId), text,
      label: `${block.title}: ${requirementStateLabel(block.state)}` + (text ? `, ${text}` : ""), href: blockHref(page.pageId, block.blockId),
    };
  });
}

/**
 * The matrix of the whole space's keyed blocks (`all`) under a filter: columns are the keys it keeps, grouped by the section path of
 * the block on the first page that has it (the page's own outline, sectionName) where the server sends it, else by the catalog's
 * bundle or the key's prefix; rows the pages with something it keeps (every page for „Vše“), each with its next steps. A block
 * outside the filter stays in place, dimmed, so switching the filter only fades cells and leaves out the pages with nothing left.
 */
export function overviewMatrix(all: RequirementOverview, filter: OverviewFilter, options: { today: string; bundles?: ReadonlyMap<string, string>; titles?: ReadonlyMap<string, string> }): Matrix {
  const first = new Map<string, RequirementBlock>(), matching = new Set<string>(), keys = new Set<string>();
  for (const page of all.pages) for (const cell of page.cells) {
    if (!first.has(cell.key)) first.set(cell.key, cell);
    if (keeps[filter](cell)) { matching.add(cell.blockId); keys.add(cell.key); }
  }
  const groupOf = (key: string) => { const section = first.get(key)?.section; return section ? sectionName(section) : options.bundles?.get(key) ?? prefixOf(key); };
  // Ordinal, as the server sorts the keys.
  const unsorted = [...keys].sort((a, b) => a < b ? -1 : a > b ? 1 : 0)
    .map(key => ({ key, title: options.titles?.get(key) || first.get(key)?.title || key, group: groupOf(key), detail: "" }));
  const groupOrder = [...new Set(unsorted.map(column => column.group))];
  const columns = groupOrder.flatMap(group => unsorted.filter(column => column.group === group));
  const titled = new Map<string, number>();
  for (const column of columns) titled.set(`${column.group}\n${column.title}`, (titled.get(`${column.group}\n${column.title}`) ?? 0) + 1);
  for (const column of columns) if (titled.get(`${column.group}\n${column.title}`)! > 1) column.detail = column.key;
  const groups = groupOrder.map(label => ({ label, span: columns.filter(column => column.group === label).length }));
  const pages = filter === "all" ? all.pages : all.pages.filter(page => page.cells.some(cell => matching.has(cell.blockId)));
  // A row's cells and next steps are worked out when first read, so only the rows on screen cost anything; a page's readiness does not
  // depend on the filter and is kept across them.
  const rows = pages.map(page => {
    let cells: (MatrixCell | null)[] | undefined;
    return {
      pageId: page.pageId, title: page.title, href: buildShellHash({ view: "page", pageId: page.pageId }), percent: page.percent,
      get readiness() { return readinessOf(page); },
      get cells() { return cells ??= rowCells(page, columns, matching, options.today); },
    };
  });
  return { rows, columns, groups: groups.length === 1 && groups[0]!.label === "" ? [] : groups, hidden: all.pages.length - pages.length };
}

/** A product (P1): a module of the catalog by its stable key, the heading's text its name. */
export interface ProductColumn { key: string; title: string; keys: string[] }
/** What a deployment has of a product: its requirements on the page (`have` of `total`) and how many of those are finished. */
export interface ProductCell { have: number; total: number; done: number; label: string; href: string }
export interface ProductMatrix { columns: ProductColumn[]; rows: { pageId: string; title: string; href: string; cells: ProductCell[] }[] }

/**
 * Deployments × products (M4a): which product each deployment has (all its requirements on the page), has in part, or lacks. A product
 * is a catalog module by its key (`moduleKey`, the heading's anchor; the heading's text where the server sends none); requirements
 * before a catalog page's first heading belong to none.
 */
export function productMatrix(all: RequirementOverview, definitions: readonly { key: string; bundle: string; moduleKey?: string }[]): ProductMatrix {
  const products = new Map<string, ProductColumn>();
  for (const definition of definitions) {
    const key = definition.moduleKey || definition.bundle;
    if (!key) continue;
    const product = products.get(key) ?? products.set(key, { key, title: definition.bundle || key, keys: [] }).get(key)!;
    if (!product.keys.includes(definition.key)) product.keys.push(definition.key);
  }
  const columns = [...products.values()];
  const rows = all.pages.map(page => {
    const byKey = new Map(page.cells.map(cell => [cell.key, cell]));
    return { pageId: page.pageId, title: page.title, href: buildShellHash({ view: "page", pageId: page.pageId }), cells: columns.map(product => {
      const present = product.keys.flatMap(key => byKey.get(key) ?? []), done = present.filter(cell => cell.state === "done" || cell.state === "not_applicable").length;
      const label = `${product.title}: ${!present.length ? t.waited.productNone : t.waited.productHas(present.length, product.keys.length, done)}`;
      return { have: present.length, total: product.keys.length, done, label, href: present[0] ? blockHref(page.pageId, present[0].blockId) : buildShellHash({ view: "page", pageId: page.pageId }) };
    }) };
  });
  return { columns, rows };
}

export interface WaitingGroup {
  party: string; label: string;
  items: (WaitingList["parties"][number]["items"][number] & { days: number | null; href: string })[];
}

/** The server's groups in its order (the longest waiting first); an empty party is the summary's `unknownParty`. */
export function waitingGroups(list: WaitingList, today: string): WaitingGroup[] {
  return list.parties.map(({ party, items }) => ({
    party, label: party || t.summary.unknownParty,
    items: items.map(item => ({ ...item, days: daysSince(item.date, today), href: blockHref(item.pageId, item.blockId) })),
  }));
}
