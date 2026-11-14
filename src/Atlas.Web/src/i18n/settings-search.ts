import { translate } from ".";

/** The search field (settings/SearchField), small because the library shows it at once; the rest of the settings shell is in settings-shell. The Czech source. */
const cs = {
  searchField: { clear: (label: string) => `Vymazat: ${label}` },
  searchPlaceholder: "Hledat…",
};
export type SettingsSearchMessages = typeof cs;

export default translate(cs, { en: () => import("./settings-search.en") });
