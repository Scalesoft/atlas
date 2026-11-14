import { translate } from ".";

/** The search modes (search/SearchModeSwitch, SemanticBadge), small because the search opens from the shell at once; the inbox, comments, connections and API panels are in components-search. The Czech source. */
const cs = {
  search: {
    modes: "Režim hledání",
    all: "Vše",
    ask: "Zeptat se",
    commands: "Příkazy",
    semanticTip: "Stránka neobsahuje přesná slova dotazu, ale odpovídá významem.",
    semantic: "Významová shoda",
  },
};
export type SearchModeMessages = typeof cs;

export default translate(cs, { en: () => import("./components-search-mode.en") });
