import { plural } from ".";
import type { PublishingMessages } from "./components-publishing";

const en: PublishingMessages = {
  control: {
    publish: "Publish",
    publishAnyway: "Publish anyway",
    publishing: "Publishing…",
    group: "Publishing",
    withSummary: "Publish with a change note…",
    before: "Before publishing",
    backToEdit: "Back to editing",
    discard: "Discard changes…",
    checkCount: (count) => plural(count, { one: "# check", other: "# checks" }),
  },
  discard: {
    action: "Discard changes",
    text: "The page returns to the published version. The discarded state stays in the history and can be restored.",
    label: "What is discarded (against the published version)",
    done: "The changes were discarded. The page is back at the published version; the discarded state stays in the history.",
  },
  findings: {
    warning: "Warning",
    info: "Information",
    label: "Check findings",
    checking: "Checking…",
    show: "Show",
    open: "Open",
  },
  summary: {
    label: "Change note (optional)",
    help: "Shown in the page history and in Last published.",
    pageChanged: "The page changed in the meantime. Try suggesting again.",
    proposalFailed: "Could not prepare a suggestion. You can write the note by hand.",
    suggest: "Suggest",
    proposalLabel: "Suggested change note",
  },
};
export default en;
