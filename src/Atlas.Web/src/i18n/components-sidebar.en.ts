import type { SidebarMessages } from "./components-sidebar";

const en: SidebarMessages = {
  sidebar: {
    openSpace: "Open space",
    pinSpace: (name) => `Pin space ${name}`,
    pin: "Pin",
    unpin: "Unpin",
    collapseSpace: (name) => `Collapse space ${name}`,
    expandSpace: (name) => `Expand space ${name}`,
    collapseTitle: "Collapse space",
    expandTitle: "Expand space",
    sortedTip: "Pages sorted alphabetically",
    sortedSr: ", pages sorted alphabetically",
    pinnedByAdminTip: "Pinned by an admin",
    pinnedByAdminSr: ", pinned by an admin",
    findIn: (name) => `Find in the tree of space ${name}`,
    moreActions: (name) => `More actions for space ${name}`,
    find: "Find in tree",
    expandAll: "Expand all",
    collapseAll: "Collapse all",
    sortAlpha: "Sort alphabetically",
    dragPages: "Move pages by dragging",
    searchWiki: (query) => `Search the whole wiki for “${query}”`,
    matches: (count, title) => `Matches: ${count}, Enter opens ${title}`,
    noMatch: "No matches, Enter searches the whole wiki",
    pagesOf: (name) => `Pages of space ${name}`,
    empty: "This space has no pages yet.",
    loadFailed: "Could not load the pages.",
    retry: "Retry",
    loading: "Loading pages…",
  },
};
export default en;
