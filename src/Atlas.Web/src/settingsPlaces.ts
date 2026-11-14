import { parseShellHash } from "./shellRoute";
import t from "./i18n/shell";

/** The three places the application shell keeps its settings in: each has its own entry, rail heading and way back. */
export type SettingsPlace = "profile" | "space" | "wiki";
export const settingsPlaceHeading: Record<SettingsPlace, string> = { get profile() { return t.place.profile; }, get space() { return t.common.spaceSettings; }, get wiki() { return t.common.wikiAdmin; } };

/**
 * The label of the way back out of a place. Space settings say the same wherever they were opened from; the profile
 * says where its stored return route leads, and so does Správa wiki when that route is the settings of a space.
 */
export function settingsReturnLabel(place: SettingsPlace, returnRoute: string) {
  if (place === "space") return t.place.backToSpace;
  const route = parseShellHash(returnRoute);
  if (route.view === "settings" && route.settings.scope === "space") return t.place.backToSpaceSettings;
  if (place === "wiki") return t.place.backToWiki;
  switch (route.view) {
    case "page": case "pageAccess": return t.place.backToPage;
    case "space": case "pageCreation": case "pageTemplates": return t.place.backToSpace;
    case "settings": return t.place.backToAdmin;
    default: return t.place.backToWiki;
  }
}
