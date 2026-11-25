import type { ComponentsMessages } from "./components-c";

const en: ComponentsMessages = {
  saveState: {
    saving: "Saving…",
    saved: "Saved",
    offline: "Offline, saved when you reconnect",
    paused: "Offline: typing is paused",
    error: "Error",
  },
  suggestions: {
    recent: "Recently opened",
    favorites: "Favorites",
    region: "Recent and favorite pages",
    loading: "Loading recent and favorite pages…",
    retry: "Retry",
    minTitle: "Enter at least two characters",
    minText: "We search titles and content. Your recent and favorite pages show up here.",
    draft: "Draft",
  },
  sidebarResizer: {
    label: "Sidebar width",
    pixels: (value) => `${value} pixels`,
    hint: "Drag to change the width · left/right arrows · double-click restores the default width",
  },
  spaceMenu: {
    newPage: "New page",
    newBoard: "New board",
    settings: "Space settings",
    templates: "Page templates",
    access: "Access",
    pinPolicy: "Pin for everyone…",
    trash: "Page trash",
    exportEmpty: "The space has no pages yet.",
    exportHint: "A ZIP of the saved versions of the pages you can access, with attachments. Not a server backup.",
    export: "Export space",
    contentHealth: "Content health",
  },
  tagSearch: {
    tags: "Tags",
    failed: "Could not load the tags.",
    found: (count) => `Tags found: ${count}. Press Tab to reach the tag buttons.`,
    region: "Tags found",
    pagesWith: (tag) => `Pages with tag ${tag}`,
    retry: "Retry",
  },
  unsaved: {
    title: "Unsaved changes",
    saveOrDiscard: "You have unsaved changes. Do you want to save or discard them?",
    discard: "You have unsaved changes. Do you want to discard them?",
    stay: "Stay",
    discardButton: "Discard",
    saving: "Saving…",
    save: "Save",
  },
  workspacePanel: {
    close: "Close panel",
    tools: "Page tools",
    width: (label) => `Panel width: ${label}`,
  },
};
export default en;
