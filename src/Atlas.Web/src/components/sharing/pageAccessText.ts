import type { AccessPerson, AccessSource, PageAccessExplanation } from "../../sharing";
import { formatShortDate } from "../../formatDate";
import { locale } from "../../i18n";
import t from "../../i18n/components-sharing";
import type { AccessPath, ChoiceOption } from "../settings/types";

/** Page roles on the access staircase, lowest first, in the words of roles.ts (Čte → Upravuje → Spravuje). */
export const pageRoles = ["Reader", "Writer", "Admin"] as const;
export type PageRole = (typeof pageRoles)[number];
const roleIcons: Record<PageRole, string> = { Reader: "book-open", Writer: "pencil", Admin: "shield-check" };
/** The level options of AccessLevelPicker, AccessLevelBadge, GrantFlow and AccessExplanation. A function: the words are read where they show. */
export const pageRoleOptions = (): ChoiceOption[] => pageRoles.map((role) => ({ value: role, icon: roleIcons[role], ...t.access.roles[role] }));

export function pageRoleLabel(role: string | null | undefined): string {
  return role ? t.access.roles[role as PageRole]?.label ?? role : t.access.none;
}

function validity(source: AccessSource, now: Date): string {
  if (source.effective === false || (source.expiresAt && new Date(source.expiresAt) <= now)) {
    if (source.startAt && new Date(source.startAt) > now) return t.access.from(formatShortDate(source.startAt, now));
    return t.access.expired;
  }
  return source.expiresAt ? t.access.until(formatShortDate(source.expiresAt, now)) : "";
}

function expiryDateTime(value: string | null | undefined, now: Date): string | undefined {
  return value ? `${formatShortDate(value, now)} ${new Date(value).toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" })}` : undefined;
}

/** The reason of one source, e.g. „Přímo na stránce · do 31. 12.“ or „Výchozí prostoru (Čte)“ (a space role). */
export function sourceLabel(source: AccessSource, now = new Date()): string {
  const group = source.groupId ? source.name : "";
  switch (source.kind) {
    case "global": return t.access.wikiAdmin + (source.expiresAt || source.effective === false ? validity(source, now) : t.access.always);
    case "hubGlobal": return source.effective === false || (source.expiresAt && new Date(source.expiresAt) <= now)
      ? t.access.hubExpired
      : t.access.hubVerified(expiryDateTime(source.expiresAt, now) ?? t.access.nextVerification);
    case "spaceAdmin": return (group ? t.access.spaceAdminGroup(group) : t.access.spaceAdmin) +
      (source.effective === false || (source.expiresAt && new Date(source.expiresAt) <= now) ? t.access.expired : source.expiresAt ? t.access.until(expiryDateTime(source.expiresAt, now)!) : t.access.always);
    case "space": return (group ? t.access.spaceDefaultGroup(group, pageRoleLabel(source.role)) : t.access.spaceDefault(pageRoleLabel(source.role))) + validity(source, now);
    case "page": return (group ? t.access.viaGroup(group) : t.access.direct) + validity(source, now);
    default: return source.name;
  }
}

/** The paths of AccessExplanation: how each effective source reaches the page, with the page role it gives. */
export function accessPaths(sources: AccessSource[], where: { spaceName: string; pageTitle: string }, now = new Date()): AccessPath[] {
  return sources.filter((source) => source.effective !== false).map((source): AccessPath => {
    const until = source.expiresAt ? (source.kind === "hubGlobal" || source.kind === "spaceAdmin" ? expiryDateTime(source.expiresAt, now) : formatShortDate(source.expiresAt, now)) : undefined;
    switch (source.kind) {
      case "global": return { via: "role", label: t.access.pathWikiAdmin, scope: t.access.pathAllPages, level: source.role, locked: true, until };
      case "hubGlobal": return { via: "role", label: t.access.pathWikiAdminHub, scope: t.access.pathAllPages, level: source.role, locked: true, until };
      case "spaceAdmin": return source.groupId
        ? { via: "group", label: source.name, scope: t.access.pathSpaceAsAdmin(where.spaceName), level: source.role, locked: true, until }
        : { via: "spaceAdmin", label: t.access.pathSpaceAdmin(where.spaceName), scope: t.access.pathSpacePages(where.spaceName), level: source.role, locked: true, until };
      case "space": return { via: source.groupId ? "group" : "direct", label: source.name, scope: t.access.pathSpaceDefault(where.spaceName), level: source.role, until };
      default: return { via: source.groupId ? "group" : "direct", label: source.name, scope: t.access.pathPage(where.pageTitle), level: source.role, until };
    }
  });
}

/** „1 člověk“, „3 lidé“, „5 lidí“. */
export function peopleCount(count: number): string {
  return t.access.people(count);
}

/** „zobrazeno 3. 10. · upraveno 28. 9.“, „nikdy“ when there is no record. */
export function lastActivity(person: Pick<AccessPerson, "lastViewedAt" | "lastEditedAt">, now = new Date()): string {
  const viewed = person.lastViewedAt ? formatShortDate(person.lastViewedAt, now) : t.access.never;
  const edited = person.lastEditedAt ? formatShortDate(person.lastEditedAt, now) : t.access.never;
  return t.access.lastActivity(viewed, edited);
}

const denial = (key: NonNullable<PageAccessExplanation["denial"]>): string =>
  ({ inactive: t.access.denialInactive, privateDraft: t.access.denialPrivateDraft, ownAccess: t.access.denialOwnAccess, none: t.access.denialNone })[key];

/** „vidí, může upravovat — Přímo na stránce“: the decision line of „Zobrazit jako“. */
export function explanationLine(result: PageAccessExplanation, now = new Date()): string {
  const decision = t.access.decision(result.canView, result.canEdit);
  const reason = result.denial ? denial(result.denial)
    : result.sources.filter(source => source.effective !== false).map(source => sourceLabel(source, now))[0] ?? "";
  return reason ? `${decision} — ${reason}` : decision;
}
