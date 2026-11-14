import t from "../i18n/projector";
import { awaitedBy, requirementName, satisfied, type PageRequirement } from "./requirementModel";
import { requirementStateLabel } from "./requirementSummary";
import type { RequirementItem } from "./requirementTypes";

/** Whether a group of blocks (a section of the page, or one block) can be deployed. */
export type Readiness = "done" | "ready" | "waiting" | "blocked";

export interface ReadinessOf<T> {
  state: Readiness;
  /** „hotovo“, „blokováno“, „čeká na 3 prerekvizity“, or by direction „čeká na dodání“, „lze předat“, „lze nasadit“ (V1). */
  label: string;
  /** The label as the page's stored text writes it (requirementSummaryText, the server's projection): Czech in every language. */
  content: string;
  /** The unfinished prerequisites, each once, in the page order of the blocks waiting for them. */
  prerequisites: T[];
  /** While blocked: the blocked block the first blocked path ends in (it may be the block itself). */
  blocking?: T;
  /** While blocked: that path, from the block of the group down to `blocking`. */
  path: T[];
}

/**
 * The one rule for „blocked“ (U4-8): an unfinished block is blocked when it is, or when a prerequisite it waits for is blocked,
 * however deep. The path from `item` to the first blocked block, `item` first; empty when it is not blocked. A cycle ends the walk.
 * The overview, the blocks' facts and the board's lines all ask this; who asks for many blocks passes one `memo` for all of them,
 * so a page's whole index costs one walk.
 */
export function blockedPath<T extends RequirementItem>(item: T, byKey: ReadonlyMap<string, T>, memo = new Map<T, T[]>()): T[] {
  const known = memo.get(item);
  if (known) return known;
  let path: T[] = [];
  if (item.attrs.state === "blocked") path = [item];
  else if (!satisfied(item.attrs.state)) {
    // A block on the walk reads as not blocked while it is walked: the end of a cycle.
    memo.set(item, path);
    for (const next of awaitedBy(item, byKey)) {
      const rest = blockedPath(next, byKey, memo);
      if (rest.length) { path = [item, ...rest]; break; }
    }
  }
  memo.set(item, path);
  return path;
}

/** What is ready says what happens next by direction: inputs only wait to be delivered, handovers are handed over, our work deployed. */
const czechPrerequisites = (n: number) => `${n} ${n === 1 ? "prerekvizitu" : n >= 2 && n <= 4 ? "prerekvizity" : "prerekvizit"}`;
const czechReady = (open: readonly RequirementItem[]) => open.every(item => item.attrs.direction === "inbound") ? "čeká na dodání"
  : open.some(item => item.attrs.direction === "internal") ? "lze nasadit" : "lze předat";
function readyLabel(open: readonly RequirementItem[]) {
  if (open.every(item => item.attrs.direction === "inbound")) return t.readiness.delivery;
  return open.some(item => item.attrs.direction === "internal") ? t.readiness.deploy : t.readiness.handOver;
}

/**
 * Readiness of `items` against the page's blocks by key (keyed()): done when every block is finished, blocked when one is blocked on
 * its path (blockedPath), waiting while an unfinished block depends on an unfinished one, ready otherwise. The same words for the
 * overview's sections, the blocks' facts and the board's cards; the server's projection writes them too.
 */
export function sectionReadiness<T extends RequirementItem>(items: readonly T[], byKey: ReadonlyMap<string, T>, memo?: Map<T, T[]>): ReadinessOf<T> {
  const prerequisites = [...new Set(items.flatMap(item => awaitedBy(item, byKey)))];
  const open = items.filter(item => !satisfied(item.attrs.state));
  if (!open.length) {
    const label = items.length && items.every(item => item.attrs.state === "not_applicable") ? requirementStateLabel("not_applicable") : requirementStateLabel("done");
    return { state: "done", label, content: items.length && items.every(item => item.attrs.state === "not_applicable") ? "netýká se" : "hotovo", prerequisites, path: [] };
  }
  for (const item of open) {
    const path = blockedPath(item, byKey, memo);
    if (path.length) return { state: "blocked", label: requirementStateLabel("blocked"), content: "blokováno", prerequisites, blocking: path.at(-1), path };
  }
  if (prerequisites.length) return { state: "waiting", label: t.readiness.waitingForPrerequisites(prerequisites.length), content: `čeká na ${czechPrerequisites(prerequisites.length)}`, prerequisites, path: [] };
  // What waits for someone's input is not ready, whatever its prerequisites; the rest of a section may be.
  const movable = open.filter(item => item.attrs.state !== "waiting_for_input");
  if (!movable.length) return { state: "waiting", label: requirementStateLabel("waiting_for_input"), content: "čeká na vstup", prerequisites, path: [] };
  return { state: "ready", label: readyLabel(movable), content: czechReady(movable), prerequisites, path: [] };
}

/** One block's readiness (its state chip says what it is; this what can happen next). */
export const requirementReadiness = <T extends RequirementItem>(item: T, byKey: ReadonlyMap<string, T>, memo?: Map<T, T[]>) => sectionReadiness([item], byKey, memo);

/**
 * A block's fact of its readiness in a tone its state chip does not contradict (3.5.1 V6): a block blocked through a prerequisite
 * waits on it in amber („čeká na blokované: SMTP server“), an input waiting to be delivered is quiet, what can go on is green. The
 * sections and the server keep their words.
 */
export function blockReadiness<T extends RequirementItem>(readiness: ReadinessOf<T>): { label: string; tone?: "done" | "waiting_for_input" } {
  if (readiness.state === "blocked" && readiness.blocking) return { label: t.readiness.waitingForBlocked(requirementName(readiness.blocking)), tone: "waiting_for_input" };
  return readiness.state === "ready" && readiness.label !== t.readiness.delivery ? { label: readiness.label, tone: "done" } : { label: readiness.label };
}

/** What a readiness counts, for a click on it: the block it is blocked by, the first prerequisite missing, the first block not finished. */
export function readinessTarget<T extends PageRequirement>(items: readonly T[], readiness: ReadinessOf<T>): T {
  return (readiness.state === "blocked" ? readiness.blocking : readiness.state === "waiting" ? readiness.prerequisites[0]
    : items.find(item => !satisfied(item.attrs.state))) ?? items[0]!;
}
