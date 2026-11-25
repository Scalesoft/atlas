import { hintLabel } from "../keyboard/shortcuts";
import type { ComponentsTreeMessages } from "./components-tree";

const en: ComponentsTreeMessages = {
  tree: {
    noNest: "Can't nest here",
    dropRefused: (title: string) => `You cannot move the page here: you do not have permission to edit the page “${title}”.`,
    noAccessLink: "Link without access",
    open: "Open",
    openAttached: "Open attached board",
    copyLink: "Copy link",
    newSubpage: "New subpage",
    newBoard: "New board",
    rename: "Rename",
    move: "Move…",
    sortByTitle: "Sort alphabetically",
    removeFromTree: "Remove from tree",
    moveToTrash: "Move to trash…",
    expand: "Expand",
    collapse: "Collapse",
    newName: (title: string) => `New page title ${title}`,
    sortedTitle: "Subpages sorted alphabetically",
    sortedMark: "subpages sorted alphabetically",
    referenceNoAccess: "Link, you have no access to its target",
    referenceMark: " — Link",
    hasBoard: `has a deployment board (${hintLabel("Mod+Enter")})`,
    openBoardOf: (title: string) => `Open the deployment board of page ${title}`,
    deploymentBoard: "Deployment board",
    actionsOf: (title: string) => `Actions: ${title}`,
    actions: "Actions",
  },
  badge: {
    draft: "Draft",
    unpublished: "Unpublished changes",
    published: "Published",
  },
};
export default en;
