import { plural } from ".";
import type { ObjectsMessages } from "./objects";

const en: ObjectsMessages = {
  types: {
    page: { label: "Page", plural: "Pages" },
    board: { label: "Board", plural: "Boards" },
    contact: { label: "Contact", plural: "Contacts" },
    note: { label: "Note", plural: "Notes" },
  },
  kinds: {
    link: { label: "links to", incoming: "links here", use: "link" },
    placement: { label: "is on the board", incoming: "is placed here", use: "card on a board" },
    attached: { label: "belongs to the page", incoming: "has a deployment board", use: "board of a page" },
    mention: { label: "mentions", incoming: "is mentioned in a page", use: "mention" },
    reference: { label: "is in the tree", incoming: "is in the tree as a link", use: "link in the tree" },
    template: { label: "was created from a template", incoming: "a page was created from it", use: "page from a template" },
    party: { label: "is waiting for", incoming: "is waited for in a requirement", use: "party of a requirement" },
    catalog: { label: "uses from the catalog", incoming: "is used in a deployment", use: "requirement from the catalog" },
  },
  places: (count: number) => plural(count, { one: "# place", other: "# places" }),
  detail: "Object detail",
  unavailable: "Unavailable",
  untitled: "Untitled",
  leadsNowhere: "The link leads nowhere",
  brokenLink: "broken link",
};

export default en;
