import type { PageRevisionSummary, RevisionChanges } from "../../history";
import t from "../../i18n/components-history";
import { locale } from "../../i18n";
import { requirementStateLabel } from "../../projector/requirementSummary";
import type { RequirementState } from "../../projector/requirementTypes";

const stateWord = (code: string) => {
  const label = code in { planned: 1, waiting_for_input: 1, in_progress: 1, done: 1, blocked: 1, not_applicable: 1 } ? requirementStateLabel(code as RequirementState) : code;
  return label.charAt(0).toLocaleUpperCase(locale) + label.slice(1);
};
const names = (list: string[]) => list.length > 3 ? t.summary.names(list.slice(0, 3).join(", "), list.length - 3) : list.join(", ");

/** What changed in the components, in human Czech: „3 požadavky → Hotovo“, „+2 požadavky“, „Kontakty +Jana Nováková“. */
export function describeChanges(c: RevisionChanges): string[] {
  const lines: string[] = [];
  if (c.titleBefore !== null) lines.push(t.summary.titleChanged(c.titleBefore));
  const byTarget = new Map<string, number>();
  for (const s of c.states) byTarget.set(s.to, (byTarget.get(s.to) ?? 0) + 1);
  for (const [to, n] of byTarget) lines.push(`${t.summary.requirements(n)} → ${stateWord(to)}`);
  if (c.requirementsAdded) lines.push(`+${t.summary.requirements(c.requirementsAdded)}`);
  if (c.requirementsRemoved) lines.push(`−${t.summary.requirements(c.requirementsRemoved)}`);
  if (c.contactsAdded.length) lines.push(`${t.summary.contacts}: +${names(c.contactsAdded)}`);
  if (c.contactsRemoved.length) lines.push(`${t.summary.contacts}: −${names(c.contactsRemoved)}`);
  if (c.attachmentsAdded) lines.push(`+${t.summary.attachments(c.attachmentsAdded)}`);
  if (c.attachmentsRemoved) lines.push(`−${t.summary.attachments(c.attachmentsRemoved)}`);
  return lines;
}

/** Bar width in percent on a log scale, so a ten-line edit is still visible next to a rewrite (about 2 000 lines is full). */
export function sizeBar(changes: RevisionChanges | null | undefined) {
  const n = (changes?.added ?? 0) + (changes?.removed ?? 0);
  return n <= 0 ? 0 : Math.max(6, Math.min(100, Math.round(Math.log10(n + 1) / Math.log10(2000) * 100)));
}

/** Contributors with their share of the version's checkpoints, in percent. */
export function shares(changes: RevisionChanges | null | undefined) {
  const list = changes?.contributors ?? [];
  const total = list.reduce((sum, c) => sum + c.checkpoints, 0);
  return list.map(c => ({ ...c, share: total ? Math.round(c.checkpoints / total * 100) : 0 }));
}

export type HistoryFilter = "all" | "published" | "named" | "ai";
export function matchesFilter(item: PageRevisionSummary, filter: HistoryFilter) {
  return filter === "all" || filter === "published" && (item.isPublished || !!item.lastPublishedAt) || filter === "named" && !!item.label || filter === "ai" && !!item.isAi;
}

const pad = (n: number) => String(n).padStart(2, "0");
/** Local day key (yyyy-mm-dd) or hour key (yyyy-mm-dd hh) of a version, the bucket of the activity chart. */
export function bucketKey(iso: string, hourly: boolean) {
  const d = new Date(iso);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}${hourly ? ` ${pad(d.getHours())}` : ""}`;
}
/** Hours when the loaded range spans at most two days, days otherwise. */
export function isHourly(items: PageRevisionSummary[]) {
  if (!items.length) return false;
  const times = items.map(i => new Date(i.activeFrom).getTime());
  return Math.max(...times) - Math.min(...times) <= 2 * 86_400_000;
}
export interface ActivityBucket { key: string; people: number; ai: number; published: number }
/** Oldest first; only buckets that have a version. Sizes are added+removed lines from the versions' summaries (none for pre-3.6 rows). */
export function activityBuckets(items: PageRevisionSummary[]): ActivityBucket[] {
  const hourly = isHourly(items), map = new Map<string, ActivityBucket>();
  for (const item of items) {
    const key = bucketKey(item.activeFrom, hourly);
    const bucket = map.get(key) ?? { key, people: 0, ai: 0, published: 0 };
    const size = (item.changes?.added ?? 0) + (item.changes?.removed ?? 0);
    if (item.isAi) bucket.ai += size; else bucket.people += size;
    if (item.isPublished || item.lastPublishedAt) bucket.published++;
    map.set(key, bucket);
  }
  return [...map.values()].sort((a, b) => a.key.localeCompare(b.key));
}
