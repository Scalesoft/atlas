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

/** `total` cut into whole numbers in the proportion of `weights` (cumulative rounding, so the parts add up to `total`). */
export function apportion(total: number, weights: number[]): number[] {
  const sum = weights.reduce((a, b) => a + b, 0);
  let done = 0, given = 0;
  return weights.map(weight => { done += weight; const upTo = sum ? Math.round(total * done / sum) : 0; const part = upTo - given; given = upTo; return part; });
}

/** Contributors with their share of the version's checkpoints, in percent (adding up to 100). */
export function shares(changes: RevisionChanges | null | undefined) {
  const list = changes?.contributors ?? [];
  const percent = apportion(100, list.map(c => c.checkpoints));
  return list.map((c, index) => ({ ...c, share: percent[index]! }));
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
  const times = items.map(i => new Date(i.activeFrom).getTime()).filter(Number.isFinite);
  if (!times.length) return false;
  return Math.max(...times) - Math.min(...times) <= 2 * 86_400_000;
}
export interface ActivityBucket { key: string; people: number; ai: number; published: number; byPerson: Record<string, number> }
/**
 * Oldest first; only buckets that have a version. Sizes are added+removed lines from the versions' summaries (none for pre-3.6 rows).
 * A person's lines are their share of the version's saves (rounded so the shares add up); an AI version's lines are AI's.
 */
export function activityBuckets(items: PageRevisionSummary[]): ActivityBucket[] {
  const hourly = isHourly(items), map = new Map<string, ActivityBucket>();
  for (const item of items) {
    if (Number.isNaN(new Date(item.activeFrom).getTime())) continue; // no time, no bucket
    const key = bucketKey(item.activeFrom, hourly);
    const bucket = map.get(key) ?? { key, people: 0, ai: 0, published: 0, byPerson: {} };
    const size = (item.changes?.added ?? 0) + (item.changes?.removed ?? 0);
    if (item.isAi) bucket.ai += size;
    else {
      bucket.people += size;
      const list = item.changes?.contributors.length ? item.changes.contributors : [{ id: item.authorId, checkpoints: 1 }];
      apportion(size, list.map(c => c.checkpoints)).forEach((lines, index) => { const id = list[index]!.id; bucket.byPerson[id] = (bucket.byPerson[id] ?? 0) + lines; });
    }
    if (item.isPublished || item.lastPublishedAt) bucket.published++;
    map.set(key, bucket);
  }
  return [...map.values()].sort((a, b) => a.key.localeCompare(b.key));
}
/** The people of the loaded versions with most lines first (each in their own colour in the chart); `limit` of them, the rest go together. */
export function activityPeople(items: PageRevisionSummary[], buckets: ActivityBucket[], limit = 5) {
  const names = new Map<string, string>(), lines = new Map<string, number>();
  for (const item of items) { names.set(item.authorId, item.authorName); for (const c of item.changes?.contributors ?? []) names.set(c.id, c.name); }
  for (const b of buckets) for (const [id, n] of Object.entries(b.byPerson)) lines.set(id, (lines.get(id) ?? 0) + n);
  const people = [...lines].filter(([, n]) => n > 0).sort((a, b) => b[1] - a[1]).map(([id]) => ({ id, name: names.get(id) ?? "" }));
  return { shown: people.slice(0, limit), others: people.length > limit };
}
