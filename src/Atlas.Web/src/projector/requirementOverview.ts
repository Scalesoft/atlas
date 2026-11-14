import type { Node as PMNode } from "@tiptap/pm/model";
import type { Transaction } from "@tiptap/pm/state";
import t from "../i18n/projector";
import type { PageRequirement } from "./requirementModel";
import { jsonText, kindOfNode, type OverviewJson } from "../overview/overviewKinds";
import { historyStamp, latestEntry } from "./requirementHistory";
import { unknownParty } from "./requirementSummary";
import type { RequirementState } from "./requirementTypes";

// The deployment overview of one page (the summary card, and the same card at the top of a page without one): progress by section,
// what blocks, on whom we wait. Computed from the document, never stored; the projection's summary line (requirementSummary.ts) is
// the server's and stays as it is.

/** A heading outside the blocks, or a top-level block, in document order. */
export type OutlineEntry = { readonly heading: string; readonly level: number } | { readonly block: string };

export interface PageOutline {
  readonly entries: readonly OutlineEntry[];
  /** The first note (the paragraph after the title) of each blocked block. */
  readonly notes: ReadonlyMap<string, string>;
  /** Whether the page has a summary block of its own. */
  readonly summary: boolean;
  /** How many requirement blocks the page has, nested ones too. */
  readonly blocks: number;
}

export const emptyOutline: PageOutline = { entries: [], notes: new Map(), summary: false, blocks: 0 };

interface Shape<N> { kids(node: N): readonly N[]; type(node: N): string | undefined; attrs(node: N): Readonly<Record<string, unknown>> | undefined; text(node: N): string }

/** A requirement is a heading with a state; any other heading is plain. */
export const isRequirementHeading = (type: string | undefined, attrs: Readonly<Record<string, unknown>> | undefined) => type === "heading" && !!attrs?.state;

/** The facts line under a requirement heading (shape §1, §6): „do 15. 11. 2026“ and nothing else; it is no note. */
export const factsLine = /^(?:do|hotovo)[\s ]+\d{1,2}\.[\s ]*\d{1,2}\.[\s ]*\d{4}(?: · hotovo.*)?$/;
/** A note that can be a blocked requirement's reason: not the facts line, nor a line the conversion wrote (shape §5: „Kde uloženo: …“ and its kin). */
export const isReasonNote = (text: string) => !!text && !factsLine.test(text) && !/^(Kde uloženo|Chybějící prerekvizita|Dříve požadavek):/.test(text);
/** A list that can hold history lines: a bullet list, or the editor's timed list („Časová odrážka“) it becomes. */
export const historyList = (type: string | undefined) => type === "bulletList" || type === "timedList";

/**
 * A blocked requirement's reason, 3.5.2's rule: the newest „Blokováno: …“ line of its history (what the PM's „Blokuje…“ writes; `records`
 * = the texts of the section's list lines), else its first reason note (`note`, isReasonNote); "" for none.
 */
export function blockedReason(note: string, records: readonly string[]) {
  const entries = records.flatMap(text => {
    const stamp = historyStamp(text), rest = stamp ? text.slice(stamp.textStart).trim() : "";
    return /^Blokováno\b/.test(rest) ? [{ date: stamp!.date, ...(stamp!.time ? { time: stamp!.time } : {}), text: rest.replace(/^Blokováno:?\s*/, "") }] : [];
  });
  return latestEntry(entries)?.text || note;
}

