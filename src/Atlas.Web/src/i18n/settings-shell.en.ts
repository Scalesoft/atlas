import { formatNumber } from ".";
import type { SettingsShellMessages } from "./settings-shell";

const en: SettingsShellMessages = {
  cancel: "Cancel",
  saveBar: {
    save: "Save changes",
    message: "You have unsaved changes",
    region: "Save changes",
    saved: "Saved",
    saving: "Saving…",
  },
  retry: "Retry",
  dismissBanner: "Dismiss notification",
  dismissNote: "Dismiss notice",
  liveUpdate: { unavailable: "Live update unavailable.", refresh: "Refresh" },
  tags: {
    empty: "Nothing matches the search.",
    remove: (label, twin) => `Remove ${label}${twin ? ` (${twin})` : ""}`,
    search: (label) => `${label}: search`,
    suggestions: (label) => `${label}: suggestions`,
    more: (more, typing) => `+${formatNumber(more)} more – ${typing ? "refine the search" : "search"}`,
  },
  choiceList: { empty: "Nothing matches the search. Try another word.", search: (label) => `${label}: search` },
  shell: { searchSections: "Search sections", noMatch: "Nothing like that here.", sections: "Sections", closeSections: "Close sections" },
  advanced: "Advanced",
  summary: "Summary",
  lazy: {
    title: "Couldn't load the settings",
    text: "The app files couldn't be downloaded. Check your connection or refresh the page: a new version may have been released.",
    retry: "Refresh page",
  },
  trash: {
    purgeTitle: "Delete permanently?",
    emptyTitle: "Trash is empty",
    failed: "Couldn't load the trash",
    reload: "Reload",
    loading: "Loading trash…",
    restore: "Restore",
    restoreItem: (title) => `Restore: ${title}`,
    purge: "Delete permanently",
    purgeItem: (title) => `Delete permanently: ${title}`,
    actions: (title) => `Actions: ${title}`,
    purgeBody: "will be deleted along with its history. This can't be undone.",
    backupNote: "Copies in earlier backups (ZIP archives, Git backup history) remain; only a backup admin can remove them from there.",
    typeName: "To confirm, type the name",
  },
};

export default en;
