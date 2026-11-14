import { foldText } from "../../../lib/foldText";
import t from "../../../i18n/settings-access";
import { locale } from "../../../i18n";
import { accessKind, levelLabel, levelRank } from "./accessKinds";
import type { AccessEntry, AccessLevel, AccessSource, AccessSubject, PrincipalAccess, ResourceAccess } from "./accessModel";

/**
 * The access contract turned into the rows AccessEntryRow draws: one row is one sentence (who or what · level · where it
 * comes from · until when). A person's card groups the rows by space ("Celý prostor" plus the exceptions inside it); a
 * resource's panel has locked automatic rows, then groups, then people. Pure functions, so every surface words them alike.
 */
export interface AccessRow {
  key: string;
  /** Kind of the resource: its icon and its staircase. */
  kind: string;
  resourceId: string;
  title: string;
  detail?: string;
  /** In a resource's panel the row is a person or a group. */
  subject?: AccessSubject;
  level: AccessLevel | null;
  sources: AccessSource[];
  /** Automatic access (administrators): shown, never changed here. */
  locked?: boolean;
  /** The row's title opens this (a person's card, a resource). */
  href?: string;
}

/** A person's rows of one space (or of the whole wiki, or outside spaces). */
export interface AccessRowGroup {
  id: string;
  title: string;
  icon: string;
  color?: string;
  description?: string;
  rows: AccessRow[];
}

/** How a path reads in a row: "Přímý přístup", "Přes skupinu Provoz tým", "Správce wiki". */
export function sourceText(source: Pick<AccessSource, "via" | "label" | "groupSource">) {
  if (source.via === "direct") return t.view.direct;
  if (source.via === "group") return t.view.viaGroup(source.groupSource === "hub", source.label);
  return source.label;
}

/** The paths that give access now, strongest first; with none, the ones waiting for their validity. */
export function effectiveSources(sources: AccessSource[]) {
  const now = sources.filter((source) => source.effective);
  return (now.length ? now : sources).slice().sort((a, b) => levelRank(b.level) - levelRank(a.level));
}

/** "Přes skupinu Provoz tým", or with more paths "Přímý přístup a 2 další cesty". */
export function sourcesSummary(sources: AccessSource[]) {
  const shown = effectiveSources(sources);
  if (!shown.length) return "";
  const first = sourceText(shown[0]!);
  const rest = shown.length - 1;
  if (!rest) return first;
  return t.view.andMorePaths(first, rest);
}

/** The direct grant of the subject, the one a manager changes or revokes in this row. */
export function directSource(sources: AccessSource[]) {
  return sources.find((source) => source.via === "direct");
}
/** The group a right comes through (offered instead of Odebrat: it is changed on the group). */
export function groupSource(sources: AccessSource[]) {
  return effectiveSources(sources).find((source) => source.via === "group" && source.groupId);
}

/** Whether a grant applies now: pending (future start), expired, or active. */
export function sourceTiming(source: Pick<AccessSource, "startAt" | "expiresAt" | "effective">, now = Date.now()): "active" | "pending" | "expired" {
  if (source.effective) return "active";
  if (source.startAt && new Date(source.startAt).getTime() > now) return "pending";
  return "expired";
}

/** Ends within 14 days: the one validity worth a warning tone. */
export function expiringSoon(expiresAt: string | null | undefined, now = Date.now()) {
  if (!expiresAt) return false;
  const end = new Date(expiresAt).getTime();
  return end > now && end - now <= 14 * 86_400_000;
}

/** The end that limits the access of a row: the latest end of the effective paths, or none while one path is unbounded. */
export function accessEnd(sources: AccessSource[]) {
  const active = sources.filter((source) => source.effective);
  const ends = active.map(grantEnd);
  if (!ends.length || ends.some((end) => !end)) return undefined;
  return ends.filter((end): end is string => !!end).sort().at(-1);
}

export const grantEnd = (source: AccessSource) => source.verificationLimited ? source.grantExpiresAt : source.expiresAt;

const kindOrder = ["wiki", "space", "page", "vault", "secret", "token"];
const kindIndex = (kind: string) => (kindOrder.includes(kind) ? kindOrder.indexOf(kind) : kindOrder.length);

/** The row title of an entry on a person's card: "Celý prostor", "Architektura › Rozhodnutí 2026". */
export function entryTitle(entry: AccessEntry) {
  if (entry.kind === "space") return t.view.wholeSpace;
  if (entry.kind === "youtrack") return t.view.youtrackTasks;
  if (entry.kind === "page" && entry.parentTitle) return `${entry.parentTitle} › ${entry.title}`;
  return entry.title;
}

/** What the resource is and the server's note: "Trezor · 14 hesel", "Heslo v trezoru Síťová zařízení". */
export function entryDetail(entry: AccessEntry) {
  if (entry.kind === "youtrack") return t.view.youtrackProjects(entry.title);
  const kind = accessKind(entry.kind);
  const what =
    entry.kind === "space" || entry.kind === "wiki"
      ? undefined
      : entry.kind === "secret"
        ? entry.parentTitle
          ? t.view.secretInVault(entry.parentTitle)
          : t.view.secret
        : entry.kind === "token"
          ? t.view.personalToken
          : kind.one;
  return [what, entry.note].filter(Boolean).join(" · ") || undefined;
}

