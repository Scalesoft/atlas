import { formatNumber, translate } from ".";

/** Bulk selection of a list (settings/BulkSelectBar): the same words in Obsah, Schvalování and Stav obsahu. The Czech source. */
const cs = {
  selectShown: "Vybrat zobrazené",
  selectShownCount: (count: number) => `Vybrat zobrazené (${formatNumber(count)})`,
  selected: (count: number) => `Vybráno: ${formatNumber(count)}`,
  clear: "Zrušit výběr",
};
export type SelectionMessages = typeof cs;

export default translate(cs, { en: () => import("./selection.en") });
