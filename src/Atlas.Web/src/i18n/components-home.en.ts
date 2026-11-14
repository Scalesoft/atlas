import { plural } from ".";
import type { HomeMessages } from "./components-home";

const en: HomeMessages = {
  title: "Home",
  intro: "What changed in the wiki and where you were last.",
  cards: "Overview",
  showAll: "Show all",
  showAllOf: (title: string) => `Show all: ${title}`,
  retry: "Try again",
  loadFailed: (title: string) => `${title} could not be loaded`,
  loading: (title: string) => `Loading: ${title}…`,
  changes: {
    title: "What changed",
    filter: "Show changes",
    all: "All",
    ai: "Changes by AI",
    mine: "My pages",
    emptyTitle: "Nothing changed in the last 30 days",
    emptyAi: "AI changed nothing in the last 30 days",
    emptyMine: "Nothing changed on your pages in the last 30 days",
    emptyHint: "Changes of pages you can read show up here at once.",
    more: "Load more",
    further: (count: number) => plural(count, { one: "+# more change", few: "+# more changes", other: "+# more changes" }),
    openPage: (title: string) => `Open the page ${title}`,
    history: "History",
    openHistory: (title: string) => `Open the change of ${title} in the history`,
    aiMark: "AI",
    aiMarkTitle: "A change with AI; the author stays the person AI acted for.",
  },
  recent: {
    title: "Recently visited",
    emptyTitle: "You have not opened anything yet",
    emptyHint: "Pages you open will be here to come back to.",
    openPage: (title: string) => `Open ${title}`,
  },
  waiting: {
    title: "Waiting for me",
    unread: (count: number) => plural(count, { one: "# unread notification", few: "# unread notifications", other: "# unread notifications" }),
    noUnread: "No unread notifications",
    drafts: "Drafts with unpublished changes",
    noDrafts: "No drafts in progress",
    openDraft: (title: string) => `Open the draft ${title}`,
  },
};

export default en;
