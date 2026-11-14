// The rules behind the requirement blocks' views (spec 2026-10-27 §1, §5); Projector/RequirementSummary on the server computes the same.
import t from "../i18n/projector";
import { historyStamp } from "./requirementHistory";
import { requirementStates, type RequirementAttributes, type RequirementItem, type RequirementState } from "./requirementTypes";

/** One requirement block of a page, as the views read it. */
export interface PageRequirement extends RequirementItem {
  readonly id: string;
  /** Ids of the requirement blocks it is nested in, nearest last. */
  readonly ancestors: readonly string[];
}

/** Finished for the page: done, or does not apply. */
export const satisfied = (state: RequirementState) => state === "done" || state === "not_applicable";

/** How a block reads by name anywhere (C4-12): its title, else its key, else „Požadavek bez názvu“. */
export const requirementName = (item: RequirementItem) => item.title || item.attrs.key || t.model.untitled;

export const requiredKeys =(attrs: Pick<RequirementAttributes, "requires">) => attrs.requires ? attrs.requires.split(",") : [];

/** "SSL certifikát" → "ssl-certifikat": the key's alphabet, at most 96 characters; empty when nothing of the title fits. */
export function slugKey(title: string): string {
  return title.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase()
    .replace(/[^a-z0-9.]+/g, "-").replace(/^[-.]+/, "").slice(0, 96).replace(/[-.]+$/, "");
}

/** `base-2`, `base-3` … within the key's 96 characters. */
export function nextRequirementKey(base: string, taken: ReadonlySet<string>): string {
  for (let number = 2; ; number++) {
    const suffix = `-${number}`;
    const key = base.slice(0, 96 - suffix.length) + suffix;
    if (!taken.has(key)) return key;
  }
}

/** The title's slug, suffixed `-2`, `-3` … when the page has it already. */
export function uniqueKey(title: string, taken: ReadonlySet<string>): string {
  const base = slugKey(title);
  return base && taken.has(base) ? nextRequirementKey(base, taken) : base;
}

/** A dependency on another page's block (`key@pageId`): its key and page; null for a key of the same page. */
export function crossPage(reference: string): { key: string; pageId: string } | null {
  const at = reference.indexOf("@");
  return at > 0 ? { key: reference.slice(0, at), pageId: reference.slice(at + 1) } : null;
}

/** The page's blocks by key (keys are unique on a page). */
export const keyed = <T extends RequirementItem>(items: readonly T[]) => new Map(items.filter(item => item.attrs.key).map(item => [item.attrs.key, item]));

/**
 * What a block waits for: its dependencies on the page that are not finished. A key without a block on the page, and the block's own
 * key, are ignored (as the server's RequirementSummary.WithWaitingOn).
 */
export function awaitedBy<T extends RequirementItem>(item: RequirementItem, byKey: ReadonlyMap<string, T>): T[] {
  if (satisfied(item.attrs.state)) return [];
  return requiredKeys(item.attrs).filter(key => key !== item.attrs.key).flatMap(key => byKey.get(key) ?? []).filter(entry => !satisfied(entry.attrs.state));
}

/**
 * The keys unfinished blocks require that no block of the page has (a block of another page, `key@pageId`, and a block's own key
 * aside), each with the blocks that need it, in the page order of the first of them. They are what the page lacks to be deployed.
 */
export function missingPrerequisites<T extends RequirementItem>(items: readonly T[]): { key: string; neededBy: T[] }[] {
  const have = new Set(items.map(item => item.attrs.key)), missing = new Map<string, T[]>();
  for (const item of items) {
    if (satisfied(item.attrs.state)) continue;
    for (const key of requiredKeys(item.attrs)) {
      if (key === item.attrs.key || have.has(key) || crossPage(key)) continue;
      const needed = missing.get(key);
      if (needed) needed.push(item); else missing.set(key, [item]);
    }
  }
  return [...missing].map(([key, neededBy]) => ({ key, neededBy }));
}

/** "3/4 vnořených hotovo" of every block, in one pass: all nested blocks count, those that do not apply do not. */
export function nestedProgress(items: readonly PageRequirement[]): Map<string, { done: number; total: number }> {
  const progress = new Map<string, { done: number; total: number }>(items.map(item => [item.id, { done: 0, total: 0 }]));
  for (const item of items) {
    if (item.attrs.state === "not_applicable") continue;
    for (const ancestor of item.ancestors) {
      const counts = progress.get(ancestor);
      if (!counts) continue;
      counts.total++;
      if (item.attrs.state === "done") counts.done++;
    }
  }
  return progress;
}