function outlineOf<N>(root: N, shape: Shape<N>): PageOutline {
  const entries: OutlineEntry[] = [], notes = new Map<string, string>();
  let summary = false, blocks = 0;
  // The blocked requirement whose section is being read, its first reason note and its list lines (blockedReason).
  let awaiting = "", note = "", records: string[] = [];
  const settle = () => { const reason = awaiting && blockedReason(note, records); if (reason) notes.set(awaiting, reason); awaiting = note = ""; records = []; };
  // The page's top-level blocks: a heading starts a section, requirements do not nest (shape §1).
  for (const child of shape.kids(root)) {
    const type = shape.type(child), attrs = shape.attrs(child);
    if (kindOfNode(type, attrs) === "requirements") summary = true;
    if (type === "heading" || type === "requirement") settle();
    if (type === "heading") {
      if (!isRequirementHeading(type, attrs)) { entries.push({ heading: shape.text(child).trim(), level: Number(attrs?.level ?? 1) }); continue; }
      const id = String(attrs?.headingId ?? "");
      blocks++;
      entries.push({ block: id });
      if (attrs?.state === "blocked") awaiting = id;
    } else if (type === "requirement") {
      // A block of the old shape, until the conversion job has rewritten the page (model §10b): one entry, its nested blocks counted.
      const id = String(attrs?.id ?? "");
      entries.push({ block: id });
      const walk = (node: N) => {
        const own = shape.attrs(node), note = shape.kids(node)[1];
        blocks++;
        if (own?.state === "blocked" && note && shape.type(note) === "paragraph") notes.set(String(own.id ?? ""), shape.text(note).trim());
        shape.kids(node).forEach(kid => { if (shape.type(kid) === "requirement") walk(kid); });
      };
      walk(child);
    } else if (awaiting && type === "paragraph" && !attrs?.dependencies) {
      const text = shape.text(child).trim();
      if (!note && isReasonNote(text)) note = text;
    } else if (awaiting && historyList(type)) {
      for (const item of shape.kids(child)) { const line = shape.kids(item)[0]; if (line) records.push(shape.text(line).trim()); }
    }
  }
  settle();
  return { entries, notes, summary, blocks };
}


export const outlineOfJson = (document: OverviewJson) =>
  outlineOf(document, { kids: node => node.content ?? [], type: node => node.type, attrs: node => node.attrs, text: jsonText });

export const outlineOfDoc = (document: PMNode) =>
  outlineOf(document, { kids: node => node.isTextblock ? [] : node.children, type: node => node.type.name, attrs: node => node.attrs, text: node => node.textContent });

/** The top-level blocks each step touches, or null for a step that may reshape the page (one without ranges: an attribute, a level). */
function touchedBlocks(tr: Transaction): number[] | null {
  const touched: number[] = [];
  for (const [index, step] of tr.steps.entries()) {
    const doc = tr.docs[index]!;
    let ranges = 0, inside = true;
    step.getMap().forEach((from, to) => {
      const $from = doc.resolve(from), $to = doc.resolve(to);
      ranges++;
      inside &&= $from.depth > 0 && $to.depth > 0 && $from.index(0) === $to.index(0);
      if (inside) touched.push($from.index(0));
    });
    if (!ranges || !inside) return null;
  }
  return touched;
}

/**
 * Whether a transaction can change the page's sections (a heading added, removed, re-levelled or given another state, a block added or
 * removed): typing inside one block, a heading's text included, keeps them, so a keystroke walks nothing (model §2).
 */
export const reshapesSections = (tr: Transaction) => tr.docChanged && (tr.before.childCount !== tr.doc.childCount || !touchedBlocks(tr));

/**
 * Whether a transaction can change the outline: the sections, a heading's text, or a note under a blocked requirement (the card shows
 * its first). Typing anywhere else leaves it as it was.
 */
export function touchesOutline(tr: Transaction): boolean {
  if (!tr.docChanged) return false;
  const touched = tr.before.childCount === tr.doc.childCount ? touchedBlocks(tr) : null;
  if (!touched) return true;
  return touched.some(index => {
    // A block of the old shape holds its notes itself; otherwise the heading the block is under, read backwards over its section only.
    if (tr.doc.child(index).type.name === "requirement") return true;
    for (let at = index; at >= 0; at--) {
      const node = tr.doc.child(at);
      if (node.type.name === "heading") return at === index || node.attrs.state === "blocked";
    }
    return false;
  });
}

