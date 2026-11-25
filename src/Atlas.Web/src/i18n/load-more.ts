import { translate } from ".";

/** The one "load more" button (components/LoadMoreButton). The Czech source. */
const cs = {
  more: "Načíst další",
  older: "Starší…",
  failed: "Načtení dalších selhalo",
  retry: "Zkusit znovu",
};
export type LoadMoreMessages = typeof cs;

export default translate(cs, { en: () => import("./load-more.en") });
