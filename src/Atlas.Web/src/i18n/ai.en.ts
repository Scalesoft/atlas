import { plural } from ".";
import type { AiMessages } from "./ai";

const en: AiMessages = {
  unavailable: "This feature isn't available right now.",
  limit: "The assistant's usage limit for today has been reached.",
  sources: (count) => plural(count, { one: "# source", other: "# sources" }),
  status: {
    readPage: "Reading the page",
    searchSpace: (name) => name ? `Searching space ${name}` : "Searching the space",
    searchWiki: "Searching the wiki",
    writeSummary: "Writing the summary",
    writeAnswer: "Writing the answer",
    stop: "Stop",
    progress: "Progress",
  },
};

export default en;
