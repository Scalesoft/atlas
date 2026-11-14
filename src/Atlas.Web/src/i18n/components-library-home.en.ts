import { plural } from ".";
import type { LibraryHomeMessages } from "./components-library-home";

const en: LibraryHomeMessages = {
  page: {
    title: "Library",
    intro: "The spaces you can access, and a way back to your latest work.",
    create: "New space",
    tabs: "Parts of the library",
    parts: {
      spaces: { title: "Spaces", short: "" },
      personal: { title: "My space", short: "Mine" },
      shared: { title: "Shared with me", short: "Shared" },
      recent: { title: "Recent", short: "" },
      favorites: { title: "Favorites", short: "" },
      drafts: { title: "My drafts", short: "Drafts" },
      updates: { title: "Last published", short: "Published" },
      contacts: { title: "Contacts", short: "" },
      notifications: { title: "Notifications", short: "" },
    },
    unread: (count: number) => `${count} unread`,
  },
  content: {
    loadingSpaces: "Loading spaces…",
    noneOpenedTitle: "You have not opened anything yet",
    noneOpenedText: "Open a page in a space on the left, use search, or pick from the spaces you can access.",
    browse: "Browse spaces",
  },
  pages: (count: number) => plural(count, { one: "# page", other: "# pages" }),
  drafts: (count: number) => plural(count, { one: "# draft", other: "# drafts" }),
  access: "Your access",
  accessOf: (role: string) => `Your access: ${role}`,
  header: {
    lastChange: "Last change",
    settings: "Space settings",
    newContent: "New content in the space",
    newTitle: "New page or board",
    new: "New",
    page: "Page",
    board: "Board",
    newPage: "New page",
  },
};
export default en;
