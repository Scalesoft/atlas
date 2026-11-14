import type { Space } from "./api";
import { put } from "./api";
import { locale } from "./i18n";

/** The account's own pin of a space it sees. A Required space refuses `pinned: false` with 409 `space_pin_required`. */
export function setSpacePinned(spaceId: string, pinned: boolean) {
  return put<{ pinned: boolean }>(`/me/spaces/${encodeURIComponent(spaceId)}/pin`, { pinned });
}

export const SPACE_PIN_REQUIRED = "space_pin_required";

type PinnedSpace = Pick<Space, "name" | "pinned" | "pinPolicy">;
export const isSpacePinned = (space: PinnedSpace) => space.pinned || space.pinPolicy === "Required";
const pinRank = (space: PinnedSpace) => (space.pinPolicy === "Required" ? 0 : isSpacePinned(space) ? 1 : 2);

/**
 * The order of spaces wherever the reader picks one: pinned by the administrator, then the account's
 * own pins, then the rest, each group by name. The sidebar, Knihovna → Prostory and the space pickers
 * share it, so a pinned space is always at the top.
 */
export function comparePinnedFirst(first: PinnedSpace, second: PinnedSpace) {
  return pinRank(first) - pinRank(second) || first.name.localeCompare(second.name, locale);
}
export const pinnedFirst = <T extends PinnedSpace>(spaces: readonly T[]) => [...spaces].sort(comparePinnedFirst);