export function entryKey(entry: Pick<AccessEntry, "kind" | "resourceId">) {
  return `${entry.kind}:${entry.resourceId}`;
}

/**
 * A person's or group's card grouped by space: the space row ("Celý prostor") and only the exceptions inside it, so a
 * 900-page wiki stays a short list. The wiki role comes first, what lives outside spaces (API tokens) last.
 */
export function principalGroups(access: PrincipalAccess): AccessRowGroup[] {
  const groups = new Map<string, AccessRowGroup>();
  const wiki: AccessRow[] = [];
  const outside: AccessRow[] = [];
  const sorted = [...access.entries].sort((a, b) => kindIndex(a.kind) - kindIndex(b.kind) || entryTitle(a).localeCompare(entryTitle(b), locale));
  for (const entry of sorted) {
    const row: AccessRow = { key: entryKey(entry), kind: entry.kind, resourceId: entry.resourceId, title: entryTitle(entry), detail: entryDetail(entry), level: entry.level, sources: entry.sources };
    if (entry.kind === "wiki") wiki.push({ ...row, title: t.view.wikiAdmin, detail: t.view.wikiAdminDetail });
    else if (!entry.spaceId) outside.push(row);
    else {
      const id = entry.spaceId;
      const group = groups.get(id) ?? groups.set(id, { id, title: entry.spaceName ?? t.view.space, icon: entry.spaceIcon ?? accessKind("space").icon, color: entry.spaceColor ?? "blue", rows: [] }).get(id)!;
      group.rows.push(row);
    }
  }
  for (const group of groups.values()) {
    const space = group.rows.find((row) => row.kind === "space");
    const path = space ? effectiveSources(space.sources)[0] : undefined;
    const how = !path ? "" : path.via === "direct" ? t.view.directly : path.via === "group" ? t.view.viaGroupShort(path.groupSource === "hub", path.label) : ` · ${path.label}`;
    group.description = space?.level ? t.view.spaceLevel(levelLabel(space.level), how) : t.view.spaceSelected;
  }
  const result = [...groups.values()].sort((a, b) => a.title.localeCompare(b.title, locale));
  if (wiki.length) result.unshift({ id: "wiki", title: t.view.wholeWiki, icon: accessKind("wiki").icon, description: t.view.wikiRole, rows: wiki });
  if (outside.length)
    result.push({
      id: "outside",
      title: outside.every(row => row.kind === "token") ? t.view.apiMcp : t.view.outsideSpaces,
      icon: accessKind("token").icon,
      description: access.principal.kind === "user" ? t.view.tokensNote : undefined,
      rows: outside,
    });
  return result;
}

/** A resource's rows: one per person or group with its effective level and paths. */
export function resourceRows(access: ResourceAccess, subjectHref?: (subject: AccessSubject) => string | undefined): AccessRow[] {
  return access.entries
    .map((entry) => ({
      key: `${entry.subject.kind}:${entry.subject.id}`,
      kind: access.resource.kind,
      resourceId: access.resource.id,
      title: entry.subject.name,
      detail: subjectDetail(entry.subject),
      subject: entry.subject,
      level: entry.level,
      sources: entry.sources,
      href: subjectHref?.(entry.subject),
    }))
    .sort((a, b) => a.title.localeCompare(b.title, locale));
}

/** Locked rows of the automatic administrators ("Správci wiki", "Správci prostoru"). */
export function automaticRows(access: ResourceAccess): AccessRow[] {
  return access.automatic.map((item, index) => ({
    key: `automatic:${index}`,
    kind: access.resource.kind,
    resourceId: access.resource.id,
    title: item.label,
    detail: [item.count ? peopleCount(item.count) : undefined, item.detail].filter(Boolean).join(" · "),
    subject: { kind: "group", id: `automatic-${index}`, name: item.label },
    level: item.level,
    sources: [{ via: "automatic", label: t.view.automatic, level: item.level, effective: true }],
    locked: true,
  }));
}

export function subjectDetail(subject: AccessSubject) {
  if (subject.kind === "group") return [t.view.group, subject.memberCount != null ? memberCount(subject.memberCount) : subject.detail].filter(Boolean).join(" · ");
  return [subject.detail, subject.isActive === false ? t.view.deactivatedAccount : undefined].filter(Boolean).join(" · ") || undefined;
}

/** How many rows have each level: the counts of LevelLegend. */
export function levelCounts(rows: Pick<AccessRow, "level">[]) {
  const result: Partial<Record<AccessLevel, number>> = {};
  for (const row of rows) if (row.level) result[row.level] = (result[row.level] ?? 0) + 1;
  return result;
}

/** The kinds whose list the server narrows by `q` before its 200-entry cap (GET /api/access/resources/space/{id}?q=); the others ignore it. */
export const searchesOnServer = (kind: string) => kind === "space";

/** Search words over the text of a row (Czech collation, diacritics-insensitive). */
export function matchesQuery(text: string, query: string) {
  const haystack = foldText(text);
  return foldText(query).split(/\s+/).filter(Boolean).every((word) => haystack.includes(word));
}

/** „1 člen“, „3 členové“, „5 členů“. */
export function memberCount(count: number) {
  return t.view.members(count);
}
/** „1 člověk“, „3 lidé“, „5 lidí“. */
export function peopleCount(count: number) {
  return t.view.people(count);
}
