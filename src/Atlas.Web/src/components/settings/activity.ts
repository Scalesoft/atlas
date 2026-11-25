/**
 * "Kdo co kdy": the activity model behind ActivityTimeline and ActivityFilters.
 * Readable sentence first (actor + action + target), technical details only on demand.
 */
import { formatTime, formatWeekdayDate } from "../../formatDate";
import t from "../../i18n/settings-activity";

export type ActivityOutcome = "allowed" | "denied" | "changed" | "failed";
export type ActivityPeriod = "24h" | "7d" | "30d" | "all";

export interface ActivityDetail {
  label: string;
  value: string;
  /** A caveat shown with the value, e.g. "není důkaz přečtení". */
  hint?: string;
}

export interface ActivityItem {
  id: string;
  /** ISO timestamp. */
  at: string;
  actorId: string;
  actor: string;
  /** Verb phrase after the actor: "zobrazila heslo". */
  action: string;
  target?: string;
  /** Opens the target, e.g. the page an assistant edited. */
  href?: string;
  /** Read after the target: "přes MCP". */
  via?: string;
  /** Where it happened: "trezor Provoz – servery · prostor Provoz". */
  context?: string;
  /** Filter key of the kind of event (release, access, protection, network, vault...). */
  category: string;
  outcome: ActivityOutcome;
  icon: string;
  details?: ActivityDetail[];
  spaceId?: string;
  vaultId?: string;
  /** Replaces the outcome badge where the four outcomes read wrong (a backup run that is "Probíhá"). */
  badge?: { label: string; tone: "success" | "danger" | "info" | "warning" | "neutral"; icon: string };
}

export interface ActivityFilterState {
  /** Actor ids; any of them matches. */
  who?: string[];
  what?: string;
  when?: ActivityPeriod;
  result?: ActivityOutcome;
}

export const outcomeLabels: Record<ActivityOutcome, { label: string; tone: "success" | "danger" | "info" | "warning"; icon: string }> = {
  // Getters: the label is read where it is shown, in the language of the page.
  allowed: { get label() { return t.outcome.allowed; }, tone: "success", icon: "circle-check" },
  denied: { get label() { return t.outcome.denied; }, tone: "danger", icon: "ban" },
  changed: { get label() { return t.outcome.changed; }, tone: "info", icon: "pencil" },
  failed: { get label() { return t.outcome.failed; }, tone: "warning", icon: "triangle-alert" },
};

export const periodLabels: Record<ActivityPeriod, string> = {
  get "24h"() { return t.period["24h"]; },
  get "7d"() { return t.period["7d"]; },
  get "30d"() { return t.period["30d"]; },
  get all() { return t.period.all; },
};

const periodMs: Record<Exclude<ActivityPeriod, "all">, number> = { "24h": 86_400_000, "7d": 7 * 86_400_000, "30d": 30 * 86_400_000 };

export function filterActivity(items: ActivityItem[], filters: ActivityFilterState, now: Date = new Date()): ActivityItem[] {
  const since = filters.when && filters.when !== "all" ? now.getTime() - periodMs[filters.when] : undefined;
  return items
    .filter((item) => !filters.who?.length || filters.who.includes(item.actorId))
    .filter((item) => !filters.what || item.category === filters.what)
    .filter((item) => !filters.result || item.outcome === filters.result)
    .filter((item) => since === undefined || new Date(item.at).getTime() >= since)
    .sort((a, b) => b.at.localeCompare(a.at));
}

/** URL query keys are Czech words so a copied link reads well: `?kdo=jana,tomas&co=release&kdy=7d&vysledek=denied`. */
export function activityFiltersFromQuery(query: Readonly<Record<string, string>>): ActivityFilterState {
  const when = query.kdy as ActivityPeriod | undefined;
  const result = query.vysledek as ActivityOutcome | undefined;
  return {
    who: query.kdo ? query.kdo.split(",").filter(Boolean) : undefined,
    what: query.co || undefined,
    when: when && when in periodLabels ? when : undefined,
    result: result && result in outcomeLabels ? result : undefined,
  };
}

export function activityFiltersToQuery(filters: ActivityFilterState): Record<string, string | undefined> {
  return { kdo: filters.who?.length ? filters.who.join(",") : undefined, co: filters.what, kdy: filters.when, vysledek: filters.result };
}

function dayKey(date: Date) {
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
}

export function groupActivityByDay(items: ActivityItem[], now: Date = new Date()) {
  const today = dayKey(now);
  const yesterday = dayKey(new Date(now.getFullYear(), now.getMonth(), now.getDate() - 1));
  const groups: { key: string; label: string; items: ActivityItem[] }[] = [];
  for (const item of items) {
    const date = new Date(item.at);
    const key = dayKey(date);
    let group = groups.find((candidate) => candidate.key === key);
    if (!group) {
      const label =
        key === today ? t.today : key === yesterday ? t.yesterday : formatWeekdayDate(date);
      group = { key, label, items: [] };
      groups.push(group);
    }
    group.items.push(item);
  }
  return groups;
}

/** Repeats of one entry within this window fold into it: an assistant retrying a tool reads as one line with "3×". */
const repeatWindowMs = 10 * 60_000;

/** Folds runs of the same actor, action, target and result (newest first) into their newest entry; `more` holds the older ones. */
export function foldRepeats(items: ActivityItem[]) {
  const runs: { item: ActivityItem; more: ActivityItem[] }[] = [];
  const key = (item: ActivityItem) => [item.actorId, item.action, item.target, item.via, item.outcome, item.badge?.label].join("|");
  for (const item of items) {
    const last = runs.at(-1);
    const previous = last && (last.more.at(-1) ?? last.item);
    if (last && previous && key(previous) === key(item) && new Date(previous.at).getTime() - new Date(item.at).getTime() <= repeatWindowMs) last.more.push(item);
    else runs.push({ item, more: [] });
  }
  return runs;
}

export function activityTime(at: string) {
  return formatTime(at);
}
