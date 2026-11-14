/**
 * Hash routes of the two settings levels (plan 2026-09-27):
 * - global settings for the wiki administrator: `#/settings/<group>/<section>`,
 * - space settings for a space administrator (or the wiki administrator): `#/spaces/<spaceId>/settings/<section>`.
 * The application shell only needs to recognise them; the settings surface parses the rest itself.
 * Old links keep working: `#/settings` and the former tab names redirect to their new sections.
 */
export const globalSettingsBase = "#/settings";
export const globalSettingsHome = `${globalSettingsBase}/security/overview`;

export function spaceSettingsBase(spaceId: string) {
  return `#/spaces/${encodeURIComponent(spaceId)}/settings`;
}
export function spaceSettingsHash(spaceId: string, section = "general") {
  return `${spaceSettingsBase(spaceId)}/${section}`;
}

/** The former `#/settings` tabs and removed sections, now sections of their own. */
const legacySections: Record<string, string> = {
  users: "people/accounts",
  accounts: "people/accounts",
  groups: "people/groups",
  modules: "general/modules",
  audit: "security/audit",
  backups: "general/backups",
  // Záloha do Gitu became a tab of Zálohy (one page for every backup, 2026-09-29).
  "general/git-backup": "general/backups/git",
  vaults: "security/vaults",
  // Přehled práv became the person's card (security centre redesign 2026-09-29).
  "security/access": "people/accounts",
};

export type SettingsLocation =
  | { scope: "global"; hash: string; redirect: boolean }
  | { scope: "space"; spaceId: string; hash: string; redirect: boolean };

/** Which settings surface a hash belongs to, and the canonical hash when it has to be redirected. */
export function resolveSettingsHash(hash: string): SettingsLocation | undefined {
  const global = /^#\/settings(?:\/([^?]*))?(\?.*)?$/.exec(hash);
  if (global) {
    const path = (global[1] ?? "").replace(/\/+$/, "");
    if (!path) return { scope: "global", hash: globalSettingsHome, redirect: true };
    // Only an entry of the table itself: `constructor` or `toString` is a property of every object, not an old link.
    const legacy = Object.hasOwn(legacySections, path) ? legacySections[path] : undefined;
    if (legacy) return { scope: "global", hash: `${globalSettingsBase}/${legacy}`, redirect: true };
    return { scope: "global", hash, redirect: false };
  }
  const space = /^#\/spaces\/([a-zA-Z0-9-]+)\/settings(?:\/([^?]*))?(\?.*)?$/.exec(hash);
  if (space) {
    const spaceId = space[1]!;
    const path = (space[2] ?? "").replace(/\/+$/, "");
    if (!path) return { scope: "space", spaceId, hash: spaceSettingsHash(spaceId), redirect: true };
    return { scope: "space", spaceId, hash, redirect: false };
  }
  return undefined;
}

/** The first segment below `…/settings/` of a space settings hash; Obecné when the hash names none. */
export function spaceSettingsSection(hash: string) {
  return /^#\/spaces\/[a-zA-Z0-9-]+\/settings\/([^/?]+)/.exec(hash)?.[1] ?? "general";
}

/** The same section of another space. What lies below the section (a vault, a person, an exception) names a record of the old space and is dropped. */
export function sameSectionInSpace(hash: string, spaceId: string) {
  return spaceSettingsHash(spaceId, spaceSettingsSection(hash));
}

/** True when both hashes are on the same settings surface (global, or the same space). */
export function sameSettingsSurface(a: SettingsLocation | undefined, b: SettingsLocation | undefined) {
  if (!a || !b || a.scope !== b.scope) return false;
  return a.scope === "global" || (b.scope === "space" && a.spaceId === b.spaceId);
}