/** The bar's order: what is finished first, then what moves, then what stands. */
const barStates: readonly RequirementState[] = ["done", "in_progress", "waiting_for_input", "blocked", "planned"];

/** The bar's segments by state, each its share of the blocks that apply. */
export function stateSegments(counts: Readonly<Record<RequirementState, number>>) {
  const total = barStates.reduce((sum, state) => sum + counts[state], 0);
  return barStates.filter(state => counts[state]).map(state => ({ state, count: counts[state], share: counts[state] / total }));
}

/** One item of the card's bar and legend: a state, or a block's own word for it (`label`, „test“) counted in that state. */
export interface StateEntry { readonly key: string; readonly state: RequirementState; readonly label: string; readonly count: number; readonly share: number }

/**
 * The card's bar and legend: the states in the bar's order, each split by the blocks' own words (plan 3.4 §4), the blocks without one
 * first, then each word in page order; what does not apply last, with no share of the bar. The words only split their state's count:
 * the totals and the percent stay the state's.
 */
export function stateEntries(items: readonly PageRequirement[]): StateEntry[] {
  const order: readonly RequirementState[] = [...barStates, "not_applicable"], found = new Map<string, { state: RequirementState; label: string; count: number }>();
  for (const { attrs } of items) {
    const label = attrs.stateLabel.trim(), key = `${attrs.state}:${label}`, entry = found.get(key);
    if (entry) entry.count++; else found.set(key, { state: attrs.state, label, count: 1 });
  }
  const total = items.filter(item => item.attrs.state !== "not_applicable").length;
  return [...found].map(([key, entry]) => ({ key, ...entry, share: entry.state === "not_applicable" || !total ? 0 : entry.count / total }))
    .sort((a, b) => order.indexOf(a.state) - order.indexOf(b.state) || Number(!!a.label) - Number(!!b.label));
}

/** A section's name: its headings from the top joined „Portál pacienta › Core“; empty before the first heading. */
export const sectionName = (path: readonly string[]) => path.filter(Boolean).join(" › ");

export interface OverviewSection<T> {
  readonly key: string;
  /** The headings it is under, from the top; empty for the blocks before the first heading. */
  readonly path: readonly string[];
  /** The nearest heading's text; empty for the blocks before the first heading. */
  readonly title: string;
  /** The headings above it (sectionName of the rest of the path), so „Core“ of the portal and of the MPI read apart. */
  readonly context: string;
  readonly items: T[];
  readonly done: number;
  readonly total: number;
  readonly counts: Record<RequirementState, number>;
}

const zero = (): Record<RequirementState, number> => ({ planned: 0, waiting_for_input: 0, in_progress: 0, done: 0, blocked: 0, not_applicable: 0 });

/** The page's blocks by section: a top-level block belongs to the nearest heading before it, a nested one to its top-level block's. */
export function overviewSections<T extends PageRequirement>(items: readonly T[], entries: readonly OutlineEntry[]): OverviewSection<T>[] {
  const sectionOf = new Map<string, string>(), heads = new Map<string, readonly string[]>([["start", []]]);
  const stack: { title: string; level: number }[] = [];
  let current = "start";
  entries.forEach((entry, index) => {
    if ("block" in entry) { sectionOf.set(entry.block, current); return; }
    while (stack.length && stack.at(-1)!.level >= entry.level) stack.pop();
    current = `h${index}`;
    stack.push({ title: entry.heading, level: entry.level });
    heads.set(current, stack.map(head => head.title));
  });
  const grouped = new Map<string, T[]>();
  for (const item of items) {
    const key = sectionOf.get(item.ancestors[0] ?? item.id) ?? "start";
    const group = grouped.get(key);
    if (group) group.push(item); else grouped.set(key, [item]);
  }
  return [...heads.keys()].filter(key => grouped.has(key)).map(key => {
    const path = heads.get(key)!, sectionItems = grouped.get(key)!, counts = zero();
    sectionItems.forEach(item => counts[item.attrs.state]++);
    return { key, path, title: path.at(-1) ?? "", context: sectionName(path.slice(0, -1)), items: sectionItems, done: counts.done,
      total: sectionItems.length - counts.not_applicable, counts };
  });
}

