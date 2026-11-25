import { plural } from ".";
import type { ComponentsAMessages } from "./components-a";

const en: ComponentsAMessages = {
  close: "Close",
  loading: "Loading…",
  versionInfo: { whatsNew: "What's new", info: "Version information", unknown: "unknown version" },
  collaborators: {
    group: "Collaborators in the document",
    more: "More collaborators",
    moreCount: (count) => `More collaborators: ${count}`,
    agent: (name) => `${name} – writing to the page`,
    goTo: (name) => `Go to ${name}’s cursor`,
    noCursor: (name) => `${name} – cursor not available`,
    cursorUnavailable: "Cursor not available",
  },
  copyLink: {
    label: "Copy page link",
    manual: "Automatic copying is not available. Copy the selected link.",
    field: "Page link",
    copied: "Link copied",
    done: "Done",
  },
  editLock: {
    readOnly: "Read-only",
    unlock: "Read-only: unlock editing",
    lock: "Lock editing",
    edit: "Edit",
    pulse: "Read-only. Press Edit to make changes.",
  },
  documentTitle: { pageName: "Page title" },
};
export default en;