export interface RequirementSummary<T> {
  /** floor(done / blocks that apply); 0 when none apply. */
  percent: number;
  done: number;
  total: number;
  counts: Record<RequirementState, number>;
  /** Inputs not delivered yet: inbound, neither done nor not applicable. */
  missing: T[];
  /** Waiting for an input (`waiting_for_input`), with whom in `party`. */
  waiting: T[];
}

export function summarize<T extends RequirementItem>(items: readonly T[]): RequirementSummary<T> {
  const counts = Object.fromEntries(requirementStates.map(state => [state, 0])) as Record<RequirementState, number>;
  items.forEach(item => counts[item.attrs.state]++);
  const total = items.length - counts.not_applicable;
  return {
    percent: total ? Math.floor(counts.done * 100 / total) : 0, done: counts.done, total, counts,
    missing: items.filter(item => item.attrs.direction === "inbound" && !satisfied(item.attrs.state)),
    waiting: items.filter(item => item.attrs.state === "waiting_for_input"),
  };
}

/** The keys of the cycle that `from` requiring `to` would close (`to` … `from`), or null when there is none. */
export function dependencyCycle(items: readonly RequirementItem[], from: string, to: string): string[] | null {
  const requires = new Map(items.map(item => [item.attrs.key, requiredKeys(item.attrs)]));
  const seen = new Set<string>();
  const walk = (key: string): string[] | null => {
    if (key === from) return [key];
    if (seen.has(key)) return null;
    seen.add(key);
    for (const next of requires.get(key) ?? []) {
      const path = walk(next);
      if (path) return [key, ...path];
    }
    return null;
  };
  return to === from ? [from] : walk(to);
}

/**
 * "10. 9. 2026" (or "2026-09-10") → "2026-09-10"; "" stays empty; null for what is no day of the calendar. With `today`, a day and
 * month alone ("10. 9." or "10.9") are of its year (PR2-7: nobody types the year of a date next week).
 */
export function parseDate(text: string, today?: string): string | null {
  const value = text.trim();
  if (!value) return "";
  const czech = /^(\d{1,2})\.\s*(\d{1,2})\.\s*(\d{4})$/.exec(value), iso = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  const short = today && /^(\d{1,2})\.\s*(\d{1,2})\.?$/.exec(value);
  const parts = czech ? [czech[1], czech[2], czech[3]] : iso ? [iso[3], iso[2], iso[1]] : short ? [short[1], short[2], today.slice(0, 4)] : null;
  if (!parts) return null;
  const [day, month, year] = parts.map(Number) as [number, number, number];
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day ? date.toISOString().slice(0, 10) : null;
}

type Progress = Pick<RequirementAttributes, "state" | "date">;

/** A new state; done without a date gets today (Europe/Prague, as the server). */
export const withState = (attrs: Progress, state: RequirementState, today: string): Progress =>
  ({ state, date: state === "done" && !attrs.date ? today : attrs.date });

/**
 * The round check and „Netýká se“: `target` ↔ what it was. `previous` is what the first toggle returned; without it (another session,
 * a reload) back is planned, and a completion date goes with it.
 */
export function toggleState(attrs: Progress, target: "done" | "not_applicable", today: string, previous?: Progress): { next: Progress; previous?: Progress } {
  if (attrs.state !== target) return { next: withState(attrs, target, today), previous: { state: attrs.state, date: attrs.date } };
  return { next: previous ?? { state: "planned", date: target === "done" ? "" : attrs.date } };
}

/** A node of a stored rich document, as the catalog hands a block over. */
export interface BlockJson { type?: string; attrs?: Record<string, unknown>; content?: BlockJson[]; [key: string]: unknown }

const textOf = (node: BlockJson | undefined): string => typeof node?.text === "string" ? node.text : (node?.content ?? []).map(textOf).join("");
/** A line of a block's history (requirementHistory.ts), or a list left empty without them: the new page's history starts empty. */
const historyOnly = (node: BlockJson) => ((node.type === "listItem" || node.type === "timedItem") && node.content?.[0]?.type === "paragraph" && !!historyStamp(textOf(node.content[0])))
  || ((node.type === "bulletList" || node.type === "timedList") && !node.content?.length);

/**
 * A catalog, preset or other deployment's block for a new page (spec §1.5): new ids, no state, date, party, place or history lines;
 * key, direction, service, dependencies and the rest of the content stay.
 */
export function resetRequirementJson<T extends BlockJson>(block: T): T {
  const content = block.content?.map(resetRequirementJson).filter(node => !historyOnly(node));
  const attrs = block.type === "requirement"
    ? { ...block.attrs, id: globalThis.crypto.randomUUID(), state: "planned", stateLabel: "", date: "", party: "", partyContactId: "", where: "" }
    : block.attrs;
  return { ...block, ...(attrs ? { attrs } : {}), ...(content ? { content } : {}) };
}