/**
 * „23 vstupů · 4 moduly“: the blocks that apply by kind, so the headline's count says what it is made of. An input by its direction,
 * a module by its nested blocks, then handovers and the rest of our own work.
 */
export function blockKinds(items: readonly PageRequirement[]): string {
  const parents = new Set(items.flatMap(item => item.ancestors));
  const kinds = { inbound: 0, module: 0, outbound: 0, task: 0 };
  for (const item of items) {
    if (item.attrs.state === "not_applicable") continue;
    kinds[item.attrs.direction === "inbound" ? "inbound" : parents.has(item.id) ? "module" : item.attrs.direction === "outbound" ? "outbound" : "task"]++;
  }
  return [kinds.inbound && t.counts.inputs(kinds.inbound), kinds.module && t.counts.modules(kinds.module),
    kinds.outbound && t.counts.handovers(kinds.outbound), kinds.task && t.counts.tasks(kinds.task)].filter(Boolean).join(" · ");
}

/**
 * Whom the page waits on: the blocks waiting for input by party, the longest waited first and undated last, as „Na co čekáme“ of the
 * space (the server's RequirementWaitingBuilder). A planned input is to be delivered, not waited on.
 */
export const waitingByParty = <T extends PageRequirement>(items: readonly T[]) => byParty(items.filter(item => item.attrs.state === "waiting_for_input")
  .sort((a, b) => Number(!a.attrs.date) - Number(!b.attrs.date) || (a.attrs.date < b.attrs.date ? -1 : a.attrs.date > b.attrs.date ? 1 : 0)));

/** „Ještě jsme nežádali“ (plan 3.4 §3): the inputs nobody has been asked for yet (neřešeno), by party, in document order. */
export const notAskedByParty = <T extends PageRequirement>(items: readonly T[]) =>
  byParty(items.filter(item => item.attrs.direction === "inbound" && item.attrs.state === "planned"));

/** The blocks by party (as written; none named under „Bez určení“), parties in the order they first come. */
export function byParty<T extends PageRequirement>(items: readonly T[]) {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const party = item.attrs.party.trim() || unknownParty, group = groups.get(party);
    if (group) group.push(item); else groups.set(party, [item]);
  }
  return [...groups].map(([party, entries]) => ({ party, items: entries }));
}

/** Whole days from `date` to `today` (both YYYY-MM-DD), for „čeká 103 dní“; undefined without a date or for one ahead. */
export function daysSince(date: string, today: string) {
  if (!date) return undefined;
  const days = Math.round((Date.parse(today) - Date.parse(date)) / 86_400_000);
  return Number.isFinite(days) && days >= 0 ? days : undefined;
}

/** What blocks the deployment, with each block's first note. */
export const blockedItems = <T extends PageRequirement>(items: readonly T[], notes: ReadonlyMap<string, string>) =>
  items.filter(item => item.attrs.state === "blocked").map(item => ({ item, note: notes.get(item.id) ?? "" }));

/**
 * The overview at the top of a page without a summary block: a page of requirements (two blocks at least, nested ones too, so a
 * single module has its levels) and no summary of its own, which would show the same twice. A board hides it (requirements.css).
 */
export const showsAutoOverview = (outline: Pick<PageOutline, "summary" | "blocks">) => !outline.summary && outline.blocks >= 2;

/** The card's controls in Tab order (what it does not show, it does not render). */
export const cardControls = (card: Element) => [...card.querySelectorAll<HTMLElement>("button:not([disabled]), [href], [role=slider], [tabindex]:not([tabindex=\"-1\"])")]
  .filter(control => control.tabIndex >= 0);
