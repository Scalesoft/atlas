import { formatNumber, plural } from ".";
import type { SettingsAppearanceMessages } from "./settings-appearance";

const en: SettingsAppearanceMessages = {
  picker: {
    icon: "Icon",
    current: "Current",
    none: "No icon",
    preview: (icon, color) => `Preview: ${icon}, ${color}`,
    spaceName: "Space name",
    pageName: "Page name",
    fewer: "Fewer icons",
    more: (total) => `More icons (${total})`,
    failedTitle: "Couldn't load more icons",
    failedText: "Check your connection and try again. The common icons above still work.",
    loading: "Loading icons…",
    color: "Color",
  },
  colors: {
    label: "Color",
    fewer: "Fewer colors",
    more: (count) => `More colors and tones (${count})`,
    note: (hues, tones) => `${hues} colors × ${tones} tones. Every combination has a verified icon contrast.`,
    all: "All colors",
  },
  browser: {
    all: "All",
    recommended: "Recommended",
    recent: "Recently used",
    found: (matched, total) => `Found ${formatNumber(matched)} of ${formatNumber(total)}`,
    icons: (n) => plural(n, { one: "# icon", other: "# icons" }),
    search: "Search icons",
    searchPlaceholder: (total) => `Search ${formatNumber(total)} icons`,
    categories: "Icon categories",
    listbox: "Icons",
    showAll: (matched) => `Show all ${formatNumber(matched)}`,
    empty: "No icon matches. Try another word, such as “people”, “data” or “lock”.",
    everywhere: "Search all categories",
  },
};

export default en;
