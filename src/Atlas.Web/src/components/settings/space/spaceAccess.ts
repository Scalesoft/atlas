import { spaceRoles, type SpaceRole } from "../../../administration";
import t from "../../../i18n/settings-space";
import { roleLabel } from "../../../roles";
import type { ChoiceOption } from "../types";

/**
 * The space role staircase as level options (a new space's group roles in CreateSpaceForm). Who has access to a space
 * is shown by the access layer (components/settings/access, kind space).
 */
const roleIcons: Record<SpaceRole, string> = { Reader: "book-open", Writer: "pencil", Admin: "shield-check" };

/** Lowest first, in the words of roles.ts. The texts are getters, read when shown, so the language of the page applies. */
export const spaceRoleOptions: ChoiceOption[] = spaceRoles.map((role) => ({
  value: role,
  get label() { return roleLabel(role); },
  icon: roleIcons[role],
  get description() { return t.roles[role]; },
}));
