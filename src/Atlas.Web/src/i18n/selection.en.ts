import { formatNumber } from ".";
import type { SelectionMessages } from "./selection";

const en: SelectionMessages = {
  selectShown: "Select shown",
  selectShownCount: (count) => `Select shown (${formatNumber(count)})`,
  selected: (count) => `${formatNumber(count)} selected`,
  clear: "Clear selection",
};
export default en;
