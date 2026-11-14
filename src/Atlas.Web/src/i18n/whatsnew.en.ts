import type { WhatsNewMessages } from "./whatsnew";

const en: WhatsNewMessages = {
  patchTitle: (release, named) => `Atlas ${release} — fixes to version ${named}`,
  title: (named) => `Atlas ${named} — what's new`,
  patchText: "Small fixes to the version you already know.",
  text: "See what changed in the new version.",
  show: "View",
};

export default en;
