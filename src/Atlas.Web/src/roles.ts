import t from "./i18n/shell";
// The one place that turns a technical role into the word the reader sees (spec D5). Space and page roles are steps of
// the access staircase (docs/access-model.md §1): Čte → Upravuje → Spravuje; the wiki role names a person's role.
export const roleLabels = {
  get Reader() { return t.role.reader; },
  get Writer() { return t.role.writer; },
  get Admin() { return t.role.admin; },
  get WikiAdmin() { return t.role.wikiAdmin; },
} as const;
export type RoleKey = keyof typeof roleLabels;

/** An unknown role stays as it came: a server that grows a role must not lose it in the UI. */
export function roleLabel(role: string): string {
  return roleLabels[role as RoleKey] ?? role;
}
